#nullable enable
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// Options for <see cref="ServiceCollectionExtensions.UseInMemoryCosmosDB(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{InMemoryCosmosOptions}?)"/>.
/// Configures how CosmosClient and Container registrations are replaced with in-memory equivalents.
/// </summary>
public class InMemoryCosmosOptions
{
    /// <summary>
    /// Container configurations. If empty, the method registers a single default
    /// container with partition key "/id".
    /// </summary>
    public List<ContainerConfig> Containers { get; } = new();

    /// <summary>
    /// The database name to use. Falls back to "in-memory-db" if not specified.
    /// </summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// When true (default), calls <see cref="InMemoryFeedIteratorSetup.Register"/> so that
    /// <c>.ToFeedIteratorOverridable()</c> works for LINQ queries.
    /// </summary>
    public bool RegisterFeedIteratorSetup { get; set; } = true;

    /// <summary>
    /// Callback invoked with the <see cref="CosmosClient"/> after it is created.
    /// </summary>
    public Action<CosmosClient>? OnClientCreated { get; set; }

    /// <summary>
    /// Callback invoked with each <see cref="FakeCosmosHandler"/> after it is created.
    /// </summary>
    public Action<string, FakeCosmosHandler>? OnHandlerCreated { get; set; }

    /// <summary>
    /// Optional function that wraps the final <see cref="HttpMessageHandler"/>
    /// before it is passed to <see cref="CosmosClientOptions.HttpClientFactory"/>.
    /// </summary>
    public Func<HttpMessageHandler, HttpMessageHandler>? HttpMessageHandlerWrapper { get; set; }

    /// <summary>
    /// When set, each container automatically loads its state from this directory on creation
    /// and saves its state back on disposal.
    /// </summary>
    public string? StatePersistenceDirectory { get; set; }

    /// <summary>
    /// Adds a container configuration.
    /// </summary>
    public InMemoryCosmosOptions AddContainer(
        string containerName,
        string partitionKeyPath = "/id",
        string? databaseName = null)
    {
        Containers.Add(new ContainerConfig(containerName, partitionKeyPath, databaseName));
        return this;
    }

    /// <summary>
    /// Adds a container configuration using full <see cref="ContainerProperties"/>.
    /// </summary>
    public InMemoryCosmosOptions AddContainer(
        ContainerProperties containerProperties,
        string? databaseName = null)
    {
        Containers.Add(ContainerConfig.FromContainerProperties(containerProperties, databaseName));
        return this;
    }

    /// <summary>
    /// Sets <see cref="HttpMessageHandlerWrapper"/> to the specified function.
    /// </summary>
    public InMemoryCosmosOptions WithHttpMessageHandlerWrapper(
        Func<HttpMessageHandler, HttpMessageHandler> wrapper)
    {
        HttpMessageHandlerWrapper = wrapper;
        return this;
    }
}
