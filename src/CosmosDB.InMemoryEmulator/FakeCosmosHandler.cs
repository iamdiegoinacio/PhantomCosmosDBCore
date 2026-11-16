#nullable enable
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CosmosDB.InMemoryEmulator.Infrastructure.Diagnostics;
using CosmosDB.InMemoryEmulator.Infrastructure.HttpGateway;
using CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// A custom <see cref="HttpMessageHandler"/> that intercepts all Cosmos SDK HTTP
/// requests and serves responses from an <see cref="InMemoryContainer"/>, including
/// account metadata, collection metadata, partition key ranges, and query execution.
/// </summary>
public class FakeCosmosHandler : HttpMessageHandler
{
    private readonly InMemoryContainer _container;
    private readonly ConcurrentBag<string> _requestLog = new();
    private readonly ConcurrentBag<string> _queryLog = new();
    private readonly ConcurrentBag<string> _unrecognisedHeaders = new();
    private readonly List<string> _sdkVersionWarnings = new();
    private readonly QueryResultCache _queryResultCache;
    private readonly GatewayQueryExecutionService _queryExecutionService;
    private readonly string _collectionRid;
    private readonly string _databaseRid;
    private readonly int _partitionKeyRangeCount;
    private readonly string _partitionKeyPath;
    private readonly IQueryPlanStrategy _queryPlanStrategy;
    private readonly IBatchSchemaStrategy _batchSchemaStrategy;
    private readonly GatewayUsersRouteHandler _usersHandler = new();

    /// <summary>
    /// The version of the <c>PartitionedQueryExecutionInfo</c> format returned by query plan responses.
    /// If the SDK starts expecting a different version, queries may fail.
    /// </summary>
    public const int QueryPlanVersion = 2;

    /// <summary>Minimum Cosmos SDK version that has been tested with this handler.</summary>
    public static readonly Version MinTestedSdkVersion = new(3, 35, 0, 0);

    /// <summary>Maximum Cosmos SDK version that has been tested with this handler.</summary>
    public static readonly Version MaxTestedSdkVersion = new(3, 58, 0, 0);

    internal const string FakeConnectionString = "AccountEndpoint=https://localhost:9999/;AccountKey=dGVzdGtleQ==;";

    private readonly AsyncLocal<PartitionKey?> _partitionKeyOverride = new();
    private readonly AsyncLocal<PartitionKey?> _partitionKeyHint = new();

    internal void SetPartitionKeyHint(PartitionKey? partitionKey) => _partitionKeyHint.Value = partitionKey;

    internal IDisposable WithPartitionKey(PartitionKey partitionKey)
    {
        _partitionKeyOverride.Value = partitionKey;
        return new PartitionKeyScope(_partitionKeyOverride);
    }

    private sealed class PartitionKeyScope(AsyncLocal<PartitionKey?> asyncLocal) : IDisposable
    {
        public void Dispose() => asyncLocal.Value = null;
    }

    /// <summary>Recorded HTTP requests in the form "METHOD /path".</summary>
    public IReadOnlyCollection<string> RequestLog => _requestLog;

    /// <summary>The backing in-memory container that stores all data for this handler.</summary>
    public Container BackingContainer => _container;

    /// <summary>Internal typed access to the backing container.</summary>
    internal InMemoryContainer BackingInMemoryContainer => _container;

    internal string DatabaseRidInternal => _databaseRid;

    internal string GetCollectionMetadataInternal() =>
        CosmosGatewayMetadataGenerator.GetCollectionMetadata(_container, _databaseRid, _collectionRid);

    /// <summary>Recorded SQL query strings that were executed.</summary>
    public IReadOnlyCollection<string> QueryLog => _queryLog;

    /// <summary>
    /// <c>x-ms-*</c> request headers seen during request processing that are not in the
    /// known headers set.
    /// </summary>
    public IReadOnlyCollection<string> UnrecognisedHeaders => _unrecognisedHeaders;

    /// <summary>
    /// Warnings generated during handler construction if the current Cosmos SDK version
    /// falls outside the tested range.
    /// </summary>
    public IReadOnlyList<string> SdkVersionWarnings => _sdkVersionWarnings;

    /// <summary>
    /// Optional fault injection delegate. When set, it is called before normal request
    /// handling. If it returns a non-null response, that response is used immediately.
    /// </summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? FaultInjector { get; set; }

    /// <summary>
    /// When <c>true</c>, the <see cref="FaultInjector"/> delegate is also invoked for
    /// metadata requests (account info, collection metadata, partition key ranges).
    /// </summary>
    public bool FaultInjectorIncludesMetadata { get; set; }

