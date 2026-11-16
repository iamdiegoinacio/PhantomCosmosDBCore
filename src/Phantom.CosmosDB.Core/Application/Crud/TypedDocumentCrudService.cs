#nullable enable
using System.Net;
using Phantom.CosmosDB.Core.Domain.Validation;
using Phantom.CosmosDB.Core.Infrastructure.SdkInterop;
using Phantom.CosmosDB.Core.Infrastructure.Storage;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CosmosResponseFactory = Phantom.CosmosDB.Core.Infrastructure.SdkInterop.CosmosResponseFactory;

namespace Phantom.CosmosDB.Core.Application.Crud;

/// <summary>
/// Typed (<see cref="ItemResponse{T}"/>) single-item operations: Create, Read, Upsert, Replace
/// and Delete, with concurrency locking, triggers, unique-key validation, change-feed tracking
/// and rollback on post-trigger failure.
/// </summary>
internal static class TypedDocumentCrudService
{
    public static async Task<ItemResponse<T>> CreateItemAsync<T>(
        DocumentOperationContext context,
        T item,
        PartitionKey? partitionKey,
        ItemRequestOptions? requestOptions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = context.Store;
        var (json, jObj) = SerializeAndParse(context, item);

        jObj = context.ExecutePreTriggers(requestOptions, jObj, "Create");
        json = jObj.ToString(Formatting.None);
        DocumentValidator.ValidateDocumentSize(json);

        var rawItemId = jObj["id"]?.ToString();
        CosmosResourceNameValidator.ValidateDocumentId(rawItemId);
        var itemId = rawItemId!;

        PartitionKeyProcessor.ValidatePartitionKeyConsistency(partitionKey, jObj, context.PartitionKeyPaths);
        DocumentValidator.ValidatePerItemTtl(jObj);
        var pk = PartitionKeyProcessor.ExtractPartitionKeyValue(partitionKey, jObj, context.PartitionKeyPaths);
        var key = (itemId, pk!);

        return await DocumentWriteCommitter.WithItemLockAsync(store, key, () =>
        {
            store.EvictIfExpired(key, context.DefaultTimeToLive, context.PartitionKeyPaths, context.ChangeFeedLog);

            using (UniqueKeyWriteScope.Enter(context))
            {
                UniqueKeyValidator.ValidateUniqueKeys(context.ContainerProperties.UniqueKeyPolicy, store.Items, jObj, pk);
                if (!store.Items.TryAdd(key, json))
                {
                    throw DocumentExceptions.AlreadyExists(itemId);
                }
            }

            InMemoryItemStore.TrackBatchWrite(key);
            store.AddInsertionOrder(key);
            var committed = DocumentWriteCommitter.Write(context, key, json);

            try
            {
                var committedDoc = JsonParseHelpers.ParseJson(store.Items[key]);
                var responseBodyOverride = context.ExecutePostTriggers(requestOptions, committedDoc, "Create");
                DocumentWriteCommitter.ApplyPostTriggerChanges(store, key, committedDoc);
                context.ChangeFeedLog.RecordChangeFeed(itemId, pk, store.Items[key]);

                var suppressContent = requestOptions?.EnableContentResponseOnWrite == false;
                var body = responseBodyOverride is not null && !suppressContent
                    ? responseBodyOverride.ToObject<T>(JsonSerializer.Create(context.JsonSettings))!
                    : JsonConvert.DeserializeObject<T>(store.Items[key], context.JsonSettings)!;

                return CosmosResponseFactory.CreateItemResponse(
                    body, HttpStatusCode.Created, context.SessionToken, committed.ETag, suppressContent);
            }
            catch
            {
                DocumentWriteCommitter.Remove(store, key);
                throw;
            }
        }, cancellationToken);
    }

