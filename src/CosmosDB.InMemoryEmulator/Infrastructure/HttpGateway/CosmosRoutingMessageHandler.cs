#nullable enable
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Infrastructure.HttpGateway;

/// <summary>
/// Multi-container HTTP router and dynamic DDL handler (<c>POST/PUT/DELETE/GET /dbs/{db}/colls</c>).
/// Dispatches requests to the appropriate per-container <see cref="FakeCosmosHandler"/>.
/// </summary>
internal sealed class CosmosRoutingMessageHandler : HttpMessageHandler
{
    private readonly DynamicContainerRegistry _registry;
    private readonly FakeCosmosHandler _default;

    public CosmosRoutingMessageHandler(IReadOnlyDictionary<string, FakeCosmosHandler> handlers)
        : this(handlers, new DynamicContainerRegistry())
    {
    }

    public CosmosRoutingMessageHandler(
        IReadOnlyDictionary<string, FakeCosmosHandler> handlers,
        DynamicContainerRegistry registry)
    {
        if (!handlers.Any())
            throw new ArgumentException("At least one handler must be registered.", nameof(handlers));
        _registry = registry;
        foreach (var kvp in handlers)
            _registry.Handlers[kvp.Key] = kvp.Value;
        _default = handlers.Values.First();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var method = request.Method.Method;

        if (path is "/" or "")
        {
            return await InvokeHandlerAsync(_default, request, cancellationToken);
        }

        // Container creation: POST /dbs/{db}/colls
        // Container listing: GET /dbs/{db}/colls
        var collsMatch = Regex.Match(path, @"^/dbs/([^/]+)/colls/?$");
        if (collsMatch.Success)
        {
            var dbNameForColls = collsMatch.Groups[1].Value;
            if (method == "POST")
                return await HandleCreateContainerAsync(request, dbNameForColls, cancellationToken);
            if (method == "GET")
                return HandleListContainers(dbNameForColls);
        }

        // Database management: /dbs
        if (Regex.IsMatch(path, @"^/dbs/?$"))
        {
            return await HandleDatabaseListOrCreate(request, method, cancellationToken);
        }

        // Database CRUD: /dbs/{id}
        if (Regex.IsMatch(path, @"^/dbs/[^/]+/?$") && !path.Contains("/colls"))
        {
            return HandleDatabaseCrud(request, path, method);
        }

        var match = Regex.Match(path, @"/dbs/([^/]+)/colls/([^/]+)");
        if (match.Success)
        {
            var dbName = match.Groups[1].Value;
            var containerName = match.Groups[2].Value;

            // Container-level operations (not sub-resources)
            if (Regex.IsMatch(path, @"/colls/[^/]+/?$"))
            {
                if (method == "DELETE")
                {
                    return HandleDeleteContainer(dbName, containerName);
                }

                if (method == "GET")
                {
                    return HandleReadContainer(dbName, containerName);
                }

                if (method == "PUT")
                {
                    return await HandleReplaceContainer(request, dbName, containerName, cancellationToken);
                }
            }

            // Try database+container compound key first, then fall back to container-only
            var compoundKey = $"{dbName}/{containerName}";
            if (_registry.Handlers.TryGetValue(compoundKey, out var compoundHandler))
            {
                return await InvokeHandlerAsync(compoundHandler, request, cancellationToken);
            }

            if (_registry.Handlers.TryGetValue(containerName, out var handler))
            {
                return await InvokeHandlerAsync(handler, request, cancellationToken);
            }

            // SDK internal routes use base64-encoded RIDs (e.g. "AQAAAA==") for
            // partition key range and other metadata requests. Fall back to the
            // default handler for these rather than throwing.
            if (containerName.Contains('=') || path.Contains("/pkranges"))
            {
                return await InvokeHandlerAsync(_default, request, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    $"{{\"message\":\"Container '{containerName}' not found. " +
                    $"Available containers: {string.Join(", ", _registry.Handlers.Keys.OrderBy(k => k))}\"}}",
                    Encoding.UTF8, "application/json")
            };
        }

        return await InvokeHandlerAsync(_default, request, cancellationToken);
    }

