using Khors.Core.Profiles;

namespace Khors.Core.Import;

/// <summary>
/// Параметры ссылки. Разбор забирает известные параметры через <see cref="Take"/>;
/// всё, что осталось, становится <see cref="Profile.UnknownParams"/> в исходном порядке.
/// </summary>
internal sealed class QueryParameters
{
    private readonly List<Entry> _entries = [];

    public QueryParameters(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(part[(eq + 1)..]);
            _entries.Add(new Entry(key, value));
        }
    }

    public QueryParameters(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        foreach (var (key, value) in pairs)
        {
            _entries.Add(new Entry(key, value));
        }
    }

    /// <summary>Забирает параметр (без учёта регистра имени). Пустое значение — как отсутствие; при повторах берётся последнее.</summary>
    public string? Take(string key)
    {
        string? result = null;
        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                entry.Used = true;
                if (entry.Value.Length > 0)
                {
                    result = entry.Value;
                }
            }
        }

        return result;
    }

    /// <summary>Забирает все синонимы и возвращает значение первого найденного (например, <c>sni</c> и устаревший <c>peer</c>).</summary>
    public string? TakeFirst(params string[] keys)
    {
        string? result = null;
        foreach (var key in keys)
        {
            var value = Take(key);
            result ??= value;
        }

        return result;
    }

    public EquatableArray<UnknownParam> Remaining() =>
        new(_entries.Where(e => !e.Used).Select(e => new UnknownParam(e.Key, e.Value)));

    private sealed class Entry(string key, string value)
    {
        public string Key { get; } = key;

        public string Value { get; } = value;

        public bool Used { get; set; }
    }
}
