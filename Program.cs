using System.Globalization;
using System.Text;

using CommandLine;

using GTDataSQLiteConverter.Entities;
using GTDataSQLiteConverter.ParamDb;

using Microsoft.Data.Sqlite;

namespace GTDataSQLiteConverter
{
    /// <summary>
    /// The command-line front end. Every verb runs on the same ParamDb engine as GTParamDBEditor, so a
    /// paramdb and its SQLite export behave the same whichever of the two tools handles them.
    /// </summary>
    internal class Program
    {
        static int Main(string[] args)
        {
            Console.WriteLine("[-- GT3DataSQLiteConverter 1.0.2 by ddm, Nenkai, based on GT3DataSplitter by pez2k -- ]");

            // euc-jp, used by the Japanese name columns, lives in the code pages provider.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            try
            {
                return Parser.Default.ParseArguments<ExportVerbs, ImportVerbs, AddColumnVerbs, RemoveColumnVerbs>(args)
                    .MapResult(
                        (ExportVerbs verbs) => Export(verbs),
                        (ImportVerbs verbs) => Import(verbs),
                        (AddColumnVerbs verbs) => AddColumn(verbs),
                        (RemoveColumnVerbs verbs) => RemoveColumn(verbs),
                        _ => 1);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
            {
                Console.WriteLine($"ERROR: {e.Message}");
                return 1;
            }
        }

        static int Export(ExportVerbs verbs)
        {
            if (!File.Exists(verbs.InputPath))
                throw new FileNotFoundException($"Input paramdb does not exist: {verbs.InputPath}");

            // Companion files are found by the paramdb's suffix (paramdb_us.db -> paramstr_us.db, ...)
            // unless given explicitly.
            ParamDbPaths found = ParamDbPaths.FromParamDbFile(verbs.InputPath);
            var paths = new ParamDbPaths(found.DirectoryPath, found.Suffix, found.ParamDb)
            {
                ParamStr = verbs.ParamStrTablePath,
                ParamUniStr = verbs.UniStrTablePath,
                IdIndex = verbs.IDXTablePath,
                IdStrings = verbs.IDStrTablePath,
                CarColorSdb = verbs.ColorTablePath,
            };

            string output = verbs.OutputPath ?? Path.ChangeExtension(verbs.InputPath, ".sqlite");

            var warnings = new List<string>();
            ParamDbFiles files = ParamDbFiles.Load(paths, warnings);
            using LiveDatabase live = LiveDatabase.CreateFrom(files, output, isTemporary: false, warnings);

            PrintNotes(warnings);

            List<LiveTable> tables = live.Tables.Where(t => t.IsArchiveTable).ToList();
            Console.WriteLine(
                $"Exported {Path.GetFileName(verbs.InputPath)} ({live.Game.DisplayName}, {tables.Count} tables, " +
                $"{tables.Count(t => t.IsMapped)} with a column layout) to {output}");
            return 0;
        }

        static int Import(ImportVerbs verbs)
        {
            if (!File.Exists(verbs.InputPath))
                throw new FileNotFoundException($"Input SQLite database does not exist: {verbs.InputPath}");

            // Work on a copy: opening brings an older export up to date and saving records the saved
            // layouts, and neither should change the file being imported.
            string copy = Path.Combine(Path.GetTempPath(), $"GTDataSQLiteConverter_{Guid.NewGuid():N}.sqlite");
            File.Copy(verbs.InputPath, copy);

            var warnings = new List<string>();
            LiveDatabase live;
            try
            {
                live = LiveDatabase.Open(copy, warnings, isTemporary: true);
            }
            catch
            {
                File.Delete(copy);
                throw;
            }

            using (live)
            {
                PrintNotes(warnings);

                string outputDir = verbs.OutputPath ?? Path.GetDirectoryName(Path.GetFullPath(verbs.InputPath))!;
                var target = new ParamDbPaths(outputDir, verbs.Suffix ?? live.Meta.Suffix);

                SaveReport report = ParamDbWriter.Save(live, target, verbs.Backup);
                if (!report.Succeeded)
                {
                    Console.WriteLine("Nothing was written. These values cannot be stored in the game's format:");
                    foreach (CellIssue issue in report.Issues)
                        Console.WriteLine($"  {issue}");

                    return 1;
                }

                foreach ((string file, long size) in report.WrittenFiles)
                    Console.WriteLine($"Wrote {file} ({size:N0} bytes)");

                foreach (string file in report.Backups)
                    Console.WriteLine($"Backed up the original to {file}");

                foreach (string file in report.LayoutFiles)
                    Console.WriteLine($"Saved column layout to {file}");

                PrintNotes(report.Warnings);
                return 0;
            }
        }

        static int AddColumn(AddColumnVerbs verbs)
        {
            DBColumnType type = DBUtils.ColumnTypeToType(verbs.Type.ToLowerInvariant());
            if (type == DBColumnType.Unknown)
            {
                Console.WriteLine($"ERROR: '{verbs.Type}' is not a column type. Use the .headers spellings: byte, short, int, int64, float, double, id, string, unicode.");
                return 1;
            }

            int? offset = null;
            if (verbs.Offset is not null)
            {
                string hex = verbs.Offset.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? verbs.Offset[2..] : verbs.Offset;
                if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int parsed) || parsed < 0)
                {
                    Console.WriteLine($"ERROR: '{verbs.Offset}' is not a hex offset, like 30 or 0x30.");
                    return 1;
                }

                offset = parsed;
            }

            using LiveDatabase live = OpenForLayout(verbs.InputPath, verbs.Table, out LiveTable? table);
            if (table is null)
                return 1;

