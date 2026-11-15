#nullable enable
using System.Collections.Concurrent;
using System.Net;
using CosmosDB.InMemoryEmulator.Domain.Validation;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static CosmosDB.InMemoryEmulator.Domain.Primitives.ContainerConstants;

namespace CosmosDB.InMemoryEmulator.Infrastructure.Storage;

/// <summary>
/// Thread-safe in-memory document store managing items, ETags, timestamps, insertion order,
/// per-item concurrency locks, TTL evaluation/eviction, ETag precondition checks, and
/// transactional batch snapshots.
/// </summary>
internal sealed class InMemoryItemStore
{
    private static int _docRidCounter;
    private static readonly AsyncLocal<HashSet<(string Id, string PartitionKey)>?> BatchWriteTracker = new();

    public ConcurrentDictionary<(string Id, string PartitionKey), string> Items { get; } = new();
    public ConcurrentDictionary<(string Id, string PartitionKey), string> Etags { get; } = new();
    public ConcurrentDictionary<(string Id, string PartitionKey), DateTimeOffset> Timestamps { get; } = new();
    public List<(string Id, string PartitionKey)> InsertionOrder { get; } = new();
    public object InsertionOrderLock { get; } = new();
    public object UniqueKeyWriteLock { get; } = new();
    public ConcurrentDictionary<(string Id, string PartitionKey), SemaphoreSlim> ItemLocks { get; } = new();

    public SemaphoreSlim GetOrAddLock((string Id, string PartitionKey) key)
        => ItemLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    public List<(string Id, string PartitionKey)> GetOrderedKeysSnapshot()
    {
        lock (InsertionOrderLock)
        {
            return InsertionOrder.ToList();
        }
    }

    public void AddInsertionOrder((string Id, string PartitionKey) key)
    {
        lock (InsertionOrderLock)
        {
            InsertionOrder.Add(key);
        }
    }

    public void RemoveInsertionOrder((string Id, string PartitionKey) key)
    {
        lock (InsertionOrderLock)
        {
            InsertionOrder.Remove(key);
        }
    }

    public void Clear(bool includeLocks = false)
    {
        Items.Clear();
        Etags.Clear();
        Timestamps.Clear();
        if (includeLocks)
        {
            ItemLocks.Clear();
        }
        lock (InsertionOrderLock)
        {
            InsertionOrder.Clear();
        }
    }

    public int GetActiveItemCount(int? defaultTimeToLive)
        => defaultTimeToLive is null
            ? Items.Count
            : Items.Keys.Count(k => !IsExpired(k, defaultTimeToLive));

    public bool IsExpired((string Id, string PartitionKey) key, int? defaultTimeToLive)
    {
        // TTL feature is completely disabled when DefaultTimeToLive is null.
        // Per-item _ttl is ignored in this case (matches real Cosmos DB behaviour).
        if (defaultTimeToLive is null)
        {
            return false;
        }

        if (!Timestamps.TryGetValue(key, out var ts))
        {
            return false;
        }

        var elapsed = (DateTimeOffset.UtcNow - ts).TotalSeconds;

        if (Items.TryGetValue(key, out var json))
        {
            var jObj = JsonParseHelpers.ParseJson(json);
            var itemTtl = jObj["ttl"] ?? jObj["_ttl"];
            if (itemTtl is not null && int.TryParse(itemTtl.ToString(), out var perItemTtl))
            {
                // ttl = -1 means "never expire" even if container has a default TTL
                if (perItemTtl == -1) return false;
                return elapsed >= perItemTtl;
            }
        }

        // DefaultTimeToLive = -1 means TTL is ON but items without per-item ttl don't expire
        if (defaultTimeToLive.Value <= 0)
        {
            return false;
        }

        return elapsed >= defaultTimeToLive.Value;
    }