    internal FakeCosmosHandler(InMemoryContainer container)
        : this(container, new FakeCosmosHandlerOptions())
    {
    }

    internal FakeCosmosHandler(InMemoryContainer container, FakeCosmosHandlerOptions options)
    {
        _container = container;
        _partitionKeyRangeCount = Math.Max(1, options.PartitionKeyRangeCount);
        _queryResultCache = new QueryResultCache(options.CacheTtl, options.CacheMaxEntries);
        (_databaseRid, _collectionRid) = CosmosGatewayMetadataGenerator.GenerateResourceIds(container.Id);
        _partitionKeyPath = container.PartitionKeyPaths.FirstOrDefault()?.TrimStart('/') ?? "id";
        _queryPlanStrategy = options.QueryPlanStrategy;
        _batchSchemaStrategy = options.BatchSchemaStrategy;
        _queryExecutionService = new GatewayQueryExecutionService(
            _container,
            _queryResultCache,
            _queryPlanStrategy,
            _collectionRid,
            _partitionKeyRangeCount,
            _partitionKeyPath,
            _queryLog);
        CheckSdkVersion();
    }

    private void CheckSdkVersion()
    {
        var sdkVersion = typeof(CosmosClient).Assembly.GetName().Version;
        if (sdkVersion is null) return;

        if (sdkVersion < MinTestedSdkVersion)
        {
            var warning = $"FakeCosmosHandler: Cosmos SDK {sdkVersion} is older than the minimum tested version " +
                $"({MinTestedSdkVersion}). Some features may not work correctly. " +
                $"Call VerifySdkCompatibilityAsync() to check for compatibility.";
            _sdkVersionWarnings.Add(warning);
            System.Diagnostics.Trace.TraceWarning(warning);
        }
        else if (sdkVersion > MaxTestedSdkVersion)
        {
            var warning = $"FakeCosmosHandler: Cosmos SDK {sdkVersion} is newer than the last tested version " +
                $"({MaxTestedSdkVersion}). Call VerifySdkCompatibilityAsync() in your test setup " +
                $"to check for compatibility.";
            _sdkVersionWarnings.Add(warning);
            System.Diagnostics.Trace.TraceWarning(warning);
        }
    }

    internal static bool BatchSchemasAvailable => HybridRowBatchCodec.BatchSchemasAvailable;

    internal static string StripOffsetLimitStatic(string sql) => GatewayOrderByQueryRewriter.StripOffsetLimit(sql);

    internal static string BuildOrderByRewrittenQueryStatic(CosmosSqlQuery parsed) =>
        GatewayOrderByQueryRewriter.BuildOrderByRewrittenQuery(parsed);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var method = request.Method.Method;