    public static Task<ItemResponse<T>> ReadItemAsync<T>(
        DocumentOperationContext context,
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = context.Store;
        var key = (id, PartitionKeyProcessor.PartitionKeyToString(partitionKey)!);

        if (!store.Items.TryGetValue(key, out var json) || store.IsExpired(key, context.DefaultTimeToLive))
        {
            store.EvictIfExpired(key, context.DefaultTimeToLive, context.PartitionKeyPaths, context.ChangeFeedLog);
            throw DocumentExceptions.NotFound(id);
        }

        store.CheckIfNoneMatch(requestOptions, key);
        var etag = store.Etags.GetValueOrDefault(key);
        var result = JsonConvert.DeserializeObject<T>(json, context.JsonSettings)!;
        return Task.FromResult(
            CosmosResponseFactory.CreateItemResponse(result, HttpStatusCode.OK, context.SessionToken, etag));
    }

    public static async Task<ItemResponse<T>> UpsertItemAsync<T>(
        DocumentOperationContext context,
        T item,
        PartitionKey? partitionKey,
        ItemRequestOptions? requestOptions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = context.Store;
        var (json, jObj) = SerializeAndParse(context, item);

        jObj = context.ExecutePreTriggers(requestOptions, jObj, "Upsert");
        json = jObj.ToString(Formatting.None);
        DocumentValidator.ValidateDocumentSize(json);

        var rawItemId = jObj["id"]?.ToString();
        CosmosResourceNameValidator.ValidateDocumentId(rawItemId);
        var itemId = rawItemId!;
        PartitionKeyProcessor.ValidatePartitionKeyConsistency(partitionKey, jObj, context.PartitionKeyPaths);
        DocumentValidator.ValidatePerItemTtl(jObj);
        var pk = PartitionKeyProcessor.ExtractPartitionKeyValue(partitionKey, jObj, context.PartitionKeyPaths);
        var key = (itemId, pk!);

        return await DocumentWriteCommitter.WithItemLockAsync(store, key, () =>
        {
            store.EvictIfExpired(key, context.DefaultTimeToLive, context.PartitionKeyPaths, context.ChangeFeedLog);
            store.CheckIfMatch(requestOptions, key);

            EntrySnapshot previous;
            CommittedWrite committed;
            using (UniqueKeyWriteScope.Enter(context))
            {
                UniqueKeyValidator.ValidateUniqueKeys(
                    context.ContainerProperties.UniqueKeyPolicy, store.Items, jObj, pk, excludeItemId: itemId);
                previous = DocumentWriteCommitter.Capture(store, key);
                committed = DocumentWriteCommitter.Write(context, key, json);
            }

            InMemoryItemStore.TrackBatchWrite(key);
            if (!previous.Exists) store.AddInsertionOrder(key);

            try
            {
                var committedDoc = JsonParseHelpers.ParseJson(store.Items[key]);
                context.ExecutePostTriggers(requestOptions, committedDoc, "Upsert");
                DocumentWriteCommitter.ApplyPostTriggerChanges(store, key, committedDoc);
                context.ChangeFeedLog.RecordChangeFeed(itemId, pk, store.Items[key]);
            }
            catch
            {
                DocumentWriteCommitter.RollbackUpsert(store, key, previous);
                throw;
            }

            var suppressContent = requestOptions?.EnableContentResponseOnWrite == false;
            return CosmosResponseFactory.CreateItemResponse(
                JsonConvert.DeserializeObject<T>(store.Items[key], context.JsonSettings)!,
                previous.Exists ? HttpStatusCode.OK : HttpStatusCode.Created,
                context.SessionToken,
                committed.ETag,
                suppressContent);
        }, cancellationToken);
    }

