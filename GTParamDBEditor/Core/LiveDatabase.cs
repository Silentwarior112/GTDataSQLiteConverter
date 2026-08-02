using System.Data;
using System.Globalization;

using GTDataSQLiteConverter;
using GTDataSQLiteConverter.Entities;

using Microsoft.Data.Sqlite;

namespace GTParamDBEditor.Core;

public sealed class ParamDbMeta
{
    public uint AlignMask { get; set; } = 7;
    public bool LastIndexAbsolute { get; set; }
    public string Suffix { get; set; } = "";
    public string? SourceDirectory { get; set; }
    public short StrBytesPerChar { get; set; } = 1;
    public short UniStrBytesPerChar { get; set; } = -1;
    public short IdStrBytesPerChar { get; set; } = 1;

    /// <summary>See <see cref="StringTable.LengthIncludesPadding"/>; retail files differ per table.</summary>
    public bool StrLengthPadded { get; set; }
    public bool UniStrLengthPadded { get; set; } = true;
    public bool IdStrLengthPadded { get; set; }

    public bool HasIdTables { get; set; } = true;

    /// <summary>Whether carcolor.db was loaded, and so should be written back on save.</summary>
    public bool CarColorPresent { get; set; }

    /// <summary>The always-zero word at carcolor.db +0x04, carried rather than assumed.</summary>
    public uint CarColorReserved { get; set; }

    public short CarColorBytesPerChar { get; set; } = -1;
    public bool CarColorLengthPadded { get; set; } = true;
}

/// <summary>
/// The editable database. A ParamDB is unpacked into a real SQLite file that the grid reads and
/// writes through; saving turns that SQLite back into game files.
///
/// Per-table schemas match the CLI converter's export exactly, so the file stays usable with
/// `GTDataSQLiteConverter import`. Everything the editor needs on top of that (the archive's
/// alignment, the original block bytes, the string tables as loaded) lives in extra `_`-prefixed
/// tables, which the CLI ignores.
/// </summary>
public sealed class LiveDatabase : IDisposable
{
    public const string RowIdColumn = "_rowid_";

    private const string MetaTable = "_ParamDbMeta";
    private const string InfoTable = "_DatabaseTableInfo";
    private const string RawTable = "_RawTableData";
    private const string SeedStringsTable = "_SeedStrings";
    private const string SeedIdTable = "_SeedIdTable";

    private readonly SqliteConnection _connection;

    private LiveDatabase(SqliteConnection connection, string filePath, bool isTemporary)
    {
        _connection = connection;
        FilePath = filePath;
        IsTemporary = isTemporary;
    }

    public string FilePath { get; }
    public bool IsTemporary { get; }
    public List<LiveTable> Tables { get; } = new();
    public ParamDbMeta Meta { get; private set; } = new();

    public SqliteConnection Connection => _connection;

    public LiveTable? FindTable(string name)
        => Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- building

    /// <summary>Unpacks a loaded ParamDB into a new SQLite database.</summary>
    public static LiveDatabase CreateFrom(ParamDbFiles files, string sqlitePath, bool isTemporary, IList<string> warnings)
    {
        if (File.Exists(sqlitePath))
            File.Delete(sqlitePath);

        var connection = new SqliteConnection($"Data Source={sqlitePath}");
        connection.Open();

        LiveDatabase live;
        try
        {
            live = Build(files, connection, sqlitePath, isTemporary, warnings);
        }
        catch
        {
            // Otherwise a half-built database keeps the connection - and the file - alive.
            connection.Dispose();
            SqliteConnection.ClearAllPools();
            throw;
        }

        return live;
    }

