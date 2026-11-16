#nullable enable
using System.Collections.Concurrent;
using System.Net;
using CosmosResponseFactory = Phantom.CosmosDB.Core.Infrastructure.SdkInterop.CosmosResponseFactory;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core;

/// <summary>
/// In-memory implementation of <see cref="Permission"/> for testing.
/// Stores permission properties and returns synthetic responses.
/// No actual authorization is enforced.
/// </summary>
public sealed class InMemoryPermission : Permission
{
    private readonly ConcurrentDictionary<string, PermissionProperties> _parentPermissions;

    public InMemoryPermission(string id, ConcurrentDictionary<string, PermissionProperties> parentPermissions)
    {
        Id = id;
        _parentPermissions = parentPermissions;
    }

    public override string Id { get; }

    public override Task<PermissionResponse> ReadAsync(
        int? tokenExpiryInSeconds = null,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_parentPermissions.TryGetValue(Id, out var props))
            throw InMemoryCosmosException.Create($"Permission '{Id}' not found.", HttpStatusCode.NotFound, 0, string.Empty, 0);

        return Task.FromResult(CosmosResponseFactory.CreatePermissionResponse(this, props, HttpStatusCode.OK));
    }

    public override Task<PermissionResponse> ReplaceAsync(
        PermissionProperties permissionProperties,
        int? tokenExpiryInSeconds = null,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_parentPermissions.ContainsKey(Id))
            throw InMemoryCosmosException.Create($"Permission '{Id}' not found.", HttpStatusCode.NotFound, 0, string.Empty, 0);

        var updated = WithSyntheticMetadata(permissionProperties);
        _parentPermissions[Id] = updated;
        return Task.FromResult(CosmosResponseFactory.CreatePermissionResponse(this, updated, HttpStatusCode.OK));
    }

    public override Task<PermissionResponse> DeleteAsync(
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_parentPermissions.TryRemove(Id, out _))
            throw InMemoryCosmosException.Create($"Permission '{Id}' not found.", HttpStatusCode.NotFound, 0, string.Empty, 0);

        return Task.FromResult(CosmosResponseFactory.CreatePermissionResponse(this, null, HttpStatusCode.NoContent));
    }

    internal static PermissionProperties WithSyntheticMetadata(PermissionProperties source)
    {
        var json = JObject.FromObject(source);
        json["_etag"] = $"\"{Guid.NewGuid()}\"";
        json["_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        json["_token"] = $"type=resource&ver=1&sig=stub_{source.Id}";
        return JsonConvert.DeserializeObject<PermissionProperties>(json.ToString())!;
    }
}
