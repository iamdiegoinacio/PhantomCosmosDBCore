#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using CosmosDB.InMemoryEmulator.Domain.Validation;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using NSubstitute;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// In-memory implementation of <see cref="CosmosClient"/> for testing.
/// Manages a collection of <see cref="InMemoryDatabase"/> instances, each containing
/// <see cref="InMemoryContainer"/> instances.
/// </summary>
[Obsolete("Use UseInMemoryCosmosDB() or UseInMemoryCosmosContainers() instead. " +
          "InMemoryCosmosClient bypasses the SDK HTTP pipeline. " +
          "FakeCosmosHandler-based methods provide higher fidelity and native .ToFeedIterator() support.")]
internal class InMemoryCosmosClient : CosmosClient
{
    private readonly ConcurrentDictionary<string, InMemoryDatabase> _databases = new();
    private readonly ConcurrentDictionary<string, bool> _explicitlyCreatedDatabases = new();
    private bool _disposed;

    private static readonly Uri EmulatorEndpoint = new("https://localhost:8081/");
    private readonly CosmosClientOptions _clientOptions = new();
    private readonly CosmosResponseFactory _responseFactory = Substitute.For<CosmosResponseFactory>();

    public override Uri Endpoint => EmulatorEndpoint;

    public override CosmosClientOptions ClientOptions => _clientOptions;

    public override CosmosResponseFactory ResponseFactory => _responseFactory;

    public override Task<DatabaseResponse> CreateDatabaseIfNotExistsAsync(
        string id,
        int? throughput = null,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrEmpty(id);
        CosmosResourceNameValidator.ValidateResourceName(id, "Database");
        var created = false;
        var database = _databases.GetOrAdd(id, name =>
        {
            created = true;
            return new InMemoryDatabase(name, this);
        });
        _explicitlyCreatedDatabases.TryAdd(id, true);
        var response = Infrastructure.SdkInterop.CosmosResponseFactory.CreateDatabaseResponse(
            database,
            created ? HttpStatusCode.Created : HttpStatusCode.OK,
            new DatabaseProperties(database.Id));
        return Task.FromResult(response);
    }

    public override Task<DatabaseResponse> CreateDatabaseIfNotExistsAsync(
        string id,
        ThroughputProperties throughputProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        return CreateDatabaseIfNotExistsAsync(id, (int?)null, requestOptions, cancellationToken);
    }

    public override Task<DatabaseResponse> CreateDatabaseAsync(
        string id,
        int? throughput = null,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrEmpty(id);
        CosmosResourceNameValidator.ValidateResourceName(id, "Database");
        var database = new InMemoryDatabase(id, this);
        if (!_databases.TryAdd(id, database))
        {
            throw InMemoryCosmosException.Create("Database already exists.", HttpStatusCode.Conflict, 0, string.Empty, 0);
        }
        _explicitlyCreatedDatabases.TryAdd(id, true);
        var response = Infrastructure.SdkInterop.CosmosResponseFactory.CreateDatabaseResponse(
            database,
            HttpStatusCode.Created,
            new DatabaseProperties(database.Id));
        return Task.FromResult(response);
    }

    public override Task<DatabaseResponse> CreateDatabaseAsync(
        string id,
        ThroughputProperties throughputProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        return CreateDatabaseAsync(id, (int?)null, requestOptions, cancellationToken);
    }

    public override Task<ResponseMessage> CreateDatabaseStreamAsync(
        DatabaseProperties databaseProperties,
        int? throughput = null,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(databaseProperties);
        var id = databaseProperties.Id;
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrEmpty(id);
        CosmosResourceNameValidator.ValidateResourceName(id, "Database");
        var database = new InMemoryDatabase(id, this);
        if (!_databases.TryAdd(id, database))
        {
            return Task.FromResult(CreateStreamResponse(HttpStatusCode.Conflict));
        }
        _explicitlyCreatedDatabases.TryAdd(id, true);
        return Task.FromResult(CreateStreamResponse(HttpStatusCode.Created, databaseProperties));
    }