    public static async Task<ItemResponse<T>> ReplaceItemAsync<T>(
        DocumentOperationContext context,
        T item,
        string id,
        PartitionKey? partitionKey,
        ItemRequestOptions? requestOptions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = context.Store;
        var (json, jObj) = SerializeAndParse(context, item);

        CosmosResourceNameValidator.ValidateDocumentId(id);
        var bodyId = jObj["id"]?.ToString();
        if (bodyId is not null && bodyId != id)
        {
            throw DocumentExceptions.BadRequest("The 'id' property in the body does not match the 'id' parameter.");
        }

        PartitionKeyProcessor.ValidatePartitionKeyConsistency(partitionKey, jObj, context.PartitionKeyPaths);
        DocumentValidator.ValidatePerItemTtl(jObj);

        var pk = PartitionKeyProcessor.ExtractPartitionKeyValue(partitionKey, jObj, context.PartitionKeyPaths);
        var key = (id, pk!);

        return await DocumentWriteCommitter.WithItemLockAsync(store, key, () =>
        {
            if (!store.Items.ContainsKey(key) || store.IsExpired(key, context.DefaultTimeToLive))
            {
                store.EvictIfExpired(key, context.DefaultTimeToLive, context.PartitionKeyPaths, context.ChangeFeedLog);
                throw DocumentExceptions.NotFound(id);
            }

            store.CheckIfMatch(requestOptions, key);

            jObj = context.ExecutePreTriggers(requestOptions, jObj, "Replace");
            json = jObj.ToString(Formatting.None);
            DocumentValidator.ValidateDocumentSize(json);

            EntrySnapshot previous;
            CommittedWrite committed;
            using (UniqueKeyWriteScope.Enter(context))
            {
                UniqueKeyValidator.ValidateUniqueKeys(
                    context.ContainerProperties.UniqueKeyPolicy, store.Items, jObj, pk, excludeItemId: id);
                previous = DocumentWriteCommitter.Capture(store, key);
                committed = DocumentWriteCommitter.Write(context, key, json);
            }

            InMemoryItemStore.TrackBatchWrite(key);
            try
            {
                var committedDoc = JsonParseHelpers.ParseJson(store.Items[key]);
                context.ExecutePostTriggers(requestOptions, committedDoc, "Replace");
                DocumentWriteCommitter.ApplyPostTriggerChanges(store, key, committedDoc);
                context.ChangeFeedLog.RecordChangeFeed(id, pk, store.Items[key]);
            }
            catch
            {
                DocumentWriteCommitter.Restore(store, key, previous);
                throw;
            }

            var suppressContent = requestOptions?.EnableContentResponseOnWrite == false;
            return CosmosResponseFactory.CreateItemResponse(
                JsonConvert.DeserializeObject<T>(store.Items[key], context.JsonSettings)!,
                HttpStatusCode.OK,
                context.SessionToken,
                committed.ETag,
                suppressContent);
        }, cancellationToken);
    }

    public static async Task<ItemResponse<T>> DeleteItemAsync<T>(
        DocumentOperationContext context,
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = context.Store;
        var pk = PartitionKeyProcessor.PartitionKeyToString(partitionKey);
        var key = (id, pk!);

        return await DocumentWriteCommitter.WithItemLockAsync(store, key, () =>
        {
            if (!store.Items.TryGetValue(key, out var existingJson) || store.IsExpired(key, context.DefaultTimeToLive))
            {
                store.EvictIfExpired(key, context.DefaultTimeToLive, context.PartitionKeyPaths, context.ChangeFeedLog);
                throw DocumentExceptions.NotFound(id);
            }

            store.CheckIfMatch(requestOptions, key);
            context.ExecutePreTriggers(requestOptions, JsonParseHelpers.ParseJson(existingJson), "Delete");

            var previous = DocumentWriteCommitter.Capture(store, key, existingJson);
            DocumentWriteCommitter.Remove(store, key);

            InMemoryItemStore.TrackBatchWrite(key);
            try
            {
                context.ExecutePostTriggers(requestOptions, JsonParseHelpers.ParseJson(existingJson), "Delete");
                context.ChangeFeedLog.RecordDeleteTombstone(id, pk, context.PartitionKeyPaths, partitionKey);
            }
            catch
            {
                DocumentWriteCommitter.RestoreDeleted(store, key, previous);
                throw;
            }

            return CosmosResponseFactory.CreateItemResponse(
                default(T)!, HttpStatusCode.NoContent, context.SessionToken);
        }, cancellationToken);
    }

    private static (string Json, JObject Document) SerializeAndParse<T>(DocumentOperationContext context, T item)
    {
        var json = JsonConvert.SerializeObject(item, context.JsonSettings);
        DocumentValidator.ValidateDocumentSize(json);
        return (json, JsonParseHelpers.ParseJson(json));
    }
}
