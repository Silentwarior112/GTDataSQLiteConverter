namespace GTParamDBEditor.Core;

/// <summary>
/// The set of game files that together make up one region variant of a ParamDB.
/// </summary>
public sealed class ParamDbPaths
{
    public string DirectoryPath { get; }

    /// <summary>Region suffix, e.g. "" (JP), "eu", "us".</summary>
    public string Suffix { get; }

    /// <summary>The paramdb file actually selected by the user (may be named unconventionally).</summary>
    public string ParamDb { get; }

    public string ParamStr => Named("paramstr");
    public string ParamUniStr => Named("paramunistr");
    public string IdIndex => Named(".id_db_idx");
    public string IdStrings => Named(".id_db_str");

    /// <summary>The name paramdb would have when written back out with this suffix.</summary>
    public string ParamDbOut => Named("paramdb");

    /// <summary>
    /// The car colour tables. Deliberately unsuffixed: one carcolor.db is shared by every region,
    /// which is why it carries US-only and EU-only cars and both languages of every colour name.
    /// </summary>
    public string CarColorDb => Path.Combine(DirectoryPath, "carcolor.db");

    public string CarColorSdb => Path.Combine(DirectoryPath, "carcolor.sdb");

    public bool HasCarColors => File.Exists(CarColorDb) && File.Exists(CarColorSdb);

    public ParamDbPaths(string directoryPath, string suffix, string? paramDbFile = null)
    {
        DirectoryPath = directoryPath;
        Suffix = suffix ?? "";
        ParamDb = paramDbFile ?? Named("paramdb");
    }

    private string Named(string stem)
        => Path.Combine(DirectoryPath, string.IsNullOrEmpty(Suffix) ? $"{stem}.db" : $"{stem}_{Suffix}.db");

    /// <summary>
    /// Derives the companion file names from a selected paramdb file, matching the CLI converter's
    /// convention (paramdb_eu.db -> paramstr_eu.db, .id_db_idx_eu.db, ...).
    /// </summary>
    public static ParamDbPaths FromParamDbFile(string paramDbFile)
    {
        const string stemName = "paramdb";

        string full = Path.GetFullPath(paramDbFile);
        string dir = Path.GetDirectoryName(full) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(full);

        // paramdb_eu.db -> "eu". A file named anything else gets no suffix, so the companions are
        // looked for under their plain names rather than something invented from the filename.
        string suffix = "";
        if (stem.Length > stemName.Length + 1
            && stem.StartsWith(stemName, StringComparison.OrdinalIgnoreCase)
            && stem[stemName.Length] == '_')
        {
            suffix = stem[(stemName.Length + 1)..];
        }

        return new ParamDbPaths(dir, suffix, full);
    }

    public IEnumerable<string> AllOutputFiles()
    {
        yield return ParamDbOut;
        yield return ParamStr;
        yield return ParamUniStr;
        yield return IdIndex;
        yield return IdStrings;
    }

    public override string ToString()
        => string.IsNullOrEmpty(Suffix) ? DirectoryPath : $"{DirectoryPath} ({Suffix})";
}
