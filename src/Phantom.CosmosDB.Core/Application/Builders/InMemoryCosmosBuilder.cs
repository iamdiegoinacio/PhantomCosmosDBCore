#nullable enable
using Phantom.CosmosDB.Core.Application.Configuration;
using Phantom.CosmosDB.Core.Infrastructure.HttpGateway;
using Microsoft.Azure.Cosmos;

namespace Phantom.CosmosDB.Core;

/// <summary>
/// Fluent builder for creating multi-container in-memory Cosmos setups.
/// </summary>
public sealed class InMemoryCosmosBuilder
{
    private readonly List<ContainerSpec> _containers = new();
    private readonly List<DatabaseSpec> _databases = new();
    private readonly List<Func<HttpMessageHandler, HttpMessageHandler>> _wrapHandlers = new();
    private readonly List<Action<CosmosClientOptions>> _configureOptions = new();

    internal InMemoryCosmosBuilder() { }

    /// <summary>
    /// Adds a container with a single partition key path.
    /// </summary>
    public InMemoryCosmosBuilder AddContainer(
        string name,
        string partitionKeyPath,
        Action<IContainerTestSetup>? configure = null)
    {
        CosmosSetupValidator.ValidateContainerName(name);
        CosmosSetupValidator.ValidatePartitionKeyPath(partitionKeyPath);
        ValidateNoDuplicate(name);
        _containers.Add(new ContainerSpec(name, [partitionKeyPath], configure));
        return this;
    }

    /// <summary>
    /// Adds a container with hierarchical (composite) partition key paths.
    /// </summary>
    public InMemoryCosmosBuilder AddContainer(
        string name,
        string[] partitionKeyPaths,
        Action<IContainerTestSetup>? configure = null)
    {
        CosmosSetupValidator.ValidateContainerName(name);
        CosmosSetupValidator.ValidatePartitionKeyPaths(partitionKeyPaths);
        ValidateNoDuplicate(name);
        _containers.Add(new ContainerSpec(name, partitionKeyPaths, configure));
        return this;
    }

    /// <summary>
    /// Adds a named database with its own containers. Use this when a single
    /// <see cref="CosmosClient"/> talks to multiple databases that may have
    /// overlapping container names (e.g. users-db/events and orders-db/events).
    /// </summary>
    public InMemoryCosmosBuilder AddDatabase(
        string databaseName,
        Action<InMemoryDatabaseBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("Database name must not be null, empty, or whitespace.", nameof(databaseName));
        if (_databases.Any(d => d.Name == databaseName))
            throw new InvalidOperationException($"Database '{databaseName}' has already been added.");

        var builder = new InMemoryDatabaseBuilder();
        configure(builder);

        if (builder.Containers.Count == 0)
            throw new InvalidOperationException($"Database '{databaseName}' must have at least one container.");

        _databases.Add(new DatabaseSpec(databaseName, builder.Containers.ToList()));
        return this;
    }

    /// <summary>
    /// Adds an HTTP handler wrapper. Multiple calls compose: <c>WrapHandler(a).WrapHandler(b)</c>
    /// produces <c>b(a(handler))</c>.
    /// </summary>
    public InMemoryCosmosBuilder WrapHandler(Func<HttpMessageHandler, HttpMessageHandler> wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        _wrapHandlers.Add(wrapper);
        return this;
    }

    /// <summary>
    /// Configures <see cref="CosmosClientOptions"/>. Multiple calls run sequentially.
    /// Do not set <see cref="CosmosClientOptions.HttpClientFactory"/> — use
    /// <see cref="WrapHandler"/> instead.
    /// </summary>
    public InMemoryCosmosBuilder ConfigureOptions(Action<CosmosClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureOptions.Add(configure);
        return this;
    }

    internal InMemoryCosmosBuilder ApplyWrapHandler(Func<HttpMessageHandler, HttpMessageHandler>? wrapper)
    {
        if (wrapper is not null)
            _wrapHandlers.Add(wrapper);
        return this;
    }

    internal InMemoryCosmosBuilder ApplyConfigureOptions(Action<CosmosClientOptions>? configure)
    {
        if (configure is not null)
            _configureOptions.Add(configure);
        return this;
    }

    /// <summary>
    /// Builds the in-memory Cosmos setup. Can be called multiple times — each call
    /// produces an independent result with fresh containers and handlers.
    /// </summary>
    public InMemoryCosmosResult Build() => Build(singleContainerName: null);