    public void EvictIfExpired(
        (string Id, string PartitionKey) key,
        int? defaultTimeToLive,
        IReadOnlyList<string> partitionKeyPaths,
        InMemoryChangeFeedLog changeFeedLog)
    {
        if (!IsExpired(key, defaultTimeToLive))
        {
            return;
        }

        Items.TryRemove(key, out _);
        Etags.TryRemove(key, out _);
        Timestamps.TryRemove(key, out _);
        RemoveInsertionOrder(key);

        // Record a delete tombstone in the change feed so consumers see TTL evictions
        changeFeedLog.RecordDeleteTombstone(key.Id, key.PartitionKey, partitionKeyPaths, isTtlEviction: true);
    }

    public static string EnrichWithSystemProperties(
        string containerId, string json, string etag, DateTimeOffset timestamp)
    {
        var jObj = JsonParseHelpers.ParseJson(json);
        var containerHash = PartitionKeyHash.MurmurHash3(containerId);
        var docId = (uint)Interlocked.Increment(ref _docRidCounter);
        var ridBytes = new byte[8];
        Buffer.BlockCopy(BitConverter.GetBytes(containerHash), 0, ridBytes, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(docId), 0, ridBytes, 4, 4);
        jObj["_rid"] = Convert.ToBase64String(ridBytes);
        jObj["_self"] = $"dbs/db/colls/col/docs/{jObj["id"]}";
        jObj["_etag"] = etag;
        jObj["_ts"] = timestamp.ToUnixTimeSeconds();
        jObj["_attachments"] = "attachments/";
        var enriched = jObj.ToString(Formatting.None);
        DocumentValidator.ValidateDocumentSize(enriched);
        return enriched;
    }

    public IEnumerable<string> GetAllItemsForPartition(
        QueryRequestOptions? requestOptions,
        IReadOnlyList<string> partitionKeyPaths,
        int? defaultTimeToLive)
    {
        var orderedKeys = GetOrderedKeysSnapshot();

        if (requestOptions?.PartitionKey is not null
            && requestOptions.PartitionKey != PartitionKey.None)
        {
            var pk = PartitionKeyProcessor.PartitionKeyToString(requestOptions.PartitionKey.Value);

            // Hierarchical PK prefix match: when the query PK has fewer components
            // than the container's partition key paths, treat it as a prefix filter.
            if (partitionKeyPaths is { Count: > 1 })
            {
                var queryComponents = PartitionKeyProcessor.CountPartitionKeyComponents(requestOptions.PartitionKey.Value);
                if (queryComponents > 0 && queryComponents < partitionKeyPaths.Count)
                {
                    var prefix = pk + "|";
                    return orderedKeys
                        .Where(key => (key.PartitionKey?.StartsWith(prefix, StringComparison.Ordinal) ?? false)
                            && !IsExpired(key, defaultTimeToLive)
                            && Items.ContainsKey(key))
                        .Select(key => Items[key]);
                }
            }

            return orderedKeys
                .Where(key => key.PartitionKey == pk
                    && !IsExpired(key, defaultTimeToLive)
                    && Items.ContainsKey(key))
                .Select(key => Items[key]);
        }

        return orderedKeys
            .Where(key => !IsExpired(key, defaultTimeToLive) && Items.ContainsKey(key))
            .Select(key => Items[key]);
    }

