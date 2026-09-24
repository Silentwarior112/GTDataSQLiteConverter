namespace GTDataSQLiteConverter.ParamDb;

/// <summary>
/// A game whose paramdb these tools understand. The archive format is the same for all of them; what
/// differs is the order of the tables and some row layouts, which live in their own Headers subdirectory.
/// </summary>
public sealed class ParamDbGame
{
    /// <summary>Stored in the live database, so a reopened SQLite file keeps its game's layouts.</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Headers subdirectory with this game's own layouts, or null for the shared ones.</summary>
    public string? HeadersVariant { get; init; }

    /// <summary>Table name per table id.</summary>
    public required IReadOnlyList<string> TableNames { get; init; }

    public static ParamDbGame Gt3 { get; } = new()
    {
        Id = "GT3",
        DisplayName = "Gran Turismo 3",
        TableNames = Enum.GetNames<CarDatabaseFileType>(),
    };

    /// <summary>
    /// Gran Turismo Concept. The first 30 tables are GT3's, though CHASSIS, ENGINE and GEAR rows are
    /// 8 bytes longer and a few retail padding bytes carry data. The last six are GT3's too, but in a
    /// different order, with CAR moved to the end.
    /// </summary>
    public static ParamDbGame GtConcept { get; } = new()
    {
        Id = "GTC",
        DisplayName = "Gran Turismo Concept",
        HeadersVariant = "GTC",
        TableNames = Enum.GetNames<CarDatabaseFileType>()[..30]
            .Concat(new[] { "ENEMY_CARS", "EVENT", "REGULATIONS", "COURSE", "ARCADE_CAR", "CAR" })
            .ToArray(),
    };

    public static IReadOnlyList<ParamDbGame> All { get; } = new[] { Gt3, GtConcept };

    /// <summary>Databases written before games were told apart are all GT3.</summary>
    public static ParamDbGame FromId(string? id)
        => All.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Gt3;

    public string TableNameFor(short tableId, int fileIndex)
        => tableId >= 0 && tableId < TableNames.Count ? TableNames[tableId] : $"Table_{fileIndex}";

    /// <summary>
    /// Picks the game whose layouts - its own, or ones saved after adding columns - describe the most
    /// blocks at exactly their row size. The games share an archive format and table count, so the
    /// layouts are what tells them apart. Ties go to GT3.
    /// </summary>
    public static ParamDbGame Detect(GtarArchive archive)
    {
        ParamDbGame best = Gt3;
        int bestScore = -1;

        foreach (ParamDbGame game in All)
        {
            int score = 0;
            for (int i = 0; i < archive.Blocks.Count; i++)
            {
                DataBlock block = archive.Blocks[i];
                if (TableLayouts.Fits(game, game.TableNameFor(block.TableID, i), block.ElementSize))
                    score++;
            }

            if (score > bestScore)
            {
                best = game;
                bestScore = score;
            }
        }

        return best;
    }

    public override string ToString() => DisplayName;
}