    internal InMemoryCosmosResult Build(string? singleContainerName)
    {
        var totalContainers = _containers.Count + _databases.Sum(d => d.Containers.Count);
        if (totalContainers == 0)
            throw new InvalidOperationException("At least one container must be added before calling Build().");

        var registry = new DynamicContainerRegistry
        {
            DatabaseName = InMemoryCosmos.DefaultDatabaseName
        };

        var handlers = new Dictionary<string, FakeCosmosHandler>(StringComparer.Ordinal);
        var configureCallbacks = new List<(string RegistryKey, Action<IContainerTestSetup>? Configure)>();

        if (_containers.Count > 0)
        {
            var defaultDbMap = registry.DatabaseContainerKeys.GetOrAdd(
                InMemoryCosmos.DefaultDatabaseName, _ => new(StringComparer.Ordinal));

            foreach (var spec in _containers)
            {
                var container = CreateContainer(spec);
                var handler = new FakeCosmosHandler(container);
                handlers[spec.Name] = handler;
                registry.BackingContainers[spec.Name] = container;
                defaultDbMap[spec.Name] = spec.Name;
                configureCallbacks.Add((spec.Name, spec.Configure));
            }
        }

        foreach (var dbSpec in _databases)
        {
            var dbMap = registry.DatabaseContainerKeys.GetOrAdd(
                dbSpec.Name, _ => new(StringComparer.Ordinal));
            registry.CompoundKeyDatabases.Add(dbSpec.Name);

            foreach (var spec in dbSpec.Containers)
            {
                var registryKey = $"{dbSpec.Name}/{spec.Name}";
                var container = CreateContainer(spec);
                var handler = new FakeCosmosHandler(container);
                handlers[registryKey] = handler;
                registry.BackingContainers[registryKey] = container;
                dbMap[spec.Name] = registryKey;
                configureCallbacks.Add((registryKey, spec.Configure));
            }
        }

        HttpMessageHandler httpHandler = new CosmosRoutingMessageHandler(handlers, registry);

        foreach (var wrapper in _wrapHandlers)
            httpHandler = wrapper(httpHandler);

        var options = new CosmosClientOptions();
        foreach (var configure in _configureOptions)
            configure(options);

        if (options.HttpClientFactory is not null)
            throw new InvalidOperationException(
                "Do not set HttpClientFactory directly — use the wrapHandler parameter to customize " +
                "the HTTP pipeline. HttpClientFactory is managed internally by InMemoryCosmos.");

        options.ConnectionMode = ConnectionMode.Gateway;
        options.LimitToEndpoint = true;
        var capturedHandler = httpHandler;
        options.HttpClientFactory = () => new HttpClient(capturedHandler);

        var innerClient = new CosmosClient(
            FakeCosmosHandler.FakeConnectionString,
            options);

        var client = FakeCosmosHandler.WrapClient(innerClient, handlers);
        registry.Client = client;

        foreach (var (registryKey, configure) in configureCallbacks)
        {
            configure?.Invoke(registry.BackingContainers[registryKey]);
        }

        foreach (var spec in _containers)
            registry.SdkContainers[spec.Name] = client.GetContainer(InMemoryCosmos.DefaultDatabaseName, spec.Name);

        foreach (var dbSpec in _databases)
        {
            foreach (var spec in dbSpec.Containers)
            {
                var registryKey = $"{dbSpec.Name}/{spec.Name}";
                registry.SdkContainers[registryKey] = client.GetContainer(dbSpec.Name, spec.Name);
            }
        }

        var isSingle = singleContainerName is not null || totalContainers == 1;
        var singleName = singleContainerName ?? (isSingle ? GetSingleContainerRegistryKey() : null);

        return new InMemoryCosmosResult(client, registry, singleName);

        string? GetSingleContainerRegistryKey()
        {
            if (_containers.Count == 1 && _databases.Count == 0)
                return _containers[0].Name;
            if (_containers.Count == 0 && _databases.Count == 1 && _databases[0].Containers.Count == 1)
                return $"{_databases[0].Name}/{_databases[0].Containers[0].Name}";
            return null;
        }
    }

    private static InMemoryContainer CreateContainer(ContainerSpec spec)
    {
        return spec.PartitionKeyPaths.Length == 1
            ? new InMemoryContainer(spec.Name, spec.PartitionKeyPaths[0])
            : new InMemoryContainer(spec.Name, spec.PartitionKeyPaths);
    }

    private void ValidateNoDuplicate(string name)
    {
        if (_containers.Any(c => c.Name == name))
            throw new InvalidOperationException($"Container '{name}' has already been added.");
    }
}
