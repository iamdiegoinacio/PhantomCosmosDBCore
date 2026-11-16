#nullable enable
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Infrastructure.HttpGateway;

/// <summary>
/// Serves the REST routes for stored procedures, triggers, user-defined functions and the
/// delete-by-partition-key operation on top of the container's in-memory implementation.
/// </summary>
internal static class GatewayScriptsRouteHandler
{
    private static readonly Regex ScriptRouteRegex = new(
        @"/colls/[^/]+/(?<kind>sprocs|triggers|udfs)(?:/(?<id>[^/]+))?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PartitionKeyDeleteRegex = new(
        @"/colls/[^/]+/operations/partitionkeydelete/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns a response when the request targets one of the handled routes, otherwise <c>null</c>.
    /// </summary>
    public static async Task<HttpResponseMessage?> TryHandleAsync(
        HttpRequestMessage request,
        InMemoryContainer container,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var method = request.Method.Method;

        if (method == "POST" && PartitionKeyDeleteRegex.IsMatch(path))
        {
            return await HandlePartitionKeyDeleteAsync(request, container, cancellationToken);
        }

        var match = ScriptRouteRegex.Match(path);
        if (!match.Success)
        {
            return null;
        }

        var kind = match.Groups["kind"].Value.ToLowerInvariant();
        var id = match.Groups["id"].Success ? Uri.UnescapeDataString(match.Groups["id"].Value) : null;

        try
        {
            return kind switch
            {
                "sprocs" => await HandleStoredProceduresAsync(request, container, method, id, cancellationToken),
                "triggers" => await HandleTriggersAsync(request, container, method, id, cancellationToken),
                _ => await HandleUdfsAsync(request, container, method, id, cancellationToken),
            };
        }
        catch (CosmosException ex)
        {
            return CosmosHttpHeaders.CreateErrorResponse(
                ex.StatusCode, ex.Message, ex.SubStatusCode, container.CurrentSessionToken);
        }
    }

    private static async Task<HttpResponseMessage> HandlePartitionKeyDeleteAsync(
        HttpRequestMessage request,
        InMemoryContainer container,
        CancellationToken cancellationToken)
    {
        var pk = CosmosHttpHeaders.ExtractPartitionKey(request);
        if (pk is null)
        {
            return CosmosHttpHeaders.CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "The x-ms-documentdb-partitionkey header is required for partition key delete.",
                0,
                container.CurrentSessionToken);
        }

        var result = await container.DeleteAllItemsByPartitionKeyStreamAsync(pk.Value, null, cancellationToken);
        return CosmosHttpHeaders.ConvertToHttpResponse(result, container.CurrentSessionToken);
    }

