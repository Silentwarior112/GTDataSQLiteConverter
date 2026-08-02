namespace GTParamDBEditor.Core;

/// <summary>One open database: the live SQLite plus where saving should put the game files.</summary>
public sealed class EditorSession : IDisposable
{
    public required LiveDatabase Database { get; init; }

    /// <summary>Where "Save" writes. Null when a SQLite file was opened on its own.</summary>
    public ParamDbPaths? Target { get; set; }

    /// <summary>
    /// Path shown in the title bar, and the one "Reload from disk" reopens - the ParamDB, or the
    /// SQLite when one was opened directly. Follows the file after a Save As.
    /// </summary>
    public required string DisplayPath { get; set; }

    public List<string> Warnings { get; } = new();

    /// <summary>Set by any edit; cleared once the game files have been written.</summary>
    public bool IsDirty { get; set; }

    public static EditorSession OpenParamDb(string paramDbFile, string sqlitePath)
    {
        ParamDbPaths paths = ParamDbPaths.FromParamDbFile(paramDbFile);

        var warnings = new List<string>();
        ParamDbFiles files = ParamDbFiles.Load(paths, warnings);
        LiveDatabase database = LiveDatabase.CreateFrom(files, sqlitePath, isTemporary: true, warnings);

        var session = new EditorSession
        {
            Database = database,
            Target = paths,
            DisplayPath = paramDbFile,
        };
        session.Warnings.AddRange(warnings);
        return session;
    }

    public static EditorSession OpenSqlite(string sqlitePath)
    {
        var warnings = new List<string>();
        LiveDatabase database = LiveDatabase.Open(sqlitePath, warnings);

        ParamDbPaths? target = null;
        if (!string.IsNullOrEmpty(database.Meta.SourceDirectory) && Directory.Exists(database.Meta.SourceDirectory))
            target = new ParamDbPaths(database.Meta.SourceDirectory, database.Meta.Suffix);

        var session = new EditorSession
        {
            Database = database,
            Target = target,
            DisplayPath = sqlitePath,
        };
        session.Warnings.AddRange(warnings);
        return session;
    }

    public void Dispose() => Database.Dispose();
}
