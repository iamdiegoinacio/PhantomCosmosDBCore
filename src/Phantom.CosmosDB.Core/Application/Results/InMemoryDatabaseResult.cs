#nullable enable
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Phantom.CosmosDB.Core.Infrastructure.HttpGateway;
using Microsoft.Azure.Cosmos;

namespace Phantom.CosmosDB.Core;

/// <summary>
/// Provides database-scoped access to containers, test setup, and fault injection.
/// </summary>
public sealed class InMemoryDatabaseResult
{
    private readonly DynamicContainerRegistry _registry;
    private readonly ConcurrentDictionary<string, string> _containerKeys;

    internal InMemoryDatabaseResult(
        string databaseName,
        DynamicContainerRegistry registry,
        ConcurrentDictionary<string, string> containerKeys)
    {
        DatabaseName = databaseName;
        _registry = registry;
        _containerKeys = containerKeys;
    }

    /// <summary>The database name.</summary>
    public string DatabaseName { get; }

    /// <summary>
    /// All SDK <see cref="Container"/> references in this database, keyed by container name.
    /// </summary>
    public IReadOnlyDictionary<string, Container> Containers
    {
        get
        {
            var dict = new Dictionary<string, Container>(StringComparer.Ordinal);
            foreach (var (name, registryKey) in _containerKeys)
            {
                if (_registry.SdkContainers.TryGetValue(registryKey, out var container))
                    dict[name] = container;
            }
            return new ReadOnlyDictionary<string, Container>(dict);
        }
    }

    /// <summary>
    /// Returns an <see cref="IContainerTestSetup"/> for the specified container in this database.
    /// </summary>
    public IContainerTestSetup SetupContainer(string containerName)
    {
        if (!_containerKeys.TryGetValue(containerName, out var registryKey))
            throw new InvalidOperationException(
                $"Container '{containerName}' not found in database '{DatabaseName}'.");
        return _registry.BackingContainers[registryKey];
    }

    /// <summary>Exports the current container state as a JSON string.</summary>
    public string ExportState(string containerName) =>
        SetupContainer(containerName).ExportState();

    /// <summary>Exports the current container state to a file.</summary>
    public void ExportStateToFile(string containerName, string path) =>
        SetupContainer(containerName).ExportStateToFile(path);

    /// <summary>Imports container state from a JSON string, replacing all existing data.</summary>
    public void ImportState(string containerName, string json) =>
        SetupContainer(containerName).ImportState(json);

    /// <summary>Imports container state from a file, replacing all existing data.</summary>
    public void ImportStateFromFile(string containerName, string filePath) =>
        SetupContainer(containerName).ImportStateFromFile(filePath);

    /// <summary>
    /// Restores the container to its state at the specified point in time
    /// by replaying the change feed.
    /// </summary>
    public void RestoreToPointInTime(string containerName, DateTimeOffset timestamp) =>
        SetupContainer(containerName).RestoreToPointInTime(timestamp);

    /// <summary>Removes all items, ETags, timestamps, and change feed entries.</summary>
    public void ClearItems(string containerName) =>
        SetupContainer(containerName).ClearItems();

    /// <summary>
    /// Returns the <see cref="FakeCosmosHandler"/> for the specified container in this database.
    /// </summary>
    public FakeCosmosHandler GetHandler(string containerName)
    {
        if (!_containerKeys.TryGetValue(containerName, out var registryKey))
            throw new InvalidOperationException(
                $"Container '{containerName}' not found in database '{DatabaseName}'.");
        return _registry.Handlers[registryKey];
    }

    /// <summary>
    /// All <see cref="FakeCosmosHandler"/> instances in this database, keyed by container name.
    /// </summary>
    public IReadOnlyDictionary<string, FakeCosmosHandler> Handlers
    {
        get
        {
            var dict = new Dictionary<string, FakeCosmosHandler>(StringComparer.Ordinal);
            foreach (var (name, registryKey) in _containerKeys)
            {
                if (_registry.Handlers.TryGetValue(registryKey, out var handler))
                    dict[name] = handler;
            }
            return new ReadOnlyDictionary<string, FakeCosmosHandler>(dict);
        }
    }

    /// <summary>
    /// Sets or clears the fault injector on all handlers in this database.
    /// </summary>
    public void SetFaultInjector(Func<HttpRequestMessage, HttpResponseMessage?>? injector)
    {
        foreach (var (_, registryKey) in _containerKeys)
        {
            if (_registry.Handlers.TryGetValue(registryKey, out var handler))
                handler.FaultInjector = injector;
        }
    }
}
