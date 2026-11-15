#nullable enable
using System.Collections;

namespace CosmosDB.InMemoryEmulator.Application.Results;

/// <summary>
/// A read-only dictionary that detects ambiguous container names across databases
/// and throws descriptive errors.
/// </summary>
internal sealed class AmbiguityDetectingDictionary<TValue> : IReadOnlyDictionary<string, TValue>
{
    private readonly Dictionary<string, TValue> _unambiguous;
    private readonly Dictionary<string, List<string>> _ambiguous;

    internal AmbiguityDetectingDictionary(
        Dictionary<string, TValue> unambiguous,
        Dictionary<string, List<string>> ambiguous)
    {
        _unambiguous = unambiguous;
        _ambiguous = ambiguous;
    }

    public TValue this[string key]
    {
        get
        {
            if (_ambiguous.TryGetValue(key, out var databases))
                throw new KeyNotFoundException(
                    $"Container '{key}' exists in multiple databases: " +
                    string.Join(", ", databases.OrderBy(d => d)) + ". " +
                    $"Use cosmos.Database(\"...\").Containers[\"{key}\"] instead.");
            if (!_unambiguous.TryGetValue(key, out var value))
                throw new KeyNotFoundException($"Container '{key}' not found.");
            return value;
        }
    }

    public bool ContainsKey(string key) => _unambiguous.ContainsKey(key);

    public bool TryGetValue(string key, out TValue value) => _unambiguous.TryGetValue(key, out value!);

    public int Count => _unambiguous.Count;

    public IEnumerable<string> Keys => _unambiguous.Keys;

    public IEnumerable<TValue> Values => _unambiguous.Values;

    public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => _unambiguous.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
