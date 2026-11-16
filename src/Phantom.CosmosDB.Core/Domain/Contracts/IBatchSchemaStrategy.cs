#nullable enable
namespace Phantom.CosmosDB.Core;

/// <summary>
/// Strategy for resolving HybridRow batch schemas used by the transactional batch
/// wire protocol. The Cosmos SDK encodes batch operations in HybridRow format with
/// specific schema IDs.
/// </summary>
public interface IBatchSchemaStrategy
{
    /// <summary>
    /// Returns true if batch schema resolution succeeded and batch operations are supported.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// A human-readable message describing why batch schemas are unavailable,
    /// or null if they are available.
    /// </summary>
    string? UnavailableReason { get; }
}
