#nullable enable
using System.Net;
using System.Text;
using Phantom.CosmosDB.Core.Application.Batch;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core;

/// <summary>
/// In-memory implementation of <see cref="TransactionalBatch"/> for testing.
/// Executes batch operations atomically against an <see cref="InMemoryContainer"/>
/// with automatic rollback on failure, or independently when <see cref="IsAtomic"/> is <c>false</c>.
/// </summary>
internal class InMemoryTransactionalBatch : TransactionalBatch
{
    private readonly InMemoryContainer _container;
    private readonly PartitionKey _partitionKey;
    private readonly List<(Func<Task> Execute, BatchOpType Type, string? TargetId)> _operations = new();
    private long _estimatedBatchSize;
    private readonly Dictionary<int, string> _readResults = new();
    private readonly Dictionary<int, string> _writeEtags = new();
    private readonly Dictionary<int, HttpStatusCode> _opStatusCodes = new();
    private readonly Dictionary<int, int> _opSubStatusCodes = new();

    public bool IsAtomic { get; set; } = true;

    public InMemoryTransactionalBatch(InMemoryContainer container, PartitionKey partitionKey)
    {
        _container = container;
        _partitionKey = partitionKey;
    }

    public override TransactionalBatch CreateItem<T>(T item, TransactionalBatchItemRequestOptions? requestOptions = null)
    {
        var json = JsonConvert.SerializeObject(item, BatchOperationCommand.JsonSettings);
        _estimatedBatchSize += Encoding.UTF8.GetByteCount(json);
        var opIndex = _operations.Count;
        _operations.Add((async () =>
        {
            var result = await _container.CreateItemAsync(item, _partitionKey, BatchOperationCommand.ToItemRequestOptions(requestOptions));
            _writeEtags[opIndex] = result.ETag;
            _readResults[opIndex] = JsonConvert.SerializeObject(result.Resource, BatchOperationCommand.JsonSettings);
        }, BatchOpType.Create, TryExtractId(json)));
        return this;
    }

    public override TransactionalBatch UpsertItem<T>(T item, TransactionalBatchItemRequestOptions? requestOptions = null)
    {
        var json = JsonConvert.SerializeObject(item, BatchOperationCommand.JsonSettings);
        _estimatedBatchSize += Encoding.UTF8.GetByteCount(json);
        var opIndex = _operations.Count;
        _operations.Add((async () =>
        {
            var result = await _container.UpsertItemAsync(item, _partitionKey, BatchOperationCommand.ToItemRequestOptions(requestOptions));
            _writeEtags[opIndex] = result.ETag;
            _opStatusCodes[opIndex] = result.StatusCode;
            _readResults[opIndex] = JsonConvert.SerializeObject(result.Resource, BatchOperationCommand.JsonSettings);
        }, BatchOpType.Upsert, TryExtractId(json)));
        return this;
    }

    public override TransactionalBatch ReplaceItem<T>(string id, T item, TransactionalBatchItemRequestOptions? requestOptions = null)
    {
        var json = JsonConvert.SerializeObject(item, BatchOperationCommand.JsonSettings);
        _estimatedBatchSize += Encoding.UTF8.GetByteCount(json);
        var opIndex = _operations.Count;
        _operations.Add((async () =>
        {
            var result = await _container.ReplaceItemAsync(item, id, _partitionKey, BatchOperationCommand.ToItemRequestOptions(requestOptions));
            _writeEtags[opIndex] = result.ETag;
            _readResults[opIndex] = JsonConvert.SerializeObject(result.Resource, BatchOperationCommand.JsonSettings);
        }, BatchOpType.Replace, id));
        return this;
    }

    public override TransactionalBatch DeleteItem(string id, TransactionalBatchItemRequestOptions? requestOptions = null)
    {
        _estimatedBatchSize += Encoding.UTF8.GetByteCount(id) + 128;
        _operations.Add((
            async () => await _container.DeleteItemAsync<object>(id, _partitionKey, BatchOperationCommand.ToItemRequestOptions(requestOptions)),
            BatchOpType.Delete, id));
        return this;
    }

    public override TransactionalBatch ReadItem(string id, TransactionalBatchItemRequestOptions? requestOptions = null)
    {
        _estimatedBatchSize += Encoding.UTF8.GetByteCount(id) + 128;
        var operationIndex = _operations.Count;
        _operations.Add((async () =>
        {
            var result = await _container.ReadItemAsync<object>(id, _partitionKey, BatchOperationCommand.ToItemRequestOptions(requestOptions));
            _readResults[operationIndex] = JsonConvert.SerializeObject(result.Resource, BatchOperationCommand.JsonSettings);
        }, BatchOpType.Read, id));
        return this;
    }

