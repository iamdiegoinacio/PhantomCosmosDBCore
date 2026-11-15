#nullable enable
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// Options for <see cref="ServiceCollectionExtensions.UseInMemoryCosmosContainers"/>.
/// Configures how Container registrations are replaced with in-memory equivalents
/// backed by <see cref="FakeCosmosHandler"/>.
/// </summary>
public class InMemoryContainerOptions
{
    /// <summary>
    /// Container configurations. If empty, a single default container
    /// with partition key "/id" is registered.
    /// </summary>
    public List<ContainerConfig> Containers { get; } = new();

    /// <summary>
    /// The database name to use for the hidden internal <see cref="CosmosClient"/>.
    /// Falls back to "in-memory-db" if not specified.
    /// </summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// When true (default), calls <see cref="InMemoryFeedIteratorSetup.Register"/>.
    /// </summary>
    [Obsolete("FakeCosmosHandler handles .ToFeedIterator() natively. This property is ignored.")]
    public bool RegisterFeedIteratorSetup { get; set; } = true;

    /// <summary>
    /// Callback invoked with each container's <see cref="IContainerTestSetup"/> after it is created.
    /// </summary>
    public Action<IContainerTestSetup>? OnContainerCreated { get; set; }

    /// <summary>
    /// Callback invoked with each <see cref="FakeCosmosHandler"/> after it is created.
    /// </summary>
    public Action<string, FakeCosmosHandler>? OnHandlerCreated { get; set; }

    /// <summary>
    /// Optional function that wraps the final <see cref="HttpMessageHandler"/>
    /// before it is passed to the internal <see cref="CosmosClient"/>.
    /// </summary>
    public Func<HttpMessageHandler, HttpMessageHandler>? HttpMessageHandlerWrapper { get; private set; }

    /// <summary>
    /// When set, each container automatically loads its state from this directory on creation
    /// and saves its state back on disposal.
    /// </summary>
    public string? StatePersistenceDirectory { get; set; }

    /// <summary>
    /// Sets a function that wraps the final <see cref="HttpMessageHandler"/> before it is
    /// passed to the internal <see cref="CosmosClient"/>.
    /// </summary>
    public InMemoryContainerOptions WithHttpMessageHandlerWrapper(
        Func<HttpMessageHandler, HttpMessageHandler> wrapper)
    {
        HttpMessageHandlerWrapper = wrapper;
        return this;
    }

    /// <summary>
    /// Adds a container configuration.
    /// </summary>
    public InMemoryContainerOptions AddContainer(
        string containerName = "in-memory-container",
        string partitionKeyPath = "/id")
    {
        Containers.Add(new ContainerConfig(containerName, partitionKeyPath));
        return this;
    }

    /// <summary>
    /// Adds a container configuration using full <see cref="ContainerProperties"/>.
    /// </summary>
    public InMemoryContainerOptions AddContainer(ContainerProperties containerProperties)
    {
        Containers.Add(ContainerConfig.FromContainerProperties(containerProperties));
        return this;
    }
}