            int at = offset ?? table.SuggestOffset(type);
            string? problem = table.CheckNewColumn(verbs.Name, type, at);
            if (problem is not null)
            {
                Console.WriteLine($"ERROR: {problem}");
                return 1;
            }

            int rowSize = table.RowStride;
            live.AddColumn(table, verbs.Name, type, at);

            Console.WriteLine($"Added {verbs.Name} ({type} at 0x{at:X}) to {table.Name}.{RowSizeChange(rowSize, table.RowStride)}");
            return 0;
        }

        static int RemoveColumn(RemoveColumnVerbs verbs)
        {
            using LiveDatabase live = OpenForLayout(verbs.InputPath, verbs.Table, out LiveTable? table);
            if (table is null)
                return 1;

            LiveColumn? column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, verbs.Name, StringComparison.OrdinalIgnoreCase));
            if (column is null)
            {
                Console.WriteLine($"ERROR: {table.Name} has no column called {verbs.Name}.");
                return 1;
            }

            if (!table.CanRemove(column))
            {
                Console.WriteLine($"ERROR: {column.Name} is the label every row is found by, so it cannot be removed.");
                return 1;
            }

            int rowSize = table.RowStride;
            live.RemoveColumn(table, column);

            Console.WriteLine($"Removed {column.Name} from {table.Name}.{RowSizeChange(rowSize, table.RowStride)}");
            return 0;
        }

        /// <summary>Opens a SQLite export in place, for a column change the next import writes out.</summary>
        static LiveDatabase OpenForLayout(string path, string tableName, out LiveTable? table)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"SQLite database does not exist: {path}");

            var warnings = new List<string>();
            LiveDatabase live = LiveDatabase.Open(path, warnings);
            PrintNotes(warnings);

            table = live.FindTable(tableName);
            if (table is null)
                Console.WriteLine($"ERROR: there is no table called {tableName}.");
            else if (!table.IsMapped || !table.CanEditLayout)
                Console.WriteLine($"ERROR: columns cannot be added to or removed from {table.Name}.");
            else
                return live;

            table = null;
            return live;
        }

        static string RowSizeChange(int before, int after)
            => before == after ? "" : $" Rows {(after > before ? "grow" : "shrink")} from {before} to {after} bytes when imported.";

        static void PrintNotes(IEnumerable<string> notes)
        {
            foreach (string note in notes)
                Console.WriteLine($"NOTE: {note}");
        }
    }

    [Verb("export", HelpText = "Export a GT3 or GT Concept paramdb to a SQLite database.")]
    public class ExportVerbs
    {
        [Option('i', "input", Required = true, HelpText = "Input paramdb file.")]
        public string InputPath { get; set; } = "";

        [Option('o', "output", Required = false, HelpText = "Output SQLite database file. Default is based on the paramdb.")]
        public string? OutputPath { get; set; }

        [Option("idx", HelpText = "Input id_db_idx file. Default is based on the paramdb.")]
        public string? IDXTablePath { get; set; }

        [Option("istr", HelpText = "Input id_db_str file. Default is based on the paramdb.")]
        public string? IDStrTablePath { get; set; }

        [Option("pstr", HelpText = "Input paramstr file. Default is based on the paramdb.")]
        public string? ParamStrTablePath { get; set; }

        [Option("ustr", HelpText = "Input paramunistr file. Default is based on the paramdb.")]
        public string? UniStrTablePath { get; set; }

        [Option("cstr", HelpText = "Input carcolor sdb file. Default is carcolor.sdb next to the paramdb.")]
        public string? ColorTablePath { get; set; }
    }

    [Verb("import", HelpText = "Write the game files back out from a SQLite database.")]
    public class ImportVerbs
    {
        [Option('i', "input", Required = true, HelpText = "Input SQLite database.")]
        public string InputPath { get; set; } = "";

        [Option('o', "output", Required = false, HelpText = "Folder to write the game files to. Default is the SQLite file's folder.")]
        public string? OutputPath { get; set; }

        [Option('s', "suffix", HelpText = "Suffix to append to the output files, i.e eu = paramdb_eu.db. Default is the suffix of the paramdb that was exported.")]
        public string? Suffix { get; set; }

        [Option("backup", HelpText = "Copy each game file to <name>.bak before replacing it, the first time only.")]
        public bool Backup { get; set; }
    }

    [Verb("add-column", HelpText = "Add a column to a table in a SQLite export. Rows grow if it does not fit; import writes them out.")]
    public class AddColumnVerbs
    {
        [Option('i', "input", Required = true, HelpText = "SQLite database to change.")]
        public string InputPath { get; set; } = "";

        [Option('t', "table", Required = true, HelpText = "Table, e.g. GEAR.")]
        public string Table { get; set; } = "";

        [Option('n', "name", Required = true, HelpText = "Name of the new column.")]
        public string Name { get; set; } = "";

        [Option("type", Required = true, HelpText = "Type, spelled as in .headers files: byte, short, int, int64, float, double, id, string, unicode.")]
        public string Type { get; set; } = "";

        [Option("offset", HelpText = "Offset in the row, in hex. Default is just after the last column.")]
        public string? Offset { get; set; }
    }

    [Verb("remove-column", HelpText = "Remove a column from a table in a SQLite export.")]
    public class RemoveColumnVerbs
    {
        [Option('i', "input", Required = true, HelpText = "SQLite database to change.")]
        public string InputPath { get; set; } = "";

        [Option('t', "table", Required = true, HelpText = "Table, e.g. GEAR.")]
        public string Table { get; set; } = "";

        [Option('n', "name", Required = true, HelpText = "Name of the column to remove.")]
        public string Name { get; set; } = "";
    }
}
