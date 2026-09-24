using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using GTDataSQLiteConverter.Entities;

namespace GTDataSQLiteConverter
{
    public class TableMappingReader
    {
        public static List<TableColumn> ReadColumnMappings(string tableName, out int readSize, string? variant = null)
        {
            int offset = 0;
            List<TableColumn> columns = IterativeHeadersReader(tableName, ref offset, variant);

            readSize = offset;
            return columns;
        }

        /// <summary>
        /// Directory the .headers files are looked up in. Relative paths are resolved against the
        /// working directory first, then against the directory the assembly was loaded from.
        /// </summary>
        public static string HeadersDirectory { get; set; } = "Headers";

        /// <summary>
        /// A variant is a subdirectory holding another game's layouts (e.g. "GTC"). Tables it has no
        /// file for, and anything its files include, fall back to the shared files.
        /// </summary>
        public static string? GetHeadersFile(string tableName, bool checkSize = false, string? variant = null)
        {
            string fileName = Path.ChangeExtension(tableName, ".headers");
            string[] dirs = { HeadersDirectory, Path.Combine(AppContext.BaseDirectory, "Headers") };

            // Every variant file wins over every shared one, so an older Headers folder in the working
            // directory cannot hide a layout that only exists for the variant.
            IEnumerable<string> candidates = dirs.Select(dir => Path.Combine(dir, fileName));
            if (!string.IsNullOrEmpty(variant))
                candidates = dirs.Select(dir => Path.Combine(dir, variant, fileName)).Concat(candidates);

            foreach (string headersFilename in candidates)
            {
                if (!File.Exists(headersFilename))
                    continue;

                if (checkSize && new FileInfo(headersFilename).Length == 0)
                    continue;

                return headersFilename;
            }

            return null;
        }

        private static List<TableColumn> IterativeHeadersReader(string filename, ref int offset, string? variant)
        {
            using var sr = new StreamReader(filename);

            List<TableColumn> columns = new();
            var dir = Path.GetDirectoryName(filename);
            var fn = Path.GetFileNameWithoutExtension(Path.GetFileName(filename));
            int lineNumber = 0;
            while (!sr.EndOfStream)
            {
                lineNumber++;
                var debugln = $"{fn}:{lineNumber}";

                var line = sr.ReadLine()?.Trim();

                // support comments & skip empty lines
                if (string.IsNullOrEmpty(line) || line.StartsWith("//"))
                    continue;

                var split = line.Split("|");
                var id = split[0];

                if (id == "add_column")
                {
                    if (split.Length < 3 || split.Length > 4)
                        Console.WriteLine($"Metadata error: {debugln} has malformed 'add_column' - expected 2 or 3 arguments (name, type, offset?), may break!");

                    string columnName = split[1];
                    string columnTypeStr = split[2];

                    DBColumnType columnType = DBUtils.ColumnTypeToType(columnTypeStr);
                    if (columnType == DBColumnType.Unknown)
                        Console.WriteLine($"Metadata error: {debugln} has malformed 'add_column' - type '{columnTypeStr}' is invalid\n" +
                            $"Valid types: str, int8, int16, int32/int, int64, uint8, uint16, uint32/uint, uint64, float, double");

                    var column = new TableColumn
                    {
                        Name = columnName,
                        Type = columnType
                    };

                    if (split.Length == 3)
                        column.Offset = offset;
                    else
                        column.Offset = Convert.ToInt64(split[3], 16);

                    offset += DBUtils.TypeToSize(columnType);

                    columns.Add(column);
                }
                else if (id == "padding")
                {
                    if (split.Length != 2)
                        Console.WriteLine($"Metadata error: {debugln} has malformed 'padding' - expected 1 argument (length), may break!");

                    offset += Convert.ToInt32(split[1], 16);
                }
                else if (id == "include")
                {
                    if (split.Length != 2)
                        Console.WriteLine($"Metadata error: {debugln} has malformed 'include' - expected 1 argument (filename), may break!");


                    var headersFilename = GetHeadersFile($"{split[1]}.headers", variant: variant);
                    if (headersFilename == null)
                    {
                        Console.WriteLine($"Metadata error: unknown include file '{split[1]}.headers' - may break!");
                        continue;
                    }

                    columns.AddRange(IterativeHeadersReader(headersFilename, ref offset, variant));

                }
            }

            return columns;
        }
    }
}
