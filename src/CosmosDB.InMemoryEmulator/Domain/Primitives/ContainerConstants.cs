#nullable enable

namespace CosmosDB.InMemoryEmulator.Domain.Primitives;

/// <summary>
/// Domain and protocol constants enforced by the in-memory Cosmos DB container.
/// </summary>
internal static class ContainerConstants
{
    public const int MaxDocumentSizeBytes = 2 * 1024 * 1024;
    public const int MaxPartitionKeySizeBytes = 2048;
    public const int MaxComputedPropertiesCount = 20;
    public const int MaxPatchOperationsCount = 10;
    public const int RegexCacheMaxSize = 256;
    public const double SyntheticRequestCharge = 1.0;
    public const double EarthRadiusMeters = 6_371_000.0;
    public static readonly TimeSpan StoredProcedureTimeout = TimeSpan.FromSeconds(10);
}
