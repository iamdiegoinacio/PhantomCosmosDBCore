#nullable enable
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Infrastructure.HttpGateway;

/// <summary>
/// Handles Cosmos DB HTTP Gateway REST routes for database users
/// (<c>/dbs/{db}/users</c>) and permissions (<c>/dbs/{db}/users/{user}/permissions</c>).
/// </summary>
internal sealed class GatewayUsersRouteHandler
{
    private static readonly Regex UsersRouteRegex = new(
        @"^/dbs/(?<db>[^/]+)/users(?:/(?<userId>[^/]+))?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PermissionsRouteRegex = new(
        @"^/dbs/(?<db>[^/]+)/users/(?<userId>[^/]+)/permissions(?:/(?<permId>[^/]+))?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, UserEntry>> _databases =
        new(StringComparer.Ordinal);

    private sealed class UserEntry
    {
        public required JObject Properties { get; set; }
        public ConcurrentDictionary<string, JObject> Permissions { get; } = new(StringComparer.Ordinal);
    }

    public async Task<HttpResponseMessage?> TryHandleAsync(
        HttpRequestMessage request,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var method = request.Method.Method;

        var permMatch = PermissionsRouteRegex.Match(path);
        if (permMatch.Success)
        {
            var db = Uri.UnescapeDataString(permMatch.Groups["db"].Value);
            var userId = Uri.UnescapeDataString(permMatch.Groups["userId"].Value);
            var permId = permMatch.Groups["permId"].Success
                ? Uri.UnescapeDataString(permMatch.Groups["permId"].Value)
                : null;
            return await HandlePermissionsAsync(request, db, userId, permId, method, sessionToken, cancellationToken);
        }

        var userMatch = UsersRouteRegex.Match(path);
        if (userMatch.Success)
        {
            var db = Uri.UnescapeDataString(userMatch.Groups["db"].Value);
            var userId = userMatch.Groups["userId"].Success
                ? Uri.UnescapeDataString(userMatch.Groups["userId"].Value)
                : null;
            return await HandleUsersAsync(request, db, userId, method, sessionToken, cancellationToken);
        }

        return null;
    }

    private async Task<HttpResponseMessage> HandleUsersAsync(
        HttpRequestMessage request,
        string db,
        string? userId,
        string method,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        var users = _databases.GetOrAdd(db, _ => new ConcurrentDictionary<string, UserEntry>(StringComparer.Ordinal));

        if (userId is null)
        {
            if (method == "GET")
            {
                var list = new JArray(users.Values.Select(u => u.Properties));
                var envelope = new JObject
                {
                    ["_rid"] = string.Empty,
                    ["Users"] = list,
                    ["_count"] = list.Count
                };
                return CosmosHttpHeaders.CreateJsonResponse(envelope.ToString(Formatting.None), sessionToken);
            }

            if (method == "POST")
            {
                var body = await ReadJsonBodyAsync(request, cancellationToken);
                var id = body["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    return CosmosHttpHeaders.CreateErrorResponse(
                        HttpStatusCode.BadRequest, "User 'id' property is required.", 0, sessionToken);
                }

                var enriched = EnrichUser(id, db);
                var isUpsert = CosmosHttpHeaders.IsUpsertRequest(request);
                if (isUpsert)
                {
                    var existed = users.ContainsKey(id);
                    users.AddOrUpdate(
                        id,
                        _ => new UserEntry { Properties = enriched },
                        (_, existing) =>
                        {
                            existing.Properties = enriched;
                            return existing;
                        });
                    return CosmosHttpHeaders.CreateJsonResponse(
                        enriched.ToString(Formatting.None),
                        sessionToken,
                        existed ? HttpStatusCode.OK : HttpStatusCode.Created);
                }

                if (!users.TryAdd(id, new UserEntry { Properties = enriched }))
                {
                    return CosmosHttpHeaders.CreateErrorResponse(
                        HttpStatusCode.Conflict, $"User '{id}' already exists.", 0, sessionToken);
                }

                return CosmosHttpHeaders.CreateJsonResponse(
                    enriched.ToString(Formatting.None), sessionToken, HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        if (!users.TryGetValue(userId, out var userEntry))
        {
            return CosmosHttpHeaders.CreateErrorResponse(
                HttpStatusCode.NotFound, $"User '{userId}' not found.", 0, sessionToken);
        }

        switch (method)
        {
            case "GET":
                return CosmosHttpHeaders.CreateJsonResponse(
                    userEntry.Properties.ToString(Formatting.None), sessionToken);
            case "PUT":
            {
                var body = await ReadJsonBodyAsync(request, cancellationToken);
                var newId = body["id"]?.ToString();
                if (!string.IsNullOrEmpty(newId) && !string.Equals(newId, userId, StringComparison.Ordinal))
                {
                    return CosmosHttpHeaders.CreateErrorResponse(
                        HttpStatusCode.BadRequest, "Replacing user id is not allowed.", 0, sessionToken);
                }

                var enriched = EnrichUser(userId, db);
                userEntry.Properties = enriched;
                return CosmosHttpHeaders.CreateJsonResponse(
                    enriched.ToString(Formatting.None), sessionToken);
            }
            case "DELETE":
                users.TryRemove(userId, out _);
                return CosmosHttpHeaders.CreateNoContentResponse();
            default:
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }

    private async Task<HttpResponseMessage> HandlePermissionsAsync(
        HttpRequestMessage request,
        string db,
        string userId,
        string? permId,
        string method,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        var users = _databases.GetOrAdd(db, _ => new ConcurrentDictionary<string, UserEntry>(StringComparer.Ordinal));
        if (!users.TryGetValue(userId, out var userEntry))
        {
            return CosmosHttpHeaders.CreateErrorResponse(
                HttpStatusCode.NotFound, $"User '{userId}' not found.", 0, sessionToken);
        }

        var permissions = userEntry.Permissions;

        if (permId is null)
        {
            if (method == "GET")
            {
                var list = new JArray(permissions.Values);
                var envelope = new JObject
                {
                    ["_rid"] = string.Empty,
                    ["Permissions"] = list,
                    ["_count"] = list.Count
                };
                return CosmosHttpHeaders.CreateJsonResponse(envelope.ToString(Formatting.None), sessionToken);
            }

            if (method == "POST")
            {
                var body = await ReadJsonBodyAsync(request, cancellationToken);
                var id = body["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    return CosmosHttpHeaders.CreateErrorResponse(
                        HttpStatusCode.BadRequest, "Permission 'id' property is required.", 0, sessionToken);
                }

                var enriched = EnrichPermission(body, id, db, userId);
                var isUpsert = CosmosHttpHeaders.IsUpsertRequest(request);
                if (isUpsert)
                {
                    var existed = permissions.ContainsKey(id);
                    permissions[id] = enriched;
                    return CosmosHttpHeaders.CreateJsonResponse(
                        enriched.ToString(Formatting.None),
                        sessionToken,
                        existed ? HttpStatusCode.OK : HttpStatusCode.Created);
                }

                if (!permissions.TryAdd(id, enriched))
                {
                    return CosmosHttpHeaders.CreateErrorResponse(
                        HttpStatusCode.Conflict, $"Permission '{id}' already exists.", 0, sessionToken);
                }

                return CosmosHttpHeaders.CreateJsonResponse(
                    enriched.ToString(Formatting.None), sessionToken, HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        if (!permissions.TryGetValue(permId, out var existingPerm))
        {
            return CosmosHttpHeaders.CreateErrorResponse(
                HttpStatusCode.NotFound, $"Permission '{permId}' not found.", 0, sessionToken);
        }

        switch (method)
        {
            case "GET":
                return CosmosHttpHeaders.CreateJsonResponse(existingPerm.ToString(Formatting.None), sessionToken);
            case "PUT":
            {
                var body = await ReadJsonBodyAsync(request, cancellationToken);
                var enriched = EnrichPermission(body, permId, db, userId);
                permissions[permId] = enriched;
                return CosmosHttpHeaders.CreateJsonResponse(enriched.ToString(Formatting.None), sessionToken);
            }
            case "DELETE":
                permissions.TryRemove(permId, out _);
                return CosmosHttpHeaders.CreateNoContentResponse();
            default:
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }

    private static async Task<JObject> ReadJsonBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null)
        {
            return new JObject();
        }

        var text = await request.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
    }

    private static JObject EnrichUser(string id, string db)
    {
        return new JObject
        {
            ["id"] = id,
            ["_rid"] = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('='),
            ["_self"] = $"dbs/{db}/users/{id}/",
            ["_etag"] = $"\"{Guid.NewGuid()}\"",
            ["_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["_permissions"] = "permissions/"
        };
    }

    private static JObject EnrichPermission(JObject source, string id, string db, string userId)
    {
        var copy = (JObject)source.DeepClone();
        copy["id"] = id;
        copy["_rid"] = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=');
        copy["_self"] = $"dbs/{db}/users/{userId}/permissions/{id}/";
        copy["_etag"] = $"\"{Guid.NewGuid()}\"";
        copy["_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        copy["_token"] ??= $"type=resource&ver=1&sig={Guid.NewGuid():N}";
        return copy;
    }
}
