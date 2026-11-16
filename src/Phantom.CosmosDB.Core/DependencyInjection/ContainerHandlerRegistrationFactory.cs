#nullable enable
using Microsoft.Azure.Cosmos;

namespace Phantom.CosmosDB.Core.DependencyInjection;

/// <summary>
/// Centralizes the provisioning of <see cref="InMemoryContainer"/> instances, state persistence
/// wiring, <see cref="FakeCosmosHandler"/> registration, and <see cref="HttpMessageHandler"/> pipeline
/// construction for Dependency Injection extensions.
/// </summary>
internal static class ContainerHandlerRegistrationFactory
{
    public const string DefaultDatabaseName = "in-memory-db";
    public const string DefaultContainerName = "in-memory-container";

    public static (Dictionary<string, FakeCosmosHandler> Handlers, HttpMessageHandler FinalHandler) CreateHandlersAndPipeline(
        IReadOnlyList<ContainerConfig> containerConfigs,
        string defaultDatabaseName,
        string? statePersistenceDirectory,
        bool includeDatabasePrefixInStateFile,
        Action<IContainerTestSetup>? onContainerCreated,
        Action<string, FakeCosmosHandler>? onHandlerCreated,
        Func<HttpMessageHandler, HttpMessageHandler>? httpMessageHandlerWrapper)
    {
        var handlers = new Dictionary<string, FakeCosmosHandler>();

        foreach (var config in containerConfigs)
        {
            var container = config.ContainerProperties is not null
                ? new InMemoryContainer(config.ContainerProperties)
                : new InMemoryContainer(config.ContainerName, config.PartitionKeyPath);

            if (config.Throughput.HasValue)
            {
                Domain.Validation.DocumentValidator.ValidateThroughput(config.Throughput);
                container._throughput = config.Throughput.Value;
            }

            if (statePersistenceDirectory is not null)
            {
                var dbName = config.DatabaseName ?? defaultDatabaseName;
                var fileName = includeDatabasePrefixInStateFile
                    ? $"{dbName}_{config.ContainerName}.json"
                    : $"{config.ContainerName}.json";
                container.StateFilePath = Path.Combine(statePersistenceDirectory, fileName);
                container.LoadPersistedState();
            }

            onContainerCreated?.Invoke(container);

            var handler = new FakeCosmosHandler(container);
            if (includeDatabasePrefixInStateFile && config.DatabaseName is not null)
            {
                handlers[$"{config.DatabaseName}/{config.ContainerName}"] = handler;
            }

            if (!handlers.ContainsKey(config.ContainerName))
            {
                handlers[config.ContainerName] = handler;
            }

            onHandlerCreated?.Invoke(config.ContainerName, handler);
        }

        HttpMessageHandler httpHandler = handlers.Count == 1
            ? handlers.Values.First()
            : FakeCosmosHandler.CreateRouter(handlers);

        var finalHandler = httpMessageHandlerWrapper?.Invoke(httpHandler) ?? httpHandler;
        return (handlers, finalHandler);
    }

    public static CosmosClient CreateWrappedGatewayClient(
        HttpMessageHandler finalHandler,
        IReadOnlyDictionary<string, FakeCosmosHandler> handlers)
    {
        var innerClient = new CosmosClient(
            FakeCosmosHandler.FakeConnectionString,
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                HttpClientFactory = () => new HttpClient(finalHandler)
            });
        return FakeCosmosHandler.WrapClient(innerClient, handlers);
    }
}