        _requestLog.Add($"{method} {path}");
        foreach (var header in request.Headers)
        {
            if (header.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase) &&
                !CosmosHttpHeaders.KnownRequestHeaders.Contains(header.Key))
            {
                _unrecognisedHeaders.Add(header.Key);
            }
        }

        if (_container._isDeleted && path.Contains("/docs"))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    "{\"message\":\"Owner resource does not exist\"}",
                    Encoding.UTF8, "application/json")
            };
        }

        if (FaultInjectorIncludesMetadata && FaultInjector is not null)
        {
            var faultResponse = FaultInjector(request);
            if (faultResponse is not null)
            {
                return faultResponse;
            }
        }

        if (method == "GET" && path is "/" or "")
        {
            return CosmosHttpHeaders.CreateJsonResponse(
                CosmosGatewayMetadataGenerator.AccountMetadata,
                _container.CurrentSessionToken);
        }

        if (method == "GET" && path.Contains("/pkranges"))
        {
            return CosmosGatewayMetadataGenerator.HandlePartitionKeyRanges(
                request,
                _databaseRid,
                _collectionRid,
                _partitionKeyRangeCount,
                _container.CurrentSessionToken);
        }

        var scriptsResponse = await GatewayScriptsRouteHandler.TryHandleAsync(request, _container, cancellationToken);
        if (scriptsResponse is not null)
        {
            return scriptsResponse;
        }

        var usersResponse = await _usersHandler.TryHandleAsync(request, _container.CurrentSessionToken, cancellationToken);
        if (usersResponse is not null)
        {
            return usersResponse;
        }

        if (method == "GET" && path.Contains("/colls/") && !path.Contains("/docs"))
        {
            return CosmosHttpHeaders.CreateJsonResponse(
                GetCollectionMetadataInternal(),
                _container.CurrentSessionToken);
        }

        if (Regex.IsMatch(path, @"^/dbs/?$"))
        {
            if (method == "POST")
            {
                var body = request.Content is not null
                    ? await request.Content.ReadAsStringAsync(cancellationToken)
                    : "{}";
                var dbName = JObject.Parse(body)["id"]?.ToString() ?? "fake-db";
                return CosmosHttpHeaders.CreateJsonResponse(
                    CosmosGatewayMetadataGenerator.GetDatabaseMetadata(dbName, _databaseRid),
                    _container.CurrentSessionToken,
                    HttpStatusCode.Created);
            }

            var dbList = new JObject
            {
                ["_rid"] = "",
                ["Databases"] = new JArray(JObject.Parse(CosmosGatewayMetadataGenerator.GetDatabaseMetadata("fake-db", _databaseRid))),
                ["_count"] = 1
            };
            return CosmosHttpHeaders.CreateJsonResponse(
                dbList.ToString(Formatting.None),
                _container.CurrentSessionToken);
        }

        if (Regex.IsMatch(path, @"^/dbs/[^/]+/?$"))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var dbName = segments.Length > 1 ? Uri.UnescapeDataString(segments[1]) : "fake-db";
            return method switch
            {
                "GET" or "PUT" => CosmosHttpHeaders.CreateJsonResponse(
                    CosmosGatewayMetadataGenerator.GetDatabaseMetadata(dbName, _databaseRid),
                    _container.CurrentSessionToken),
                "DELETE" => CosmosHttpHeaders.CreateNoContentResponse(),
                _ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            };
        }

        if (Regex.IsMatch(path, @"^/dbs/[^/]+/colls/?$"))
        {
            if (method == "POST")
            {
                return CosmosHttpHeaders.CreateJsonResponse(
                    GetCollectionMetadataInternal(),
                    _container.CurrentSessionToken,
                    HttpStatusCode.Created);
            }

            var containerList = new JObject
            {
                ["_rid"] = _databaseRid,
                ["DocumentCollections"] = new JArray(JObject.Parse(GetCollectionMetadataInternal())),
                ["_count"] = 1
            };
            return CosmosHttpHeaders.CreateJsonResponse(
                containerList.ToString(Formatting.None),
                _container.CurrentSessionToken);
        }

        if (!FaultInjectorIncludesMetadata && FaultInjector is not null)
        {
            var faultResponse = FaultInjector(request);
            if (faultResponse is not null)
            {
                return faultResponse;
            }
        }

        if (path.Contains("/docs/") && CosmosHttpHeaders.HasDocumentId(path))
        {
            switch (method)
            {
                case "GET":
                    return await HandlePointReadAsync(request, cancellationToken);
                case "PUT":
                    return await HandleReplaceAsync(request, cancellationToken);
                case "DELETE":
                    return await HandleDeleteAsync(request, cancellationToken);
                case "PATCH":
                    return await HandlePatchAsync(request, cancellationToken);
            }
        }

        if (method == "POST" && path.Contains("/docs"))
        {
            if (CosmosHttpHeaders.IsBatchRequest(request))
            {
                return await HybridRowBatchCodec.HandleBatchAsync(
                    request,
                    _container,
                    _batchSchemaStrategy,
                    cancellationToken);
            }

            if (CosmosHttpHeaders.IsQueryPlanRequest(request))
            {
                return await _queryExecutionService.HandleQueryPlanAsync(request, cancellationToken);
            }

            if (CosmosHttpHeaders.IsQueryRequest(request))
            {
                return await _queryExecutionService.HandleQueryAsync(
                    request,
                    _partitionKeyOverride.Value ?? _partitionKeyHint.Value,
                    cancellationToken);
            }

            if (CosmosHttpHeaders.IsUpsertRequest(request))
            {
                return await HandleUpsertAsync(request, cancellationToken);
            }

            return await HandleCreateAsync(request, cancellationToken);
        }

        if (method == "GET" && path.Contains("/docs"))
        {
            return await _queryExecutionService.HandleReadFeedAsync(
                request,
                _partitionKeyOverride.Value ?? _partitionKeyHint.Value,
                cancellationToken);
        }

        if (path.Contains("/offers"))
        {
            return CosmosGatewayMetadataGenerator.HandleOffersRequest(_container, _collectionRid);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                $"{{\"message\":\"FakeCosmosHandler: unrecognised route {method} {path}\"}}",
                Encoding.UTF8,
                "application/json")
        };
    }

    private async Task<HttpResponseMessage> HandleCreateAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var pk = CosmosHttpHeaders.ExtractPartitionKey(request) ?? PartitionKey.None;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await _container.CreateItemStreamAsync(stream, pk, CosmosHttpHeaders.BuildItemRequestOptions(request), cancellationToken);
        return CosmosHttpHeaders.ConvertToHttpResponse(result, _container.CurrentSessionToken, CosmosHttpHeaders.ChargeKind.Write);
    }

    private async Task<HttpResponseMessage> HandleUpsertAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var pk = CosmosHttpHeaders.ExtractPartitionKey(request) ?? PartitionKey.None;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await _container.UpsertItemStreamAsync(stream, pk, CosmosHttpHeaders.BuildItemRequestOptions(request), cancellationToken);
        return CosmosHttpHeaders.ConvertToHttpResponse(result, _container.CurrentSessionToken, CosmosHttpHeaders.ChargeKind.Write);
    }

    private async Task<HttpResponseMessage> HandlePointReadAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = CosmosHttpHeaders.ExtractDocumentId(request);
        var pk = CosmosHttpHeaders.ExtractPartitionKey(request) ?? PartitionKey.None;
        var result = await _container.ReadItemStreamAsync(id, pk, CosmosHttpHeaders.BuildItemRequestOptions(request), cancellationToken);
        return CosmosHttpHeaders.ConvertToHttpResponse(result, _container.CurrentSessionToken, CosmosHttpHeaders.ChargeKind.Read);
    }

    private async Task<HttpResponseMessage> HandleReplaceAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = CosmosHttpHeaders.ExtractDocumentId(request);
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var pk = CosmosHttpHeaders.ExtractPartitionKey(request) ?? PartitionKey.None;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await _container.ReplaceItemStreamAsync(stream, id, pk, CosmosHttpHeaders.BuildItemRequestOptions(request), cancellationToken);
        return CosmosHttpHeaders.ConvertToHttpResponse(result, _container.CurrentSessionToken, CosmosHttpHeaders.ChargeKind.Write);
    }

    private async Task<HttpResponseMessage> HandleDeleteAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = CosmosHttpHeaders.ExtractDocumentId(request);
        var pk = CosmosHttpHeaders.ExtractPartitionKey(request) ?? PartitionKey.None;
        var result = await _container.DeleteItemStreamAsync(id, pk, CosmosHttpHeaders.BuildItemRequestOptions(request), cancellationToken);
        return CosmosHttpHeaders.ConvertToHttpResponse(result, _container.CurrentSessionToken, CosmosHttpHeaders.ChargeKind.Write);
    }

    private async Task<HttpResponseMessage> HandlePatchAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = CosmosHttpHeaders.ExtractDocumentId(request);
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var pk = CosmosHttpHeaders.ExtractPartitionKey(request) ?? PartitionKey.None;
        var (operations, condition) = CosmosHttpHeaders.ParsePatchBody(body);
        var options = new PatchItemRequestOptions();
        if (condition is not null)
        {
            options.FilterPredicate = condition;
        }
        if (request.Headers.IfMatch.Any())
        {
            options.IfMatchEtag = request.Headers.IfMatch.First().Tag;
        }
        var result = await _container.PatchItemStreamAsync(id, pk, operations, options, cancellationToken);
        return CosmosHttpHeaders.ConvertToHttpResponse(result, _container.CurrentSessionToken, CosmosHttpHeaders.ChargeKind.Write);
    }

    internal static HttpMessageHandler CreateRouter(
        IReadOnlyDictionary<string, FakeCosmosHandler> handlers)
    {
        return new CosmosRoutingMessageHandler(handlers);
    }

    internal CosmosClient WrapClient(CosmosClient innerClient)
        => WrapClient(innerClient, new Dictionary<string, FakeCosmosHandler> { [_container.Id] = this });

    internal static CosmosClient WrapClient(
        CosmosClient innerClient,
        IReadOnlyDictionary<string, FakeCosmosHandler> handlers)
        => new PkAwareCosmosClient(innerClient, handlers);

    internal CosmosClient CreateClient()
        => CreateClient(new Dictionary<string, FakeCosmosHandler> { [_container.Id] = this });

    internal static CosmosClient CreateClient(
        Dictionary<string, FakeCosmosHandler> handlers)
    {
        HttpMessageHandler httpHandler = handlers.Count == 1
            ? handlers.Values.First()
            : CreateRouter(handlers);

        var innerClient = new CosmosClient(
            FakeConnectionString,
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                HttpClientFactory = () => new HttpClient(httpHandler)
            });

        return WrapClient(innerClient, handlers);
    }

    /// <summary>
    /// Runs a self-test to verify that the current Cosmos SDK version still uses the
    /// HTTP contract this handler expects.
    /// </summary>
    public static Task VerifySdkCompatibilityAsync() =>
        SdkCompatibilityVerifier.VerifyAsync();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _queryResultCache.Clear();
            _container.Dispose();
        }

        base.Dispose(disposing);
    }
}
