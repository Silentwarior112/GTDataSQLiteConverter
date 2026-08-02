using GTDataSQLiteConverter.Formats;

namespace GTParamDBEditor.Core;

/// <summary>
/// A ParamDB as it exists on disk: the table archive plus the string and ID side tables it points into.
/// </summary>
public sealed class ParamDbFiles
{
    public required ParamDbPaths Paths { get; init; }
    public required GtarArchive Archive { get; init; }
    public required StringTable Strings { get; init; }
    public required StringTable UniStrings { get; init; }

    public IDTable IdTable { get; init; } = new();
    public StringTable IdStrings { get; init; } = new();
    public bool HasIdTables { get; init; }

    /// <summary>carcolor.db, if it sits alongside. Shared by every region, so it has no suffix.</summary>
    public CarColorTable? CarColors { get; init; }

    public static ParamDbFiles Load(ParamDbPaths paths, IList<string> warnings)
    {
        if (!File.Exists(paths.ParamDb))
            throw new FileNotFoundException($"ParamDB not found: {paths.ParamDb}");

        foreach (string required in new[] { paths.ParamStr, paths.ParamUniStr })
        {
            if (!File.Exists(required))
                throw new FileNotFoundException(
                    $"'{Path.GetFileName(required)}' is missing from {paths.DirectoryPath}. " +
                    "It has to sit next to the paramdb file - the labels and names live in it.");
        }

        var archive = GtarArchive.Read(paths.ParamDb);
        var strings = StringTableIO.Read(paths.ParamStr);
        var uniStrings = StringTableIO.Read(paths.ParamUniStr);

        var idTable = new IDTable();
        var idStrings = new StringTable();
        bool hasIdTables = File.Exists(paths.IdIndex) && File.Exists(paths.IdStrings);

        if (hasIdTables)
        {
            idTable.Read(paths.IdIndex);
            idStrings = StringTableIO.Read(paths.IdStrings);
        }
        else
        {
            warnings.Add(
                $"'{Path.GetFileName(paths.IdIndex)}' / '{Path.GetFileName(paths.IdStrings)}' not found - " +
                "row labels will be shown as raw hashes.");
        }

        CarColorTable? carColors = null;
        if (paths.HasCarColors)
        {
            try
            {
                carColors = CarColorTable.Read(paths.CarColorDb, paths.CarColorSdb);
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                warnings.Add($"carcolor.db could not be read and will be left alone: {e.Message}");
            }
        }

        return new ParamDbFiles
        {
            Paths = paths,
            Archive = archive,
            Strings = strings,
            UniStrings = uniStrings,
            IdTable = idTable,
            IdStrings = idStrings,
            HasIdTables = hasIdTables,
            CarColors = carColors,
        };
    }

    /// <summary>Resolves a row label hash to its text, or a parseable placeholder when unknown.</summary>
    public string? ResolveId(ulong hash)
    {
        if (hash == 0)
            return null;

        long index = IdTable.GetStringIndex(hash);
        if (index >= 0 && index < IdStrings.Strings.Count)
            return IdStrings.Strings[(int)index];

        return GtHash.ToPlaceholder(hash);
    }
}