    private async Task<HttpResponseMessage> HandleCreateContainerAsync(
        HttpRequestMessage request,
        string dbName,
        CancellationToken cancellationToken)
    {
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : "{}";
        var containerProps = JsonConvert.DeserializeObject<ContainerProperties>(body)
            ?? new ContainerProperties("unknown", "/id");

        var containerName = containerProps.Id;
        var registryKey = GetNewRegistryKey(dbName, containerName);

        var isIfNotExists = CosmosHttpHeaders.IsUpsertRequest(request);

        if (_registry.Handlers.ContainsKey(registryKey))
        {
            if (isIfNotExists && _registry.Handlers.TryGetValue(registryKey, out var existingHandler))
            {
                return CreateContainerMetadataResponse(existingHandler, HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    $"{{\"message\":\"Container '{containerName}' already exists.\"}}",
                    Encoding.UTF8, "application/json")
            };
        }

        var newContainer = new InMemoryContainer(containerProps);

        if (request.Headers.TryGetValues("x-ms-offer-throughput", out var throughputValues) &&
            int.TryParse(throughputValues.FirstOrDefault(), out var throughputValue))
        {
            newContainer._throughput = throughputValue;
        }

        var newHandler = new FakeCosmosHandler(newContainer);

        _registry.Handlers[registryKey] = newHandler;
        _registry.BackingContainers[registryKey] = newContainer;

        var dbMap = _registry.DatabaseContainerKeys.GetOrAdd(dbName, _ => new(StringComparer.Ordinal));
        dbMap[containerName] = registryKey;

        if (_registry.Client is not null)
        {
            _registry.SdkContainers[registryKey] =
                _registry.Client.GetContainer(dbName, containerName);
        }

        return CreateContainerMetadataResponse(newHandler, HttpStatusCode.Created);
    }