    public override TransactionalBatch CreateItemStream(Stream streamPayload, TransactionalBatchItemRequestOptions? requestOptions = null)
    {
        var json = new StreamReader(streamPayload).ReadToEnd();
        _estimatedBatchSize += Encoding.UTF8.GetByteCount(json);
        var opIndex = _operations.Count;
        _operations.Add((
            () => BatchOperationCommand.ExecuteStreamWriteAsync(
                json,
                opIndex,
                stream => _container.CreateItemStreamAsync(stream, _partitionKey, BatchOperationCommand.ToItemRequestOptions(requestOptions)),
                _writeEtags,
                _readResults),
            BatchOpType.Create, TryExtractId(json)));
        return this;
    }

    public override TransactionalBatch UpsertItemStream(Stream streamPayload, TransactionalBatchItemRequestOptions? requestOptions = null)
    {
        var json = new StreamReader(streamPayload).ReadToEnd();
        _estimatedBatchSize += Encoding.UTF8.GetByteCount(json);
        var opIndex = _operations.Count;
        _operations.Add((
            () => BatchOperationCommand.ExecuteStreamWriteAsync(
                json,
                opIndex,
                stream => _container.UpsertItemStreamAsync(stream, _partitionKey, BatchOperationCommand.ToItemRequestOptions(requestOptions)),
                _writeEtags,
                _readResults,
                _opStatusCodes),
            BatchOpType.Upsert, TryExtractId(json)));
        return this;
    }

    public override TransactionalBatch ReplaceItemStream(string id, Stream streamPayload, TransactionalBatchItemRequestOptions? requestOptions = null)
    {
        var json = new StreamReader(streamPayload).ReadToEnd();
        _estimatedBatchSize += Encoding.UTF8.GetByteCount(json);
        var opIndex = _operations.Count;
        _operations.Add((
            () => BatchOperationCommand.ExecuteStreamWriteAsync(
                json,
                opIndex,
                stream => _container.ReplaceItemStreamAsync(stream, id, _partitionKey, BatchOperationCommand.ToItemRequestOptions(requestOptions)),
                _writeEtags,
                _readResults),
            BatchOpType.Replace, id));
        return this;
    }

    public override TransactionalBatch PatchItem(
        string id,
        IReadOnlyList<PatchOperation> patchOperations,
        TransactionalBatchPatchItemRequestOptions? requestOptions = null)
    {
        var estimatedSize = patchOperations.Sum(op =>
        {
            var json = JsonConvert.SerializeObject(op, BatchOperationCommand.JsonSettings);
            return Encoding.UTF8.GetByteCount(json);
        });
        _estimatedBatchSize += estimatedSize;
        var opIndex = _operations.Count;
        _operations.Add((async () =>
        {
            var patchOptions = BatchOperationCommand.ToPatchItemRequestOptions(requestOptions);
            var result = await _container.PatchItemAsync<object>(id, _partitionKey, patchOperations, patchOptions);
            _writeEtags[opIndex] = result.ETag;
            _readResults[opIndex] = JsonConvert.SerializeObject(result.Resource, BatchOperationCommand.JsonSettings);
        }, BatchOpType.Patch, id));
        return this;
    }

