using System.Buffers.Binary;

using GTDataSQLiteConverter;
using GTDataSQLiteConverter.Entities;
using GTDataSQLiteConverter.Formats;

using Microsoft.Data.Sqlite;

namespace GTParamDBEditor.Core;

public sealed class SaveReport
{
    public List<CellIssue> Issues { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<(string File, long Size)> WrittenFiles { get; } = new();
    public List<string> Backups { get; } = new();

    public bool Succeeded => Issues.Count == 0;
}

/// <summary>
/// Turns the live SQLite database back into the game's files.
///
/// Everything is serialised into memory and validated first; the game folder is only touched once
/// every byte is known to be good, so a rejected value can never leave a half-written database behind.
/// </summary>
public static class ParamDbWriter
{
    public static SaveReport Save(LiveDatabase live, ParamDbPaths target, bool createBackups)
    {
        var report = new SaveReport();

        (StringTable strings, StringTable uniStrings, StringTable idStrings) = live.ReadSeedStrings();

        var idTable = new IDTable();
        foreach ((ulong hash, long index) in live.ReadSeedIds())
            idTable.Add(hash, index);

        var context = new WriteContext
        {
            Strings = new StringPool(strings),
            UniStrings = new StringPool(uniStrings),
            Ids = new IdRegistry(idTable, idStrings),
        };

        var archive = new GtarArchive
        {
            AlignMask = live.Meta.AlignMask,
            LastIndexIsAbsolute = live.Meta.LastIndexAbsolute,
        };

        foreach (LiveTable table in live.Tables.Where(t => t.IsArchiveTable).OrderBy(t => t.FileIndex))
            archive.Blocks.Add(BuildBlock(live, table, context, report));

        if (!report.Succeeded)
            return report;

        var files = new List<(string Path, byte[] Data)>();

        using (var stream = new MemoryStream())
        {
            archive.Write(stream);
            files.Add((target.ParamDbOut, stream.ToArray()));
        }

        files.Add((target.ParamStr, Serialize(strings)));
        files.Add((target.ParamUniStr, Serialize(uniStrings)));

        if (idTable.Entries.Count > 0)
        {
            using var stream = new MemoryStream();
            idTable.Write(stream);
            files.Add((target.IdIndex, stream.ToArray()));
            files.Add((target.IdStrings, Serialize(idStrings)));
        }
        else
        {
            report.Warnings.Add("No row labels could be resolved, so the .id_db_* files were left alone.");
        }

        if (live.Meta.CarColorPresent && !AppendCarColors(live, target, files, report))
            return report;

        Commit(files, createBackups, report);
        return report;
    }

    /// <summary>
    /// Rebuilds carcolor.db and carcolor.sdb from their two tables. Returns false when a value could
    /// not be stored, so the save stops before anything is written - carcolor.db not matching the
    /// paramdb it ships with would be worse than not saving.
    /// </summary>
    private static bool AppendCarColors(LiveDatabase live, ParamDbPaths target, List<(string Path, byte[] Data)> files, SaveReport report)
    {
        var original = new CarColorTable
        {
            Reserved = live.Meta.CarColorReserved,
            Names = live.ReadCarColorNames(),
        };

        CarColorTable? rebuilt = CarColorSqlite.Build(live.Connection, original, report);

        if (rebuilt is null)
            return report.Succeeded; // no carcolor tables in this database is fine; issues are not

        files.Add((target.CarColorDb, rebuilt.Write()));
        files.Add((target.CarColorSdb, Serialize(rebuilt.Names)));
        return true;
    }

    private static byte[] Serialize(StringTable table)
    {
        using var stream = new MemoryStream();
        StringTableIO.Write(stream, table);
        return stream.ToArray();
    }

    private static DataBlock BuildBlock(LiveDatabase live, LiveTable table, WriteContext context, SaveReport report)
    {
        var block = new DataBlock
        {
            Version = table.Version,
            TableID = table.TableId,
            NumOfElements = table.SourceRowCount,
            ElementSize = table.SourceElementSize,
            Buffer = table.SourceData,
        };

        if (!table.IsMapped)
            return block;

        int stride = table.RowStride;
        List<byte[]> rows = ReadRows(live, table, context, stride, report);

        if (!report.Succeeded)
            return block;

        if (rows.Count > ushort.MaxValue)
        {
            report.Issues.Add(new CellIssue(table.Name, rows.Count, "-",
                $"a table can hold at most {ushort.MaxValue} rows"));
            return block;
        }

        // The game binary-searches rows by the 64 bit label hash at offset 0, so they must be sorted.
        // OrderBy is stable, which keeps rows that share a hash in the order the file had them.
        if (table.PrimaryKeyColumn is not null)
        {
            rows = rows.OrderBy(static row => BinaryPrimitives.ReadUInt64LittleEndian(row)).ToList();

            // Retail data already ships a few tables with two rows sharing a hash. Only flag
            // duplicates the editing introduced, otherwise every save nags about them.
            HashSet<ulong> knownDuplicates = FindSourceDuplicates(table);

            for (int i = 1; i < rows.Count; i++)
            {
                ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(rows[i]);

                if (hash != BinaryPrimitives.ReadUInt64LittleEndian(rows[i - 1]) || knownDuplicates.Contains(hash))
                    continue;

                report.Warnings.Add(
                    $"'{table.Name}' now has more than one row with the label hash {GtHash.ToPlaceholder(hash)}. " +
                    "The game looks rows up by that hash, so it will only ever find one of them.");
                break;
            }
        }
        else
        {
            report.Warnings.Add($"'{table.Name}' has no label column, so its rows were left in their current order.");
        }

        var buffer = new byte[rows.Count * stride];
        for (int i = 0; i < rows.Count; i++)
            rows[i].CopyTo(buffer, i * stride);

        block.NumOfElements = (ushort)rows.Count;
        block.ElementSize = (ushort)stride;
        block.Buffer = buffer;
        return block;
    }

