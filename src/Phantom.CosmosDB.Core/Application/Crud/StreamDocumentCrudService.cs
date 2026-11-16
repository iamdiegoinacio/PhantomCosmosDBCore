#nullable enable
using System.Net;
using Phantom.CosmosDB.Core.Domain.Primitives;
using Phantom.CosmosDB.Core.Domain.Validation;
using Phantom.CosmosDB.Core.Infrastructure.SdkInterop;
using Phantom.CosmosDB.Core.Infrastructure.Storage;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CosmosResponseFactory = Phantom.CosmosDB.Core.Infrastructure.SdkInterop.CosmosResponseFactory;

namespace Phantom.CosmosDB.Core.Application.Crud;

/// <summary>
/// Stream (<see cref="ResponseMessage"/>) single-item operations. Unlike the typed variants, validation
/// failures are reported as HTTP status codes on the response rather than thrown.
/// </summary>
internal static class StreamDocumentCrudService
{
    private const int InconsistentPartitionKeySubStatus = CosmosSubStatusCodes.PartitionKeyMismatch;

    public static async Task<ResponseMessage> CreateItemStreamAsync(
        DocumentOperationContext context,
        Stream streamPayload,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = context.Store;

        if (!TryParsePayload(context, streamPayload, out var json, out var jObj, out var failure))
            return failure!;

        jObj = context.ExecutePreTriggers(requestOptions, jObj!, "Create");
        json = jObj.ToString(Formatting.None);
        if (!DocumentValidator.IsDocumentSizeValid(json))
            return Respond(context, HttpStatusCode.RequestEntityTooLarge);
        if (!DocumentValidator.IsPerItemTtlValid(jObj))
            return Respond(context, HttpStatusCode.BadRequest);

        var itemId = jObj["id"]?.ToString() ?? Guid.NewGuid().ToString();
        if (!CosmosResourceNameValidator.IsDocumentIdValid(itemId))
            return Respond(context, HttpStatusCode.BadRequest);

        if (!PartitionKeyProcessor.IsPartitionKeyConsistent(partitionKey, jObj, context.PartitionKeyPaths))
            return Respond(context, HttpStatusCode.BadRequest, subStatusCode: InconsistentPartitionKeySubStatus);

        var pk = PartitionKeyProcessor.ExtractPartitionKeyValue(partitionKey, jObj, context.PartitionKeyPaths);
        var key = (itemId, pk!);

        return await DocumentWriteCommitter.WithItemLockAsync(store, key, () =>
        {
            store.EvictIfExpired(key, context.DefaultTimeToLive, context.PartitionKeyPaths, context.ChangeFeedLog);

            using (UniqueKeyWriteScope.Enter(context))
            {
                if (!UniqueKeyValidator.ValidateUniqueKeysStream(
                        context.ContainerProperties.UniqueKeyPolicy, store.Items, jObj, pk))
                    return Respond(context, HttpStatusCode.Conflict, subStatusCode: CosmosSubStatusCodes.UniqueKeyConstraintViolation);
                if (!store.Items.TryAdd(key, json))
                    return Respond(context, HttpStatusCode.Conflict);
            }

            InMemoryItemStore.TrackBatchWrite(key);
            store.AddInsertionOrder(key);
            var committed = DocumentWriteCommitter.Write(context, key, json);

            try
            {
                context.ExecutePostTriggers(requestOptions, JsonParseHelpers.ParseJson(committed.Json), "Create");
                context.ChangeFeedLog.RecordChangeFeed(itemId, pk, store.Items[key]);
            }
            catch
            {
                DocumentWriteCommitter.Remove(store, key);
                throw;
            }

            return Respond(context, HttpStatusCode.Created, BodyUnlessSuppressed(requestOptions, committed.Json), committed.ETag);
        }, cancellationToken);
    }

    public static Task<ResponseMessage> ReadItemStreamAsync(
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
            return Task.FromResult(Respond(context, HttpStatusCode.NotFound));
        }

        var etag = store.Etags.GetValueOrDefault(key);
        if (!store.CheckIfNoneMatchStream(requestOptions, key))
        {
            return Task.FromResult(Respond(context, HttpStatusCode.NotModified, etag: etag));
        }

