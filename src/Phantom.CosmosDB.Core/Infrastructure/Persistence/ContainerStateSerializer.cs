#nullable enable
using Phantom.CosmosDB.Core.Domain.Primitives;
using Phantom.CosmosDB.Core.Domain.Validation;
using Phantom.CosmosDB.Core.Infrastructure.Storage;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Infrastructure.Persistence;

/// <summary>
/// Exports and imports container state to/from JSON strings and files, and handles
/// point-in-time restore (PITR) by replaying the change feed log.
/// </summary>
internal static class ContainerStateSerializer
{
    public static string ExportState(
        IReadOnlyList<(string Id, string PartitionKey)> orderedKeys,
        IReadOnlyDictionary<(string Id, string PartitionKey), string> items,
        Func<(string Id, string PartitionKey), bool> isExpired)
    {
        var validItems = orderedKeys
            .Where(key => items.ContainsKey(key) && !isExpired(key))
            .Select(key => JsonParseHelpers.ParseJson(items[key]))
            .ToList();
        var state = new JObject { ["items"] = new JArray(validItems) };
        return state.ToString(Formatting.Indented);
    }

    public static void ImportState(
        string json,
        Action clearItems,
        Func<JObject, string?> extractPartitionKey,
        Action<JObject, string?> validateUniqueKeys,
        Func<string, string, DateTimeOffset, string> enrichWithSystemProperties,
        Action<(string Id, string PartitionKey), string, string, DateTimeOffset> storeImportedItem)
    {
        var state = JObject.Parse(json);

        if (state["items"] is not JArray items)
            return; // No "items" key — do nothing (preserve existing data)

        clearItems();

        foreach (var item in items)
        {
            var itemJson = item.ToString(Formatting.None);
            if (item["id"] is null)
                throw new InvalidOperationException("Each imported item must have an 'id' property.");
            var id = item["id"]!.ToString();
            var jObj = JsonParseHelpers.ParseJson(itemJson);
            var pk = extractPartitionKey(jObj);
            var key = (id, pk!);

            validateUniqueKeys(jObj, pk);

            var importEtag = ETagValue.GenerateNextString();
            var now = DateTimeOffset.UtcNow;
            var enriched = enrichWithSystemProperties(itemJson, importEtag, now);
            storeImportedItem(key, enriched, importEtag, now);
        }
    }

    public static void RestoreToPointInTime(
        DateTimeOffset pointInTime,
        InMemoryChangeFeedLog changeFeedLog,
        Action clearStorageAndLocks,
        Func<string, string, DateTimeOffset, string> enrichWithSystemProperties,
        Action<(string Id, string PartitionKey), string, string, DateTimeOffset> setRestoredItem,
        Action<IReadOnlyList<(string Id, string PartitionKey)>> setRestoredInsertionOrder)
    {
        var feedSnapshot = changeFeedLog.GetSnapshotUpTo(pointInTime);

        // Replay: keep the last entry per (Id, PartitionKey), skip if it was a delete.
        // TTL eviction tombstones (marked with _ttlEviction) are ignored so that PITR
        // can resurrect items whose TTL expired — matching real Cosmos PITR behaviour.
        var lastPerKey = new Dictionary<(string Id, string PartitionKey), (string Json, bool IsDelete)>();
        foreach (var entry in feedSnapshot)
        {
            if (entry.IsDelete && entry.Json.Contains("\"_ttlEviction\":true", StringComparison.Ordinal))
                continue;
            lastPerKey[(entry.Id, entry.PartitionKey)] = (entry.Json, entry.IsDelete);
        }

        clearStorageAndLocks();

        // Rebuild insertion order from the change feed replay sequence.
        var insertionOrderKeys = new List<(string Id, string PartitionKey)>();
        foreach (var entry in feedSnapshot)
        {
            if (entry.IsDelete && entry.Json.Contains("\"_ttlEviction\":true", StringComparison.Ordinal))
                continue;
            var entryKey = (entry.Id, entry.PartitionKey);
            if (entry.IsDelete)
            {
                insertionOrderKeys.Remove(entryKey);
            }
            else if (!insertionOrderKeys.Contains(entryKey))
            {
                insertionOrderKeys.Add(entryKey);
            }
        }

        foreach (var kvp in lastPerKey)
        {
            if (kvp.Value.IsDelete) continue;

            var key = kvp.Key;
            var etag = $"\"{Guid.NewGuid()}\"";
            var enriched = enrichWithSystemProperties(kvp.Value.Json, etag, pointInTime);
            setRestoredItem(key, enriched, etag, pointInTime);
        }

        setRestoredInsertionOrder(insertionOrderKeys);
    }
}
