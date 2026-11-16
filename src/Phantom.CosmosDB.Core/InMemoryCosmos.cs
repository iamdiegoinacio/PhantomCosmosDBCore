#nullable enable
using Phantom.CosmosDB.Core.Application.Configuration;
using Microsoft.Azure.Cosmos;

namespace Phantom.CosmosDB.Core;

/// <summary>
/// Entry point for creating in-memory Cosmos DB test infrastructure.
/// Use <see cref="Create(string, string, Func{HttpMessageHandler, HttpMessageHandler}?, Action{CosmosClientOptions}?, Action{IContainerTestSetup}?)"/>
/// for single-container setups (the 80% case) or <see cref="Builder"/> for multi-container/advanced scenarios.
/// </summary>
public static class InMemoryCosmos
{
    /// <summary>
    /// The default database name used by <see cref="Create(string, string, Func{HttpMessageHandler, HttpMessageHandler}?, Action{CosmosClientOptions}?, Action{IContainerTestSetup}?)"/>
    /// and <see cref="InMemoryCosmosBuilder.AddContainer(string, string, Action{IContainerTestSetup}?)"/>.
    /// Value is <c>"default"</c>.
    /// </summary>
    public const string DefaultDatabaseName = "default";

    /// <summary>
    /// Creates a single-container in-memory Cosmos setup with a single partition key path.
    /// </summary>
    public static InMemoryCosmosResult Create(
        string containerName,
        string partitionKeyPath = "/id",
        Func<HttpMessageHandler, HttpMessageHandler>? wrapHandler = null,
        Action<CosmosClientOptions>? configureOptions = null,
        Action<IContainerTestSetup>? configureContainer = null)
    {
        CosmosSetupValidator.ValidateContainerName(containerName);
        CosmosSetupValidator.ValidatePartitionKeyPath(partitionKeyPath);

        return Builder()
            .AddContainer(containerName, partitionKeyPath, configureContainer)
            .ApplyWrapHandler(wrapHandler)
            .ApplyConfigureOptions(configureOptions)
            .Build(singleContainerName: containerName);
    }

    /// <summary>
    /// Creates a single-container in-memory Cosmos setup with hierarchical (composite) partition key paths.
    /// </summary>
    public static InMemoryCosmosResult Create(
        string containerName,
        string[] partitionKeyPaths,
        Func<HttpMessageHandler, HttpMessageHandler>? wrapHandler = null,
        Action<CosmosClientOptions>? configureOptions = null,
        Action<IContainerTestSetup>? configureContainer = null)
    {
        CosmosSetupValidator.ValidateContainerName(containerName);
        CosmosSetupValidator.ValidatePartitionKeyPaths(partitionKeyPaths);

        return Builder()
            .AddContainer(containerName, partitionKeyPaths, configureContainer)
            .ApplyWrapHandler(wrapHandler)
            .ApplyConfigureOptions(configureOptions)
            .Build(singleContainerName: containerName);
    }

    /// <summary>
    /// Returns a builder for multi-container or advanced configuration.
    /// </summary>
    public static InMemoryCosmosBuilder Builder() => new();
}
