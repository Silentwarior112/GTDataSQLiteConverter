using GTDataSQLiteConverter;

namespace GTParamDBEditor.Core;

/// <summary>
/// The .headers files carry hand-written notes above some columns ("0 = Road car", bit meanings, ...).
/// TableMappingReader drops them; this walks the same files in the same order so the notes can be
/// shown as column tooltips.
/// </summary>
public static class HeaderDocs
{
    private static readonly Dictionary<string, List<string?>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Documentation per column, in the same order TableMappingReader produces columns.</summary>
    public static List<string?> ForTable(string tableName)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(tableName, out List<string?>? cached))
                return cached;

            var docs = new List<string?>();
            try
            {
                string? file = TableMappingReader.GetHeadersFile(tableName);
                if (file is not null)
                    Collect(file, docs, depth: 0);
            }
            catch (IOException)
            {
                // Tooltips are a nicety; never let them break opening a database.
            }

            _cache[tableName] = docs;
            return docs;
        }
    }

    private static void Collect(string file, List<string?> docs, int depth)
    {
        if (depth > 8)
            return;

        var pending = new List<string>();

        foreach (string raw in File.ReadLines(file))
        {
            string line = raw.Trim();

            if (line.Length == 0)
                continue;

            if (line.StartsWith("//"))
            {
                pending.Add(line[2..].Trim());
                continue;
            }

            string[] split = line.Split('|');
            switch (split[0])
            {
                case "add_column":
                    docs.Add(pending.Count > 0 ? string.Join(Environment.NewLine, pending) : null);
                    pending.Clear();
                    break;

                case "include" when split.Length >= 2:
                {
                    string? included = TableMappingReader.GetHeadersFile($"{split[1]}.headers");
                    if (included is not null)
                        Collect(included, docs, depth + 1);
                    pending.Clear();
                    break;
                }

                default:
                    pending.Clear();
                    break;
            }
        }
    }
}