    public override async Task<TransactionalBatchResponse> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_operations.Count > BatchOperationCommand.MaxBatchOperations)
        {
            throw InMemoryCosmosException.Create(
                "Batch request has more operations than what is supported.",
                HttpStatusCode.BadRequest,
                0,
                string.Empty,
                0);
        }

        if (_estimatedBatchSize > BatchOperationCommand.MaxBatchSizeBytes)
        {
            var overSizeResults = new List<(HttpStatusCode status, bool isSuccess)>();
            for (var i = 0; i < _operations.Count; i++)
                overSizeResults.Add((HttpStatusCode.FailedDependency, false));
            return new InMemoryBatchResponse(
                HttpStatusCode.RequestEntityTooLarge,
                false,
                overSizeResults,
                _readResults,
                _writeEtags,
                "Request size is too large.",
                _container.CurrentSessionToken);
        }

        if (!IsAtomic)
        {
            var bulkResults = new List<(HttpStatusCode status, bool isSuccess)>(_operations.Count);
            for (var i = 0; i < _operations.Count; i++)
            {
                try
                {
                    var (execute, opType, _) = _operations[i];
                    await execute();
                    var statusCode = opType switch
                    {
                        BatchOpType.Read => HttpStatusCode.OK,
                        BatchOpType.Delete => HttpStatusCode.NoContent,
                        BatchOpType.Replace => HttpStatusCode.OK,
                        BatchOpType.Patch => HttpStatusCode.OK,
                        BatchOpType.Upsert => _opStatusCodes.TryGetValue(i, out var sc) ? sc : HttpStatusCode.Created,
                        _ => HttpStatusCode.Created
                    };
                    bulkResults.Add((statusCode, true));
                }
                catch (CosmosException ex)
                {
                    _opSubStatusCodes[i] = ex.SubStatusCode;
                    bulkResults.Add((ex.StatusCode, false));
                }
            }

            return new InMemoryBatchResponse(
                HttpStatusCode.OK,
                true,
                bulkResults,
                _readResults,
                _writeEtags,
                sessionToken: _container.CurrentSessionToken,
                opSubStatusCodes: _opSubStatusCodes);
        }

        var duplicateIndex = FindDuplicateMutatingIdIndex();
        if (duplicateIndex >= 0)
        {
            var conflictResults = new List<(HttpStatusCode status, bool isSuccess)>(_operations.Count);
            for (var i = 0; i < _operations.Count; i++)
            {
                conflictResults.Add(i == duplicateIndex
                    ? (HttpStatusCode.BadRequest, false)
                    : (HttpStatusCode.FailedDependency, false));
            }

            return new InMemoryBatchResponse(
                HttpStatusCode.BadRequest,
                false,
                conflictResults,
                _readResults,
                _writeEtags,
                $"Duplicate mutating operation on item id '{_operations[duplicateIndex].TargetId}' at index {duplicateIndex} within an atomic batch.",
                _container.CurrentSessionToken);
        }

        var itemsSnapshot = _container.SnapshotItems();
        var etagsSnapshot = _container.SnapshotEtags();
        var timestampsSnapshot = _container.SnapshotTimestamps();
        var changeFeedCount = _container.GetChangeFeedCount();
        _container.BeginBatchTracking();

        var operationResults = new List<(HttpStatusCode status, bool isSuccess)>();
        var failedIndex = -1;
        HttpStatusCode failedStatusCode = default;
        var failedSubStatusCode = 0;

        for (var i = 0; i < _operations.Count; i++)
        {
            try
            {
                var (execute, opType, _) = _operations[i];
                await execute();
                var statusCode = opType switch
                {
                    BatchOpType.Read => HttpStatusCode.OK,
                    BatchOpType.Delete => HttpStatusCode.NoContent,
                    BatchOpType.Replace => HttpStatusCode.OK,
                    BatchOpType.Patch => HttpStatusCode.OK,
                    BatchOpType.Upsert => _opStatusCodes.TryGetValue(i, out var sc) ? sc : HttpStatusCode.Created,
                    _ => HttpStatusCode.Created
                };
                operationResults.Add((statusCode, true));
            }
            catch (CosmosException ex)
            {
                failedIndex = i;
                failedStatusCode = ex.StatusCode;
                failedSubStatusCode = ex.SubStatusCode;
                _opSubStatusCodes[i] = ex.SubStatusCode;
                operationResults.Add((ex.StatusCode, false));
                break;
            }
        }

        if (failedIndex >= 0)
        {
            var touchedKeys = _container.EndBatchTracking();
            _container.RestoreSnapshot(itemsSnapshot, etagsSnapshot, timestampsSnapshot, changeFeedCount, touchedKeys);
            for (var i = 0; i < failedIndex; i++)
            {
                operationResults[i] = (HttpStatusCode.FailedDependency, false);
            }

            for (var i = failedIndex + 1; i < _operations.Count; i++)
            {
                operationResults.Add((HttpStatusCode.FailedDependency, false));
            }

            return new InMemoryBatchResponse(
                failedStatusCode,
                false,
                operationResults,
                _readResults,
                _writeEtags,
                $"Batch operation at index {failedIndex} failed with status code {(int)failedStatusCode}.",
                _container.CurrentSessionToken,
                failedSubStatusCode,
                _opSubStatusCodes);
        }

        _container.EndBatchTracking();
        return new InMemoryBatchResponse(
            HttpStatusCode.OK,
            true,
            operationResults,
            _readResults,
            _writeEtags,
            sessionToken: _container.CurrentSessionToken,
            opSubStatusCodes: _opSubStatusCodes);
    }

    public override Task<TransactionalBatchResponse> ExecuteAsync(
        TransactionalBatchRequestOptions requestOptions,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(cancellationToken);

    private int FindDuplicateMutatingIdIndex()
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < _operations.Count; i++)
        {
            var (_, opType, targetId) = _operations[i];
            if (opType == BatchOpType.Read || string.IsNullOrEmpty(targetId))
                continue;

            if (!seenIds.Add(targetId))
                return i;
        }

        return -1;
    }

    private static string? TryExtractId(string json)
    {
        try
        {
            return JObject.Parse(json)["id"]?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
