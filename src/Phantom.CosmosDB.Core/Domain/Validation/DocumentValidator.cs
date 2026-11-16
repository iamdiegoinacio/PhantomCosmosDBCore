#nullable enable
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using static Phantom.CosmosDB.Core.Domain.Primitives.ContainerConstants;

namespace Phantom.CosmosDB.Core.Domain.Validation;

/// <summary>
/// Validates document size, per-item TTL, query pagination options, patch paths,
/// container replacement properties, and computed property definitions.
/// </summary>
internal static class DocumentValidator
{
    private static readonly HashSet<string> ReservedComputedPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "_rid", "_ts", "_etag", "_self", "_attachments"
    };

    private static readonly HashSet<string> ReservedSystemPatchPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/_rid", "/_ts", "/_self", "/_etag", "/_attachments"
    };

    private static readonly string[] ProhibitedCpClauses =
        { " WHERE ", " ORDER BY ", " GROUP BY ", " TOP ", " DISTINCT ", " OFFSET ", " LIMIT ", " JOIN " };

    public static void ValidateDocumentSize(string json)
    {
        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > MaxDocumentSizeBytes)
        {
            throw InMemoryCosmosException.Create(
                $"Request size is too large. Max allowed size in bytes: {MaxDocumentSizeBytes}. Found: {byteCount}.",
                HttpStatusCode.RequestEntityTooLarge, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }
    }

    public static bool IsDocumentSizeValid(string json)
        => Encoding.UTF8.GetByteCount(json) <= MaxDocumentSizeBytes;

    public static void ValidatePerItemTtl(JObject jObj)
    {
        var ttlToken = jObj["ttl"] ?? jObj["_ttl"];
        if (ttlToken is not null && int.TryParse(ttlToken.ToString(), out var ttlValue) && ttlValue == 0)
        {
            throw InMemoryCosmosException.Create(
                "The value of ttl must be either -1 or a positive integer.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }
    }

    public static bool IsPerItemTtlValid(JObject jObj)
    {
        var ttlToken = jObj["ttl"] ?? jObj["_ttl"];
        return !(ttlToken is not null && int.TryParse(ttlToken.ToString(), out var ttlValue) && ttlValue == 0);
    }

    public static void ValidateThroughput(int? throughput, bool isAutoscale = false)
    {
        if (!throughput.HasValue)
            return;

        if (isAutoscale)
        {
            if (throughput.Value < 1000 || throughput.Value % 1000 != 0)
            {
                throw InMemoryCosmosException.Create(
                    $"The autoscale maximum throughput value of {throughput.Value} must be at least 1000 and a multiple of 1000.",
                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
            }
        }
        else
        {
            if (throughput.Value < 400 || throughput.Value % 100 != 0)
            {
                throw InMemoryCosmosException.Create(
                    $"The throughput value of {throughput.Value} must be at least 400 and a multiple of 100.",
                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
            }
        }
    }

    public static void ValidateMaxItemCount(QueryRequestOptions? requestOptions)
    {
        if (requestOptions?.MaxItemCount == 0)
        {
            throw InMemoryCosmosException.Create(
                "MaxItemCount must be a positive value or -1.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), 0);
        }
    }

    public static int ParseContinuationToken(string? continuationToken)
    {
        if (continuationToken is null)
            return 0;

        if (int.TryParse(continuationToken, out var offset))
            return offset;

        throw InMemoryCosmosException.Create(
            $"Invalid continuation token '{continuationToken}'.",
            HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), 0);
    }

    public static void ValidatePatchPaths(
        IReadOnlyList<PatchOperation> operations,
        IReadOnlyList<string> partitionKeyPaths,
        ContainerProperties? containerProperties)
    {
        foreach (var op in operations)
        {
            var path = op.Path;
            if (ReservedSystemPatchPaths.Contains(path))
            {
                throw InMemoryCosmosException.Create(
                    $"Cannot patch system property '{path}'.",
                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
            }

            if (string.Equals(path, "/id", StringComparison.OrdinalIgnoreCase))
            {
                throw InMemoryCosmosException.Create(
                    "Cannot patch the 'id' field.",
                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
            }

            if (partitionKeyPaths is { Count: > 0 })
            {
                foreach (var pkPath in partitionKeyPaths)
                {
                    if (string.Equals(path, pkPath, StringComparison.OrdinalIgnoreCase))
                    {
                        throw InMemoryCosmosException.Create(
                            "Cannot patch the partition key field.",
                            HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                    }
                }
            }

            var cps = containerProperties?.ComputedProperties;
            if (cps is { Count: > 0 })
            {
                foreach (var cp in cps)
                {
                    if (string.Equals(path, "/" + cp.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        throw InMemoryCosmosException.Create(
                            "Cannot patch a computed property path.",
                            HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                    }
                }
            }
        }
    }

    public static void ValidateContainerReplace(ContainerProperties existingProperties, ContainerProperties newProperties)
    {
        // Real Cosmos DB rejects partition key path changes
        var existingPkPath = existingProperties.PartitionKeyPath;
        var newPkPath = newProperties.PartitionKeyPath;
        if (!string.IsNullOrEmpty(newPkPath) && !string.IsNullOrEmpty(existingPkPath) && newPkPath != existingPkPath)
        {
            throw InMemoryCosmosException.Create(
                "Partition key paths for a container cannot be changed.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }
    }

    public static void EnsureEtagExcludedPath(IndexingPolicy policy)
    {
        const string etagPath = "/\"_etag\"/?";
        if (!policy.ExcludedPaths.Any(p => p.Path == etagPath))
            policy.ExcludedPaths.Add(new ExcludedPath { Path = etagPath });
    }

    public static void ValidateComputedProperties(ContainerProperties properties)
    {
        var cps = properties.ComputedProperties;
        if (cps is null or { Count: 0 }) return;

        if (cps.Count > 20)
        {
            throw InMemoryCosmosException.Create(
                "A container can have at most 20 computed properties.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), 0);
        }

        // Two-pass: collect all names first for cross-CP reference check (order-independent)
        var allNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cp in cps)
        {
            if (!allNames.Add(cp.Name))
            {
                throw InMemoryCosmosException.Create(
                    $"Duplicate computed property name '{cp.Name}'.",
                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), 0);
            }
        }

        foreach (var cp in cps)
        {
            if (ReservedComputedPropertyNames.Contains(cp.Name))
            {
                throw InMemoryCosmosException.Create(
                    $"Computed property name '{cp.Name}' is a reserved system property name.",
                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), 0);
            }

            if (string.IsNullOrWhiteSpace(cp.Query))
            {
                throw InMemoryCosmosException.Create(
                    $"Computed property '{cp.Name}' must have a non-empty query.",
                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), 0);
            }

            // Normalize whitespace for robust checking
            var normalized = Regex.Replace(cp.Query.Trim(), @"\s+", " ");
            if (!normalized.StartsWith("SELECT VALUE", StringComparison.OrdinalIgnoreCase))
            {
                throw InMemoryCosmosException.Create(
                    "Computed property query must use 'SELECT VALUE' syntax.",
                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), 0);
            }

            // Check entire query for prohibited clauses
            foreach (var clause in ProhibitedCpClauses)
            {
                if (normalized.IndexOf(clause, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw InMemoryCosmosException.Create(
                        $"Computed property query cannot contain '{clause.Trim()}' clause.",
                        HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), 0);
                }
            }

            // Check for self-referencing CP
            var selfPattern = @"\." + Regex.Escape(cp.Name) + @"(?![a-zA-Z0-9_])";
            if (Regex.IsMatch(normalized, selfPattern, RegexOptions.IgnoreCase))
            {
                throw InMemoryCosmosException.Create(
                    $"Computed property '{cp.Name}' cannot reference itself.",
                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), 0);
            }

            // Check for cross-CP references (all other CP names, order-independent)
            foreach (var otherName in allNames)
            {
                if (otherName.Equals(cp.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var crossPattern = @"\." + Regex.Escape(otherName) + @"(?![a-zA-Z0-9_])";
                if (Regex.IsMatch(normalized, crossPattern, RegexOptions.IgnoreCase))
                {
                    throw InMemoryCosmosException.Create(
                        $"Computed property '{cp.Name}' cannot reference another computed property '{otherName}'.",
                        HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), 0);
                }
            }
        }
    }
}
