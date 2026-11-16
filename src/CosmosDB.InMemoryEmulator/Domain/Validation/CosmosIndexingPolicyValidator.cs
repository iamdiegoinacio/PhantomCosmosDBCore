#nullable enable
using System.Net;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator.Domain.Validation;

/// <summary>
/// Validates queries against container indexing policies to ensure parity with Azure Cosmos DB.
/// Enforces requirements such as mandatory composite indexes for multi-property ORDER BY.
/// </summary>
internal static class CosmosIndexingPolicyValidator
{
    /// <summary>
    /// Validates whether a multi-property ORDER BY clause has a matching composite index
    /// in the container's indexing policy, throwing a 400 BadRequest (SubStatusCode 1004) if not.
    /// </summary>
    public static void ValidateCompositeIndexesForOrderBy(
        IndexingPolicy? indexingPolicy,
        OrderByField[]? orderByFields,
        string? fromAlias = null,
        bool enforceWhenNoCompositeIndexesDefined = false)
    {
        if (orderByFields is null || orderByFields.Length <= 1)
        {
            // Single-property ORDER BY does not require a composite index.
            return;
        }

        if (indexingPolicy is null || indexingPolicy.CompositeIndexes is null || indexingPolicy.CompositeIndexes.Count == 0)
        {
            if (!enforceWhenNoCompositeIndexesDefined)
            {
                return;
            }

            throw InMemoryCosmosException.Create(
                "The order by query does not have a corresponding composite index that it can be served from. " +
                "Learn more: https://aka.ms/CosmosDB/sql/errors/composite-index-missing",
                HttpStatusCode.BadRequest,
                CosmosSubStatusCodes.CrossPartitionQueryNotServable,
                Guid.NewGuid().ToString(),
                ContainerConstants.SyntheticRequestCharge);
        }

        var normalizedFromAlias = fromAlias ?? "c";

        // Extract normalized paths and orders from the ORDER BY fields
        var requestedPaths = new List<(string Path, bool Ascending)>();
        foreach (var field in orderByFields)
        {
            var rawPath = field.Field;
            if (string.IsNullOrEmpty(rawPath))
                continue;

            // Strip alias prefix (e.g. "c.name" -> "/name")
            if (rawPath.StartsWith(normalizedFromAlias + ".", StringComparison.OrdinalIgnoreCase))
            {
                rawPath = rawPath[(normalizedFromAlias.Length + 1)..];
            }

            var path = "/" + rawPath.TrimStart('/').Replace('.', '/');
            requestedPaths.Add((path, field.Ascending));
        }

        if (requestedPaths.Count <= 1)
        {
            return;
        }

        var matched = false;
        foreach (var compositeIndex in indexingPolicy.CompositeIndexes)
        {
            if (compositeIndex.Count < requestedPaths.Count)
                continue;

            // In Cosmos DB, a query can match a prefix of a composite index.
            // Check direct match (same directions)
            var matchesForward = true;
            var matchesInverted = true;

            for (var i = 0; i < requestedPaths.Count; i++)
            {
                var req = requestedPaths[i];
                var indexComponent = compositeIndex[i];
                var indexPath = indexComponent.Path;
                if (!string.Equals(req.Path, indexPath, StringComparison.OrdinalIgnoreCase))
                {
                    matchesForward = false;
                    matchesInverted = false;
                    break;
                }

                var indexAscending = indexComponent.Order == CompositePathSortOrder.Ascending;
                if (req.Ascending != indexAscending)
                {
                    matchesForward = false;
                }
                if (req.Ascending == indexAscending)
                {
                    matchesInverted = false;
                }
            }

            if (matchesForward || matchesInverted)
            {
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            throw InMemoryCosmosException.Create(
                "The order by query does not have a corresponding composite index that it can be served from. " +
                "Learn more: https://aka.ms/CosmosDB/sql/errors/composite-index-missing",
                HttpStatusCode.BadRequest,
                CosmosSubStatusCodes.CrossPartitionQueryNotServable,
                Guid.NewGuid().ToString(),
                ContainerConstants.SyntheticRequestCharge);
        }
    }
}
