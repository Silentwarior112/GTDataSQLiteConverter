using System.Globalization;

using GTDataSQLiteConverter.Entities;

using Microsoft.Data.Sqlite;

namespace GTDataSQLiteConverter.ParamDb;

/// <summary>
/// Presents carcolor.db as two ordinary editable tables, and turns them back into the file.
///
///   CARCOLOR_CARS     one row per colour slot: which colours a car is sold in, and in what order
///   CARCOLOR_PALETTE  the shared palette every car draws from
///
/// The file's awkward parts are hidden here rather than pushed at whoever is editing: colour ids
/// are sparse keys rather than row numbers, the swatch is stored BGR, and each car's colour list is
/// a run inside one flat pool. What is left is two flat tables that the grid and the SQL pane can
/// treat like any other.
/// </summary>
public static class CarColorSqlite
{
    public const string CarsTable = "CARCOLOR_CARS";
    public const string PaletteTable = "CARCOLOR_PALETTE";

    /// <summary>Per-car pool offsets, kept only so an untouched file still saves byte for byte.</summary>
    public const string LayoutTable = "_CarColorLayout";

    public static List<LiveColumn> CarColumns() => new()
    {
        new LiveColumn { Name = "CarLabel", Type = DBColumnType.Id, Offset = 0 },
        new LiveColumn { Name = "Slot", Type = DBColumnType.Int, Offset = 8, Documentation = "Order the colours are offered in. The game shows them in this order, so it is not sorted." },
        new LiveColumn { Name = "ColorId", Type = DBColumnType.Int, Offset = 12, Documentation = "Must match a ColorId in CARCOLOR_PALETTE." },
    };

    public static List<LiveColumn> PaletteColumns() => new()
    {
        new LiveColumn { Name = "ColorId", Type = DBColumnType.Int, Offset = 0, Documentation = "Sparse key, not a row number. Referenced by CARCOLOR_CARS.ColorId." },
        new LiveColumn { Name = "Rgb", Type = DBColumnType.String, Offset = 4, Documentation = "#RRGGBB. Stored in the file as BGR; the conversion is done on save." },
        new LiveColumn { Name = "LatinName", Type = DBColumnType.String, Offset = 8 },
        new LiveColumn { Name = "JapaneseName", Type = DBColumnType.String, Offset = 10, Documentation = "Often the same string as the western name - 383 of the 612 retail colours share one." },
    };

    public static LiveTable MakeCarsTable(int rowCount) => Make(CarsTable, CarColumns(), rowCount);

    public static LiveTable MakePaletteTable(int rowCount) => Make(PaletteTable, PaletteColumns(), rowCount);

    private static LiveTable Make(string name, List<LiveColumn> columns, int rowCount)
    {
        var table = new LiveTable
        {
            Name = name,
            IsArchiveTable = false,
            FileIndex = int.MaxValue,
            TableId = -1,
            MappedRowSize = 0,
            RowCount = rowCount,
        };

        table.Columns.AddRange(columns);
        return table;
    }

    // ---------------------------------------------------------------- filling

