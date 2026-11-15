#nullable enable
using CosmosDB.InMemoryEmulator.DependencyInjection;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// Extension methods for replacing Cosmos DB service registrations with in-memory equivalents.
/// Designed to be called from <c>ConfigureTestServices</c> inside <c>WebApplicationFactory</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Replaces all registered <see cref="CosmosClient"/> and <see cref="Container"/>
    /// instances in the service collection with in-memory equivalents backed by
    /// <see cref="FakeCosmosHandler"/>.
    /// </summary>
    public static IServiceCollection UseInMemoryCosmosDB(
        this IServiceCollection services,
        Action<InMemoryCosmosOptions>? configure = null)
    {
        var options = new InMemoryCosmosOptions();
        configure?.Invoke(options);

        var databaseName = options.DatabaseName ?? ContainerHandlerRegistrationFactory.DefaultDatabaseName;

        var existingContainerDescriptors = services
            .Where(d => d.ServiceType == typeof(Container))
            .ToList();
        var containerLifetime = existingContainerDescriptors.FirstOrDefault()?.Lifetime
                                ?? ServiceLifetime.Singleton;

        services.RemoveAll<CosmosClient>();

        List<ContainerConfig> containerConfigs;
        if (options.Containers.Count > 0)
        {
            containerConfigs = options.Containers;
            services.RemoveAll<Container>();
        }
        else
        {
            containerConfigs = [new ContainerConfig(ContainerHandlerRegistrationFactory.DefaultContainerName)];
        }

        var (handlers, finalHandler) = ContainerHandlerRegistrationFactory.CreateHandlersAndPipeline(
            containerConfigs,
            databaseName,
            options.StatePersistenceDirectory,
            includeDatabasePrefixInStateFile: true,
            onContainerCreated: null,
            options.OnHandlerCreated,
            options.HttpMessageHandlerWrapper);

        var client = ContainerHandlerRegistrationFactory.CreateWrappedGatewayClient(finalHandler, handlers);
        options.OnClientCreated?.Invoke(client);

        if (options.Containers.Count > 0 || existingContainerDescriptors.Count == 0)
        {
            foreach (var config in containerConfigs)
            {
                var dbName = config.DatabaseName ?? databaseName;
                var containerName = config.ContainerName;
                services.Add(new ServiceDescriptor(
                    typeof(Container),
                    _ => client.GetContainer(dbName, containerName),
                    containerLifetime));
            }
        }

        services.AddSingleton<CosmosClient>(client);
        return services;
    }

    /// <summary>
    /// Replaces all registered <see cref="Container"/> instances in the service collection
    /// with in-memory equivalents backed by <see cref="FakeCosmosHandler"/>.
    /// Does NOT replace <see cref="CosmosClient"/>.
    /// </summary>
    public static IServiceCollection UseInMemoryCosmosContainers(
        this IServiceCollection services,
        Action<InMemoryContainerOptions>? configure = null)
    {
        var options = new InMemoryContainerOptions();
        configure?.Invoke(options);

        var databaseName = options.DatabaseName ?? ContainerHandlerRegistrationFactory.DefaultDatabaseName;

        var existingDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(Container));
        var lifetime = existingDescriptor?.Lifetime ?? ServiceLifetime.Singleton;

        services.RemoveAll<Container>();

        var containerConfigs = options.Containers.Count > 0
            ? options.Containers
            : [new ContainerConfig(ContainerHandlerRegistrationFactory.DefaultContainerName)];

        var (handlers, finalHandler) = ContainerHandlerRegistrationFactory.CreateHandlersAndPipeline(
            containerConfigs,
            databaseName,
            options.StatePersistenceDirectory,
            includeDatabasePrefixInStateFile: false,
            options.OnContainerCreated,
            options.OnHandlerCreated,
            options.HttpMessageHandlerWrapper);

        var client = ContainerHandlerRegistrationFactory.CreateWrappedGatewayClient(finalHandler, handlers);

        foreach (var config in containerConfigs)
        {
            var containerName = config.ContainerName;
            services.Add(new ServiceDescriptor(
                typeof(Container),
                _ => client.GetContainer(databaseName, containerName),
                lifetime));
        }

        return services;
    }

    /// <summary>
    /// Replaces a typed <typeparamref name="TClient"/> registration with an in-memory equivalent
    /// backed by <see cref="FakeCosmosHandler"/> using a Castle.Core dynamic proxy.
    /// </summary>
    public static IServiceCollection UseInMemoryCosmosDB<TClient>(
        this IServiceCollection services,
        Action<InMemoryCosmosOptions>? configure = null)
        where TClient : CosmosClient
    {
        var options = new InMemoryCosmosOptions();
        configure?.Invoke(options);

        var databaseName = options.DatabaseName ?? ContainerHandlerRegistrationFactory.DefaultDatabaseName;

        var existingDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TClient));
        var lifetime = existingDescriptor?.Lifetime ?? ServiceLifetime.Singleton;

        services.RemoveAll<TClient>();

        var containerConfigs = options.Containers.Count > 0
            ? options.Containers
            : [new ContainerConfig(ContainerHandlerRegistrationFactory.DefaultContainerName)];

        var (handlers, finalHandler) = ContainerHandlerRegistrationFactory.CreateHandlersAndPipeline(
            containerConfigs,
            databaseName,
            options.StatePersistenceDirectory,
            includeDatabasePrefixInStateFile: true,
            onContainerCreated: null,
            options.OnHandlerCreated,
            options.HttpMessageHandlerWrapper);

        var client = TypedCosmosClientProxyFactory.CreateProxy<TClient>(finalHandler, handlers);
        options.OnClientCreated?.Invoke(client);

        services.Add(new ServiceDescriptor(typeof(TClient), _ => client, lifetime));
        return services;
    }
}
