#nullable enable
using Newtonsoft.Json;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// Configuration options for <see cref="FakeCosmosHandler"/>.
/// </summary>
public sealed class FakeCosmosHandlerOptions
{
    /// <summary>
    /// How long query result cache entries remain valid before eviction.
    /// Defaults to 5 minutes.
    /// </summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of query result cache entries before overflow eviction.
    /// Defaults to 100.
    /// </summary>
    public int CacheMaxEntries { get; init; } = 100;

    /// <summary>
    /// Number of partition key ranges to expose. The SDK sends a separate query
    /// per range; the handler serves the same data regardless. Increasing this
    /// exercises cross-partition fan-out paths in the SDK.
    /// Defaults to 1.
    /// </summary>
    public int PartitionKeyRangeCount { get; init; } = 1;

    /// <summary>
    /// Strategy for building query plan responses. Override to customise or fix
    /// query plan generation if a new SDK version changes the expected format.
    /// Defaults to <see cref="DefaultQueryPlanStrategy"/>.
    /// </summary>
    public IQueryPlanStrategy QueryPlanStrategy { get; init; } = new DefaultQueryPlanStrategy();

    /// <summary>
    /// Strategy for resolving HybridRow batch schemas. Override to customise
    /// batch schema resolution if a new SDK version changes the batch wire format.
    /// Defaults to <see cref="DefaultBatchSchemaStrategy"/>.
    /// </summary>
    public IBatchSchemaStrategy BatchSchemaStrategy { get; init; } = new DefaultBatchSchemaStrategy();
}

/// <summary>
/// Document type used by <see cref="FakeCosmosHandler.VerifySdkCompatibilityAsync"/>
/// to validate SDK compatibility. Public because NSubstitute proxy generation
/// requires accessible types.
/// </summary>
public sealed class CompatibilityDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = "";

    [JsonProperty("partitionKey")]
    public string PartitionKey { get; set; } = "";

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("value")]
    public int Value { get; set; }
}
