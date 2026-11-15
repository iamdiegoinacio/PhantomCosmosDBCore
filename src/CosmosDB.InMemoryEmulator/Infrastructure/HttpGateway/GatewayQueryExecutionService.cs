#nullable enable
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Infrastructure.HttpGateway;

/// <summary>
/// Executes SQL queries, query plan requests, read-feed requests, and HTTP change-feed
/// requests against an <see cref="InMemoryContainer"/>, managing pagination and range filtering.
/// </summary>
internal sealed class GatewayQueryExecutionService
{
    private readonly InMemoryContainer _container;
    private readonly QueryResultCache _queryResultCache;
    private readonly IQueryPlanStrategy _queryPlanStrategy;
    private readonly string _collectionRid;
    private readonly int _partitionKeyRangeCount;
    private readonly string _partitionKeyPath;
    private readonly ConcurrentBag<string> _queryLog;

    public GatewayQueryExecutionService(
        InMemoryContainer container,
        QueryResultCache queryResultCache,
        IQueryPlanStrategy queryPlanStrategy,
        string collectionRid,
        int partitionKeyRangeCount,
        string partitionKeyPath,
        ConcurrentBag<string> queryLog)
    {
        _container = container;
        _queryResultCache = queryResultCache;
        _queryPlanStrategy = queryPlanStrategy;
        _collectionRid = collectionRid;
        _partitionKeyRangeCount = partitionKeyRangeCount;
        _partitionKeyPath = partitionKeyPath;
        _queryLog = queryLog;
    }

    public async Task<HttpResponseMessage> HandleQueryPlanAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var queryBody = JsonParseHelpers.ParseJson(body);
        var sqlQuery = queryBody["query"]?.ToString() ?? "SELECT * FROM c";