    public static void Create(SqliteConnection connection, CarColorTable source, ParamDbFiles files, IList<string> warnings)
    {
        Execute(connection, $"""
            DROP TABLE IF EXISTS {LiveDatabase.Quote(CarsTable)};
            DROP TABLE IF EXISTS {LiveDatabase.Quote(PaletteTable)};
            DROP TABLE IF EXISTS {LiveDatabase.Quote(LayoutTable)};
            CREATE TABLE {LiveDatabase.Quote(CarsTable)} (CarLabel TEXT, Slot INTEGER, ColorId INTEGER);
            CREATE TABLE {LiveDatabase.Quote(PaletteTable)} (ColorId INTEGER, Rgb TEXT, LatinName TEXT, JapaneseName TEXT);
            CREATE TABLE {LiveDatabase.Quote(LayoutTable)} (CarLabel TEXT, PoolOffset INTEGER);
            """);

        int unresolved = 0;

        using (SqliteCommand cars = connection.CreateCommand())
        using (SqliteCommand layout = connection.CreateCommand())
        {
            cars.CommandText = $"INSERT INTO {LiveDatabase.Quote(CarsTable)} (CarLabel, Slot, ColorId) VALUES ($label, $slot, $color);";
            SqliteParameter label = cars.Parameters.Add("$label", SqliteType.Text);
            SqliteParameter slot = cars.Parameters.Add("$slot", SqliteType.Integer);
            SqliteParameter color = cars.Parameters.Add("$color", SqliteType.Integer);

            layout.CommandText = $"INSERT INTO {LiveDatabase.Quote(LayoutTable)} (CarLabel, PoolOffset) VALUES ($label, $offset);";
            SqliteParameter layoutLabel = layout.Parameters.Add("$label", SqliteType.Text);
            SqliteParameter layoutOffset = layout.Parameters.Add("$offset", SqliteType.Integer);

            foreach (CarColorSet set in source.Cars)
            {
                string name = files.ResolveId(set.CarHash) ?? GtHash.ToPlaceholder(set.CarHash);
                if (GtHash.TryParsePlaceholder(name, out _))
                    unresolved++;

                for (int i = 0; i < set.ColorIds.Count; i++)
                {
                    label.Value = name;
                    slot.Value = i;
                    color.Value = set.ColorIds[i];
                    cars.ExecuteNonQuery();
                }

                layoutLabel.Value = name;
                layoutOffset.Value = set.SourcePoolOffset;
                layout.ExecuteNonQuery();
            }
        }

        using (SqliteCommand palette = connection.CreateCommand())
        {
            palette.CommandText = $"INSERT INTO {LiveDatabase.Quote(PaletteTable)} (ColorId, Rgb, LatinName, JapaneseName) VALUES ($id, $rgb, $latin, $jp);";
            SqliteParameter id = palette.Parameters.Add("$id", SqliteType.Integer);
            SqliteParameter rgb = palette.Parameters.Add("$rgb", SqliteType.Text);
            SqliteParameter latin = palette.Parameters.Add("$latin", SqliteType.Text);
            SqliteParameter japanese = palette.Parameters.Add("$jp", SqliteType.Text);

            foreach (CarColor entry in source.Palette)
            {
                id.Value = entry.ColorId;
                rgb.Value = entry.RgbHex;
                latin.Value = source.GetLatinName(entry) ?? "";
                japanese.Value = source.GetJapaneseName(entry) ?? "";
                palette.ExecuteNonQuery();
            }
        }

        if (unresolved > 0)
        {
            warnings.Add(
                $"carcolor.db lists {unresolved} car(s) with no label in this region's id table - they are shown as raw hashes. " +
                "That is expected: one carcolor.db is shared by all three regions.");
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- reading back

    /// <summary>
    /// Rebuilds the file from the two tables. Anything that would not survive the trip - an unknown
    /// colour id, an unreadable swatch - is reported rather than silently dropped.
    /// </summary>
    public static CarColorTable? Build(SqliteConnection connection, CarColorTable original, SaveReport report)
    {
        var rebuilt = new CarColorTable { Reserved = original.Reserved };

        // The names are appended to the table as it was loaded, so untouched strings keep their
        // exact original bytes - carcolor.sdb holds a few that do not survive a decode/encode trip.
        rebuilt.Names = original.Names;
        var pool = new StringPool(rebuilt.Names);

        var paletteIds = new HashSet<uint>();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT ColorId, Rgb, LatinName, JapaneseName FROM {LiveDatabase.Quote(PaletteTable)} ORDER BY rowid;";
            using SqliteDataReader reader = command.ExecuteReader();

            int row = 0;
            while (reader.Read())
            {
                row++;

                if (reader.IsDBNull(0))
                {
                    report.Issues.Add(new CellIssue(PaletteTable, row, "ColorId", "a colour needs an id"));
                    continue;
                }

                long id = reader.GetInt64(0);
                if (id < 0 || id > uint.MaxValue)
                {
                    report.Issues.Add(new CellIssue(PaletteTable, row, "ColorId", $"{id} is not a usable colour id"));
                    continue;
                }

                if (!paletteIds.Add((uint)id))
                {
                    report.Issues.Add(new CellIssue(PaletteTable, row, "ColorId", $"id {id} is used by more than one colour"));
                    continue;
                }

                string rgbText = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (!TryParseRgb(rgbText, out uint packed))
                {
                    report.Issues.Add(new CellIssue(PaletteTable, row, "Rgb", $"'{rgbText}' is not a colour like #RRGGBB"));
                    continue;
                }

                try
                {
                    rebuilt.Palette.Add(new CarColor
                    {
                        ColorId = (uint)id,
                        PackedBgr = packed,
                        LatinNameIndex = pool.Intern(reader.IsDBNull(2) ? "" : reader.GetString(2)),
                        JapaneseNameIndex = pool.Intern(reader.IsDBNull(3) ? "" : reader.GetString(3)),
                    });
                }
                catch (System.Text.EncoderFallbackException)
                {
                    report.Issues.Add(new CellIssue(PaletteTable, row, "LatinName/JapaneseName",
                        "contains characters the colour name table's encoding cannot store"));
                }
            }
        }

        Dictionary<string, int> layout = ReadLayout(connection);

        var sets = new Dictionary<string, CarColorSet>(StringComparer.Ordinal);
        var order = new List<string>();

        using (SqliteCommand command = connection.CreateCommand())
        {
            // Slot decides the order the game offers the colours in; rowid only breaks ties.
            command.CommandText = $"SELECT CarLabel, ColorId FROM {LiveDatabase.Quote(CarsTable)} ORDER BY CarLabel, Slot, rowid;";
            using SqliteDataReader reader = command.ExecuteReader();

            int row = 0;
            while (reader.Read())
            {
                row++;

                string label = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (label.Length == 0)
                {
                    report.Issues.Add(new CellIssue(CarsTable, row, "CarLabel", "a colour slot needs a car"));
                    continue;
                }

                if (reader.IsDBNull(1))
                {
                    report.Issues.Add(new CellIssue(CarsTable, row, "ColorId", "a colour slot needs a colour id"));
                    continue;
                }

                long colorId = reader.GetInt64(1);
                if (colorId < 0 || colorId > uint.MaxValue || !paletteIds.Contains((uint)colorId))
                {
                    report.Issues.Add(new CellIssue(CarsTable, row, "ColorId",
                        $"{colorId} is not a colour in {PaletteTable}"));
                    continue;
                }

                if (!sets.TryGetValue(label, out CarColorSet? set))
                {
                    set = new CarColorSet
                    {
                        CarHash = GtHash.TryParsePlaceholder(label, out ulong raw) ? raw : GtHash.Hash(label),
                        SourcePoolOffset = layout.GetValueOrDefault(label, -1),
                    };
                    sets[label] = set;
                    order.Add(label);
                }

                set.ColorIds.Add((uint)colorId);
            }
        }

        foreach (string label in order)
            rebuilt.Cars.Add(sets[label]);

        if (rebuilt.Cars.Count == 0 && rebuilt.Palette.Count == 0)
            return null;

        foreach (CarColorSet set in rebuilt.Cars.Where(s => s.ColorIds.Count == 0))
            report.Warnings.Add($"A car in {CarsTable} ended up with no colours at all.");

        int unreferenced = rebuilt.Palette.Count - rebuilt.Cars.SelectMany(c => c.ColorIds).Distinct().Count();
        if (unreferenced > 0)
            report.Warnings.Add($"{unreferenced} colour(s) in {PaletteTable} are not offered on any car.");

        return report.Succeeded ? rebuilt : null;
    }

    private static Dictionary<string, int> ReadLayout(SqliteConnection connection)
    {
        var layout = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT CarLabel, PoolOffset FROM {LiveDatabase.Quote(LayoutTable)};";
            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                    layout[reader.GetString(0)] = (int)reader.GetInt64(1);
            }
        }
        catch (SqliteException)
        {
            // No stored layout: the pool just gets laid out afresh.
        }

        return layout;
    }

    // ---------------------------------------------------------------- colours

    public static bool TryParseRgb(string? text, out uint packedBgr)
    {
        packedBgr = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        ReadOnlySpan<char> span = text.Trim();
        if (span[0] == '#')
            span = span[1..];

        if (span.Length != 6 || !uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return false;

        packedBgr = CarColor.PackRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
}