    private HttpResponseMessage HandleDeleteContainer(string dbName, string containerName)
    {
        var registryKey = ResolveExistingRegistryKey(dbName, containerName);
        if (registryKey is null || !_registry.Handlers.TryRemove(registryKey, out var handler))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    $"{{\"message\":\"Container '{containerName}' not found.\"}}",
                    Encoding.UTF8, "application/json")
            };
        }

        _registry.BackingContainers.TryRemove(registryKey, out _);
        _registry.SdkContainers.TryRemove(registryKey, out _);

        if (_registry.DatabaseContainerKeys.TryGetValue(dbName, out var dbMap))
            dbMap.TryRemove(containerName, out _);

        handler.BackingInMemoryContainer._isDeleted = true;

        return new HttpResponseMessage(HttpStatusCode.NoContent)
        {
            Headers = { { "x-ms-request-charge", "1" } }
        };
    }

    private HttpResponseMessage HandleReadContainer(string dbName, string containerName)
    {
        var registryKey = ResolveExistingRegistryKey(dbName, containerName);
        if (registryKey is not null && _registry.Handlers.TryGetValue(registryKey, out var handler))
        {
            return CreateContainerMetadataResponse(handler, HttpStatusCode.OK);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                $"{{\"message\":\"Container '{containerName}' not found.\"}}",
                Encoding.UTF8, "application/json")
        };
    }

    private HttpResponseMessage HandleListContainers(string dbName)
    {
        var containerArray = new JArray();

        if (_registry.DatabaseContainerKeys.TryGetValue(dbName, out var containerKeys))
        {
            foreach (var (_, registryKey) in containerKeys)
            {
                if (_registry.Handlers.TryGetValue(registryKey, out var h))
                    containerArray.Add(JObject.Parse(h.GetCollectionMetadataInternal()));
            }
        }
        else
        {
            foreach (var handler in _registry.Handlers.Values)
            {
                containerArray.Add(JObject.Parse(handler.GetCollectionMetadataInternal()));
            }
        }

        var result = new JObject
        {
            ["_rid"] = _default.DatabaseRidInternal,
            ["DocumentCollections"] = containerArray,
            ["_count"] = containerArray.Count
        };

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(result.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("x-ms-request-charge", "1");
        return response;
    }

    private async Task<HttpResponseMessage> HandleReplaceContainer(
        HttpRequestMessage request,
        string dbName,
        string containerName,
        CancellationToken cancellationToken)
    {
        var registryKey = ResolveExistingRegistryKey(dbName, containerName);
        if (registryKey is null || !_registry.Handlers.TryGetValue(registryKey, out var handler))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var props = JObject.Parse(body);
            var newPkPath = props.SelectToken("partitionKey.paths[0]")?.ToString();
            if (newPkPath is not null)
            {
                var existingPkPath = handler.BackingInMemoryContainer.PartitionKeyPaths.FirstOrDefault();
                if (existingPkPath is not null && !string.Equals(newPkPath, existingPkPath, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            $"{{\"message\":\"Partition key path cannot be changed. " +
                            $"Existing: '{existingPkPath}', Requested: '{newPkPath}'\"}}",
                            Encoding.UTF8, "application/json")
                    };
                }
            }

            var container = handler.BackingInMemoryContainer;
            if (props["defaultTtl"] is { Type: JTokenType.Integer } ttlToken)
            {
                var ttl = ttlToken.Value<int>();
                if (ttl != 0) container.DefaultTimeToLive = ttl;
            }
            else if (props["defaultTtl"] is { Type: JTokenType.Null })
            {
                container.DefaultTimeToLive = null;
            }
        }

        return CreateContainerMetadataResponse(handler, HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> HandleDatabaseListOrCreate(
        HttpRequestMessage request,
        string method,
        CancellationToken cancellationToken)
    {
        _ = method;
        return await InvokeHandlerAsync(_default, request, cancellationToken);
    }

    private HttpResponseMessage HandleDatabaseCrud(
        HttpRequestMessage request,
        string path,
        string method)
    {
        _ = request;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var dbName = segments.Length > 1 ? Uri.UnescapeDataString(segments[1]) : "fake-db";
        _registry.KnownDatabases.Add(dbName);

        return method switch
        {
            "GET" or "PUT" => CosmosHttpHeaders.CreateJsonResponse(
                CosmosGatewayMetadataGenerator.GetDatabaseMetadata(dbName, _default.DatabaseRidInternal),
                _default.BackingInMemoryContainer.CurrentSessionToken),
            "DELETE" => new HttpResponseMessage(HttpStatusCode.NoContent) { Headers = { { "x-ms-request-charge", "1" } } },
            _ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
        };
    }

    private string? ResolveExistingRegistryKey(string dbName, string containerName)
    {
        var compoundKey = $"{dbName}/{containerName}";
        if (_registry.Handlers.ContainsKey(compoundKey))
            return compoundKey;
        if (_registry.Handlers.ContainsKey(containerName))
            return containerName;
        return null;
    }

    private string GetNewRegistryKey(string dbName, string containerName)
    {
        return _registry.CompoundKeyDatabases.Contains(dbName)
            ? $"{dbName}/{containerName}"
            : containerName;
    }

    private static HttpResponseMessage CreateContainerMetadataResponse(
        FakeCosmosHandler handler,
        HttpStatusCode statusCode)
    {
        var metadata = handler.GetCollectionMetadataInternal();
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(metadata, Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("x-ms-request-charge", "1");
        response.Headers.TryAddWithoutValidation("etag", $"\"{Guid.NewGuid()}\"");
        return response;
    }

    private static Task<HttpResponseMessage> InvokeHandlerAsync(
        FakeCosmosHandler handler,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        return invoker.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var handler in _registry.Handlers.Values)
            {
                handler.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