        var parsed = CosmosSqlParser.TryParse(sqlQuery, out var p) ? p : null;
        var queryPlan = _queryPlanStrategy.BuildQueryPlan(sqlQuery, parsed, _collectionRid);
        return CosmosHttpHeaders.CreateJsonResponse(
            queryPlan.ToString(Formatting.None),
            _container.CurrentSessionToken);
    }

    public async Task<HttpResponseMessage> HandleQueryAsync(
        HttpRequestMessage request,
        PartitionKey? fallbackPartitionKey,
        CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var queryBody = JsonParseHelpers.ParseJson(body);
        var sqlQuery = queryBody["query"]?.ToString() ?? "SELECT * FROM c";
        _queryLog.Add(sqlQuery);

        var partitionKey = CosmosHttpHeaders.ExtractPartitionKey(request) ?? fallbackPartitionKey;
        var maxItemCount = CosmosHttpHeaders.ExtractMaxItemCount(request);
        var continuation = CosmosHttpHeaders.DecodeContinuation(request);
        var rangeId = CosmosHttpHeaders.ExtractPartitionKeyRangeId(request);

        List<JToken> allDocuments;
        string cacheKey;
        int offset;
        string? payloadPropertyName = null;

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

            if (CosmosSqlParser.TryParse(sqlQuery, out var parsed))
            {
                var orderByItemsField = parsed.SelectFields
                    .FirstOrDefault(field => GatewayOrderByQueryRewriter.IsOrderByItemsArray(field.SqlExpr));
                var isOrderByQuery = orderByItemsField is not null && parsed.OrderByFields is { Length: > 0 };

                var groupByItemsField = parsed.SelectFields
                    .FirstOrDefault(field => string.Equals(field.Alias, "groupByItems", StringComparison.OrdinalIgnoreCase));
                var isGroupByQuery = groupByItemsField is not null && parsed.GroupByFields is { Length: > 0 };

                if (isGroupByQuery)
                {
                    allDocuments = await HandleGroupByQueryAsync(
                        parsed, queryBody, partitionKey, cancellationToken);
                }
                else if (isOrderByQuery)
                {
                    var payloadField = parsed.SelectFields
                        .FirstOrDefault(field => string.Equals(field.Alias, "payload", StringComparison.OrdinalIgnoreCase))
                        ?? parsed.SelectFields
                            .FirstOrDefault(field => field != orderByItemsField);
                    var orderByAlias = orderByItemsField!.Alias ?? "orderByItems";
                    payloadPropertyName = payloadField?.Alias ?? "payload";

                    allDocuments = await HandleOrderByQueryAsync(
                        parsed, queryBody, partitionKey, orderByAlias, payloadPropertyName, cancellationToken);
                }
                else
                {
                    var simplifiedSql = CosmosSqlParser.SimplifySdkQuery(parsed);
                    var queryDef = BuildQueryDefinition(simplifiedSql, queryBody);
                    var requestOptions = partitionKey is not null
                        ? new QueryRequestOptions { PartitionKey = partitionKey }
                        : null;
                    allDocuments = await DrainIterator(
                        _container.GetItemQueryIterator<JToken>(queryDef, requestOptions: requestOptions),
                        cancellationToken);
                }
            }
            else
            {
                var queryDef = BuildQueryDefinition(sqlQuery, queryBody);
                var requestOptions = partitionKey is not null
                    ? new QueryRequestOptions { PartitionKey = partitionKey }
                    : null;
                allDocuments = await DrainIterator(
                    _container.GetItemQueryIterator<JToken>(queryDef, requestOptions: requestOptions),
                    cancellationToken);
            }
        }

        allDocuments = FilterDocumentsByRange(allDocuments, rangeId, payloadPropertyName);

        return BuildPagedResponse(allDocuments, offset, maxItemCount, cacheKey);
    }

    public async Task<HttpResponseMessage> HandleReadFeedAsync(
        HttpRequestMessage request,
        PartitionKey? fallbackPartitionKey,
        CancellationToken cancellationToken)
    {
        if (CosmosHttpHeaders.IsChangeFeedRequest(request))
        {
            return await HandleChangeFeedAsync(request);
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

    private async Task<List<JToken>> HandleOrderByQueryAsync(
        CosmosSqlQuery parsed,
        JObject queryBody,
        PartitionKey? partitionKey,
        string orderByAlias,
        string payloadAlias,
        CancellationToken cancellationToken)
    {
        var hasComplexOrderBy = parsed.OrderByFields?.Any(f => f.Field is null) ?? false;

        string simplifiedSql;
        List<string> complexOrderByAliases = [];
        try
        {
            if (hasComplexOrderBy)
            {
                simplifiedSql = GatewayOrderByQueryRewriter.BuildComplexOrderBySql(
                    parsed, payloadAlias, out complexOrderByAliases);
            }
            else
            {
                simplifiedSql = CosmosSqlParser.SimplifySdkQuery(parsed);
            }
        }
        catch
        {
            simplifiedSql = GatewayOrderByQueryRewriter.BuildFallbackOrderBySql(parsed);
        }

        var queryDef = BuildQueryDefinition(simplifiedSql, queryBody);
        var requestOptions = partitionKey is not null
            ? new QueryRequestOptions { PartitionKey = partitionKey }
            : null;
        var feedIterator = _container.GetItemQueryIterator<JObject>(queryDef, requestOptions: requestOptions);
        var rawDocuments = new List<JObject>();
        while (feedIterator.HasMoreResults)
        {
            var page = await feedIterator.ReadNextAsync(cancellationToken);
            rawDocuments.AddRange(page);
        }

        List<string> orderByPaths;
        if (hasComplexOrderBy && complexOrderByAliases.Count > 0)
        {
            orderByPaths = complexOrderByAliases;
        }
        else
        {
            try
            {
                orderByPaths = GatewayOrderByQueryRewriter.ExtractOrderByItemPaths(parsed);
            }
            catch
            {
                orderByPaths = parsed.OrderByFields?.Select(field =>
                    GatewayOrderByQueryRewriter.StripFromAlias(
                        field.Field ?? CosmosSqlParser.ExprToString(field.Expression),
                        parsed.FromAlias)).ToList() ?? [];
            }
        }

        var documents = new List<JToken>();
        var payloadField = parsed.SelectFields
            .FirstOrDefault(f => string.Equals(f.Alias, payloadAlias, StringComparison.OrdinalIgnoreCase));
        var fromAlias = parsed.FromAlias ?? "c";

        foreach (var doc in rawDocuments)
        {
            var orderByItems = new JArray();
            foreach (var path in orderByPaths)
            {
                var value = doc.SelectToken(path)?.DeepClone() ?? JValue.CreateNull();
                orderByItems.Add(new JObject { ["item"] = value });
            }

            JToken payloadValue;
            if (hasComplexOrderBy)
            {
                var fullDoc = doc["_doc"]?.DeepClone() ?? doc.DeepClone();
                if (fullDoc is JObject fullDocObj)
                {
                    foreach (var alias in complexOrderByAliases)
                        fullDocObj.Remove(alias);
                }

                if (payloadField?.SqlExpr is ObjectLiteralExpression objLit)
                    payloadValue = GatewayOrderByQueryRewriter.BuildComplexPayloadValue(
                        (JObject)fullDoc, objLit, fromAlias, parsed, doc, complexOrderByAliases);
                else
                    payloadValue = fullDoc;
            }
            else
            {
                payloadValue = GatewayOrderByQueryRewriter.BuildPayloadValue(doc, payloadField, fromAlias);
            }

            var wrapped = new JObject
            {
                ["_rid"] = (hasComplexOrderBy
                    ? doc["_doc"]?["_rid"]?.ToString()
                    : doc["_rid"]?.ToString()) ?? Guid.NewGuid().ToString("N")[..8],
                [orderByAlias] = orderByItems,
                [payloadAlias] = payloadValue
            };
            documents.Add(wrapped);
        }

        return documents;
    }

    private async Task<List<JToken>> HandleGroupByQueryAsync(
        CosmosSqlQuery parsed,
        JObject queryBody,
        PartitionKey? partitionKey,
        CancellationToken cancellationToken)
    {
        var originalSql = GatewayOrderByQueryRewriter.ReconstructGroupByQuery(parsed);
        var queryDef = BuildQueryDefinition(originalSql, queryBody);
        var requestOptions = partitionKey is not null
            ? new QueryRequestOptions { PartitionKey = partitionKey }
            : null;
        var rawResults = await DrainIterator(
            _container.GetItemQueryIterator<JObject>(queryDef, requestOptions: requestOptions),
            cancellationToken);

        var payloadField = parsed.SelectFields
            .FirstOrDefault(f => string.Equals(f.Alias, "payload", StringComparison.OrdinalIgnoreCase));
        var aggregateTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (payloadField?.SqlExpr is ObjectLiteralExpression payloadObj)
        {
            foreach (var prop in payloadObj.Properties)
            {
                if (prop.Value is ObjectLiteralExpression inner
                    && inner.Properties.Length == 1
                    && string.Equals(inner.Properties[0].Key, "item", StringComparison.OrdinalIgnoreCase)
                    && inner.Properties[0].Value is FunctionCallExpression func)
                {
                    aggregateTypes[prop.Key] = func.FunctionName.ToUpperInvariant();
                }
            }
        }

        var groupByResultKeys = new List<string>();
        if (payloadField?.SqlExpr is ObjectLiteralExpression payloadObjForKeys)
        {
            foreach (var prop in payloadObjForKeys.Properties)
            {
                if (!(prop.Value is ObjectLiteralExpression inner
                    && inner.Properties.Length == 1
                    && string.Equals(inner.Properties[0].Key, "item", StringComparison.OrdinalIgnoreCase)))
                {
                    groupByResultKeys.Add(prop.Key);
                }
            }
        }

        return rawResults.Select(doc =>
        {
            var jObj = doc as JObject ?? JObject.Parse(doc.ToString());

            var groupByItems = new JArray();
            foreach (var key in groupByResultKeys)
            {
                groupByItems.Add(new JObject { ["item"] = jObj.SelectToken(key)?.DeepClone() ?? JValue.CreateNull() });
            }

            var payload = new JObject();
            foreach (var prop in jObj.Properties())
            {
                if (aggregateTypes.TryGetValue(prop.Name, out var aggType))
                {
                    JToken itemValue = aggType == "AVG"
                        ? new JObject { ["sum"] = prop.Value, ["count"] = 1 }
                        : prop.Value.DeepClone();
                    payload[prop.Name] = new JObject { ["item"] = itemValue };
                }
                else
                {
                    payload[prop.Name] = prop.Value.DeepClone();
                }
            }

            return (JToken)new JObject
            {
                ["groupByItems"] = groupByItems,
                ["payload"] = payload
            };
        }).ToList();
    }

    private async Task<HttpResponseMessage> HandleChangeFeedAsync(HttpRequestMessage request)
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

        var iterator = _container.GetChangeFeedIterator<JObject>(checkpoint);
        var allChanges = new List<JObject>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            if (page.StatusCode == HttpStatusCode.NotModified)
                break;
            allChanges.AddRange(page);
        }
        var totalChanges = allChanges.Count;

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

    private List<JToken> FilterDocumentsByRange(
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
            ? payload : obj;
        var pkToken = targetDoc.SelectToken(_partitionKeyPath);
        var pkValue = pkToken is not null ? InMemoryContainer.JTokenToTypedKey(pkToken) ?? "" : "";
        return PartitionKeyHash.GetRangeIndex(pkValue, _partitionKeyRangeCount);
    }

    private HttpResponseMessage BuildPagedResponse(
        List<JToken> allDocuments,
        int offset,
        int maxItemCount,
        string cacheKey)
    {
        var paged = allDocuments.Skip(offset).ToList();
        string? continuationToken = null;
        if (maxItemCount > 0 && paged.Count > maxItemCount)
        {
            paged = paged.Take(maxItemCount).ToList();
            _queryResultCache.Set(cacheKey, allDocuments);
            continuationToken = CosmosHttpHeaders.EncodeContinuation(cacheKey, offset + maxItemCount);
        }
        else
        {
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

        response.Headers.Add("x-ms-request-charge", "1");
        response.Headers.Add("x-ms-activity-id", Guid.NewGuid().ToString());
        response.Headers.Add("x-ms-session-token", _container.CurrentSessionToken);
        response.Headers.Add("x-ms-item-count", paged.Count.ToString());

        if (continuationToken is not null)
        {
            response.Headers.Add("x-ms-continuation", continuationToken);
        }

        return response;
    }

    private static async Task<List<JToken>> DrainIterator<T>(
        FeedIterator<T> feedIterator,
        CancellationToken cancellationToken)
        where T : JToken
    {
        var allDocuments = new List<JToken>();
        while (feedIterator.HasMoreResults)
        {
            var page = await feedIterator.ReadNextAsync(cancellationToken);
            allDocuments.AddRange(page);
        }

        return allDocuments;
    }

    private static QueryDefinition BuildQueryDefinition(string sqlQuery, JObject queryBody)
    {
        var queryDef = new QueryDefinition(sqlQuery);
        if (queryBody["parameters"] is JArray parameters)
        {
            foreach (var parameter in parameters)
            {
                var paramName = parameter["name"]?.ToString();
                var paramValue = parameter["value"];
                if (paramName is not null)
                {
                    queryDef = queryDef.WithParameter(paramName, paramValue);
                }
            }
        }

        return queryDef;
    }
}
