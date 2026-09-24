namespace GTDataSQLiteConverter.ParamDb;

/// <summary>
/// The .headers files carry hand-written notes above some columns ("0 = Road car", bit meanings, ...).
/// TableMappingReader drops them; this walks the same files in the same order so the notes can be
/// shown as column tooltips.
/// </summary>
public static class HeaderDocs
{
    private static readonly Dictionary<string, List<string?>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Documentation per column, in the same order TableMappingReader produces columns.</summary>
    public static List<string?> ForTable(string tableName, string? variant = null)
    {
        string? file = TableMappingReader.GetHeadersFile(tableName, variant: variant);
        return file is null ? new List<string?>() : ForFile(file, variant);
    }

    /// <summary>The same for one .headers file, with its includes looked up for <paramref name="variant"/>.</summary>
    public static List<string?> ForFile(string file, string? variant = null)
    {
        string key = Key(file, variant);

        lock (_cache)
        {
            if (_cache.TryGetValue(key, out List<string?>? cached))
                return cached;

            var docs = new List<string?>();
            try
            {
                Collect(file, variant, docs, depth: 0);
            }
            catch (IOException)
            {
                // Tooltips are a nicety; never let them break opening a database.
            }

            _cache[key] = docs;
            return docs;
        }
    }

    /// <summary>Drops what was read from a file that has just been rewritten.</summary>
    public static void Forget(string file)
    {
        string suffix = "|" + Path.GetFullPath(file);

        lock (_cache)
        {
            foreach (string key in _cache.Keys.Where(k => k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList())
                _cache.Remove(key);
        }
    }

    private static string Key(string file, string? variant) => $"{variant}|{Path.GetFullPath(file)}";

    private static void Collect(string file, string? variant, List<string?> docs, int depth)
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
                    string? included = TableMappingReader.GetHeadersFile($"{split[1]}.headers", variant: variant);
                    if (included is not null)
                        Collect(included, variant, docs, depth + 1);
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
