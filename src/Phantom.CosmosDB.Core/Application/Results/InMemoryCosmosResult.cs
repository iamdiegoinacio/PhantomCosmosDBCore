#nullable enable
using System.Collections.ObjectModel;
using Phantom.CosmosDB.Core.Application.Results;
using Phantom.CosmosDB.Core.Infrastructure.HttpGateway;
using Microsoft.Azure.Cosmos;

namespace Phantom.CosmosDB.Core;

/// <summary>
/// The result of creating an in-memory Cosmos setup. Provides access to the
/// <see cref="CosmosClient"/>, SDK <see cref="Container"/> references,
/// test setup operations, and fault injection handlers.
/// </summary>
public sealed class InMemoryCosmosResult : IDisposable, IAsyncDisposable
{
    private readonly DynamicContainerRegistry _registry;
    private readonly string? _singleContainerName;

    internal InMemoryCosmosResult(
        CosmosClient client,
        DynamicContainerRegistry registry,
        string? singleContainerName)
    {
        Client = client;
        _registry = registry;
        _singleContainerName = singleContainerName;
    }

    /// <summary>The <see cref="CosmosClient"/> backed by in-memory handlers.</summary>
    public CosmosClient Client { get; }

    /// <summary>
    /// The SDK <see cref="Container"/> for single-container setups.
    /// Throws <see cref="InvalidOperationException"/> if multiple containers are registered.
    /// </summary>
    public Container Container
    {
        get
        {
            if (_singleContainerName is null)
                throw new InvalidOperationException(
                    "Container property requires a single-container setup. " +
                    $"Use Containers[\"name\"] instead. Available containers: {AvailableNames}.");
            return _registry.SdkContainers[_singleContainerName];
        }
    }

    /// <summary>
    /// All SDK <see cref="Container"/> references keyed by container name.
    /// </summary>
    public IReadOnlyDictionary<string, Container> Containers =>
        BuildFlatDictionary(
            (dbName, containerName, registryKey) =>
                _registry.SdkContainers.TryGetValue(registryKey, out var c) ? c : null!);

    /// <summary>
    /// Returns an <see cref="IContainerTestSetup"/> for the specified container.
    /// For single-container setups, <paramref name="containerName"/> can be omitted.
    /// </summary>
    public IContainerTestSetup SetupContainer(string? containerName = null)
    {
        if (containerName is null)
        {
            if (_singleContainerName is null)
                throw new InvalidOperationException(
                    "SetupContainer() with no name requires a single-container setup. " +
                    $"Use SetupContainer(\"name\") instead. Available containers: {AvailableNames}.");
            return _registry.BackingContainers[_singleContainerName];
        }

        var registryKey = ResolveContainerRegistryKey(containerName, "SetupContainer");
        return _registry.BackingContainers[registryKey];
    }

    /// <summary>Exports the current container state as a JSON string.</summary>
    public string ExportState(string? containerName = null) =>
        SetupContainer(containerName).ExportState();

    /// <summary>Exports the current container state to a file.</summary>
    public void ExportStateToFile(string path, string? containerName = null) =>
        SetupContainer(containerName).ExportStateToFile(path);

    /// <summary>Imports container state from a JSON string, replacing all existing data.</summary>
    public void ImportState(string json, string? containerName = null) =>
        SetupContainer(containerName).ImportState(json);

    /// <summary>Imports container state from a file, replacing all existing data.</summary>
    public void ImportStateFromFile(string filePath, string? containerName = null) =>
        SetupContainer(containerName).ImportStateFromFile(filePath);

    /// <summary>
    /// Restores the container to its state at the specified point in time
    /// by replaying the change feed.
    /// </summary>
    public void RestoreToPointInTime(DateTimeOffset timestamp, string? containerName = null) =>
        SetupContainer(containerName).RestoreToPointInTime(timestamp);

    /// <summary>Removes all items, ETags, timestamps, and change feed entries.</summary>
    public void ClearItems(string? containerName = null) =>
        SetupContainer(containerName).ClearItems();

    /// <summary>
    /// The <see cref="FakeCosmosHandler"/> for single-container setups.
    /// Throws <see cref="InvalidOperationException"/> if multiple containers are registered.
    /// </summary>
    public FakeCosmosHandler Handler
    {
        get
        {
            if (_singleContainerName is null)
                throw new InvalidOperationException(
                    "Handler property requires a single-container setup. " +
                    $"Use Handlers[\"name\"] or GetHandler(\"name\") instead. Available containers: {AvailableNames}.");
            return _registry.Handlers[_singleContainerName];
        }
    }

