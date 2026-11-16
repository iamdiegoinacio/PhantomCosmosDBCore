#nullable enable
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Infrastructure.HttpGateway;

/// <summary>
/// Generates Cosmos DB HTTP Gateway metadata payloads (Account, Database, Collection,
/// PartitionKeyRanges, Offers) and deterministic Resource IDs (RIDs).
/// </summary>
internal static class CosmosGatewayMetadataGenerator
{
    private static int _ridCounter;
    public const string PkRangesEtag = "\"pk-etag-1\"";

    public const string AccountMetadata = """
        {
            "id": "fake-account",
            "_rid": "",
            "databasesLink": "/dbs/",
            "mediaLink": "/media/",
            "addressesLink": "",
            "userConsistencyPolicy": { "defaultConsistencyLevel": "Session" },
            "writableLocations": [
                { "name": "East US", "databaseAccountEndpoint": "https://localhost:9999/" }
            ],
            "readableLocations": [
                { "name": "East US", "databaseAccountEndpoint": "https://localhost:9999/" }
            ],
            "systemReplicationPolicy": { "minReplicaSetSize": 1, "maxReplicaCount": 4 },
            "readPolicy": { "primaryReadCoefficient": 1, "secondaryReadCoefficient": 1 },
            "queryEngineConfiguration": "{\"maxSqlQueryInputLength\":262144,\"maxJoinsPerSqlQuery\":5,\"maxLogicalAndPerSqlQuery\":500,\"maxLogicalOrPerSqlQuery\":500,\"maxUdfRefPerSqlQuery\":10,\"maxInExpressionItemsCount\":16000,\"queryMaxInMemorySortDocumentCount\":500,\"maxQueryRequestTimeoutFraction\":0.9,\"sqlAllowNonFiniteNumbers\":false,\"sqlAllowAggregateFunctions\":true,\"sqlAllowSubQuery\":true,\"sqlAllowScalarSubQuery\":true,\"allowNewKeywords\":true,\"sqlAllowLike\":true,\"sqlAllowGroupByClause\":true,\"maxSpatialQueryCells\":12,\"spatialMaxGeometryPointCount\":256,\"sqlDisableOptimizationFlags\":0,\"sqlAllowTop\":true,\"enableSpatialIndexing\":true}"
        }
        """;

    public static (string DbRid, string CollRid) GenerateResourceIds(string containerId)
    {
        var instanceId = (uint)Interlocked.Increment(ref _ridCounter);
        var dbBytes = BitConverter.GetBytes(instanceId);
        var containerHash = PartitionKeyHash.MurmurHash3(containerId);
        var collBytes = new byte[8];
        Buffer.BlockCopy(dbBytes, 0, collBytes, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(containerHash), 0, collBytes, 4, 4);
        return (Convert.ToBase64String(dbBytes), Convert.ToBase64String(collBytes));
    }

