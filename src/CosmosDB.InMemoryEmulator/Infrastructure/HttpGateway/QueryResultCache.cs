#nullable enable
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Infrastructure.HttpGateway;

/// <summary>
/// Thread-safe query result cache with TTL-based eviction and bounded size.
/// Prevents unbounded memory growth when consumers abandon mid-page iteration.
/// </summary>
internal sealed class QueryResultCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;

    public QueryResultCache(TimeSpan ttl, int maxEntries)
    {
        _ttl = ttl;
        _maxEntries = maxEntries;
    }

    public bool TryGet(string key, out List<JToken> value)
    {
        EvictStale();
        if (_entries.TryGetValue(key, out var entry) && !entry.IsExpired(_ttl))
        {
            entry.Touch();
            value = entry.Items;
            return true;
        }

        _entries.TryRemove(key, out _);
        value = null!;
        return false;
    }

    public void Set(string key, List<JToken> items)
    {
        EvictStale();
        _entries[key] = new CacheEntry(items);
    }

    public void Remove(string key)
    {
        _entries.TryRemove(key, out _);
    }

    public void Clear()
    {
        _entries.Clear();
    }

    private void EvictStale()
    {
        var keysToRemove = _entries
            .Where(pair => pair.Value.IsExpired(_ttl))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _entries.TryRemove(key, out _);
        }

        // If still over capacity after TTL eviction, remove oldest entries
        if (_entries.Count > _maxEntries)
        {
            var excess = _entries
                .OrderBy(pair => pair.Value.LastAccessed)
                .Take(_entries.Count - _maxEntries)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in excess)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed class CacheEntry
    {
        public List<JToken> Items { get; }
        public DateTime CreatedAt { get; }
        public DateTime LastAccessed { get; private set; }

        public CacheEntry(List<JToken> items)
        {
            Items = items;
            CreatedAt = DateTime.UtcNow;
            LastAccessed = DateTime.UtcNow;
        }

        public bool IsExpired(TimeSpan ttl) => DateTime.UtcNow - CreatedAt > ttl;

        public void Touch() => LastAccessed = DateTime.UtcNow;
    }
}