    private static ResponseMessage CreateStreamResponse(HttpStatusCode statusCode, DatabaseProperties? databaseProperties = null)
    {
        var msg = new ResponseMessage(statusCode);
        msg.Headers["x-ms-activity-id"] = Guid.NewGuid().ToString();
        msg.Headers["x-ms-request-charge"] = "1";
        msg.Headers["x-ms-session-token"] = "0:0#0";
        if (databaseProperties is not null)
        {
            var json = JsonConvert.SerializeObject(databaseProperties);
            msg.Content = new MemoryStream(Encoding.UTF8.GetBytes(json));
        }
        return msg;
    }

    public Task<ResponseMessage> CreateDatabaseStreamAsync(
        DatabaseProperties databaseProperties,
        ThroughputProperties throughputProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        return CreateDatabaseStreamAsync(databaseProperties, (int?)null, requestOptions, cancellationToken);
    }

    public override Database GetDatabase(string id)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrEmpty(id);
        return _databases.GetOrAdd(id, name => new InMemoryDatabase(name, this));
    }

    public override Container GetContainer(string databaseId, string containerId)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(databaseId);
        ArgumentException.ThrowIfNullOrEmpty(databaseId);
        ArgumentNullException.ThrowIfNull(containerId);
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        var database = _databases.GetOrAdd(databaseId, name => new InMemoryDatabase(name, this));
        return database.GetOrCreateContainer(containerId);
    }

    public override FeedIterator<T> GetDatabaseQueryIterator<T>(
        string? queryText = null,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
    {
        var offset = int.TryParse(continuationToken, out var o) ? o : 0;
        IEnumerable<DatabaseProperties> items = _databases.Values
            .Select(db => new DatabaseProperties(db.Id));
        var idFilter = CosmosResourceNameValidator.ExtractIdFilter(queryText);
        if (idFilter is not null)
            items = items.Where(d => string.Equals(d.Id, idFilter, StringComparison.Ordinal));
        return new InMemoryFeedIterator<T>(items.Cast<T>().ToList(), requestOptions?.MaxItemCount, offset);
    }

    public override FeedIterator<T> GetDatabaseQueryIterator<T>(
        QueryDefinition queryDefinition,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
    {
        return GetDatabaseQueryIterator<T>(queryDefinition?.QueryText, continuationToken, requestOptions);
    }

    public override FeedIterator GetDatabaseQueryStreamIterator(
        string? queryText = null,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
    {
        return new InMemoryStreamFeedIterator(
            () => _databases.Values
                .Select(db => (object)new { id = db.Id })
                .ToList(),
            "Databases");
    }

    public override FeedIterator GetDatabaseQueryStreamIterator(
        QueryDefinition queryDefinition,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
    {
        return GetDatabaseQueryStreamIterator((string?)null, continuationToken, requestOptions);
    }

    public override Task<AccountProperties> ReadAccountAsync()
    {
        try
        {
            var account = (AccountProperties)Activator.CreateInstance(
                typeof(AccountProperties),
                nonPublic: true)!;
            var idProp = typeof(AccountProperties).GetProperty(nameof(AccountProperties.Id));
            idProp?.SetValue(account, "in-memory-emulator");
            return Task.FromResult(account);
        }
        catch (Exception ex) when (ex is MissingMethodException or MissingMemberException or TargetInvocationException)
        {
            Trace.TraceWarning(
                "InMemoryCosmosClient: AccountProperties reflection failed " +
                $"({ex.GetType().Name}: {ex.Message}). " +
                "Falling back to NSubstitute stub.");
            var stub = Substitute.For<AccountProperties>();
            return Task.FromResult(stub);
        }
    }

    internal bool RemoveDatabase(string id)
    {
        _explicitlyCreatedDatabases.TryRemove(id, out _);
        return _databases.TryRemove(id, out _);
    }

    internal bool IsDatabaseExplicitlyCreated(string id) => _explicitlyCreatedDatabases.ContainsKey(id);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            foreach (var db in _databases.Values)
                foreach (var container in db.GetAllContainers())
                    container.Dispose();
        }
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal static void ValidateResourceName(string name, string resourceType) =>
        CosmosResourceNameValidator.ValidateResourceName(name, resourceType);

    internal static string? ExtractIdFilter(string? queryText) =>
        CosmosResourceNameValidator.ExtractIdFilter(queryText);
}
