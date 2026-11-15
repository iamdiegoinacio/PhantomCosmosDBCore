#nullable enable
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// A <see cref="Container"/> decorator that intercepts query operations to capture
/// the <see cref="PartitionKey"/> from <see cref="QueryRequestOptions"/> and set it
/// on the owning <see cref="FakeCosmosHandler"/> via its internal scoped override.
/// </summary>
internal sealed class PartitionKeyCapturingContainer : Container
{
    private readonly Container _inner;
    private readonly FakeCosmosHandler _handler;

    internal PartitionKeyCapturingContainer(Container inner, FakeCosmosHandler handler)
    {
        _inner = inner;
        _handler = handler;
    }

    public override string Id => _inner.Id;
    public override Database Database => _inner.Database;
    public override Conflicts Conflicts => _inner.Conflicts;
    public override Scripts Scripts => _inner.Scripts;

    public override Task<ItemResponse<T>> CreateItemAsync<T>(
        T item, PartitionKey? partitionKey = null, ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.CreateItemAsync(item, partitionKey, requestOptions, cancellationToken);

    public override Task<ItemResponse<T>> ReadItemAsync<T>(
        string id, PartitionKey partitionKey, ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.ReadItemAsync<T>(id, partitionKey, requestOptions, cancellationToken);

    public override Task<ItemResponse<T>> UpsertItemAsync<T>(
        T item, PartitionKey? partitionKey = null, ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.UpsertItemAsync(item, partitionKey, requestOptions, cancellationToken);

    public override Task<ItemResponse<T>> ReplaceItemAsync<T>(
        T item, string id, PartitionKey? partitionKey = null, ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.ReplaceItemAsync(item, id, partitionKey, requestOptions, cancellationToken);

    public override Task<ItemResponse<T>> DeleteItemAsync<T>(
        string id, PartitionKey partitionKey, ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.DeleteItemAsync<T>(id, partitionKey, requestOptions, cancellationToken);

    public override Task<ItemResponse<T>> PatchItemAsync<T>(
        string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations,
        PatchItemRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        => _inner.PatchItemAsync<T>(id, partitionKey, patchOperations, requestOptions, cancellationToken);

    public override Task<ResponseMessage> CreateItemStreamAsync(
        Stream streamPayload, PartitionKey partitionKey, ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.CreateItemStreamAsync(streamPayload, partitionKey, requestOptions, cancellationToken);

    public override Task<ResponseMessage> ReadItemStreamAsync(
        string id, PartitionKey partitionKey, ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.ReadItemStreamAsync(id, partitionKey, requestOptions, cancellationToken);

    public override Task<ResponseMessage> UpsertItemStreamAsync(
        Stream streamPayload, PartitionKey partitionKey, ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.UpsertItemStreamAsync(streamPayload, partitionKey, requestOptions, cancellationToken);

    public override Task<ResponseMessage> ReplaceItemStreamAsync(
        Stream streamPayload, string id, PartitionKey partitionKey, ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.ReplaceItemStreamAsync(streamPayload, id, partitionKey, requestOptions, cancellationToken);

    public override Task<ResponseMessage> DeleteItemStreamAsync(
        string id, PartitionKey partitionKey, ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.DeleteItemStreamAsync(id, partitionKey, requestOptions, cancellationToken);

    public override Task<ResponseMessage> PatchItemStreamAsync(
        string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations,
        PatchItemRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        => _inner.PatchItemStreamAsync(id, partitionKey, patchOperations, requestOptions, cancellationToken);

    public override Task<FeedResponse<T>> ReadManyItemsAsync<T>(
        IReadOnlyList<(string id, PartitionKey partitionKey)> items,
        ReadManyRequestOptions? readManyRequestOptions = null, CancellationToken cancellationToken = default)
        => _inner.ReadManyItemsAsync<T>(items, readManyRequestOptions, cancellationToken);

    public override Task<ResponseMessage> ReadManyItemsStreamAsync(
        IReadOnlyList<(string id, PartitionKey partitionKey)> items,
        ReadManyRequestOptions? readManyRequestOptions = null, CancellationToken cancellationToken = default)
        => _inner.ReadManyItemsStreamAsync(items, readManyRequestOptions, cancellationToken);

    public override FeedIterator<T> GetItemQueryIterator<T>(
        QueryDefinition queryDefinition, string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => MaybeWrap(_inner.GetItemQueryIterator<T>(queryDefinition, continuationToken, requestOptions), requestOptions);

    public override FeedIterator<T> GetItemQueryIterator<T>(
        string? queryText = null, string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => MaybeWrap(_inner.GetItemQueryIterator<T>(queryText, continuationToken, requestOptions), requestOptions);

    public override FeedIterator<T> GetItemQueryIterator<T>(
        FeedRange feedRange, QueryDefinition queryDefinition, string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => MaybeWrap(_inner.GetItemQueryIterator<T>(feedRange, queryDefinition, continuationToken, requestOptions), requestOptions);

    public override FeedIterator GetItemQueryStreamIterator(
        QueryDefinition queryDefinition, string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => MaybeWrap(_inner.GetItemQueryStreamIterator(queryDefinition, continuationToken, requestOptions), requestOptions);

    public override FeedIterator GetItemQueryStreamIterator(
        string? queryText = null, string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => MaybeWrap(_inner.GetItemQueryStreamIterator(queryText, continuationToken, requestOptions), requestOptions);

    public override FeedIterator GetItemQueryStreamIterator(
        FeedRange feedRange, QueryDefinition queryDefinition, string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => MaybeWrap(_inner.GetItemQueryStreamIterator(feedRange, queryDefinition, continuationToken, requestOptions), requestOptions);

    public override IOrderedQueryable<T> GetItemLinqQueryable<T>(
        bool allowSynchronousQueryExecution = false, string? continuationToken = null,
        QueryRequestOptions? requestOptions = null, CosmosLinqSerializerOptions? linqSerializerOptions = null)
    {
        _handler.SetPartitionKeyHint(requestOptions?.PartitionKey);
        return _inner.GetItemLinqQueryable<T>(allowSynchronousQueryExecution, continuationToken, requestOptions, linqSerializerOptions);
    }

    public override Task<ContainerResponse> ReadContainerAsync(
        ContainerRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        => _inner.ReadContainerAsync(requestOptions, cancellationToken);

    public override Task<ResponseMessage> ReadContainerStreamAsync(
        ContainerRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        => _inner.ReadContainerStreamAsync(requestOptions, cancellationToken);

    public override Task<ContainerResponse> ReplaceContainerAsync(
        ContainerProperties containerProperties, ContainerRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.ReplaceContainerAsync(containerProperties, requestOptions, cancellationToken);

    public override Task<ResponseMessage> ReplaceContainerStreamAsync(
        ContainerProperties containerProperties, ContainerRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.ReplaceContainerStreamAsync(containerProperties, requestOptions, cancellationToken);

    public override Task<ContainerResponse> DeleteContainerAsync(
        ContainerRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        => _inner.DeleteContainerAsync(requestOptions, cancellationToken);

    public override Task<ResponseMessage> DeleteContainerStreamAsync(
        ContainerRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        => _inner.DeleteContainerStreamAsync(requestOptions, cancellationToken);

    public override Task<int?> ReadThroughputAsync(CancellationToken cancellationToken = default)
        => _inner.ReadThroughputAsync(cancellationToken);

    public override Task<ThroughputResponse> ReadThroughputAsync(
        RequestOptions requestOptions, CancellationToken cancellationToken = default)
        => _inner.ReadThroughputAsync(requestOptions, cancellationToken);

    public override Task<ThroughputResponse> ReplaceThroughputAsync(
        int throughput, RequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        => _inner.ReplaceThroughputAsync(throughput, requestOptions, cancellationToken);

    public override Task<ThroughputResponse> ReplaceThroughputAsync(
        ThroughputProperties throughputProperties, RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.ReplaceThroughputAsync(throughputProperties, requestOptions, cancellationToken);

    public override FeedIterator<T> GetChangeFeedIterator<T>(
        ChangeFeedStartFrom changeFeedStartFrom, ChangeFeedMode changeFeedMode,
        ChangeFeedRequestOptions? changeFeedRequestOptions = null)
        => _inner.GetChangeFeedIterator<T>(changeFeedStartFrom, changeFeedMode, changeFeedRequestOptions);

    public override FeedIterator GetChangeFeedStreamIterator(
        ChangeFeedStartFrom changeFeedStartFrom, ChangeFeedMode changeFeedMode,
        ChangeFeedRequestOptions? changeFeedRequestOptions = null)
        => _inner.GetChangeFeedStreamIterator(changeFeedStartFrom, changeFeedMode, changeFeedRequestOptions);

    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilder<T>(
        string processorName, ChangesHandler<T> onChangesDelegate)
        => _inner.GetChangeFeedProcessorBuilder(processorName, onChangesDelegate);

    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilder<T>(
        string processorName, ChangeFeedHandler<T> onChangesDelegate)
        => _inner.GetChangeFeedProcessorBuilder(processorName, onChangesDelegate);

    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilder(
        string processorName, ChangeFeedStreamHandler onChangesDelegate)
        => _inner.GetChangeFeedProcessorBuilder(processorName, onChangesDelegate);

    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilderWithManualCheckpoint<T>(
        string processorName, ChangeFeedHandlerWithManualCheckpoint<T> onChangesDelegate)
        => _inner.GetChangeFeedProcessorBuilderWithManualCheckpoint(processorName, onChangesDelegate);

    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilderWithManualCheckpoint(
        string processorName, ChangeFeedStreamHandlerWithManualCheckpoint onChangesDelegate)
        => _inner.GetChangeFeedProcessorBuilderWithManualCheckpoint(processorName, onChangesDelegate);

    public override ChangeFeedEstimator GetChangeFeedEstimator(
        string processorName, Container leaseContainer)
        => _inner.GetChangeFeedEstimator(processorName, leaseContainer);

    public override ChangeFeedProcessorBuilder GetChangeFeedEstimatorBuilder(
        string processorName, ChangesEstimationHandler estimationDelegate, TimeSpan? estimationPeriod = null)
        => _inner.GetChangeFeedEstimatorBuilder(processorName, estimationDelegate, estimationPeriod);

    public override Task<IReadOnlyList<FeedRange>> GetFeedRangesAsync(CancellationToken cancellationToken = default)
        => _inner.GetFeedRangesAsync(cancellationToken);

    public override TransactionalBatch CreateTransactionalBatch(PartitionKey partitionKey)
        => _inner.CreateTransactionalBatch(partitionKey);

    public override Task<ResponseMessage> DeleteAllItemsByPartitionKeyStreamAsync(
        PartitionKey partitionKey, RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => _inner.DeleteAllItemsByPartitionKeyStreamAsync(partitionKey, requestOptions, cancellationToken);

    private FeedIterator<T> MaybeWrap<T>(FeedIterator<T> inner, QueryRequestOptions? requestOptions)
        => requestOptions?.PartitionKey is { } pk
            ? new PkCapturingFeedIterator<T>(inner, _handler, pk)
            : inner;

    private FeedIterator MaybeWrap(FeedIterator inner, QueryRequestOptions? requestOptions)
        => requestOptions?.PartitionKey is { } pk
            ? new PkCapturingStreamFeedIterator(inner, _handler, pk)
            : inner;
}
