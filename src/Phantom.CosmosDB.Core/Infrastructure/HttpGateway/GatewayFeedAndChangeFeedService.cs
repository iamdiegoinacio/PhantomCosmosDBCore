#nullable enable
using System.Globalization;
using System.Net;
using System.Text;
using Phantom.CosmosDB.Core.Application.ChangeFeed;
using Phantom.CosmosDB.Core.Domain.Primitives;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Infrastructure.HttpGateway;

/// <summary>
/// Handles HTTP Gateway Read-Feed (<c>GET /docs</c>), Change-Feed (<c>A-IM</c>),
/// partition key range filtering, and continuation-token pagination.
/// </summary>
internal sealed class GatewayFeedAndChangeFeedService
{
    private readonly InMemoryContainer _container;
    private readonly QueryResultCache _queryResultCache;
    private readonly string _collectionRid;
    private readonly int _partitionKeyRangeCount;
    private readonly string _partitionKeyPath;

    public GatewayFeedAndChangeFeedService(
        InMemoryContainer container,
        QueryResultCache queryResultCache,
        string collectionRid,
        int partitionKeyRangeCount,
        string partitionKeyPath)
    {
        _container = container;
        _queryResultCache = queryResultCache;
        _collectionRid = collectionRid;
        _partitionKeyRangeCount = partitionKeyRangeCount;
        _partitionKeyPath = partitionKeyPath;
    }

    public async Task<HttpResponseMessage> HandleReadFeedAsync(
        HttpRequestMessage request,
        PartitionKey? fallbackPartitionKey,
        CancellationToken cancellationToken)
    {
        if (CosmosHttpHeaders.IsChangeFeedRequest(request))
        {
            return HandleChangeFeed(request);
        }

        var maxItemCount = CosmosHttpHeaders.ExtractMaxItemCount(request);
        var continuation = CosmosHttpHeaders.DecodeContinuation(request);
        var rangeId = CosmosHttpHeaders.ExtractPartitionKeyRangeId(request);

        List<JToken> allDocuments;
        string cacheKey;
        int offset;

        if (continuation is not null && _queryResultCache.TryGet(continuation.Value.Key, out var cached))
        {
            allDocuments = cached;
            cacheKey = continuation.Value.Key;
            offset = continuation.Value.Offset;
        }
        else
        {
            offset = 0;
            cacheKey = Guid.NewGuid().ToString("N");

            var requestOptions = new QueryRequestOptions();
            var partitionKey = CosmosHttpHeaders.ExtractPartitionKey(request) ?? fallbackPartitionKey;
            if (partitionKey is not null)
            {
                requestOptions.PartitionKey = partitionKey;
            }

            allDocuments = await DrainIterator(
                _container.GetItemQueryIterator<JObject>(requestOptions: requestOptions),
                cancellationToken);
        }

        allDocuments = FilterDocumentsByRange(allDocuments, rangeId);

        return BuildPagedResponse(allDocuments, offset, maxItemCount, cacheKey);
    }