        return Task.FromResult(Respond(context, HttpStatusCode.OK, json, etag));
    }

    public static async Task<ResponseMessage> UpsertItemStreamAsync(
        DocumentOperationContext context,
        Stream streamPayload,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = context.Store;

        if (!TryParsePayload(context, streamPayload, out var json, out var jObj, out var failure))
            return failure!;

        jObj = context.ExecutePreTriggers(requestOptions, jObj!, "Upsert");
        json = jObj.ToString(Formatting.None);
        if (!DocumentValidator.IsDocumentSizeValid(json))
            return Respond(context, HttpStatusCode.RequestEntityTooLarge);
        if (!DocumentValidator.IsPerItemTtlValid(jObj))
            return Respond(context, HttpStatusCode.BadRequest);

        var rawItemId = jObj["id"]?.ToString();
        if (!CosmosResourceNameValidator.IsDocumentIdValid(rawItemId))
            return Respond(context, HttpStatusCode.BadRequest);
        var itemId = rawItemId!;
        if (!PartitionKeyProcessor.IsPartitionKeyConsistent(partitionKey, jObj, context.PartitionKeyPaths))
            return Respond(context, HttpStatusCode.BadRequest, subStatusCode: InconsistentPartitionKeySubStatus);

        var pk = PartitionKeyProcessor.ExtractPartitionKeyValue(partitionKey, jObj, context.PartitionKeyPaths);
        var key = (itemId, pk!);

        return await DocumentWriteCommitter.WithItemLockAsync(store, key, () =>
        {
            store.EvictIfExpired(key, context.DefaultTimeToLive, context.PartitionKeyPaths, context.ChangeFeedLog);
            if (!store.CheckIfMatchStream(requestOptions, key))
                return Respond(context, HttpStatusCode.PreconditionFailed);

            EntrySnapshot previous;
            CommittedWrite committed;
            using (UniqueKeyWriteScope.Enter(context))
            {
                if (!UniqueKeyValidator.ValidateUniqueKeysStream(
                        context.ContainerProperties.UniqueKeyPolicy, store.Items, jObj, pk, excludeItemId: itemId))
                    return Respond(context, HttpStatusCode.Conflict, subStatusCode: CosmosSubStatusCodes.UniqueKeyConstraintViolation);

                previous = DocumentWriteCommitter.Capture(store, key);
                committed = DocumentWriteCommitter.Write(context, key, json);
            }

            InMemoryItemStore.TrackBatchWrite(key);
            if (!previous.Exists) store.AddInsertionOrder(key);

            try
            {
                context.ExecutePostTriggers(requestOptions, JsonParseHelpers.ParseJson(committed.Json), "Upsert");
                context.ChangeFeedLog.RecordChangeFeed(itemId, pk, store.Items[key]);
            }
            catch
            {
                DocumentWriteCommitter.RollbackUpsert(store, key, previous);
                throw;
            }

            return Respond(
                context,
                previous.Exists ? HttpStatusCode.OK : HttpStatusCode.Created,
                BodyUnlessSuppressed(requestOptions, committed.Json),
                committed.ETag);
        }, cancellationToken);
    }

    public static async Task<ResponseMessage> ReplaceItemStreamAsync(
        DocumentOperationContext context,
        Stream streamPayload,
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = context.Store;

        var rawJson = CosmosResponseFactory.ReadStream(streamPayload);
        if (!DocumentValidator.IsDocumentSizeValid(rawJson))
            return Respond(context, HttpStatusCode.RequestEntityTooLarge);

        var key = (id, PartitionKeyProcessor.PartitionKeyToString(partitionKey)!);
        var pk = key.Item2;

        JObject jObj;
        try { jObj = JsonParseHelpers.ParseJson(rawJson); }
        catch (JsonReaderException) { return Respond(context, HttpStatusCode.BadRequest); }

        if (!PartitionKeyProcessor.IsPartitionKeyConsistent(partitionKey, jObj, context.PartitionKeyPaths))
            return Respond(context, HttpStatusCode.BadRequest, subStatusCode: InconsistentPartitionKeySubStatus);

        if (!CosmosResourceNameValidator.IsDocumentIdValid(id))
            return Respond(context, HttpStatusCode.BadRequest);

        var bodyId = jObj["id"]?.ToString();
        if (bodyId is not null && bodyId != id)
            return Respond(context, HttpStatusCode.BadRequest);

        return await DocumentWriteCommitter.WithItemLockAsync(store, key, () =>
        {
            if (!store.Items.TryGetValue(key, out var previousJson) || store.IsExpired(key, context.DefaultTimeToLive))
            {
                store.EvictIfExpired(key, context.DefaultTimeToLive, context.PartitionKeyPaths, context.ChangeFeedLog);
                return Respond(context, HttpStatusCode.NotFound);
            }

            if (!store.CheckIfMatchStream(requestOptions, key))
                return Respond(context, HttpStatusCode.PreconditionFailed);

            var previous = DocumentWriteCommitter.Capture(store, key, previousJson);

            jObj = context.ExecutePreTriggers(requestOptions, jObj, "Replace");
            var json = jObj.ToString(Formatting.None);
            if (!DocumentValidator.IsDocumentSizeValid(json))
                return Respond(context, HttpStatusCode.RequestEntityTooLarge);
            if (!DocumentValidator.IsPerItemTtlValid(jObj))
                return Respond(context, HttpStatusCode.BadRequest);

            CommittedWrite committed;
            using (UniqueKeyWriteScope.Enter(context))
            {
                if (!UniqueKeyValidator.ValidateUniqueKeysStream(
                        context.ContainerProperties.UniqueKeyPolicy, store.Items, jObj, pk, excludeItemId: id))
                    return Respond(context, HttpStatusCode.Conflict, subStatusCode: CosmosSubStatusCodes.UniqueKeyConstraintViolation);

                committed = DocumentWriteCommitter.Write(context, key, json);
            }

            InMemoryItemStore.TrackBatchWrite(key);
            try
            {
                context.ExecutePostTriggers(requestOptions, JsonParseHelpers.ParseJson(committed.Json), "Replace");
                context.ChangeFeedLog.RecordChangeFeed(id, pk, store.Items[key]);
            }
            catch
            {
                DocumentWriteCommitter.Restore(store, key, previous);
                throw;
            }

            return Respond(context, HttpStatusCode.OK, BodyUnlessSuppressed(requestOptions, committed.Json), committed.ETag);
        }, cancellationToken);
    }

    public static async Task<ResponseMessage> DeleteItemStreamAsync(
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
                return Respond(context, HttpStatusCode.NotFound);
            }

            if (!store.CheckIfMatchStream(requestOptions, key))
                return Respond(context, HttpStatusCode.PreconditionFailed);

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

            return Respond(context, HttpStatusCode.NoContent);
        }, cancellationToken);
    }

    /// <summary>
    /// Reads and validates a request payload (size + JSON syntax). On failure <paramref name="failure"/>
    /// holds the HTTP response to return.
    /// </summary>
    private static bool TryParsePayload(
        DocumentOperationContext context,
        Stream streamPayload,
        out string json,
        out JObject? document,
        out ResponseMessage? failure)
    {
        json = CosmosResponseFactory.ReadStream(streamPayload);
        document = null;
        failure = null;

        if (!DocumentValidator.IsDocumentSizeValid(json))
        {
            failure = Respond(context, HttpStatusCode.RequestEntityTooLarge);
            return false;
        }

        try
        {
            document = JsonParseHelpers.ParseJson(json);
            return true;
        }
        catch (JsonReaderException)
        {
            failure = Respond(context, HttpStatusCode.BadRequest);
            return false;
        }
    }

    private static string? BodyUnlessSuppressed(ItemRequestOptions? requestOptions, string json)
        => requestOptions?.EnableContentResponseOnWrite == false ? null : json;

    private static ResponseMessage Respond(
        DocumentOperationContext context,
        HttpStatusCode statusCode,
        string? json = null,
        string? etag = null,
        int subStatusCode = 0)
        => CosmosResponseFactory.CreateResponseMessage(statusCode, context.SessionToken, json, etag, subStatusCode);
}