    /// <summary>
    /// All <see cref="FakeCosmosHandler"/> instances keyed by container name.
    /// </summary>
    public IReadOnlyDictionary<string, FakeCosmosHandler> Handlers =>
        BuildFlatDictionary(
            (dbName, containerName, registryKey) =>
                _registry.Handlers.TryGetValue(registryKey, out var h) ? h : null!);

    /// <summary>
    /// Returns the <see cref="FakeCosmosHandler"/> for the specified container.
    /// </summary>
    public FakeCosmosHandler GetHandler(string name)
    {
        var registryKey = ResolveContainerRegistryKey(name, "GetHandler");
        return _registry.Handlers[registryKey];
    }

    /// <summary>
    /// Sets or clears the fault injector on all handlers.
    /// Pass <c>null</c> to clear.
    /// </summary>
    public void SetFaultInjector(Func<HttpRequestMessage, HttpResponseMessage?>? injector)
    {
        foreach (var handler in _registry.Handlers.Values)
            handler.FaultInjector = injector;
    }

    /// <summary>
    /// Returns an <see cref="InMemoryDatabaseResult"/> for the specified database.
    /// </summary>
    public InMemoryDatabaseResult Database(string databaseName)
    {
        if (!_registry.DatabaseContainerKeys.TryGetValue(databaseName, out var containerKeys))
            throw new InvalidOperationException(
                $"Database '{databaseName}' not found. Available databases: " +
                string.Join(", ", _registry.DatabaseContainerKeys.Keys.OrderBy(k => k)) + ".");
        return new InMemoryDatabaseResult(databaseName, _registry, containerKeys);
    }

    /// <summary>
    /// All databases keyed by name.
    /// </summary>
    public IReadOnlyDictionary<string, InMemoryDatabaseResult> Databases
    {
        get
        {
            var dict = new Dictionary<string, InMemoryDatabaseResult>(StringComparer.Ordinal);
            foreach (var (dbName, containerKeys) in _registry.DatabaseContainerKeys)
                dict[dbName] = new InMemoryDatabaseResult(dbName, _registry, containerKeys);
            return new ReadOnlyDictionary<string, InMemoryDatabaseResult>(dict);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Client.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private string AvailableNames => string.Join(", ",
        _registry.DatabaseContainerKeys.Values
            .SelectMany(m => m.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k));

    private string ResolveContainerRegistryKey(string containerName, string methodName)
    {
        var matches = new List<(string DatabaseName, string RegistryKey)>();
        foreach (var (dbName, containers) in _registry.DatabaseContainerKeys)
        {
            if (containers.TryGetValue(containerName, out var registryKey))
                matches.Add((dbName, registryKey));
        }

        return matches.Count switch
        {
            0 => throw new InvalidOperationException(
                $"Container '{containerName}' not found. Available containers: {AvailableNames}."),
            1 => matches[0].RegistryKey,
            _ => throw new InvalidOperationException(
                $"Container '{containerName}' exists in multiple databases: " +
                string.Join(", ", matches.Select(m => m.DatabaseName).OrderBy(n => n)) + ". " +
                $"Use cosmos.Database(\"...\").{methodName}(\"{containerName}\") instead.")
        };
    }

    private IReadOnlyDictionary<string, T> BuildFlatDictionary<T>(
        Func<string, string, string, T?> valueSelector) where T : class
    {
        var unambiguous = new Dictionary<string, T>(StringComparer.Ordinal);
        var ambiguous = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (dbName, containers) in _registry.DatabaseContainerKeys)
        {
            foreach (var (containerName, registryKey) in containers)
            {
                var value = valueSelector(dbName, containerName, registryKey);
                if (value is null) continue;

                if (ambiguous.ContainsKey(containerName))
                {
                    ambiguous[containerName].Add(dbName);
                }
                else if (unambiguous.ContainsKey(containerName))
                {
                    var existingDb = _registry.DatabaseContainerKeys
                        .Where(d => d.Value.ContainsKey(containerName))
                        .Select(d => d.Key)
                        .First(d => d != dbName);
                    ambiguous[containerName] = [existingDb, dbName];
                    unambiguous.Remove(containerName);
                }
                else
                {
                    unambiguous[containerName] = value;
                }
            }
        }

        if (ambiguous.Count == 0)
            return new ReadOnlyDictionary<string, T>(unambiguous);

        return new AmbiguityDetectingDictionary<T>(unambiguous, ambiguous);
    }
}
