namespace YrrpAnalyser;

/// <summary>
/// A minimal INI reader for the embedded spawn.ini and spawnmap.ini. Deliberately forgiving:
/// these files come out of the client and off other people's machines, and a stray line is not
/// a reason to fail a whole replay.
/// </summary>
public sealed class IniDocument
{
    private readonly Dictionary<string, IniSection> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public static readonly IniDocument Empty = new();

    public IReadOnlyCollection<IniSection> Sections => _sections.Values;

    public IniSection? GetSection(string name) =>
        _sections.TryGetValue(name, out var s) ? s : null;

    public bool HasSection(string name) => _sections.ContainsKey(name);

    public string GetString(string section, string key, string fallback = "") =>
        GetSection(section)?.GetString(key, fallback) ?? fallback;

    public int GetInt(string section, string key, int fallback = 0) =>
        GetSection(section)?.GetInt(key, fallback) ?? fallback;

    public bool GetBool(string section, string key, bool fallback = false) =>
        GetSection(section)?.GetBool(key, fallback) ?? fallback;

    public static IniDocument Parse(string text)
    {
        var doc = new IniDocument();
        IniSection? current = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;
            if (line[0] == ';') continue;

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close <= 1) continue;
                var name = line[1..close];
                if (!doc._sections.TryGetValue(name, out current))
                {
                    current = new IniSection(name);
                    doc._sections[name] = current;
                }
                continue;
            }

            if (current is null) continue;

            int eq = line.IndexOf('=');
            if (eq < 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..];

            // The game strips a trailing comment on read; spawnmap.ini relies on it.
            int semi = value.IndexOf(';');
            if (semi >= 0) value = value[..semi];

            current.Set(key, value.Trim());
        }

        return doc;
    }
}

public sealed class IniSection(string name)
{
    private readonly List<KeyValuePair<string, string>> _ordered = [];
    private readonly Dictionary<string, string> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; } = name;
    public IReadOnlyList<KeyValuePair<string, string>> Entries => _ordered;

    public void Set(string key, string value)
    {
        if (_byKey.ContainsKey(key))
        {
            for (int i = 0; i < _ordered.Count; i++)
            {
                if (string.Equals(_ordered[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    _ordered[i] = new KeyValuePair<string, string>(key, value);
                    break;
                }
            }
        }
        else
        {
            _ordered.Add(new KeyValuePair<string, string>(key, value));
        }
        _byKey[key] = value;
    }

    public bool Has(string key) => _byKey.ContainsKey(key);

    public string GetString(string key, string fallback = "") =>
        _byKey.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    public int GetInt(string key, int fallback = 0) =>
        _byKey.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

    public bool GetBool(string key, bool fallback = false)
    {
        if (!_byKey.TryGetValue(key, out var v) || v.Length == 0) return fallback;
        return v[0] switch
        {
            'y' or 'Y' or 't' or 'T' or '1' => true,
            'n' or 'N' or 'f' or 'F' or '0' => false,
            _ => fallback,
        };
    }
}
