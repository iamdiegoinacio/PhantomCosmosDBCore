#nullable enable
using System.Net;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using CosmosDB.InMemoryEmulator.Domain.Validation;
using CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop;
using CosmosDB.InMemoryEmulator.Infrastructure.Storage;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static CosmosDB.InMemoryEmulator.Domain.Primitives.ContainerConstants;
using CosmosResponseFactory = CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop.CosmosResponseFactory;

namespace CosmosDB.InMemoryEmulator.Application.Crud;

/// <summary>
/// Orchestrates typed and stream CRUD operations (Create, Read, Upsert, Replace, Delete,
/// ReadMany, DeleteAllItemsByPartitionKey) with concurrency locking, triggers, unique key
/// validation, and change feed tracking.
/// </summary>
internal static class DocumentCrudService
{
    public static async Task<ItemResponse<T>> CreateItemAsync<T>(
        T item,
        PartitionKey? partitionKey,
        ItemRequestOptions? requestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        string containerId,
        IReadOnlyList<string> partitionKeyPaths,
        ContainerProperties containerProperties,
        int? defaultTimeToLive,
        JsonSerializerSettings jsonSettings,
        Func<ItemRequestOptions?, JObject, string, JObject> executePreTriggers,
        Func<ItemRequestOptions?, JObject, string, JObject?> executePostTriggers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonConvert.SerializeObject(item, jsonSettings);
        DocumentValidator.ValidateDocumentSize(json);
        var jObj = JsonParseHelpers.ParseJson(json);

        jObj = executePreTriggers(requestOptions, jObj, "Create");
        json = jObj.ToString(Formatting.None);
        DocumentValidator.ValidateDocumentSize(json);

        var itemId = jObj["id"]?.ToString() ?? throw new InvalidOperationException("Item must have an 'id' property.");
        if (itemId.Length == 0)
        {
            throw InMemoryCosmosException.Create(
                "The 'id' property cannot be an empty string.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        PartitionKeyProcessor.ValidatePartitionKeyConsistency(partitionKey, jObj, partitionKeyPaths);
        DocumentValidator.ValidatePerItemTtl(jObj);
        var pk = PartitionKeyProcessor.ExtractPartitionKeyValue(partitionKey, jObj, partitionKeyPaths);
        var key = (itemId, pk!);

        var itemLock = store.GetOrAddLock(key);
        await itemLock.WaitAsync(cancellationToken);
        try
        {
            store.EvictIfExpired(key, defaultTimeToLive, partitionKeyPaths, changeFeedLog);

            var hasUniqueKeys = containerProperties.UniqueKeyPolicy?.UniqueKeys.Count > 0;
            if (hasUniqueKeys)
            {
                lock (store.UniqueKeyWriteLock)
                {
                    UniqueKeyValidator.ValidateUniqueKeys(containerProperties.UniqueKeyPolicy, store.Items, jObj, pk);
                    if (!store.Items.TryAdd(key, json))
                    {
                        throw InMemoryCosmosException.Create(
                            $"Entity with the specified id already exists in the system. id = {itemId}",
                            HttpStatusCode.Conflict, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                    }
                }
            }
            else
            {
                if (!store.Items.TryAdd(key, json))
                {
                    throw InMemoryCosmosException.Create(
                        $"Entity with the specified id already exists in the system. id = {itemId}",
                        HttpStatusCode.Conflict, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                }
            }

            InMemoryItemStore.TrackBatchWrite(key);
            store.AddInsertionOrder(key);
            var etag = ETagValue.GenerateNextString();
            store.Etags[key] = etag;
            store.Timestamps[key] = DateTimeOffset.UtcNow;
            store.Items[key] = InMemoryItemStore.EnrichWithSystemProperties(
                containerId, json, etag, store.Timestamps[key]);

            try
            {
                var committedDoc = JsonParseHelpers.ParseJson(store.Items[key]);
                var responseBodyOverride = executePostTriggers(requestOptions, committedDoc, "Create");
                var postTriggerJson = committedDoc.ToString(Formatting.None);
                if (postTriggerJson != store.Items[key])
                {
                    DocumentValidator.ValidateDocumentSize(postTriggerJson);
                    store.Items[key] = postTriggerJson;
                }

                changeFeedLog.RecordChangeFeed(itemId, pk, store.Items[key]);

                var suppressContent = requestOptions?.EnableContentResponseOnWrite == false;
                if (responseBodyOverride is not null && !suppressContent)
                {
                    return CosmosResponseFactory.CreateItemResponse(
                        responseBodyOverride.ToObject<T>(JsonSerializer.Create(jsonSettings))!,
                        HttpStatusCode.Created,
                        changeFeedLog.CurrentSessionToken,
                        etag,
                        suppressContent);
                }

                return CosmosResponseFactory.CreateItemResponse(
                    JsonConvert.DeserializeObject<T>(store.Items[key], jsonSettings)!,
                    HttpStatusCode.Created,
                    changeFeedLog.CurrentSessionToken,
                    etag,
                    suppressContent);
            }
            catch
            {
                store.Items.TryRemove(key, out _);
                store.Etags.TryRemove(key, out _);
                store.Timestamps.TryRemove(key, out _);
                store.RemoveInsertionOrder(key);
                throw;
            }
        }
        finally
        {
            itemLock.Release();
        }
    }

    public static Task<ItemResponse<T>> ReadItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        IReadOnlyList<string> partitionKeyPaths,
        int? defaultTimeToLive,
        JsonSerializerSettings jsonSettings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pk = PartitionKeyProcessor.PartitionKeyToString(partitionKey);
        var key = (id, pk!);

        if (!store.Items.TryGetValue(key, out var json) || store.IsExpired(key, defaultTimeToLive))
        {
            store.EvictIfExpired(key, defaultTimeToLive, partitionKeyPaths, changeFeedLog);
            throw InMemoryCosmosException.Create(
                $"Resource Not Found. Entity with the specified id does not exist in the system. id = {id}",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        store.CheckIfNoneMatch(requestOptions, key);
        var etag = store.Etags.GetValueOrDefault(key);
        var result = JsonConvert.DeserializeObject<T>(json, jsonSettings)!;
        return Task.FromResult(
            CosmosResponseFactory.CreateItemResponse(result, HttpStatusCode.OK, changeFeedLog.CurrentSessionToken, etag));
    }

    public static async Task<ItemResponse<T>> UpsertItemAsync<T>(
        T item,
        PartitionKey? partitionKey,
        ItemRequestOptions? requestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        string containerId,
        IReadOnlyList<string> partitionKeyPaths,
        ContainerProperties containerProperties,
        int? defaultTimeToLive,
        JsonSerializerSettings jsonSettings,
        Func<ItemRequestOptions?, JObject, string, JObject> executePreTriggers,
        Func<ItemRequestOptions?, JObject, string, JObject?> executePostTriggers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonConvert.SerializeObject(item, jsonSettings);
        DocumentValidator.ValidateDocumentSize(json);
        var jObj = JsonParseHelpers.ParseJson(json);

        jObj = executePreTriggers(requestOptions, jObj, "Upsert");
        json = jObj.ToString(Formatting.None);
        DocumentValidator.ValidateDocumentSize(json);

        var itemId = jObj["id"]?.ToString() ?? throw new InvalidOperationException("Item must have an 'id' property.");
        PartitionKeyProcessor.ValidatePartitionKeyConsistency(partitionKey, jObj, partitionKeyPaths);
        DocumentValidator.ValidatePerItemTtl(jObj);
        var pk = PartitionKeyProcessor.ExtractPartitionKeyValue(partitionKey, jObj, partitionKeyPaths);
        var key = (itemId, pk!);

        var itemLock = store.GetOrAddLock(key);
        await itemLock.WaitAsync(cancellationToken);
        try
        {
            store.EvictIfExpired(key, defaultTimeToLive, partitionKeyPaths, changeFeedLog);
            store.CheckIfMatch(requestOptions, key);

            bool existed;
            string? previousJson;
            string? previousEtag;
            DateTimeOffset? previousTimestamp;
            string etag;
            var hasUniqueKeys = containerProperties.UniqueKeyPolicy?.UniqueKeys.Count > 0;
            if (hasUniqueKeys)
            {
                lock (store.UniqueKeyWriteLock)
                {
                    UniqueKeyValidator.ValidateUniqueKeys(
                        containerProperties.UniqueKeyPolicy, store.Items, jObj, pk, excludeItemId: itemId);
                    existed = store.Items.ContainsKey(key);
                    previousJson = existed ? store.Items[key] : null;
                    previousEtag = existed ? store.Etags.GetValueOrDefault(key) : null;
                    previousTimestamp = existed ? store.Timestamps.GetValueOrDefault(key) : null;
                    etag = ETagValue.GenerateNextString();
                    store.Etags[key] = etag;
                    store.Timestamps[key] = DateTimeOffset.UtcNow;
                    store.Items[key] = InMemoryItemStore.EnrichWithSystemProperties(
                        containerId, json, etag, store.Timestamps[key]);
                }
            }
            else
            {
                existed = store.Items.ContainsKey(key);
                previousJson = existed ? store.Items[key] : null;
                previousEtag = existed ? store.Etags.GetValueOrDefault(key) : null;
                previousTimestamp = existed ? store.Timestamps.GetValueOrDefault(key) : null;
                etag = ETagValue.GenerateNextString();
                store.Etags[key] = etag;
                store.Timestamps[key] = DateTimeOffset.UtcNow;
                store.Items[key] = InMemoryItemStore.EnrichWithSystemProperties(
                    containerId, json, etag, store.Timestamps[key]);
            }

            InMemoryItemStore.TrackBatchWrite(key);
            if (!existed) store.AddInsertionOrder(key);

            try
            {
                var committedDoc = JsonParseHelpers.ParseJson(store.Items[key]);
                executePostTriggers(requestOptions, committedDoc, "Upsert");
                var postTriggerJson = committedDoc.ToString(Formatting.None);
                if (postTriggerJson != store.Items[key])
                {
                    DocumentValidator.ValidateDocumentSize(postTriggerJson);
                    store.Items[key] = postTriggerJson;
                }
                changeFeedLog.RecordChangeFeed(itemId, pk, store.Items[key]);
            }
            catch
            {
                if (existed && previousJson is not null)
                {
                    store.Items[key] = previousJson;
                    store.Etags[key] = previousEtag!;
                    if (previousTimestamp.HasValue) store.Timestamps[key] = previousTimestamp.Value;
                }
                else
                {
                    store.Items.TryRemove(key, out _);
                    store.Etags.TryRemove(key, out _);
                    store.Timestamps.TryRemove(key, out _);
                    store.RemoveInsertionOrder(key);
                }
                throw;
            }

            var suppressContent = requestOptions?.EnableContentResponseOnWrite == false;
            return CosmosResponseFactory.CreateItemResponse(
                JsonConvert.DeserializeObject<T>(store.Items[key], jsonSettings)!,
                existed ? HttpStatusCode.OK : HttpStatusCode.Created,
                changeFeedLog.CurrentSessionToken,
                etag,
                suppressContent);
        }
        finally
        {
            itemLock.Release();
        }
    }

    public static async Task<ItemResponse<T>> ReplaceItemAsync<T>(
        T item,
        string id,
        PartitionKey? partitionKey,
        ItemRequestOptions? requestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        string containerId,
        IReadOnlyList<string> partitionKeyPaths,
        ContainerProperties containerProperties,
        int? defaultTimeToLive,
        JsonSerializerSettings jsonSettings,
        Func<ItemRequestOptions?, JObject, string, JObject> executePreTriggers,
        Func<ItemRequestOptions?, JObject, string, JObject?> executePostTriggers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonConvert.SerializeObject(item, jsonSettings);
        DocumentValidator.ValidateDocumentSize(json);
        var jObj = JsonParseHelpers.ParseJson(json);

        var bodyId = jObj["id"]?.ToString();
        if (bodyId is not null && bodyId != id)
        {
            throw InMemoryCosmosException.Create(
                "The 'id' property in the body does not match the 'id' parameter.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        PartitionKeyProcessor.ValidatePartitionKeyConsistency(partitionKey, jObj, partitionKeyPaths);
        DocumentValidator.ValidatePerItemTtl(jObj);

        var pk = PartitionKeyProcessor.ExtractPartitionKeyValue(partitionKey, jObj, partitionKeyPaths);
        var key = (id, pk!);

        var itemLock = store.GetOrAddLock(key);
        await itemLock.WaitAsync(cancellationToken);
        try
        {
            if (!store.Items.ContainsKey(key) || store.IsExpired(key, defaultTimeToLive))
            {
                store.EvictIfExpired(key, defaultTimeToLive, partitionKeyPaths, changeFeedLog);
                throw InMemoryCosmosException.Create(
                    $"Resource Not Found. Entity with the specified id does not exist in the system. id = {id}",
                    HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
            }

            store.CheckIfMatch(requestOptions, key);

            jObj = executePreTriggers(requestOptions, jObj, "Replace");
            json = jObj.ToString(Formatting.None);
            DocumentValidator.ValidateDocumentSize(json);

            string previousJson;
            string? previousEtag;
            DateTimeOffset previousTimestamp;
            string etag;
            var hasUniqueKeys = containerProperties.UniqueKeyPolicy?.UniqueKeys.Count > 0;
            if (hasUniqueKeys)
            {
                lock (store.UniqueKeyWriteLock)
                {
                    UniqueKeyValidator.ValidateUniqueKeys(
                        containerProperties.UniqueKeyPolicy, store.Items, jObj, pk, excludeItemId: id);
                    previousJson = store.Items[key];
                    previousEtag = store.Etags.GetValueOrDefault(key);
                    previousTimestamp = store.Timestamps.GetValueOrDefault(key);
                    etag = ETagValue.GenerateNextString();
                    store.Etags[key] = etag;
                    store.Timestamps[key] = DateTimeOffset.UtcNow;
                    store.Items[key] = InMemoryItemStore.EnrichWithSystemProperties(
                        containerId, json, etag, store.Timestamps[key]);
                }
            }
            else
            {
                previousJson = store.Items[key];
                previousEtag = store.Etags.GetValueOrDefault(key);
                previousTimestamp = store.Timestamps.GetValueOrDefault(key);
                etag = ETagValue.GenerateNextString();
                store.Etags[key] = etag;
                store.Timestamps[key] = DateTimeOffset.UtcNow;
                store.Items[key] = InMemoryItemStore.EnrichWithSystemProperties(
                    containerId, json, etag, store.Timestamps[key]);
            }

            InMemoryItemStore.TrackBatchWrite(key);
            try
            {
                var committedDoc = JsonParseHelpers.ParseJson(store.Items[key]);
                executePostTriggers(requestOptions, committedDoc, "Replace");
                var postTriggerJson = committedDoc.ToString(Formatting.None);
                if (postTriggerJson != store.Items[key])
                {
                    DocumentValidator.ValidateDocumentSize(postTriggerJson);
                    store.Items[key] = postTriggerJson;
                }
                changeFeedLog.RecordChangeFeed(id, pk, store.Items[key]);
            }
            catch
            {
                store.Items[key] = previousJson;
                store.Etags[key] = previousEtag!;
                store.Timestamps[key] = previousTimestamp;
                throw;
            }

            var suppressContent = requestOptions?.EnableContentResponseOnWrite == false;
            return CosmosResponseFactory.CreateItemResponse(
                JsonConvert.DeserializeObject<T>(store.Items[key], jsonSettings)!,
                HttpStatusCode.OK,
                changeFeedLog.CurrentSessionToken,
                etag,
                suppressContent);
        }
        finally
        {
            itemLock.Release();
        }
    }

    public static async Task<ItemResponse<T>> DeleteItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        IReadOnlyList<string> partitionKeyPaths,
        int? defaultTimeToLive,
        Func<ItemRequestOptions?, JObject, string, JObject> executePreTriggers,
        Func<ItemRequestOptions?, JObject, string, JObject?> executePostTriggers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            executePreTriggers(requestOptions, JsonParseHelpers.ParseJson(existingJson), "Delete");

            var previousEtag = store.Etags.TryGetValue(key, out var e) ? e : null;
            var previousTimestamp = store.Timestamps.TryGetValue(key, out var ts) ? ts : (DateTimeOffset?)null;

            store.Items.TryRemove(key, out _);
            store.Etags.TryRemove(key, out _);
            store.Timestamps.TryRemove(key, out _);
            store.RemoveInsertionOrder(key);

            InMemoryItemStore.TrackBatchWrite(key);
            try
            {
                executePostTriggers(requestOptions, JsonParseHelpers.ParseJson(existingJson), "Delete");
                changeFeedLog.RecordDeleteTombstone(id, pk, partitionKeyPaths, partitionKey);
            }
            catch
            {
                store.Items[key] = existingJson;
                if (previousEtag is not null) store.Etags[key] = previousEtag;
                if (previousTimestamp.HasValue) store.Timestamps[key] = previousTimestamp.Value;
                store.AddInsertionOrder(key);
                throw;
            }

            return CosmosResponseFactory.CreateItemResponse(
                default(T)!, HttpStatusCode.NoContent, changeFeedLog.CurrentSessionToken);
        }
        finally
        {
            itemLock.Release();
        }
    }

    public static async Task<ResponseMessage> CreateItemStreamAsync(
        Stream streamPayload,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        string containerId,
        IReadOnlyList<string> partitionKeyPaths,
        ContainerProperties containerProperties,
        int? defaultTimeToLive,
        Func<ItemRequestOptions?, JObject, string, JObject> executePreTriggers,
        Func<ItemRequestOptions?, JObject, string, JObject?> executePostTriggers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = CosmosResponseFactory.ReadStream(streamPayload);
        if (!DocumentValidator.IsDocumentSizeValid(json))
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.RequestEntityTooLarge, changeFeedLog.CurrentSessionToken);

        JObject jObj;
        try { jObj = JsonParseHelpers.ParseJson(json); }
        catch (JsonReaderException)
        {
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken);
        }

        jObj = executePreTriggers(requestOptions, jObj, "Create");
        json = jObj.ToString(Formatting.None);
        if (!DocumentValidator.IsDocumentSizeValid(json))
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.RequestEntityTooLarge, changeFeedLog.CurrentSessionToken);
        if (!DocumentValidator.IsPerItemTtlValid(jObj))
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken);

        var itemId = jObj["id"]?.ToString() ?? Guid.NewGuid().ToString();
        if (itemId.Length == 0)
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken);

        if (!PartitionKeyProcessor.IsPartitionKeyConsistent(partitionKey, jObj, partitionKeyPaths))
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken, subStatusCode: 1001);

        var pk = PartitionKeyProcessor.ExtractPartitionKeyValue(partitionKey, jObj, partitionKeyPaths);
        var key = (itemId, pk!);

        var itemLock = store.GetOrAddLock(key);
        await itemLock.WaitAsync(cancellationToken);
        try
        {
            store.EvictIfExpired(key, defaultTimeToLive, partitionKeyPaths, changeFeedLog);

            var hasUniqueKeys = containerProperties.UniqueKeyPolicy?.UniqueKeys.Count > 0;
            if (hasUniqueKeys)
            {
                lock (store.UniqueKeyWriteLock)
                {
                    if (!UniqueKeyValidator.ValidateUniqueKeysStream(containerProperties.UniqueKeyPolicy, store.Items, jObj, pk))
                        return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.Conflict, changeFeedLog.CurrentSessionToken);
                    if (!store.Items.TryAdd(key, json))
                        return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.Conflict, changeFeedLog.CurrentSessionToken);
                }
            }
            else
            {
                if (!store.Items.TryAdd(key, json))
                    return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.Conflict, changeFeedLog.CurrentSessionToken);
            }

            InMemoryItemStore.TrackBatchWrite(key);
            store.AddInsertionOrder(key);
            var etag = ETagValue.GenerateNextString();
            store.Etags[key] = etag;
            store.Timestamps[key] = DateTimeOffset.UtcNow;
            var enrichedJson = InMemoryItemStore.EnrichWithSystemProperties(containerId, json, etag, store.Timestamps[key]);
            store.Items[key] = enrichedJson;

            try
            {
                executePostTriggers(requestOptions, JsonParseHelpers.ParseJson(enrichedJson), "Create");
                changeFeedLog.RecordChangeFeed(itemId, pk, store.Items[key]);
            }
            catch
            {
                store.Items.TryRemove(key, out _);
                store.Etags.TryRemove(key, out _);
                store.Timestamps.TryRemove(key, out _);
                store.RemoveInsertionOrder(key);
                throw;
            }

            return CosmosResponseFactory.CreateResponseMessage(
                HttpStatusCode.Created,
                changeFeedLog.CurrentSessionToken,
                requestOptions?.EnableContentResponseOnWrite == false ? null : enrichedJson,
                etag);
        }
        finally
        {
            itemLock.Release();
        }
    }

    public static Task<ResponseMessage> ReadItemStreamAsync(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        IReadOnlyList<string> partitionKeyPaths,
        int? defaultTimeToLive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pk = PartitionKeyProcessor.PartitionKeyToString(partitionKey);
        var key = (id, pk!);
        if (!store.Items.TryGetValue(key, out var json) || store.IsExpired(key, defaultTimeToLive))
        {
            store.EvictIfExpired(key, defaultTimeToLive, partitionKeyPaths, changeFeedLog);
            return Task.FromResult(
                CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.NotFound, changeFeedLog.CurrentSessionToken));
        }

        var etag = store.Etags.GetValueOrDefault(key);
        if (!store.CheckIfNoneMatchStream(requestOptions, key))
        {
            return Task.FromResult(
                CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.NotModified, changeFeedLog.CurrentSessionToken, etag: etag));
        }

        return Task.FromResult(
            CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.OK, changeFeedLog.CurrentSessionToken, json, etag));
    }

    public static async Task<ResponseMessage> UpsertItemStreamAsync(
        Stream streamPayload,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        string containerId,
        IReadOnlyList<string> partitionKeyPaths,
        ContainerProperties containerProperties,
        int? defaultTimeToLive,
        Func<ItemRequestOptions?, JObject, string, JObject> executePreTriggers,
        Func<ItemRequestOptions?, JObject, string, JObject?> executePostTriggers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = CosmosResponseFactory.ReadStream(streamPayload);
        if (!DocumentValidator.IsDocumentSizeValid(json))
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.RequestEntityTooLarge, changeFeedLog.CurrentSessionToken);

        JObject jObj;
        try { jObj = JsonParseHelpers.ParseJson(json); }
        catch (JsonReaderException)
        {
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken);
        }

        jObj = executePreTriggers(requestOptions, jObj, "Upsert");
        json = jObj.ToString(Formatting.None);
        if (!DocumentValidator.IsDocumentSizeValid(json))
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.RequestEntityTooLarge, changeFeedLog.CurrentSessionToken);
        if (!DocumentValidator.IsPerItemTtlValid(jObj))
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken);

        var itemId = jObj["id"]?.ToString();
        if (itemId is null)
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken);
        if (!PartitionKeyProcessor.IsPartitionKeyConsistent(partitionKey, jObj, partitionKeyPaths))
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken, subStatusCode: 1001);

        var pk = PartitionKeyProcessor.ExtractPartitionKeyValue(partitionKey, jObj, partitionKeyPaths);
        var key = (itemId, pk!);

        var itemLock = store.GetOrAddLock(key);
        await itemLock.WaitAsync(cancellationToken);
        try
        {
            store.EvictIfExpired(key, defaultTimeToLive, partitionKeyPaths, changeFeedLog);
            if (!store.CheckIfMatchStream(requestOptions, key))
            {
                return CosmosResponseFactory.CreateResponseMessage(
                    HttpStatusCode.PreconditionFailed, changeFeedLog.CurrentSessionToken);
            }

            bool existed;
            string etag;
            string enrichedJson;
            string? previousJson = null;
            string? previousEtag = null;
            DateTimeOffset? previousTimestamp = null;
            var hasUniqueKeys = containerProperties.UniqueKeyPolicy?.UniqueKeys.Count > 0;
            if (hasUniqueKeys)
            {
                lock (store.UniqueKeyWriteLock)
                {
                    if (!UniqueKeyValidator.ValidateUniqueKeysStream(
                        containerProperties.UniqueKeyPolicy, store.Items, jObj, pk, excludeItemId: itemId))
                    {
                        return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.Conflict, changeFeedLog.CurrentSessionToken);
                    }
                    existed = store.Items.TryGetValue(key, out previousJson);
                    if (existed) { previousEtag = store.Etags.GetValueOrDefault(key); previousTimestamp = store.Timestamps.GetValueOrDefault(key); }
                    etag = ETagValue.GenerateNextString();
                    store.Etags[key] = etag;
                    store.Timestamps[key] = DateTimeOffset.UtcNow;
                    enrichedJson = InMemoryItemStore.EnrichWithSystemProperties(containerId, json, etag, store.Timestamps[key]);
                    store.Items[key] = enrichedJson;
                }
            }
            else
            {
                if (!UniqueKeyValidator.ValidateUniqueKeysStream(
                    containerProperties.UniqueKeyPolicy, store.Items, jObj, pk, excludeItemId: itemId))
                {
                    return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.Conflict, changeFeedLog.CurrentSessionToken);
                }
                existed = store.Items.TryGetValue(key, out previousJson);
                if (existed) { previousEtag = store.Etags.GetValueOrDefault(key); previousTimestamp = store.Timestamps.GetValueOrDefault(key); }
                etag = ETagValue.GenerateNextString();
                store.Etags[key] = etag;
                store.Timestamps[key] = DateTimeOffset.UtcNow;
                enrichedJson = InMemoryItemStore.EnrichWithSystemProperties(containerId, json, etag, store.Timestamps[key]);
                store.Items[key] = enrichedJson;
            }

            InMemoryItemStore.TrackBatchWrite(key);
            if (!existed) store.AddInsertionOrder(key);
            try
            {
                executePostTriggers(requestOptions, JsonParseHelpers.ParseJson(enrichedJson), "Upsert");
                changeFeedLog.RecordChangeFeed(itemId, pk, store.Items[key]);
            }
            catch
            {
                if (existed && previousJson is not null)
                {
                    store.Items[key] = previousJson;
                    if (previousEtag is not null) store.Etags[key] = previousEtag;
                    if (previousTimestamp.HasValue) store.Timestamps[key] = previousTimestamp.Value;
                }
                else
                {
                    store.Items.TryRemove(key, out _);
                    store.Etags.TryRemove(key, out _);
                    store.Timestamps.TryRemove(key, out _);
                    store.RemoveInsertionOrder(key);
                }
                throw;
            }

            return CosmosResponseFactory.CreateResponseMessage(
                existed ? HttpStatusCode.OK : HttpStatusCode.Created,
                changeFeedLog.CurrentSessionToken,
                requestOptions?.EnableContentResponseOnWrite == false ? null : enrichedJson,
                etag);
        }
        finally
        {
            itemLock.Release();
        }
    }

    public static async Task<ResponseMessage> ReplaceItemStreamAsync(
        Stream streamPayload,
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        string containerId,
        IReadOnlyList<string> partitionKeyPaths,
        ContainerProperties containerProperties,
        int? defaultTimeToLive,
        Func<ItemRequestOptions?, JObject, string, JObject> executePreTriggers,
        Func<ItemRequestOptions?, JObject, string, JObject?> executePostTriggers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = CosmosResponseFactory.ReadStream(streamPayload);
        if (!DocumentValidator.IsDocumentSizeValid(json))
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.RequestEntityTooLarge, changeFeedLog.CurrentSessionToken);

        var pk = PartitionKeyProcessor.PartitionKeyToString(partitionKey);
        var key = (id, pk!);

        JObject jObj;
        try { jObj = JsonParseHelpers.ParseJson(json); }
        catch (JsonReaderException)
        {
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken);
        }

        if (!PartitionKeyProcessor.IsPartitionKeyConsistent(partitionKey, jObj, partitionKeyPaths))
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken, subStatusCode: 1001);

        var bodyId = jObj["id"]?.ToString();
        if (bodyId is not null && bodyId != id)
        {
            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken);
        }

        var itemLock = store.GetOrAddLock(key);
        await itemLock.WaitAsync(cancellationToken);
        try
        {
            if (!store.Items.TryGetValue(key, out var previousJson) || store.IsExpired(key, defaultTimeToLive))
            {
                store.EvictIfExpired(key, defaultTimeToLive, partitionKeyPaths, changeFeedLog);
                return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.NotFound, changeFeedLog.CurrentSessionToken);
            }

            if (!store.CheckIfMatchStream(requestOptions, key))
            {
                return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.PreconditionFailed, changeFeedLog.CurrentSessionToken);
            }

            var previousEtag = store.Etags.GetValueOrDefault(key);
            var previousTimestamp = store.Timestamps.GetValueOrDefault(key);

            jObj = executePreTriggers(requestOptions, jObj, "Replace");
            json = jObj.ToString(Formatting.None);
            if (!DocumentValidator.IsDocumentSizeValid(json))
                return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.RequestEntityTooLarge, changeFeedLog.CurrentSessionToken);
            if (!DocumentValidator.IsPerItemTtlValid(jObj))
                return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.BadRequest, changeFeedLog.CurrentSessionToken);

            string etag;
            string enrichedJson;
            var hasUniqueKeys = containerProperties.UniqueKeyPolicy?.UniqueKeys.Count > 0;
            if (hasUniqueKeys)
            {
                lock (store.UniqueKeyWriteLock)
                {
                    if (!UniqueKeyValidator.ValidateUniqueKeysStream(
                        containerProperties.UniqueKeyPolicy, store.Items, jObj, pk, excludeItemId: id))
                    {
                        return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.Conflict, changeFeedLog.CurrentSessionToken);
                    }
                    etag = ETagValue.GenerateNextString();
                    store.Etags[key] = etag;
                    store.Timestamps[key] = DateTimeOffset.UtcNow;
                    enrichedJson = InMemoryItemStore.EnrichWithSystemProperties(containerId, json, etag, store.Timestamps[key]);
                    store.Items[key] = enrichedJson;
                }
            }
            else
            {
                if (!UniqueKeyValidator.ValidateUniqueKeysStream(
                    containerProperties.UniqueKeyPolicy, store.Items, jObj, pk, excludeItemId: id))
                {
                    return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.Conflict, changeFeedLog.CurrentSessionToken);
                }
                etag = ETagValue.GenerateNextString();
                store.Etags[key] = etag;
                store.Timestamps[key] = DateTimeOffset.UtcNow;
                enrichedJson = InMemoryItemStore.EnrichWithSystemProperties(containerId, json, etag, store.Timestamps[key]);
                store.Items[key] = enrichedJson;
            }

            InMemoryItemStore.TrackBatchWrite(key);
            try
            {
                executePostTriggers(requestOptions, JsonParseHelpers.ParseJson(enrichedJson), "Replace");
                changeFeedLog.RecordChangeFeed(id, pk, store.Items[key]);
            }
            catch
            {
                store.Items[key] = previousJson;
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

    public static async Task<ResponseMessage> DeleteItemStreamAsync(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        IReadOnlyList<string> partitionKeyPaths,
        int? defaultTimeToLive,
        Func<ItemRequestOptions?, JObject, string, JObject> executePreTriggers,
        Func<ItemRequestOptions?, JObject, string, JObject?> executePostTriggers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pk = PartitionKeyProcessor.PartitionKeyToString(partitionKey);
        var key = (id, pk!);

        var itemLock = store.GetOrAddLock(key);
        await itemLock.WaitAsync(cancellationToken);
        try
        {
            if (!store.Items.TryGetValue(key, out var existingJson) || store.IsExpired(key, defaultTimeToLive))
            {
                store.EvictIfExpired(key, defaultTimeToLive, partitionKeyPaths, changeFeedLog);
                return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.NotFound, changeFeedLog.CurrentSessionToken);
            }

            if (!store.CheckIfMatchStream(requestOptions, key))
            {
                return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.PreconditionFailed, changeFeedLog.CurrentSessionToken);
            }

            executePreTriggers(requestOptions, JsonParseHelpers.ParseJson(existingJson), "Delete");

            var previousEtag = store.Etags.TryGetValue(key, out var e) ? e : null;
            var previousTimestamp = store.Timestamps.TryGetValue(key, out var ts) ? ts : (DateTimeOffset?)null;

            store.Items.TryRemove(key, out _);
            store.Etags.TryRemove(key, out _);
            store.Timestamps.TryRemove(key, out _);
            store.RemoveInsertionOrder(key);

            InMemoryItemStore.TrackBatchWrite(key);
            try
            {
                executePostTriggers(requestOptions, JsonParseHelpers.ParseJson(existingJson), "Delete");
                changeFeedLog.RecordDeleteTombstone(id, pk, partitionKeyPaths, partitionKey);
            }
            catch
            {
                store.Items[key] = existingJson;
                if (previousEtag is not null) store.Etags[key] = previousEtag;
                if (previousTimestamp.HasValue) store.Timestamps[key] = previousTimestamp.Value;
                store.AddInsertionOrder(key);
                throw;
            }

            return CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.NoContent, changeFeedLog.CurrentSessionToken);
        }
        finally
        {
            itemLock.Release();
        }
    }

    public static Task<FeedResponse<T>> ReadManyItemsAsync<T>(
        IReadOnlyList<(string id, PartitionKey partitionKey)> items,
        ReadManyRequestOptions? readManyRequestOptions,
        InMemoryItemStore store,
        int? defaultTimeToLive,
        JsonSerializerSettings jsonSettings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(items);
        var results = new List<T>();
        var etagParts = new List<string>();
        foreach (var (itemId, pk) in items)
        {
            var pkStr = PartitionKeyProcessor.PartitionKeyToString(pk);
            var key = (itemId, pkStr!);
            if (store.Items.TryGetValue(key, out var json) && !store.IsExpired(key, defaultTimeToLive))
            {
                var jObj = JsonParseHelpers.ParseJson(json);
                var itemEtag = jObj["_etag"]?.ToString();
                if (itemEtag != null) etagParts.Add(itemEtag);
                var deserialized = JsonConvert.DeserializeObject<T>(json, jsonSettings);
                if (deserialized is not null)
                {
                    results.Add(deserialized);
                }
            }
        }
        var compositeEtag = etagParts.Count > 0 ? $"\"{string.Join(",", etagParts)}\"" : null;
        if (readManyRequestOptions?.IfNoneMatchEtag != null && compositeEtag == readManyRequestOptions.IfNoneMatchEtag)
        {
            return Task.FromResult(
                CosmosResponseFactory.CreateFeedResponse<T>(Array.Empty<T>(), HttpStatusCode.NotModified, compositeEtag));
        }
        return Task.FromResult(
            CosmosResponseFactory.CreateFeedResponse<T>(results, HttpStatusCode.OK, compositeEtag));
    }

    public static Task<ResponseMessage> ReadManyItemsStreamAsync(
        IReadOnlyList<(string id, PartitionKey partitionKey)> items,
        ReadManyRequestOptions? readManyRequestOptions,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        int? defaultTimeToLive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(items);
        var results = new JArray();
        var etagParts = new List<string>();
        foreach (var (itemId, pk) in items)
        {
            var pkStr = PartitionKeyProcessor.PartitionKeyToString(pk);
            var key = (itemId, pkStr!);
            if (store.Items.TryGetValue(key, out var json) && !store.IsExpired(key, defaultTimeToLive))
            {
                var jObj = JsonParseHelpers.ParseJson(json);
                var itemEtag = jObj["_etag"]?.ToString();
                if (itemEtag != null) etagParts.Add(itemEtag);
                results.Add(jObj);
            }
        }
        var compositeEtag = etagParts.Count > 0 ? $"\"{string.Join(",", etagParts)}\"" : null;
        if (readManyRequestOptions?.IfNoneMatchEtag != null && compositeEtag == readManyRequestOptions.IfNoneMatchEtag)
        {
            return Task.FromResult(
                CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.NotModified, changeFeedLog.CurrentSessionToken, etag: compositeEtag));
        }
        var envelope = new JObject { ["_rid"] = "", ["Documents"] = results, ["_count"] = results.Count };
        return Task.FromResult(
            CosmosResponseFactory.CreateResponseMessage(
                HttpStatusCode.OK, changeFeedLog.CurrentSessionToken, envelope.ToString(Formatting.None), compositeEtag));
    }

    public static Task<ResponseMessage> DeleteAllItemsByPartitionKeyStreamAsync(
        PartitionKey partitionKey,
        InMemoryItemStore store,
        InMemoryChangeFeedLog changeFeedLog,
        IReadOnlyList<string> partitionKeyPaths,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pk = PartitionKeyProcessor.PartitionKeyToString(partitionKey);
        foreach (var key in store.Items.Keys.Where(k => k.PartitionKey == pk).ToList())
        {
            store.Items.TryRemove(key, out _);
            store.Etags.TryRemove(key, out _);
            store.Timestamps.TryRemove(key, out _);
            store.RemoveInsertionOrder(key);
            changeFeedLog.RecordDeleteTombstone(key.Id, pk, partitionKeyPaths, partitionKey);
        }
        return Task.FromResult(
            CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.OK, changeFeedLog.CurrentSessionToken));
    }
}
