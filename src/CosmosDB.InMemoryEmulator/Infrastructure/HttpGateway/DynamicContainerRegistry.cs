#nullable enable
using System.Collections.Concurrent;
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator.Infrastructure.HttpGateway;

/// <summary>
/// Shared registry for dynamic container management. Holds the mutable dictionaries
/// that both <see cref="CosmosRoutingMessageHandler"/> and <see cref="InMemoryCosmosResult"/> reference.
/// </summary>
internal sealed class DynamicContainerRegistry
{
    public ConcurrentDictionary<string, FakeCosmosHandler> Handlers { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, InMemoryContainer> BackingContainers { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, Container> SdkContainers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> KnownDatabases { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Maps database name → (container name → registry key).
    /// Registry keys are plain container names for default DB, compound "db/container" for explicit databases.
    /// </summary>
    public ConcurrentDictionary<string, ConcurrentDictionary<string, string>> DatabaseContainerKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Set of database names that use compound registry keys (added via AddDatabase).</summary>
    public HashSet<string> CompoundKeyDatabases { get; } = new(StringComparer.Ordinal);

    /// <summary>Client reference set after client creation so dynamic containers can register SDK containers.</summary>
    public CosmosClient? Client { get; set; }

    /// <summary>Database name used for dynamic container registration.</summary>
    public string DatabaseName { get; set; } = "default";
}
