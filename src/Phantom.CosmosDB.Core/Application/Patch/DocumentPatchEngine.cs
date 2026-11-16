#nullable enable
using System.Net;
using Phantom.CosmosDB.Core.Application.Query;
using Phantom.CosmosDB.Core.Application.Query.Evaluation;
using Phantom.CosmosDB.Core.Domain.Primitives;
using Phantom.CosmosDB.Core.Domain.Validation;
using Phantom.CosmosDB.Core.Infrastructure.SdkInterop;
using Phantom.CosmosDB.Core.Infrastructure.Storage;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Phantom.CosmosDB.Core.Domain.Primitives.ContainerConstants;
using CosmosResponseFactory = Phantom.CosmosDB.Core.Infrastructure.SdkInterop.CosmosResponseFactory;

namespace Phantom.CosmosDB.Core.Application.Patch;

/// <summary>
/// Orchestrates typed and stream Patch operations with filter predicate evaluation,
/// pre/post-trigger execution, unique key validation, and change feed recording.
/// </summary>
internal static class DocumentPatchEngine
{
    public static async Task<ItemResponse<T>> PatchItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        IReadOnlyList<PatchOperation> patchOperations,
        PatchItemRequestOptions? requestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        string containerId,
        IReadOnlyList<string> partitionKeyPaths,
        ContainerProperties containerProperties,
        int? defaultTimeToLive,
        JsonSerializerSettings jsonSettings,
        Func<IDictionary<string, object?>, QueryExecutionContext> createQueryContext,
        Func<ItemRequestOptions?, JObject, string, JObject> executePreTriggers,
        Func<ItemRequestOptions?, JObject, string, JObject?> executePostTriggers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (patchOperations is null || patchOperations.Count == 0)
        {
            throw InMemoryCosmosException.Create("Patch request has no operations.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        if (patchOperations.Count > 10)
        {
            throw InMemoryCosmosException.Create("Patch request has too many operations.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        DocumentValidator.ValidatePatchPaths(patchOperations, partitionKeyPaths, containerProperties);

        var pk = PartitionKeyProcessor.PartitionKeyToString(partitionKey);
        var key = (id, pk!);

        var itemLock = store.GetOrAddLock(key);
        await itemLock.WaitAsync(cancellationToken);
        try
        {
            if (!store.Items.TryGetValue(key, out var existingJson) || store.IsExpired(key, defaultTimeToLive))
            {
                store.EvictIfExpired(key, defaultTimeToLive, partitionKeyPaths, changeFeedLog);
                throw InMemoryCosmosException.Create(
                    $"Resource Not Found. Entity with the specified id does not exist in the system. id = {id}",
                    HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
            }

            store.CheckIfMatch(requestOptions, key);
            store.CheckIfNoneMatchForWrite(requestOptions, key);

            var jObj = JsonParseHelpers.ParseJson(existingJson);

            if (requestOptions?.FilterPredicate is not null)
            {
                var predicateSql = $"SELECT * {requestOptions.FilterPredicate}";
                var predicateParsed = CosmosSqlParser.Parse(predicateSql);
                if (predicateParsed.Where is not null)
                {
                    var matches = WhereExpressionEvaluator.Evaluate(
                        predicateParsed.Where,
                        jObj,
                        predicateParsed.FromAlias,
                        createQueryContext(new Dictionary<string, object?>()),
                        null,
                        treatUndefinedAsNull: true);
                    if (!matches)
                    {
                        throw InMemoryCosmosException.Create("Precondition Failed",
                            HttpStatusCode.PreconditionFailed, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                    }
                }
            }

            jObj = executePreTriggers(requestOptions, jObj, "Replace");

            PatchOperationApplier.ApplyPatchOperations(jObj, patchOperations);
            DocumentValidator.ValidatePerItemTtl(jObj);
            var updatedJson = jObj.ToString(Formatting.None);
            DocumentValidator.ValidateDocumentSize(updatedJson);

            var hasUniqueKeys = containerProperties.UniqueKeyPolicy?.UniqueKeys.Count > 0;
            string etag;
            if (hasUniqueKeys)
            {
                lock (store.UniqueKeyWriteLock)
                {
                    UniqueKeyValidator.ValidateUniqueKeys(
                        containerProperties.UniqueKeyPolicy, store.Items, jObj, pk, excludeItemId: id);
                    etag = ETagValue.GenerateNextString();
                    store.Etags[key] = etag;
                    store.Timestamps[key] = DateTimeOffset.UtcNow;
                    var enriched = InMemoryItemStore.EnrichWithSystemProperties(
                        containerId, updatedJson, etag, store.Timestamps[key]);
                    store.Items[key] = enriched;
                }
            }
            else
            {
                etag = ETagValue.GenerateNextString();
                store.Etags[key] = etag;
                store.Timestamps[key] = DateTimeOffset.UtcNow;
                var enriched = InMemoryItemStore.EnrichWithSystemProperties(
                    containerId, updatedJson, etag, store.Timestamps[key]);
                store.Items[key] = enriched;
            }
            var enrichedJson = store.Items[key];

            InMemoryItemStore.TrackBatchWrite(key);
            executePostTriggers(requestOptions, JsonParseHelpers.ParseJson(enrichedJson), "Replace");
            changeFeedLog.RecordChangeFeed(id, pk, store.Items[key]);

            var suppressContent = requestOptions?.EnableContentResponseOnWrite == false;
            var result = JsonConvert.DeserializeObject<T>(enrichedJson, jsonSettings)!;
            return CosmosResponseFactory.CreateItemResponse(
                result, HttpStatusCode.OK, changeFeedLog.CurrentSessionToken, etag, suppressContent);
        }
        finally
        {
            itemLock.Release();
        }
    }

    public static async Task<ResponseMessage> PatchItemStreamAsync(
        string id,
        PartitionKey partitionKey,
        IReadOnlyList<PatchOperation> patchOperations,
        PatchItemRequestOptions? requestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        string containerId,
        IReadOnlyList<string> partitionKeyPaths,
        ContainerProperties containerProperties,
        int? defaultTimeToLive,
        Func<IDictionary<string, object?>, QueryExecutionContext> createQueryContext,
        Func<ItemRequestOptions?, JObject, string, JObject> executePreTriggers,
        Func<ItemRequestOptions?, JObject, string, JObject?> executePostTriggers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (patchOperations is null || patchOperations.Count == 0 || patchOperations.Count > 10)
        {
            return CosmosResponseFactory.CreateResponseMessage(
                HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken);
        }

        try
        {
            DocumentValidator.ValidatePatchPaths(patchOperations, partitionKeyPaths, containerProperties);
        }
        catch (CosmosException)
        {
            return CosmosResponseFactory.CreateResponseMessage(
                HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken);
        }

        var pk = PartitionKeyProcessor.PartitionKeyToString(partitionKey);
        var key = (id, pk!);

        var itemLock = store.GetOrAddLock(key);
        await itemLock.WaitAsync(cancellationToken);
        try
        {
            if (!store.Items.TryGetValue(key, out var existingJson) || store.IsExpired(key, defaultTimeToLive))
            {
                store.EvictIfExpired(key, defaultTimeToLive, partitionKeyPaths, changeFeedLog);
                return CosmosResponseFactory.CreateResponseMessage(
                    HttpStatusCode.NotFound, changeFeedLog.CurrentSessionToken);
            }

            if (!store.CheckIfMatchStream(requestOptions, key))
            {
                return CosmosResponseFactory.CreateResponseMessage(
                    HttpStatusCode.PreconditionFailed, changeFeedLog.CurrentSessionToken);
            }

            var jObj = JsonParseHelpers.ParseJson(existingJson);

            if (requestOptions?.FilterPredicate is not null)
            {
                var predicateSql = $"SELECT * {requestOptions.FilterPredicate}";
                var predicateParsed = CosmosSqlParser.Parse(predicateSql);
                if (predicateParsed.Where is not null)
                {
                    var matches = WhereExpressionEvaluator.Evaluate(
                        predicateParsed.Where,
                        jObj,
                        predicateParsed.FromAlias,
                        createQueryContext(new Dictionary<string, object?>()),
                        null,
                        treatUndefinedAsNull: true);
                    if (!matches)
                    {
                        return CosmosResponseFactory.CreateResponseMessage(
                            HttpStatusCode.PreconditionFailed, changeFeedLog.CurrentSessionToken);
                    }
                }
            }

            jObj = executePreTriggers(requestOptions, jObj, "Replace");

            try
            {
                PatchOperationApplier.ApplyPatchOperations(jObj, patchOperations);
            }
            catch (CosmosException ex)
            {
                return CosmosResponseFactory.CreateResponseMessage(
                    ex.StatusCode, changeFeedLog.CurrentSessionToken, subStatusCode: ex.SubStatusCode);
            }

            var updatedJson = jObj.ToString(Formatting.None);
            if (!DocumentValidator.IsDocumentSizeValid(updatedJson))
            {
                return CosmosResponseFactory.CreateResponseMessage(
                    HttpStatusCode.RequestEntityTooLarge, changeFeedLog.CurrentSessionToken);
            }
            if (!DocumentValidator.IsPerItemTtlValid(jObj))
            {
                return CosmosResponseFactory.CreateResponseMessage(
                    HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken);
            }

            var previousEtag = store.Etags.GetValueOrDefault(key);
            var previousTimestamp = store.Timestamps.GetValueOrDefault(key);

            var hasUniqueKeys = containerProperties.UniqueKeyPolicy?.UniqueKeys.Count > 0;
            string etag;
            if (hasUniqueKeys)
            {
                lock (store.UniqueKeyWriteLock)
                {
                    if (!UniqueKeyValidator.ValidateUniqueKeysStream(
                        containerProperties.UniqueKeyPolicy, store.Items, jObj, pk, excludeItemId: id))
                    {
                        return CosmosResponseFactory.CreateResponseMessage(
                            HttpStatusCode.Conflict,
                            changeFeedLog.CurrentSessionToken,
                            subStatusCode: CosmosSubStatusCodes.UniqueKeyConstraintViolation);
                    }
                    etag = ETagValue.GenerateNextString();
                    store.Etags[key] = etag;
                    store.Timestamps[key] = DateTimeOffset.UtcNow;
                    var enriched = InMemoryItemStore.EnrichWithSystemProperties(
                        containerId, updatedJson, etag, store.Timestamps[key]);
                    store.Items[key] = enriched;
                }
            }
            else
            {
                etag = ETagValue.GenerateNextString();
                store.Etags[key] = etag;
                store.Timestamps[key] = DateTimeOffset.UtcNow;
                var enriched = InMemoryItemStore.EnrichWithSystemProperties(
                    containerId, updatedJson, etag, store.Timestamps[key]);
                store.Items[key] = enriched;
            }
            var enrichedJson = store.Items[key];

            InMemoryItemStore.TrackBatchWrite(key);
            try
            {
                executePostTriggers(requestOptions, JsonParseHelpers.ParseJson(enrichedJson), "Replace");
                changeFeedLog.RecordChangeFeed(id, pk, store.Items[key]);
            }
            catch
            {
                store.Items[key] = existingJson;
                if (previousEtag is not null) store.Etags[key] = previousEtag;
                store.Timestamps[key] = previousTimestamp;
                throw;
            }

            return CosmosResponseFactory.CreateResponseMessage(
                HttpStatusCode.OK,
                changeFeedLog.CurrentSessionToken,
                requestOptions?.EnableContentResponseOnWrite == false ? null : enrichedJson,
                etag);
        }
        finally
        {
            itemLock.Release();
        }
    }
}
