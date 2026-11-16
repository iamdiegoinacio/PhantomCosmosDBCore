#nullable enable
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.ChangeFeed;

/// <summary>
/// Projects raw change-feed log entries into the Cosmos DB "all versions and deletes" (full fidelity)
/// wire shape: <c>{ "current": {...}, "previous": {...}, "metadata": { operationType, lsn, crts, ... } }</c>.
/// The previous image is reconstructed from earlier entries of the same item still retained in the log.
/// </summary>
internal static class FullFidelityChangeFeedProjector
{
    public static List<JObject> Project(
        IReadOnlyList<(DateTimeOffset Timestamp, string Id, string PartitionKey, string Json, bool IsDelete)> allEntries,
        int startIndex)
    {
        var lastSeen = new Dictionary<(string Id, string Pk), JObject>();
        var result = new List<JObject>();

        for (var index = 0; index < allEntries.Count; index++)
        {
            var entry = allEntries[index];
            var key = (entry.Id, entry.PartitionKey);
            lastSeen.TryGetValue(key, out var previous);
            var document = JObject.Parse(entry.Json);

            if (index >= startIndex)
            {
                result.Add(BuildEnvelope(entry.Timestamp, document, previous, entry.IsDelete));
            }

            if (entry.IsDelete)
            {
                lastSeen.Remove(key);
            }
            else
            {
                lastSeen[key] = document;
            }
        }

        return result;
    }

    private static JObject BuildEnvelope(
        DateTimeOffset timestamp,
        JObject document,
        JObject? previous,
        bool isDelete)
    {
        var lsn = document["_lsn"]?.Value<long>() ?? 0;
        var operationType = isDelete ? "delete" : previous is null ? "create" : "replace";

        var metadata = new JObject
        {
            ["operationType"] = operationType,
            ["lsn"] = lsn,
            ["crts"] = timestamp.ToUnixTimeSeconds()
        };

        if (previous is not null)
        {
            metadata["previousImageLSN"] = previous["_lsn"]?.Value<long>() ?? 0;
        }

        if (isDelete)
        {
            metadata["timeToLiveExpired"] = document["_ttlEviction"]?.Value<bool>() ?? false;
        }

        var envelope = new JObject();
        if (isDelete)
        {
            envelope["current"] = new JObject();
            envelope["previous"] = previous ?? document;
        }
        else
        {
            envelope["current"] = document;
            if (previous is not null)
            {
                envelope["previous"] = previous;
            }
        }

        envelope["metadata"] = metadata;
        return envelope;
    }
}