    private static async Task<HttpResponseMessage> HandleStoredProceduresAsync(
        HttpRequestMessage request,
        InMemoryContainer container,
        string method,
        string? id,
        CancellationToken cancellationToken)
    {
        var scripts = container.Scripts;
        var session = container.CurrentSessionToken;

        if (id is null)
        {
            if (method == "GET")
            {
                return await ListAsync(
                    scripts.GetStoredProcedureQueryIterator<JObject>((string?)null),
                    "StoredProcedures", session);
            }

            if (method == "POST")
            {
                var props = await ReadBodyAsync<StoredProcedureProperties>(request, cancellationToken);
                return Convert(await scripts.CreateStoredProcedureStreamAsync(props, null, cancellationToken), session);
            }

            return MethodNotAllowed();
        }

        switch (method)
        {
            case "GET":
                return Convert(await scripts.ReadStoredProcedureStreamAsync(id, null, cancellationToken), session);
            case "PUT":
            {
                var props = await ReadBodyAsync<StoredProcedureProperties>(request, cancellationToken);
                return Convert(await scripts.ReplaceStoredProcedureStreamAsync(props, null, cancellationToken), session);
            }
            case "DELETE":
                return Convert(await scripts.DeleteStoredProcedureStreamAsync(id, null, cancellationToken), session);
            case "POST":
            {
                var body = request.Content is null
                    ? "[]"
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                var pk = CosmosHttpHeaders.ExtractPartitionKey(request) ?? PartitionKey.None;
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(body) ? "[]" : body));
                var result = await scripts.ExecuteStoredProcedureStreamAsync(id, stream, pk, null, cancellationToken);
                var response = Convert(result, session);
                var log = result.Headers["x-ms-documentdb-script-log-results"];
                if (log is not null)
                {
                    response.Headers.TryAddWithoutValidation("x-ms-documentdb-script-log-results", log);
                }

                return response;
            }
            default:
                return MethodNotAllowed();
        }
    }

    private static async Task<HttpResponseMessage> HandleTriggersAsync(
        HttpRequestMessage request,
        InMemoryContainer container,
        string method,
        string? id,
        CancellationToken cancellationToken)
    {
        var scripts = container.Scripts;
        var session = container.CurrentSessionToken;

        if (id is null)
        {
            if (method == "GET")
            {
                return await ListAsync(scripts.GetTriggerQueryIterator<JObject>((string?)null), "Triggers", session);
            }

            if (method == "POST")
            {
                var props = await ReadBodyAsync<TriggerProperties>(request, cancellationToken);
                return Convert(await scripts.CreateTriggerStreamAsync(props, null, cancellationToken), session);
            }

            return MethodNotAllowed();
        }

        switch (method)
        {
            case "GET":
                return Convert(await scripts.ReadTriggerStreamAsync(id, null, cancellationToken), session);
            case "PUT":
            {
                var props = await ReadBodyAsync<TriggerProperties>(request, cancellationToken);
                return Convert(await scripts.ReplaceTriggerStreamAsync(props, null, cancellationToken), session);
            }
            case "DELETE":
                return Convert(await scripts.DeleteTriggerStreamAsync(id, null, cancellationToken), session);
            default:
                return MethodNotAllowed();
        }
    }

    private static async Task<HttpResponseMessage> HandleUdfsAsync(
        HttpRequestMessage request,
        InMemoryContainer container,
        string method,
        string? id,
        CancellationToken cancellationToken)
    {
        var scripts = container.Scripts;
        var session = container.CurrentSessionToken;

        if (id is null)
        {
            if (method == "GET")
            {
                return await ListAsync(
                    scripts.GetUserDefinedFunctionQueryIterator<JObject>((string?)null),
                    "UserDefinedFunctions", session);
            }

            if (method == "POST")
            {
                var props = await ReadBodyAsync<UserDefinedFunctionProperties>(request, cancellationToken);
                return Convert(await scripts.CreateUserDefinedFunctionStreamAsync(props, null, cancellationToken), session);
            }

            return MethodNotAllowed();
        }

        switch (method)
        {
            case "GET":
                return Convert(await scripts.ReadUserDefinedFunctionStreamAsync(id, null, cancellationToken), session);
            case "PUT":
            {
                var props = await ReadBodyAsync<UserDefinedFunctionProperties>(request, cancellationToken);
                return Convert(await scripts.ReplaceUserDefinedFunctionStreamAsync(props, null, cancellationToken), session);
            }
            case "DELETE":
                return Convert(await scripts.DeleteUserDefinedFunctionStreamAsync(id, null, cancellationToken), session);
            default:
                return MethodNotAllowed();
        }
    }

    private static async Task<T> ReadBodyAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        return JsonConvert.DeserializeObject<T>(body)
               ?? throw new InvalidOperationException("Request body could not be parsed.");
    }

    private static async Task<HttpResponseMessage> ListAsync(
        FeedIterator<JObject> iterator,
        string collectionName,
        string sessionToken)
    {
        var items = new JArray();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            foreach (var item in page)
            {
                items.Add(item);
            }
        }

        var payload = new JObject
        {
            ["_rid"] = string.Empty,
            [collectionName] = items,
            ["_count"] = items.Count
        };
        return CosmosHttpHeaders.CreateJsonResponse(payload.ToString(Formatting.None), sessionToken);
    }

    private static HttpResponseMessage Convert(ResponseMessage message, string sessionToken)
        => CosmosHttpHeaders.ConvertToHttpResponse(message, sessionToken);

    private static HttpResponseMessage MethodNotAllowed()
        => new(HttpStatusCode.MethodNotAllowed);
}