    public void CheckIfMatch(ItemRequestOptions? requestOptions, (string Id, string PartitionKey) key)
    {
        if (requestOptions?.IfMatchEtag is null || requestOptions.IfMatchEtag == "*")
        {
            return;
        }

        if (!Etags.TryGetValue(key, out var currentEtag))
        {
            return;
        }

        if (requestOptions.IfMatchEtag != currentEtag)
        {
            throw InMemoryCosmosException.Create(
                "Precondition Failed", HttpStatusCode.PreconditionFailed, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }
    }

    public void CheckIfNoneMatch(ItemRequestOptions? requestOptions, (string Id, string PartitionKey) key)
    {
        if (requestOptions?.IfNoneMatchEtag is null)
        {
            return;
        }

        if (requestOptions.IfNoneMatchEtag == "*" && Etags.ContainsKey(key))
        {
            throw InMemoryCosmosException.Create(
                "Not Modified", HttpStatusCode.NotModified, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        if (Etags.TryGetValue(key, out var currentEtag) && requestOptions.IfNoneMatchEtag == currentEtag)
        {
            throw InMemoryCosmosException.Create(
                "Not Modified", HttpStatusCode.NotModified, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }
    }

    public void CheckIfNoneMatchForWrite(ItemRequestOptions? requestOptions, (string Id, string PartitionKey) key)
    {
        if (requestOptions?.IfNoneMatchEtag is null)
        {
            return;
        }

        if (requestOptions.IfNoneMatchEtag == "*" && Etags.ContainsKey(key))
        {
            throw InMemoryCosmosException.Create(
                "Precondition Failed", HttpStatusCode.PreconditionFailed, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        if (Etags.TryGetValue(key, out var currentEtag) && requestOptions.IfNoneMatchEtag == currentEtag)
        {
            throw InMemoryCosmosException.Create(
                "Precondition Failed", HttpStatusCode.PreconditionFailed, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }
    }

    public bool CheckIfMatchStream(ItemRequestOptions? requestOptions, (string Id, string PartitionKey) key)
    {
        if (requestOptions?.IfMatchEtag is null || requestOptions.IfMatchEtag == "*")
        {
            return true;
        }

        if (Etags.TryGetValue(key, out var currentEtag) && requestOptions.IfMatchEtag != currentEtag)
        {
            return false;
        }

        return true;
    }

    public bool CheckIfNoneMatchStream(ItemRequestOptions? requestOptions, (string Id, string PartitionKey) key)
    {
        if (requestOptions?.IfNoneMatchEtag is null)
        {
            return true;
        }

        if (requestOptions.IfNoneMatchEtag == "*" && Etags.ContainsKey(key))
        {
            return false;
        }

        if (Etags.TryGetValue(key, out var currentEtag) && requestOptions.IfNoneMatchEtag == currentEtag)
        {
            return false;
        }

        return true;
    }

    public Dictionary<(string Id, string PartitionKey), string> SnapshotItems()
        => new(Items);

    public Dictionary<(string Id, string PartitionKey), string> SnapshotEtags()
        => new(Etags);

    public Dictionary<(string Id, string PartitionKey), DateTimeOffset> SnapshotTimestamps()
        => new(Timestamps);

    public static void BeginBatchTracking()
        => BatchWriteTracker.Value = new HashSet<(string Id, string PartitionKey)>();

    public static HashSet<(string Id, string PartitionKey)> EndBatchTracking()
    {
        var keys = BatchWriteTracker.Value ?? new HashSet<(string Id, string PartitionKey)>();
        BatchWriteTracker.Value = null;
        return keys;
    }

    public static void TrackBatchWrite((string Id, string PartitionKey) key)
        => BatchWriteTracker.Value?.Add(key);

    public void RestoreSnapshot(
        Dictionary<(string Id, string PartitionKey), string> itemsSnapshot,
        Dictionary<(string Id, string PartitionKey), string> etagsSnapshot,
        Dictionary<(string Id, string PartitionKey), DateTimeOffset> timestampsSnapshot,
        int changeFeedCount,
        IReadOnlySet<(string Id, string PartitionKey)> touchedKeys,
        InMemoryChangeFeedLog changeFeedLog)
    {
        foreach (var key in touchedKeys)
        {
            if (itemsSnapshot.TryGetValue(key, out var snapshotItem))
            {
                Items[key] = snapshotItem;
                Etags[key] = etagsSnapshot[key];
                Timestamps[key] = timestampsSnapshot[key];
            }
            else
            {
                Items.TryRemove(key, out _);
                Etags.TryRemove(key, out _);
                Timestamps.TryRemove(key, out _);
                RemoveInsertionOrder(key);
            }
        }

        lock (InsertionOrderLock)
        {
            foreach (var key in touchedKeys)
            {
                if (itemsSnapshot.ContainsKey(key) && !InsertionOrder.Contains(key))
                {
                    InsertionOrder.Add(key);
                }
            }
        }

        changeFeedLog.TrimToCount(changeFeedCount);
    }
}
