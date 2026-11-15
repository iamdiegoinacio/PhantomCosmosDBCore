#nullable enable
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// A <see cref="CosmosClient"/> wrapper that overrides <see cref="GetContainer"/>
/// to return <see cref="PartitionKeyCapturingContainer"/> instances, enabling
/// transparent prefix partition key support for hierarchical partition keys.
/// </summary>
internal sealed class PkAwareCosmosClient : CosmosClient
{
    private readonly CosmosClient _inner;
    private readonly IReadOnlyDictionary<string, FakeCosmosHandler> _handlers;

    internal PkAwareCosmosClient(
        CosmosClient inner,
        IReadOnlyDictionary<string, FakeCosmosHandler> handlers) : base()
    {
        _inner = inner;
        _handlers = handlers;
    }

    public override Container GetContainer(string databaseId, string containerId)
    {
        var inner = _inner.GetContainer(databaseId, containerId);
        if (_handlers.TryGetValue(containerId, out var handler) ||
            _handlers.TryGetValue($"{databaseId}/{containerId}", out handler))
            return new PartitionKeyCapturingContainer(inner, handler);
        return inner;
    }

    public override Database GetDatabase(string id) => _inner.GetDatabase(id);
    public override Uri Endpoint => _inner.Endpoint;
    public override CosmosClientOptions ClientOptions => _inner.ClientOptions;

    public override Task<DatabaseResponse> CreateDatabaseAsync(
        string id, int? throughput = null, RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.CreateDatabaseAsync(id, throughput, requestOptions, cancellationToken);

    public override Task<DatabaseResponse> CreateDatabaseAsync(
        string id, ThroughputProperties throughputProperties, RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.CreateDatabaseAsync(id, throughputProperties, requestOptions, cancellationToken);

    public override Task<DatabaseResponse> CreateDatabaseIfNotExistsAsync(
        string id, int? throughput = null, RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.CreateDatabaseIfNotExistsAsync(id, throughput, requestOptions, cancellationToken);

    public override Task<DatabaseResponse> CreateDatabaseIfNotExistsAsync(
        string id, ThroughputProperties throughputProperties, RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.CreateDatabaseIfNotExistsAsync(id, throughputProperties, requestOptions, cancellationToken);

    public override Task<ResponseMessage> CreateDatabaseStreamAsync(
        DatabaseProperties databaseProperties, int? throughput = null,
        RequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        => _inner.CreateDatabaseStreamAsync(databaseProperties, throughput, requestOptions, cancellationToken);

    public override FeedIterator<T> GetDatabaseQueryIterator<T>(
        QueryDefinition queryDefinition, string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => _inner.GetDatabaseQueryIterator<T>(queryDefinition, continuationToken, requestOptions);

    public override FeedIterator<T> GetDatabaseQueryIterator<T>(
        string? queryText = null, string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => _inner.GetDatabaseQueryIterator<T>(queryText, continuationToken, requestOptions);

    public override FeedIterator GetDatabaseQueryStreamIterator(
        QueryDefinition queryDefinition, string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => _inner.GetDatabaseQueryStreamIterator(queryDefinition, continuationToken, requestOptions);

    public override FeedIterator GetDatabaseQueryStreamIterator(
        string? queryText = null, string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => _inner.GetDatabaseQueryStreamIterator(queryText, continuationToken, requestOptions);

    public override Task<AccountProperties> ReadAccountAsync()
        => _inner.ReadAccountAsync();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
    }
}