    private static LiveDatabase Build(ParamDbFiles files, SqliteConnection connection, string sqlitePath, bool isTemporary, IList<string> warnings)
    {
        var live = new LiveDatabase(connection, sqlitePath, isTemporary)
        {
            Meta = new ParamDbMeta
            {
                AlignMask = files.Archive.AlignMask,
                LastIndexAbsolute = files.Archive.LastIndexIsAbsolute,
                Suffix = files.Paths.Suffix,
                SourceDirectory = files.Paths.DirectoryPath,
                StrBytesPerChar = files.Strings.BytesPerCharacter,
                UniStrBytesPerChar = files.UniStrings.BytesPerCharacter,
                IdStrBytesPerChar = files.IdStrings.BytesPerCharacter,
                StrLengthPadded = files.Strings.LengthIncludesPadding,
                UniStrLengthPadded = files.UniStrings.LengthIncludesPadding,
                IdStrLengthPadded = files.IdStrings.LengthIncludesPadding,
                HasIdTables = files.HasIdTables,
                CarColorPresent = files.CarColors is not null,
                CarColorReserved = files.CarColors?.Reserved ?? 0,
                CarColorBytesPerChar = files.CarColors?.Names.BytesPerCharacter ?? -1,
                CarColorLengthPadded = files.CarColors?.Names.LengthIncludesPadding ?? true,
            },
        };

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            live.CreateMetaTables();
            live.WriteMeta();
            live.WriteSeeds(files);

            for (int i = 0; i < files.Archive.Blocks.Count; i++)
                live.ImportBlock(files, i, warnings);

            if (files.CarColors is not null)
            {
                CarColorSqlite.Create(connection, files.CarColors, files, warnings);
                live.AddCarColorTables(files.CarColors.Cars.Sum(c => c.ColorIds.Count), files.CarColors.Palette.Count);
            }

            transaction.Commit();
        }

