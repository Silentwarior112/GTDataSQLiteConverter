using System.Text;

using GTDataSQLiteConverter.Entities;

namespace GTDataSQLiteConverter.ParamDb;

/// <summary>A table's columns and the row size they describe, as read from one .headers file.</summary>
public sealed class TableLayout
{
    public required string FilePath { get; init; }

    /// <summary>Saved under Headers/Custom after columns were added or removed, rather than one of the game's own.</summary>
    public bool IsCustom { get; init; }

    public required List<LiveColumn> Columns { get; init; }

    public required int RowSize { get; init; }

    public bool Matches(IReadOnlyList<LiveColumn> columns, int rowSize)
        => RowSize == rowSize
           && Columns.Count == columns.Count
           && Columns.OrderBy(c => c.Offset).Zip(columns.OrderBy(c => c.Offset)).All(pair => pair.First.IsSameField(pair.Second));
}

/// <summary>
/// Finds each table's column layout.
///
/// A game's own layouts are its .headers files (see <see cref="ParamDbGame.HeadersVariant"/>).
/// Columns added or removed - in the editor, or with the CLI's add-column and remove-column - are
/// saved to Headers/Custom/&lt;game&gt;/&lt;TABLE&gt;.headers,
/// which is used for any paramdb whose rows in that table are exactly the size it describes. A widened
/// file opens with the columns it was widened with, and an untouched one with the game's.
/// </summary>
public static class TableLayouts
{
    private const string CustomFolder = "Custom";

    public static TableLayout? ReadDefault(ParamDbGame game, string tableName, IList<string> warnings)
    {
        string? file = TableMappingReader.GetHeadersFile(tableName, variant: game.HeadersVariant);
        return file is null ? null : Read(file, isCustom: false, game, tableName, warnings);
    }

    public static TableLayout? ReadCustom(ParamDbGame game, string tableName, IList<string> warnings)
    {
        string? file = FindCustom(game, tableName);
        return file is null ? null : Read(file, isCustom: true, game, tableName, warnings);
    }

    /// <summary>The saved layout when its rows are this size, otherwise the game's own.</summary>
    public static TableLayout? Choose(ParamDbGame game, string tableName, int rowSize, IList<string> warnings)
    {
        TableLayout? custom = ReadCustom(game, tableName, warnings);
        if (custom?.RowSize == rowSize)
            return custom;

        if (custom is not null)
        {
            warnings.Add(
                $"'{tableName}': {custom.FilePath} lays out {custom.RowSize}-byte rows but this file's are " +
                $"{rowSize} bytes, so the game's own layout was used.");
        }

        return ReadDefault(game, tableName, warnings);
    }

    /// <summary>Whether a layout this game has for the table describes rows of this size.</summary>
    public static bool Fits(ParamDbGame game, string tableName, int rowSize)
    {
        var ignored = new List<string>();
        return ReadDefault(game, tableName, ignored)?.RowSize == rowSize
            || ReadCustom(game, tableName, ignored)?.RowSize == rowSize;
    }

    /// <summary>
    /// Saves a table's edited layout under Headers/Custom. A table back to the game's own layout
    /// writes nothing: a saved layout other files may still use is left alone.
    /// </summary>
    /// <returns>The saved layout the table now uses (null for the game's own), and whether it was written.</returns>
    public static (string? FilePath, bool Written) Save(ParamDbGame game, LiveTable table, IList<string> notes)
    {
        if (ReadDefault(game, table.Name, new List<string>())?.Matches(table.Columns, table.RowStride) == true)
            return (null, false);

        string file = FindCustom(game, table.Name)
                      ?? Path.Combine(WriteRoot(), CustomFolder, game.Id, table.Name + ".headers");
        string text = Format(table.Columns, table.RowStride);

        if (File.Exists(file))
        {
            if (File.ReadAllText(file) == text)
                return (file, false);

            if (table.LayoutFile is null || !SamePath(table.LayoutFile, file))
                notes.Add($"{file} held a different {table.Name} layout, which has been replaced.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        HeaderDocs.Forget(file);
        return (file, true);
    }

    /// <summary>A layout as a .headers file: every column in row order, with padding for the gaps.</summary>
    public static string Format(IEnumerable<LiveColumn> columns, int rowSize)
    {
        var text = new StringBuilder();
        int at = 0;

        foreach (LiveColumn column in columns.OrderBy(c => c.Offset))
        {
            // Padding lengths are hex, as TableMappingReader reads them.
            if (column.Offset > at)
                text.Append("padding|").Append((column.Offset - at).ToString("X")).Append("\r\n");

            if (!string.IsNullOrEmpty(column.Documentation))
            {
                foreach (string line in column.Documentation.Split('\n'))
                {
                    string comment = line.TrimEnd('\r');
                    text.Append(comment.Length == 0 ? "//" : "// " + comment).Append("\r\n");
                }
            }

            text.Append("add_column|").Append(column.Name).Append('|').Append(TypeName(column.Type)).Append("\r\n");
            at = column.Offset + column.Size;
        }

        if (rowSize > at)
            text.Append("padding|").Append((rowSize - at).ToString("X")).Append("\r\n");

        return text.ToString();
    }

    private static TableLayout Read(string file, bool isCustom, ParamDbGame game, string tableName, IList<string> warnings)
    {
        List<TableColumn> mappings = TableMappingReader.ReadColumnMappings(file, out int size, game.HeadersVariant);
        List<string?> docs = HeaderDocs.ForFile(file, game.HeadersVariant);

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

        return new TableLayout { FilePath = file, IsCustom = isCustom, Columns = columns, RowSize = size };
    }

    /// <summary>Looked up in the same places, and the same order, as every other .headers file.</summary>
    private static string[] Roots()
        => new[] { TableMappingReader.HeadersDirectory, Path.Combine(AppContext.BaseDirectory, "Headers") };

    private static string? FindCustom(ParamDbGame game, string tableName)
        => Roots().Select(root => Path.Combine(root, CustomFolder, game.Id, tableName + ".headers")).FirstOrDefault(File.Exists);

    private static string WriteRoot()
        => Roots().FirstOrDefault(Directory.Exists) ?? Roots()[^1];

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string TypeName(DBColumnType type) => type switch
    {
        DBColumnType.Byte => "byte",
        DBColumnType.Short => "ushort",
        DBColumnType.Int => "uint",
        DBColumnType.Int64 => "int64",
        DBColumnType.Float => "float",
        DBColumnType.Double => "double",
        DBColumnType.Id => "id",
        DBColumnType.String => "string",
        DBColumnType.Unicode => "unicode",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No .headers spelling for this type."),
    };
}