    private static List<byte[]> ReadRows(LiveDatabase live, LiveTable table, WriteContext context, int stride, SaveReport report)
    {
        var rows = new List<byte[]>(table.RowCount);

        Dictionary<ulong, int> templates = BuildTemplateIndex(table);

        string columnList = string.Join(", ", table.Columns.Select(c => LiveDatabase.Quote(c.Name)));

        using SqliteCommand command = live.Connection.CreateCommand();
        command.CommandText = $"SELECT {columnList} FROM {LiveDatabase.Quote(table.Name)} ORDER BY rowid;";
        using SqliteDataReader reader = command.ExecuteReader();

        var values = new object[table.Columns.Count];
        int rowNumber = 0;

        while (reader.Read())
        {
            rowNumber++;
            reader.GetValues(values);

            var row = new byte[stride];

            // Start from the original bytes of the row with this label so anything the .headers file
            // does not describe survives the round trip.
            if (table.Columns[0].Type == DBColumnType.Id
                && templates.Count > 0
                && values[0] is string label
                && templates.TryGetValue(GtHash.TryParsePlaceholder(label, out ulong raw) ? raw : GtHash.Hash(label), out int sourceRow))
            {
                int start = sourceRow * table.SourceElementSize;
                int length = Math.Min(table.SourceElementSize, Math.Min(stride, table.SourceData.Length - start));
                if (length > 0)
                    table.SourceData.AsSpan(start, length).CopyTo(row);
            }

            for (int i = 0; i < table.Columns.Count; i++)
            {
                object? value = values[i] is DBNull ? null : values[i];
                CellCodec.Write(row, table.Columns[i], value, context, table.Name, rowNumber, report.Issues);
            }

            rows.Add(row);

            if (report.Issues.Count > 200)
            {
                report.Warnings.Add("Stopped listing problems after 200.");
                break;
            }
        }

        return rows;
    }

    private static HashSet<ulong> FindSourceDuplicates(LiveTable table)
    {
        var seen = new HashSet<ulong>();
        var duplicates = new HashSet<ulong>();

        if (table.SourceElementSize < sizeof(ulong))
            return duplicates;

        for (int i = 0; i < table.SourceRowCount; i++)
        {
            int start = i * table.SourceElementSize;
            if (start + sizeof(ulong) > table.SourceData.Length)
                break;

            ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(table.SourceData.AsSpan(start));
            if (!seen.Add(hash))
                duplicates.Add(hash);
        }

        return duplicates;
    }

    private static Dictionary<ulong, int> BuildTemplateIndex(LiveTable table)
    {
        var templates = new Dictionary<ulong, int>();

        if (table.SourceElementSize < sizeof(ulong) || table.PrimaryKeyColumn is null)
            return templates;

        for (int i = 0; i < table.SourceRowCount; i++)
        {
            int start = i * table.SourceElementSize;
            if (start + sizeof(ulong) > table.SourceData.Length)
                break;

            templates.TryAdd(BinaryPrimitives.ReadUInt64LittleEndian(table.SourceData.AsSpan(start)), i);
        }

        return templates;
    }

    private static void Commit(List<(string Path, byte[] Data)> files, bool createBackups, SaveReport report)
    {
        string directory = Path.GetDirectoryName(files[0].Path)!;
        Directory.CreateDirectory(directory);

        // Check every target is writable before replacing any of them - swapping in half a database
        // because the game had the fourth file open would be far worse than not saving at all.
        foreach ((string path, _) in files)
        {
            if (!File.Exists(path))
                continue;

            using FileStream probe = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        }

        var temporaries = new List<(string Temp, string Final)>();

        try
        {
            foreach ((string path, byte[] data) in files)
            {
                string temp = path + ".editor-tmp";
                File.WriteAllBytes(temp, data);
                temporaries.Add((temp, path));
            }

            foreach ((string temp, string final) in temporaries)
            {
                if (createBackups && File.Exists(final))
                {
                    // Only ever back up once, so the .bak stays the pristine original.
                    string backup = final + ".bak";
                    if (!File.Exists(backup))
                    {
                        File.Copy(final, backup);
                        report.Backups.Add(backup);
                    }
                }

                File.Move(temp, final, overwrite: true);
                report.WrittenFiles.Add((final, new FileInfo(final).Length));
            }
        }
        finally
        {
            // Cleanup must never replace the real exception with its own.
            foreach ((string temp, _) in temporaries)
            {
                try
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