        return live;
    }

    /// <summary>Reopens a SQLite database produced by this editor (or by the CLI converter).</summary>
    public static LiveDatabase Open(string sqlitePath, IList<string> warnings)
    {
        var connection = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadWrite");
        connection.Open();

        try
        {
            var live = new LiveDatabase(connection, sqlitePath, isTemporary: false);
            live.CreateMetaTables();
            live.ReadMeta();
            live.ReadTables(warnings);
            return live;
        }
        catch
        {
            connection.Dispose();
            SqliteConnection.ClearAllPools();
            throw;
        }
    }

    private void CreateMetaTables()
    {
        Execute($"""
            CREATE TABLE IF NOT EXISTS {Quote(MetaTable)} (Key TEXT PRIMARY KEY, Value TEXT);
            CREATE TABLE IF NOT EXISTS {Quote(InfoTable)} (TableName TEXT, TableID INTEGER, Version INTEGER);
            CREATE TABLE IF NOT EXISTS {Quote(RawTable)} (
                FileIndex INTEGER PRIMARY KEY, TableName TEXT, TableID INTEGER, Version INTEGER,
                ElementSize INTEGER, NumOfElements INTEGER, IsMapped INTEGER, Data BLOB);
            CREATE TABLE IF NOT EXISTS {Quote(SeedStringsTable)} (Kind TEXT, Idx INTEGER, Value TEXT, Raw BLOB);
            CREATE TABLE IF NOT EXISTS {Quote(SeedIdTable)} (Hash TEXT PRIMARY KEY, StrIndex INTEGER);
            """);
    }

    private void WriteMeta()
    {
        Execute($"DELETE FROM {Quote(MetaTable)};");

        var values = new Dictionary<string, string?>
        {
            ["AlignMask"] = Meta.AlignMask.ToString(CultureInfo.InvariantCulture),
            ["LastIndexAbsolute"] = Meta.LastIndexAbsolute ? "1" : "0",
            ["Suffix"] = Meta.Suffix,
            ["SourceDirectory"] = Meta.SourceDirectory,
            ["StrBytesPerChar"] = Meta.StrBytesPerChar.ToString(CultureInfo.InvariantCulture),
            ["UniStrBytesPerChar"] = Meta.UniStrBytesPerChar.ToString(CultureInfo.InvariantCulture),
            ["IdStrBytesPerChar"] = Meta.IdStrBytesPerChar.ToString(CultureInfo.InvariantCulture),
            ["StrLengthPadded"] = Meta.StrLengthPadded ? "1" : "0",
            ["UniStrLengthPadded"] = Meta.UniStrLengthPadded ? "1" : "0",
            ["IdStrLengthPadded"] = Meta.IdStrLengthPadded ? "1" : "0",
            ["HasIdTables"] = Meta.HasIdTables ? "1" : "0",
            ["CarColorPresent"] = Meta.CarColorPresent ? "1" : "0",
            ["CarColorReserved"] = Meta.CarColorReserved.ToString(CultureInfo.InvariantCulture),
            ["CarColorBytesPerChar"] = Meta.CarColorBytesPerChar.ToString(CultureInfo.InvariantCulture),
            ["CarColorLengthPadded"] = Meta.CarColorLengthPadded ? "1" : "0",
        };

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"INSERT OR REPLACE INTO {Quote(MetaTable)} (Key, Value) VALUES ($k, $v);";
        SqliteParameter key = command.Parameters.Add("$k", SqliteType.Text);
        SqliteParameter value = command.Parameters.Add("$v", SqliteType.Text);

        foreach ((string k, string? v) in values)
        {
            key.Value = k;
            value.Value = (object?)v ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    private void ReadMeta()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = $"SELECT Key, Value FROM {Quote(MetaTable)};";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                    values[reader.GetString(0)] = reader.GetString(1);
            }
        }

        Meta = new ParamDbMeta
        {
            AlignMask = values.TryGetValue("AlignMask", out string? a) && uint.TryParse(a, out uint mask) ? mask : 7,
            LastIndexAbsolute = values.GetValueOrDefault("LastIndexAbsolute") == "1",
            Suffix = values.GetValueOrDefault("Suffix") ?? "",
            SourceDirectory = values.GetValueOrDefault("SourceDirectory"),
            StrBytesPerChar = ParseShort(values.GetValueOrDefault("StrBytesPerChar"), 1),
            UniStrBytesPerChar = ParseShort(values.GetValueOrDefault("UniStrBytesPerChar"), -1),
            IdStrBytesPerChar = ParseShort(values.GetValueOrDefault("IdStrBytesPerChar"), 1),
            StrLengthPadded = values.GetValueOrDefault("StrLengthPadded") == "1",
            UniStrLengthPadded = values.GetValueOrDefault("UniStrLengthPadded") != "0",
            IdStrLengthPadded = values.GetValueOrDefault("IdStrLengthPadded") == "1",
            HasIdTables = values.GetValueOrDefault("HasIdTables") != "0",
            CarColorPresent = values.GetValueOrDefault("CarColorPresent") == "1",
            CarColorReserved = uint.TryParse(values.GetValueOrDefault("CarColorReserved"), out uint reserved) ? reserved : 0,
            CarColorBytesPerChar = ParseShort(values.GetValueOrDefault("CarColorBytesPerChar"), -1),
            CarColorLengthPadded = values.GetValueOrDefault("CarColorLengthPadded") != "0",
        };

        static short ParseShort(string? text, short fallback)
            => short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out short v) ? v : fallback;
    }

    private void AddCarColorTables(int carRows, int paletteRows)
    {
        Tables.Add(CarColorSqlite.MakeCarsTable(carRows));
        Tables.Add(CarColorSqlite.MakePaletteTable(paletteRows));
    }

    /// <summary>The colour names, seeded so untouched strings keep their exact original bytes.</summary>
    public StringTable ReadCarColorNames()
    {
        var table = new StringTable
        {
            BytesPerCharacter = Meta.CarColorBytesPerChar,
            LengthIncludesPadding = Meta.CarColorLengthPadded,
        };

        FillSeed(table, "carcolor");
        return table;
    }

    private void WriteSeeds(ParamDbFiles files)
    {
        Execute($"DELETE FROM {Quote(SeedStringsTable)}; DELETE FROM {Quote(SeedIdTable)};");

        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = $"INSERT INTO {Quote(SeedStringsTable)} (Kind, Idx, Value, Raw) VALUES ($kind, $idx, $value, $raw);";
            SqliteParameter kind = command.Parameters.Add("$kind", SqliteType.Text);
            SqliteParameter idx = command.Parameters.Add("$idx", SqliteType.Integer);
            SqliteParameter value = command.Parameters.Add("$value", SqliteType.Text);
            SqliteParameter raw = command.Parameters.Add("$raw", SqliteType.Blob);

            var seeds = new List<(string, StringTable)>
            {
                ("param", files.Strings),
                ("uni", files.UniStrings),
                ("id", files.IdStrings),
            };

            if (files.CarColors is not null)
                seeds.Add(("carcolor", files.CarColors.Names));

            foreach ((string name, StringTable table) in seeds)
            {
                kind.Value = name;
                for (int i = 0; i < table.Strings.Count; i++)
                {
                    idx.Value = i;
                    value.Value = table.Strings[i];
                    raw.Value = (object?)table.GetRawData(i) ?? DBNull.Value;
                    command.ExecuteNonQuery();
                }
            }
        }

        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = $"INSERT OR REPLACE INTO {Quote(SeedIdTable)} (Hash, StrIndex) VALUES ($hash, $index);";
            SqliteParameter hash = command.Parameters.Add("$hash", SqliteType.Text);
            SqliteParameter index = command.Parameters.Add("$index", SqliteType.Integer);

            foreach ((ulong h, long i) in files.IdTable.Entries)
            {
                hash.Value = h.ToString("X16", CultureInfo.InvariantCulture);
                index.Value = i;
                command.ExecuteNonQuery();
            }
        }
    }

    public (StringTable Strings, StringTable UniStrings, StringTable IdStrings) ReadSeedStrings()
    {
        var param = new StringTable { BytesPerCharacter = Meta.StrBytesPerChar, LengthIncludesPadding = Meta.StrLengthPadded };
        var uni = new StringTable { BytesPerCharacter = Meta.UniStrBytesPerChar, LengthIncludesPadding = Meta.UniStrLengthPadded };
        var id = new StringTable { BytesPerCharacter = Meta.IdStrBytesPerChar, LengthIncludesPadding = Meta.IdStrLengthPadded };

        FillSeed(param, "param");
        FillSeed(uni, "uni");
        FillSeed(id, "id");

        return (param, uni, id);
    }

    /// <summary>
    /// Loads one seeded string table by name. Matching on the kind explicitly matters: a catch-all
    /// would quietly pour any other table's strings into this one.
    /// </summary>
    private void FillSeed(StringTable target, string kind)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"SELECT Idx, Value, Raw FROM {Quote(SeedStringsTable)} WHERE Kind = $kind ORDER BY Idx;";
        command.Parameters.AddWithValue("$kind", kind);

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            // Idx is dense and ordered; anything else would mean the file was tampered with.
            int index = reader.GetInt32(0);
            while (target.Strings.Count < index)
            {
                target.Strings.Add("");
                target.RawData.Add(null);
            }

            string value = reader.IsDBNull(1) ? "" : reader.GetString(1);
            byte[]? raw = reader.IsDBNull(2) ? null : (byte[])reader["Raw"];

            if (target.Strings.Count == index)
            {
                target.Strings.Add(value);
                target.RawData.Add(raw);
            }
            else
            {
                target.Strings[index] = value;
                target.RawData[index] = raw;
            }
        }
    }

    public IEnumerable<(ulong Hash, long StrIndex)> ReadSeedIds()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"SELECT Hash, StrIndex FROM {Quote(SeedIdTable)};";
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (ulong.TryParse(reader.GetString(0), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                yield return (hash, reader.GetInt64(1));
        }
    }

    private void ImportBlock(ParamDbFiles files, int fileIndex, IList<string> warnings)
    {
        DataBlock block = files.Archive.Blocks[fileIndex];
        string name = TableNameFor(block.TableID, fileIndex);

        if (block.TableID != fileIndex)
            warnings.Add($"Block #{fileIndex} reports table id {block.TableID}; using its position for ordering.");

        List<LiveColumn> columns = ReadColumns(name, out int mappedRowSize, warnings);

        byte[] data = block.Buffer ?? Array.Empty<byte>();
        int expected = block.NumOfElements * block.ElementSize;
        if (data.Length > expected)
            data = data[..expected];

        var table = new LiveTable
        {
            Name = name,
            FileIndex = fileIndex,
            TableId = block.TableID,
            Version = block.Version,
            SourceElementSize = block.ElementSize,
            SourceRowCount = block.NumOfElements,
            SourceData = data,
            MappedRowSize = mappedRowSize,
            RowCount = block.NumOfElements,
        };
        table.Columns.AddRange(columns);

        Tables.Add(table);

        InsertInfoRow(table);
        InsertRawData(table);

        if (!table.IsMapped)
        {
            warnings.Add($"'{name}' has no column mapping - its {block.NumOfElements} rows are kept as-is and cannot be edited.");
            return;
        }

        if (table.HasSizeMismatch)
        {
            warnings.Add(
                $"'{name}': the mapping describes {mappedRowSize} bytes per row but the file has " +
                $"{block.ElementSize}. Unmapped bytes are preserved for rows whose label is unchanged.");
        }

        CreateTable(table);
        FillTable(table, files);
    }

    private static string TableNameFor(short tableId, int fileIndex)
    {
        var type = (CarDatabaseFileType)tableId;
        return Enum.IsDefined(type) ? type.ToString() : $"Table_{fileIndex}";
    }

    private static List<LiveColumn> ReadColumns(string tableName, out int mappedRowSize, IList<string> warnings)
    {
        mappedRowSize = 0;

        string? headersFile = TableMappingReader.GetHeadersFile(tableName);
        if (headersFile is null)
            return new List<LiveColumn>();

        List<TableColumn> mappings = TableMappingReader.ReadColumnMappings(headersFile, out int size);
        mappedRowSize = size;

        List<string?> docs = HeaderDocs.ForTable(tableName);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<LiveColumn>(mappings.Count);

        for (int i = 0; i < mappings.Count; i++)
        {
            TableColumn mapping = mappings[i];

            string name = mapping.Name;
            if (!used.Add(name))
            {
                int suffix = 2;
                while (!used.Add($"{name}_{suffix}"))
                    suffix++;

                warnings.Add($"'{tableName}' declares '{name}' more than once; the later one is shown as '{name}_{suffix}'.");
                name = $"{name}_{suffix}";
            }

            if (mapping.Type == DBColumnType.Unknown)
            {
                warnings.Add($"'{tableName}.{name}' has an unknown type in its .headers file and was dropped.");
                continue;
            }

            columns.Add(new LiveColumn
            {
                Name = name,
                Type = mapping.Type,
                Offset = (int)mapping.Offset,
                Documentation = i < docs.Count ? docs[i] : null,
            });
        }

        return columns;
    }

    private void InsertInfoRow(LiveTable table)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"INSERT INTO {Quote(InfoTable)} (TableName, TableID, Version) VALUES ($name, $id, $version);";
        command.Parameters.AddWithValue("$name", table.Name);
        command.Parameters.AddWithValue("$id", table.TableId);
        command.Parameters.AddWithValue("$version", table.Version);
        command.ExecuteNonQuery();
    }

    private void InsertRawData(LiveTable table)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"""
            INSERT OR REPLACE INTO {Quote(RawTable)}
                (FileIndex, TableName, TableID, Version, ElementSize, NumOfElements, IsMapped, Data)
            VALUES ($fileIndex, $name, $id, $version, $elementSize, $rows, $mapped, $data);
            """;
        command.Parameters.AddWithValue("$fileIndex", table.FileIndex);
        command.Parameters.AddWithValue("$name", table.Name);
        command.Parameters.AddWithValue("$id", table.TableId);
        command.Parameters.AddWithValue("$version", table.Version);
        command.Parameters.AddWithValue("$elementSize", table.SourceElementSize);
        command.Parameters.AddWithValue("$rows", table.SourceRowCount);
        command.Parameters.AddWithValue("$mapped", table.IsMapped ? 1 : 0);
        command.Parameters.AddWithValue("$data", table.SourceData);
        command.ExecuteNonQuery();
    }

    private void CreateTable(LiveTable table)
    {
        string columns = string.Join(",\n    ", table.Columns.Select(c => $"{Quote(c.Name)} {c.SqliteType}"));
        Execute($"DROP TABLE IF EXISTS {Quote(table.Name)};\nCREATE TABLE {Quote(table.Name)} (\n    {columns}\n);");
    }

    private void FillTable(LiveTable table, ParamDbFiles files)
    {
        if (table.SourceRowCount == 0)
            return;

        string columnList = string.Join(", ", table.Columns.Select(c => Quote(c.Name)));
        string parameterList = string.Join(", ", table.Columns.Select((_, i) => $"$p{i}"));

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"INSERT INTO {Quote(table.Name)} ({columnList}) VALUES ({parameterList});";

        var parameters = new SqliteParameter[table.Columns.Count];
        for (int i = 0; i < parameters.Length; i++)
            parameters[i] = AddValueParameter(command, $"$p{i}");

        command.Prepare();

        // Rows shorter than the mapping are padded so a slightly-off .headers file cannot crash the load.
        byte[] rowBuffer = new byte[table.RowStride];

        for (int rowIndex = 0; rowIndex < table.SourceRowCount; rowIndex++)
        {
            Array.Clear(rowBuffer);

            int start = rowIndex * table.SourceElementSize;
            int available = Math.Min(table.SourceElementSize, Math.Max(0, table.SourceData.Length - start));
            if (available > 0)
                table.SourceData.AsSpan(start, available).CopyTo(rowBuffer);

            for (int i = 0; i < table.Columns.Count; i++)
                parameters[i].Value = CellCodec.Read(rowBuffer, table.Columns[i], files) ?? (object)DBNull.Value;

            command.ExecuteNonQuery();
        }
    }

    /// <summary>Rebuilds the table list from a SQLite database that was not created in this session.</summary>
    private void ReadTables(IList<string> warnings)
    {
        var rawRows = new List<(int FileIndex, string Name, short TableId, ushort Version, ushort ElementSize, ushort Rows, byte[] Data)>();

        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = $"SELECT FileIndex, TableName, TableID, Version, ElementSize, NumOfElements, Data FROM {Quote(RawTable)} ORDER BY FileIndex;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rawRows.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    (short)reader.GetInt64(2),
                    (ushort)reader.GetInt64(3),
                    (ushort)reader.GetInt64(4),
                    (ushort)reader.GetInt64(5),
                    reader.IsDBNull(6) ? Array.Empty<byte>() : (byte[])reader["Data"]));
            }
        }

        if (rawRows.Count == 0)
        {
            // A plain CLI export: no raw block data, so only the mapped tables can be reconstructed.
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = $"SELECT TableName, TableID, Version FROM {Quote(InfoTable)} ORDER BY TableID;";
            using SqliteDataReader reader = command.ExecuteReader();

            int fileIndex = 0;
            while (reader.Read())
            {
                rawRows.Add((fileIndex++, reader.GetString(0), (short)reader.GetInt64(1), (ushort)reader.GetInt64(2), 0, 0, Array.Empty<byte>()));
            }

            if (rawRows.Count > 0)
                warnings.Add("This SQLite file was not produced by the editor; bytes not covered by a .headers mapping cannot be preserved.");
        }

        if (rawRows.Count == 0)
            throw new InvalidDataException("This SQLite file has no ParamDB table information in it.");

        foreach (var raw in rawRows)
        {
            List<LiveColumn> columns = ReadColumns(raw.Name, out int mappedRowSize, warnings);

            var table = new LiveTable
            {
                Name = raw.Name,
                FileIndex = raw.FileIndex,
                TableId = raw.TableId,
                Version = raw.Version,
                SourceElementSize = raw.ElementSize,
                SourceRowCount = raw.Rows,
                SourceData = raw.Data,
                MappedRowSize = mappedRowSize,
            };
            table.Columns.AddRange(columns);

            table.RowCount = table.IsMapped && TableExists(table.Name) ? CountRows(table.Name) : raw.Rows;
            Tables.Add(table);
        }

        if (Meta.CarColorPresent && TableExists(CarColorSqlite.CarsTable) && TableExists(CarColorSqlite.PaletteTable))
            AddCarColorTables(CountRows(CarColorSqlite.CarsTable), CountRows(CarColorSqlite.PaletteTable));
    }

    private bool TableExists(string name)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    // ---------------------------------------------------------------- editing

    public int CountRows(string tableName)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Quote(tableName)};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void RefreshRowCounts()
    {
        foreach (LiveTable table in Tables)
        {
            if (table.IsMapped && TableExists(table.Name))
                table.RowCount = CountRows(table.Name);
        }
    }

    /// <summary>Loads a table into a DataTable carrying the SQLite rowid, which the grid edits.</summary>
    public DataTable LoadTable(LiveTable table)
    {
        var dataTable = new DataTable(table.Name);
        dataTable.Columns.Add(RowIdColumn, typeof(long));

        foreach (LiveColumn column in table.Columns)
        {
            DataColumn dataColumn = dataTable.Columns.Add(column.Name, column.ClrType);
            dataColumn.AllowDBNull = true;
            dataColumn.ExtendedProperties["LiveColumn"] = column;
        }

        string columnList = string.Join(", ", table.Columns.Select(c => Quote(c.Name)));

        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = $"SELECT rowid, {columnList} FROM {Quote(table.Name)} ORDER BY rowid;";
            using SqliteDataReader reader = command.ExecuteReader();

            var values = new object[table.Columns.Count + 1];
            dataTable.BeginLoadData();
            while (reader.Read())
            {
                values[0] = reader.GetInt64(0);
                for (int i = 0; i < table.Columns.Count; i++)
                    values[i + 1] = Coerce(reader, i + 1, table.Columns[i]);

                dataTable.Rows.Add(values);
            }
            dataTable.EndLoadData();
        }

        dataTable.AcceptChanges();
        table.RowCount = dataTable.Rows.Count;
        return dataTable;
    }

    private static object Coerce(SqliteDataReader reader, int ordinal, LiveColumn column)
    {
        if (reader.IsDBNull(ordinal))
            return DBNull.Value;

        try
        {
            if (column.ClrType == typeof(string))
                return reader.GetString(ordinal);
            if (column.ClrType == typeof(double))
                return reader.GetDouble(ordinal);
            return reader.GetInt64(ordinal);
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
        {
            return DBNull.Value;
        }
    }

    /// <summary>
    /// SQLite infers a parameter's storage class from the value it is given. Declaring a type here
    /// would coerce every number into text, so these stay untyped.
    /// </summary>
    private static SqliteParameter AddValueParameter(SqliteCommand command, string name)
    {
        var parameter = new SqliteParameter(name, DBNull.Value);
        command.Parameters.Add(parameter);
        return parameter;
    }

    /// <summary>Writes pending grid edits back into SQLite. Returns the number of rows affected.</summary>
    public int Flush(LiveTable table, DataTable dataTable)
    {
        if (!HasPendingChanges(dataTable))
            return 0;

        int affected = 0;
        string columnList = string.Join(", ", table.Columns.Select(c => Quote(c.Name)));
        string parameterList = string.Join(", ", table.Columns.Select((_, i) => $"$p{i}"));
        string assignments = string.Join(", ", table.Columns.Select((c, i) => $"{Quote(c.Name)} = $p{i}"));

        using SqliteTransaction transaction = _connection.BeginTransaction();

        using (SqliteCommand delete = _connection.CreateCommand())
        {
            delete.CommandText = $"DELETE FROM {Quote(table.Name)} WHERE rowid = $rowid;";
            SqliteParameter rowId = delete.Parameters.Add("$rowid", SqliteType.Integer);

            foreach (DataRow row in dataTable.Rows)
            {
                if (row.RowState != DataRowState.Deleted)
                    continue;

                object original = row[RowIdColumn, DataRowVersion.Original];
                if (original is DBNull)
                    continue; // added then deleted before ever reaching SQLite

                rowId.Value = original;
                affected += delete.ExecuteNonQuery();
            }
        }

        using (SqliteCommand update = _connection.CreateCommand())
        using (SqliteCommand insert = _connection.CreateCommand())
        using (SqliteCommand lastRowId = _connection.CreateCommand())
        {
            update.CommandText = $"UPDATE {Quote(table.Name)} SET {assignments} WHERE rowid = $rowid;";
            insert.CommandText = $"INSERT INTO {Quote(table.Name)} ({columnList}) VALUES ({parameterList});";
            lastRowId.CommandText = "SELECT last_insert_rowid();";

            var updateParameters = new SqliteParameter[table.Columns.Count];
            var insertParameters = new SqliteParameter[table.Columns.Count];
            for (int i = 0; i < table.Columns.Count; i++)
            {
                updateParameters[i] = AddValueParameter(update, $"$p{i}");
                insertParameters[i] = AddValueParameter(insert, $"$p{i}");
            }
            SqliteParameter updateRowId = update.Parameters.Add("$rowid", SqliteType.Integer);

            foreach (DataRow row in dataTable.Rows)
            {
                switch (row.RowState)
                {
                    case DataRowState.Modified:
                        for (int i = 0; i < table.Columns.Count; i++)
                            updateParameters[i].Value = row[table.Columns[i].Name] ?? DBNull.Value;

                        updateRowId.Value = row[RowIdColumn, DataRowVersion.Original];
                        affected += update.ExecuteNonQuery();
                        break;

                    case DataRowState.Added:
                        for (int i = 0; i < table.Columns.Count; i++)
                            insertParameters[i].Value = row[table.Columns[i].Name] ?? DBNull.Value;

                        insert.ExecuteNonQuery();
                        row[RowIdColumn] = Convert.ToInt64(lastRowId.ExecuteScalar(), CultureInfo.InvariantCulture);
                        affected++;
                        break;
                }
            }
        }

        transaction.Commit();

        dataTable.AcceptChanges();
        table.RowCount = dataTable.Rows.Count;
        return affected;
    }

    // ---------------------------------------------------------------- raw SQL

    public sealed record SqlResult(DataTable? Rows, int RecordsAffected, string Message);

    public SqlResult ExecuteSql(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;

        using SqliteDataReader reader = command.ExecuteReader();

        if (reader.FieldCount == 0)
            return new SqlResult(null, reader.RecordsAffected, $"{Math.Max(reader.RecordsAffected, 0)} row(s) affected.");

        var results = new DataTable();
        for (int i = 0; i < reader.FieldCount; i++)
            results.Columns.Add(UniqueName(results, reader.GetName(i)), typeof(object));

        var values = new object[reader.FieldCount];
        while (reader.Read())
        {
            reader.GetValues(values);
            results.Rows.Add(values);
        }

        return new SqlResult(results, reader.RecordsAffected, $"{results.Rows.Count} row(s) returned.");

        static string UniqueName(DataTable table, string name)
        {
            if (!table.Columns.Contains(name))
                return name;

            int suffix = 2;
            while (table.Columns.Contains($"{name}_{suffix}"))
                suffix++;

            return $"{name}_{suffix}";
        }
    }

    public void Execute(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static bool HasPendingChanges(DataTable dataTable)
    {
        foreach (DataRow row in dataTable.Rows)
        {
            if (row.RowState != DataRowState.Unchanged)
                return true;
        }

        return false;
    }

    public void CopyTo(string destination)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"VACUUM INTO '{destination.Replace("'", "''")}';";
        command.ExecuteNonQuery();
    }

    public static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>
    /// Closing down must not be able to throw: this runs while the window is closing, and an
    /// exception there would leave the editor holding a session whose connection is already dead.
    /// </summary>
    public void Dispose()
    {
        try
        {
            _connection.Close();
            _connection.Dispose();
            SqliteConnection.ClearAllPools();
        }
        catch (SqliteException)
        {
        }

        if (!IsTemporary)
            return;

        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is not worth bothering the user about.
        }
    }
}
