#nullable enable
using System.Net;
using Phantom.CosmosDB.Core.Domain.Validation;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CosmosResponseFactory = Phantom.CosmosDB.Core.Infrastructure.SdkInterop.CosmosResponseFactory;

namespace Phantom.CosmosDB.Core.Application.Crud;

/// <summary>
/// Multi-item operations: <c>ReadMany</c> (typed and stream) and deleting every item of a partition key.
/// </summary>
internal static class BulkDocumentReadDeleteService
{
    public static Task<FeedResponse<T>> ReadManyItemsAsync<T>(
        DocumentOperationContext context,
        IReadOnlyList<(string id, PartitionKey partitionKey)> items,
        ReadManyRequestOptions? readManyRequestOptions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(items);

        var results = new List<T>();
        var etags = new List<string>();
        foreach (var json in EnumerateLiveDocuments(context, items))
        {
            var etag = JsonParseHelpers.ParseJson(json)["_etag"]?.ToString();
            if (etag is not null) etags.Add(etag);

            var deserialized = JsonConvert.DeserializeObject<T>(json, context.JsonSettings);
            if (deserialized is not null)
            {
                results.Add(deserialized);
            }
        }

        var compositeEtag = ComposeEtag(etags);
        if (IsNotModified(readManyRequestOptions, compositeEtag))
        {
            return Task.FromResult(
                CosmosResponseFactory.CreateFeedResponse<T>(Array.Empty<T>(), HttpStatusCode.NotModified, compositeEtag));
        }

        return Task.FromResult(CosmosResponseFactory.CreateFeedResponse<T>(results, HttpStatusCode.OK, compositeEtag));
    }

    public static Task<ResponseMessage> ReadManyItemsStreamAsync(
        DocumentOperationContext context,
        IReadOnlyList<(string id, PartitionKey partitionKey)> items,
        ReadManyRequestOptions? readManyRequestOptions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(items);

        var results = new JArray();
        var etags = new List<string>();
        foreach (var json in EnumerateLiveDocuments(context, items))
        {
            var document = JsonParseHelpers.ParseJson(json);
            var etag = document["_etag"]?.ToString();
            if (etag is not null) etags.Add(etag);
            results.Add(document);
        }

        var compositeEtag = ComposeEtag(etags);
        if (IsNotModified(readManyRequestOptions, compositeEtag))
        {
            return Task.FromResult(CosmosResponseFactory.CreateResponseMessage(
                HttpStatusCode.NotModified, context.SessionToken, etag: compositeEtag));
        }

        var envelope = new JObject { ["_rid"] = "", ["Documents"] = results, ["_count"] = results.Count };
        return Task.FromResult(CosmosResponseFactory.CreateResponseMessage(
            HttpStatusCode.OK, context.SessionToken, envelope.ToString(Formatting.None), compositeEtag));
    }

    public static Task<ResponseMessage> DeleteAllItemsByPartitionKeyStreamAsync(
        DocumentOperationContext context,
        PartitionKey partitionKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = context.Store;
        var pk = PartitionKeyProcessor.PartitionKeyToString(partitionKey);

        foreach (var key in store.Items.Keys.Where(k => k.PartitionKey == pk).ToList())
        {
            DocumentWriteCommitter.Remove(store, key);
            context.ChangeFeedLog.RecordDeleteTombstone(key.Id, pk, context.PartitionKeyPaths, partitionKey);
        }

        return Task.FromResult(
            CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.OK, context.SessionToken));
    }

    /// <summary>Yields the stored JSON of every requested item that exists and has not expired.</summary>
    private static IEnumerable<string> EnumerateLiveDocuments(
        DocumentOperationContext context,
        IReadOnlyList<(string id, PartitionKey partitionKey)> items)
    {
        foreach (var (itemId, partitionKey) in items)
        {
            var key = (itemId, PartitionKeyProcessor.PartitionKeyToString(partitionKey)!);
            if (context.Store.Items.TryGetValue(key, out var json) && !context.Store.IsExpired(key, context.DefaultTimeToLive))
            {
                yield return json;
            }
        }
    }

    private static string? ComposeEtag(IReadOnlyList<string> etags)
        => etags.Count > 0 ? $"\"{string.Join(",", etags)}\"" : null;

    private static bool IsNotModified(ReadManyRequestOptions? options, string? compositeEtag)
        => options?.IfNoneMatchEtag is not null && compositeEtag == options.IfNoneMatchEtag;
}