    public HttpResponseMessage HandleChangeFeed(HttpRequestMessage request)
    {
        var maxItemCount = CosmosHttpHeaders.ExtractMaxItemCount(request);
        var rangeId = CosmosHttpHeaders.ExtractPartitionKeyRangeId(request);

        long checkpoint = 0;
        if (request.Headers.IfNoneMatch.Count > 0)
        {
            var tag = request.Headers.IfNoneMatch.First().Tag;
            var unquoted = tag?.Trim('"') ?? "";
            if (unquoted != "*" && long.TryParse(unquoted, out var parsed))
            {
                checkpoint = parsed;
            }
        }

        var allChanges = new List<JObject>();
        var rawEntries = _container.GetChangeFeedEntries();
        var startIndex = (int)Math.Min(checkpoint, int.MaxValue);

        if (CosmosHttpHeaders.IsFullFidelityChangeFeedRequest(request))
        {
            var envelopes = FullFidelityChangeFeedProjector.Project(rawEntries, startIndex);
            allChanges.AddRange(envelopes);
        }
        else
        {
            var incrementalEntries = rawEntries
                .Skip(startIndex)
                .GroupBy(entry => (entry.Id, entry.PartitionKey))
                .Select(group => group.Last())
                .Where(entry => !entry.IsDelete)
                .Select(entry => JObject.Parse(entry.Json));
            allChanges.AddRange(incrementalEntries);
        }

        var totalChanges = Math.Max(0, rawEntries.Count - startIndex);

        if (request.Headers.IfModifiedSince is { } modifiedSince)
        {
            var sinceEpoch = modifiedSince.ToUnixTimeSeconds();
            allChanges = allChanges
                .Where(change => (change["_ts"] ?? change["metadata"]?["crts"])?.Type is not JTokenType.Integer
                                 || (change["_ts"] ?? change["metadata"]!["crts"])!.Value<long>() >= sinceEpoch)
                .ToList();
        }

        allChanges = FilterDocumentsByRange(allChanges.Cast<JToken>().ToList(), rangeId)
            .Cast<JObject>().ToList();

        var paged = maxItemCount > 0 && allChanges.Count > maxItemCount
            ? allChanges.Take(maxItemCount).ToList()
            : allChanges;

        var newCheckpoint = checkpoint + totalChanges;

        if (paged.Count == 0)
        {
            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModified.Headers.Add("x-ms-request-charge", "1");
            notModified.Headers.Add("x-ms-activity-id", Guid.NewGuid().ToString());
            notModified.Headers.Add("x-ms-item-count", "0");
            notModified.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(
                $"\"{newCheckpoint}\"");
            return notModified;
        }

        var responseBody = new JObject
        {
            ["_rid"] = _collectionRid,
            ["Documents"] = new JArray(paged),
            ["_count"] = paged.Count
        };

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody.ToString(), Encoding.UTF8, "application/json")
        };

        response.Headers.Add("x-ms-request-charge", "1");
        response.Headers.Add("x-ms-activity-id", Guid.NewGuid().ToString());
        response.Headers.Add("x-ms-session-token", _container.CurrentSessionToken);
        response.Headers.Add("x-ms-item-count", paged.Count.ToString());
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(
            $"\"{newCheckpoint}\"");

        if (paged.Count < allChanges.Count)
        {
            response.Headers.Add("x-ms-continuation", $"\"{newCheckpoint}\"");
        }

        return response;
    }

    public List<JToken> FilterDocumentsByRange(
        List<JToken> documents,
        string? rangeId,
        string? payloadPropertyName = null)
    {
        if (_partitionKeyRangeCount <= 1 || rangeId is null)
        {
            return documents;
        }

        if (!int.TryParse(rangeId, out var rangeIndex))
        {
            return documents;
        }

        return documents.Where(document => GetRangeIndex(document, payloadPropertyName) == rangeIndex).ToList();
    }

    private int GetRangeIndex(JToken document, string? payloadPropertyName)
    {
        if (document is not JObject obj)
        {
            return 0;
        }

        var targetDoc = payloadPropertyName is not null && obj[payloadPropertyName] is JObject payload
            ? payload
            : obj;
        if (obj["metadata"] is JObject && obj["current"] is JObject current)
        {
            targetDoc = current.HasValues ? current : obj["previous"] as JObject ?? current;
        }

        var pkToken = targetDoc.SelectToken(_partitionKeyPath);
        var pkValue = pkToken is not null ? InMemoryContainer.JTokenToTypedKey(pkToken) ?? "" : "";
        return PartitionKeyHash.GetRangeIndex(pkValue, _partitionKeyRangeCount);
    }

    public HttpResponseMessage BuildPagedResponse(
        List<JToken> allDocuments,
        int offset,
        int maxItemCount,
        string cacheKey)
    {
        List<JToken> paged;
        string? continuationToken = null;

        if (maxItemCount > 0 && offset + maxItemCount < allDocuments.Count)
        {
            paged = allDocuments.Skip(offset).Take(maxItemCount).ToList();
            _queryResultCache.Set(cacheKey, allDocuments);
            continuationToken = CosmosHttpHeaders.EncodeContinuation(cacheKey, offset + maxItemCount);
        }
        else
        {
            paged = offset > 0 ? allDocuments.Skip(offset).ToList() : allDocuments;
            _queryResultCache.Remove(cacheKey);
        }

        var responseBody = new JObject
        {
            ["_rid"] = _collectionRid,
            ["Documents"] = new JArray(paged),
            ["_count"] = paged.Count
        };

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody.ToString(), Encoding.UTF8, "application/json")
        };

        var pageCharge = RequestChargeCalculator.Query(
            paged.Count, Encoding.UTF8.GetByteCount(responseBody.ToString(Formatting.None)));
        response.Headers.Add("x-ms-request-charge", pageCharge.ToString(CultureInfo.InvariantCulture));
        response.Headers.Add("x-ms-activity-id", Guid.NewGuid().ToString());
        response.Headers.Add("x-ms-session-token", _container.CurrentSessionToken);
        response.Headers.Add("x-ms-item-count", paged.Count.ToString());

        if (continuationToken is not null)
        {
            response.Headers.Add("x-ms-continuation", continuationToken);
        }

        return response;
    }

    public static async Task<List<JToken>> DrainIterator<T>(
        FeedIterator<T> feedIterator,
        CancellationToken cancellationToken)
        where T : JToken
    {
        var allDocuments = new List<JToken>();
        while (feedIterator.HasMoreResults)
        {
            var page = await feedIterator.ReadNextAsync(cancellationToken);
            foreach (var item in page)
            {
                allDocuments.Add(item);
            }
        }
        return allDocuments;
    }
}
