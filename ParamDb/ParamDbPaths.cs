using System.Diagnostics.CodeAnalysis;

namespace GTDataSQLiteConverter.ParamDb;

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

    // Companion files follow the suffix unless pointed somewhere else (the CLI's --pstr and friends).
    private readonly string? _paramStr;
    private readonly string? _paramUniStr;
    private readonly string? _idIndex;
    private readonly string? _idStrings;
    private readonly string? _carColorSdb;

    [AllowNull] public string ParamStr { get => _paramStr ?? Named("paramstr"); init => _paramStr = value; }
    [AllowNull] public string ParamUniStr { get => _paramUniStr ?? Named("paramunistr"); init => _paramUniStr = value; }
    [AllowNull] public string IdIndex { get => _idIndex ?? Named(".id_db_idx"); init => _idIndex = value; }
    [AllowNull] public string IdStrings { get => _idStrings ?? Named(".id_db_str"); init => _idStrings = value; }

    /// <summary>The name paramdb would have when written back out with this suffix.</summary>
    public string ParamDbOut => Named("paramdb");

    /// <summary>
    /// The car colour tables. Deliberately unsuffixed: one carcolor.db is shared by every region,
    /// which is why it carries US-only and EU-only cars and both languages of every colour name.
    /// </summary>
    public string CarColorDb => Path.Combine(DirectoryPath, "carcolor.db");

    [AllowNull] public string CarColorSdb { get => _carColorSdb ?? Path.Combine(DirectoryPath, "carcolor.sdb"); init => _carColorSdb = value; }

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
    /// Derives the companion file names from a selected paramdb file
    /// (paramdb_eu.db -> paramstr_eu.db, .id_db_idx_eu.db, ...).
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