    public static string GetDatabaseMetadata(string databaseName, string databaseRid)
    {
        var metadata = new JObject
        {
            ["id"] = databaseName,
            ["_rid"] = databaseRid,
            ["_self"] = $"dbs/{databaseRid}/",
            ["_etag"] = "\"00000000-0000-0000-0000-000000000000\"",
            ["_colls"] = "colls/",
            ["_users"] = "users/",
            ["_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        return metadata.ToString(Formatting.None);
    }

    public static string GetCollectionMetadata(
        InMemoryContainer container,
        string databaseRid,
        string collectionRid)
    {
        var paths = new JArray(container.PartitionKeyPaths.Select(path => (JToken)path));
        var policy = container.IndexingPolicy;
        var includedPaths = new JArray(policy.IncludedPaths.Select(p => new JObject { ["path"] = p.Path }));
        var excludedPaths = new JArray(policy.ExcludedPaths.Select(p => new JObject { ["path"] = p.Path }));
        if (!excludedPaths.Any(p => p["path"]?.ToString() == "/\"_etag\"/?"))
            excludedPaths.Add(new JObject { ["path"] = "/\"_etag\"/?" });

        var indexingPolicyObj = new JObject
        {
            ["indexingMode"] = policy.IndexingMode.ToString().ToLowerInvariant(),
            ["automatic"] = policy.Automatic,
            ["includedPaths"] = includedPaths,
            ["excludedPaths"] = excludedPaths
        };

        if (policy.CompositeIndexes.Count > 0)
        {
            var compositeIndexes = new JArray(policy.CompositeIndexes.Select(indexSet =>
                new JArray(indexSet.Select(idx => new JObject
                {
                    ["path"] = idx.Path,
                    ["order"] = idx.Order.ToString().ToLowerInvariant()
                }))));
            indexingPolicyObj["compositeIndexes"] = compositeIndexes;
        }

        if (policy.SpatialIndexes.Count > 0)
        {
            var spatialIndexes = new JArray(policy.SpatialIndexes.Select(si =>
            {
                var obj = new JObject { ["path"] = si.Path };
                if (si.SpatialTypes.Count > 0)
                    obj["types"] = new JArray(si.SpatialTypes.Select(t => (JToken)t.ToString()));
                return obj;
            }));
            indexingPolicyObj["spatialIndexes"] = spatialIndexes;
        }

        var metadata = new JObject
        {
            ["id"] = container.Id,
            ["_rid"] = collectionRid,
            ["_self"] = $"dbs/{databaseRid}/colls/{collectionRid}/",
            ["_etag"] = "\"00000000-0000-0000-0000-000000000000\"",
            ["_ts"] = 1700000000,
            ["partitionKey"] = new JObject
            {
                ["paths"] = paths,
                ["kind"] = paths.Count > 1 ? "MultiHash" : "Hash",
                ["version"] = 2
            },
            ["indexingPolicy"] = indexingPolicyObj,
            ["geospatialConfig"] = new JObject { ["type"] = "Geography" }
        };

        if (container.DefaultTimeToLive.HasValue)
        {
            metadata["defaultTtl"] = container.DefaultTimeToLive.Value;
        }

        var uniqueKeys = container.UniqueKeyPolicy?.UniqueKeys;
        if (uniqueKeys is { Count: > 0 })
        {
            metadata["uniqueKeyPolicy"] = new JObject
            {
                ["uniqueKeys"] = new JArray(uniqueKeys.Select(uk => new JObject
                {
                    ["paths"] = new JArray(uk.Paths.Select(p => (JToken)p))
                }))
            };
        }

        var computedProperties = container.ContainerPropertiesInternal.ComputedProperties;
        if (computedProperties is { Count: > 0 })
        {
            metadata["computedProperties"] = new JArray(computedProperties.Select(cp => new JObject
            {
                ["name"] = cp.Name,
                ["query"] = cp.Query
            }));
        }

        return metadata.ToString(Formatting.None);
    }

    public static string GetPartitionKeyRanges(
        string databaseRid,
        string collectionRid,
        int partitionKeyRangeCount)
    {
        var ranges = new JArray();
        var step = 0x1_0000_0000L / partitionKeyRangeCount;
        for (var i = 0; i < partitionKeyRangeCount; i++)
        {
            var minInclusive = PartitionKeyHash.RangeBoundaryToHex(i * step);
            var maxExclusive = i == partitionKeyRangeCount - 1 ? "FF" : PartitionKeyHash.RangeBoundaryToHex((i + 1) * step);
            ranges.Add(new JObject
            {
                ["id"] = i.ToString(),
                ["_rid"] = collectionRid,
                ["minInclusive"] = minInclusive,
                ["maxExclusive"] = maxExclusive,
                ["throughputFraction"] = 1.0 / partitionKeyRangeCount,
                ["status"] = "online",
                ["_self"] = $"dbs/{databaseRid}/colls/{collectionRid}/pkranges/{i}/",
                ["_ts"] = 1700000000,
                ["_etag"] = PkRangesEtag.Trim('"')
            });
        }

        var result = new JObject
        {
            ["_rid"] = collectionRid,
            ["PartitionKeyRanges"] = ranges,
            ["_count"] = partitionKeyRangeCount
        };
        return result.ToString(Formatting.None);
    }

    public static HttpResponseMessage HandlePartitionKeyRanges(
        HttpRequestMessage request,
        string databaseRid,
        string collectionRid,
        int partitionKeyRangeCount,
        string sessionToken)
    {
        if (request.Headers.IfNoneMatch.Any(etag => etag.Tag == PkRangesEtag))
        {
            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModified.Headers.Add("x-ms-request-charge", "0");
            notModified.Headers.Add("x-ms-activity-id", Guid.NewGuid().ToString());
            notModified.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(PkRangesEtag);
            return notModified;
        }

        var json = GetPartitionKeyRanges(databaseRid, collectionRid, partitionKeyRangeCount);
        var response = CosmosHttpHeaders.CreateJsonResponse(json, sessionToken);
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(PkRangesEtag);
        return response;
    }

    public static HttpResponseMessage HandleOffersRequest(
        InMemoryContainer container,
        string collectionRid)
    {
        var offer = new JObject
        {
            ["offerVersion"] = "V2",
            ["offerType"] = "Invalid",
            ["content"] = new JObject
            {
                ["offerThroughput"] = container._throughput,
                ["offerIsRUPerMinuteThroughputEnabled"] = false
            },
            ["offerResourceId"] = collectionRid,
            ["id"] = "offer-" + collectionRid,
            ["_rid"] = "offer-" + collectionRid,
            ["_self"] = "offers/offer-" + collectionRid + "/",
            ["_etag"] = $"\"{Guid.NewGuid()}\"",
            ["_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var wrapper = new JObject
        {
            ["_rid"] = "",
            ["Offers"] = new JArray(offer),
            ["_count"] = 1
        };

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(wrapper.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-ms-request-charge", "1");
        response.Headers.Add("x-ms-activity-id", Guid.NewGuid().ToString());
        return response;
    }
}
