#nullable enable
using System.Collections.Concurrent;
using System.Net;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using static CosmosDB.InMemoryEmulator.Domain.Primitives.ContainerConstants;

namespace CosmosDB.InMemoryEmulator.Domain.Validation;

/// <summary>
/// Validates unique key constraints within a partition key scope.
/// </summary>
internal static class UniqueKeyValidator
{
    public static void ValidateUniqueKeys(
        UniqueKeyPolicy? policy,
        ConcurrentDictionary<(string Id, string PartitionKey), string> items,
        JObject jObj,
        string? partitionKey,
        string? excludeItemId = null)
    {
        if (policy == null || policy.UniqueKeys.Count == 0) return;

        foreach (var uniqueKey in policy.UniqueKeys)
        {
            // Cosmos DB unique key paths use /parent/child format.
            // JObject.SelectToken uses dot notation (parent.child).
            var newValues = uniqueKey.Paths
                .Select(p => jObj.SelectToken(p.TrimStart('/').Replace('/', '.'))?.ToString())
                .ToList();

            foreach (var (existingKey, existingJson) in items)
            {
                if (existingKey.PartitionKey != partitionKey) continue;
                if (excludeItemId != null && existingKey.Id == excludeItemId) continue;

                var existingObj = JsonParseHelpers.ParseJson(existingJson);
                var existingValues = uniqueKey.Paths
                    .Select(p => existingObj.SelectToken(p.TrimStart('/').Replace('/', '.'))?.ToString())
                    .ToList();

                if (newValues.SequenceEqual(existingValues))
                {
                    throw InMemoryCosmosException.Create(
                        "Unique index constraint violation.",
                        HttpStatusCode.Conflict,
                        CosmosSubStatusCodes.UniqueKeyConstraintViolation,
                        Guid.NewGuid().ToString(),
                        SyntheticRequestCharge);
                }
            }
        }
    }

    public static bool ValidateUniqueKeysStream(
        UniqueKeyPolicy? policy,
        ConcurrentDictionary<(string Id, string PartitionKey), string> items,
        JObject jObj,
        string? partitionKey,
        string? excludeItemId = null)
    {
        try
        {
            ValidateUniqueKeys(policy, items, jObj, partitionKey, excludeItemId);
            return true;
        }
        catch (CosmosException)
        {
            return false;
        }
    }
}
