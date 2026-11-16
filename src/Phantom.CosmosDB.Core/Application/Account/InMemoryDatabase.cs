#nullable enable
#pragma warning disable CS0618 // InMemoryCosmosClient is obsolete but InMemoryDatabase still depends on it
using System.Collections.Concurrent;
using System.Net;
using Phantom.CosmosDB.Core.Domain.Validation;
using CosmosResponseFactory = Phantom.CosmosDB.Core.Infrastructure.SdkInterop.CosmosResponseFactory;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Fluent;

namespace Phantom.CosmosDB.Core;

/// <summary>
/// In-memory implementation of <see cref="Database"/> for testing.
/// Manages a collection of <see cref="InMemoryContainer"/> instances.
/// </summary>
internal class InMemoryDatabase : Database
{
    private readonly ConcurrentDictionary<string, InMemoryContainer> _containers = new();
    private readonly ConcurrentDictionary<string, bool> _explicitlyCreatedContainers = new();
    private readonly ConcurrentDictionary<string, InMemoryUser> _users = new();
    private readonly InMemoryCosmosClient? _client;
    private int _throughput = 400;

    public InMemoryDatabase(string id) : this(id, null) { }

    public InMemoryDatabase(string id, InMemoryCosmosClient? client)
    {
        Id = id;
        _client = client;
    }

    public override string Id { get; }

    public override CosmosClient Client => _client!;

    internal InMemoryContainer GetOrCreateContainer(string containerId, string partitionKeyPath = "/id")
    {
        var isNew = false;
        var container = _containers.GetOrAdd(containerId, name =>
        {
            isNew = true;
            return new InMemoryContainer(name, partitionKeyPath);
        });
        if (isNew)
            container.ExplicitlyCreated = false;
        container.OnDeleted ??= () => _containers.TryRemove(containerId, out _);
        container.SetParentDatabase(Id);
        return container;
    }

    internal InMemoryContainer GetOrCreateContainer(ContainerProperties containerProperties)
    {
        var isNew = false;
        var container = _containers.GetOrAdd(containerProperties.Id, _ =>
        {
            isNew = true;
            return new InMemoryContainer(containerProperties);
        });
        if (isNew)
            container.ExplicitlyCreated = false;
        container.OnDeleted ??= () => _containers.TryRemove(containerProperties.Id, out _);
        container.SetParentDatabase(Id);
        return container;
    }

    public override Task<ContainerResponse> CreateContainerIfNotExistsAsync(
        string id,
        string partitionKeyPath,
        int? throughput = null,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        var created = false;
        var container = _containers.GetOrAdd(id, name =>
        {
            created = true;
            return new InMemoryContainer(name, partitionKeyPath);
        });
        if (!created && !container.ExplicitlyCreated)
        {
            var replacement = new InMemoryContainer(id, partitionKeyPath)
            {
                OnDeleted = () => _containers.TryRemove(id, out _),
                ExplicitlyCreated = true
            };
            replacement.SetParentDatabase(Id);
            _containers[id] = replacement;
            container = replacement;
            created = true;
        }
        container.OnDeleted ??= () => _containers.TryRemove(id, out _);
        container.SetParentDatabase(Id);
        container.ExplicitlyCreated = true;
        if (throughput.HasValue)
            container._throughput = throughput.Value;
        _explicitlyCreatedContainers.TryAdd(id, true);
        var response = CosmosResponseFactory.CreateContainerResponse(
            container,
            new ContainerProperties(container.Id, partitionKeyPath ?? "/id"),
            created ? HttpStatusCode.Created : HttpStatusCode.OK);
        return Task.FromResult(response);
    }

    public override Task<ContainerResponse> CreateContainerIfNotExistsAsync(
        ContainerProperties containerProperties,
        int? throughput = null,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        var id = containerProperties.Id;
        if (string.IsNullOrEmpty(containerProperties.PartitionKeyPath) && containerProperties.PartitionKeyPaths is null)
            containerProperties.PartitionKeyPath = "/id";
        var created = false;
        var container = _containers.GetOrAdd(id, _ =>
        {
            created = true;
            return new InMemoryContainer(containerProperties);
        });
        if (!created && !container.ExplicitlyCreated)
        {
            var replacement = new InMemoryContainer(containerProperties)
            {
                OnDeleted = () => _containers.TryRemove(id, out _),
                ExplicitlyCreated = true,
                DefaultTimeToLive = containerProperties.DefaultTimeToLive
            };
            replacement.SetParentDatabase(Id);
            if (containerProperties.IndexingPolicy is not null)
                replacement.IndexingPolicy = containerProperties.IndexingPolicy;
            _containers[id] = replacement;
            container = replacement;
            created = true;
        }
        container.OnDeleted ??= () => _containers.TryRemove(id, out _);
        container.SetParentDatabase(Id);
        container.ExplicitlyCreated = true;
        if (throughput.HasValue)
            container._throughput = throughput.Value;
        _explicitlyCreatedContainers.TryAdd(id, true);
        if (created)
        {
            container.DefaultTimeToLive = containerProperties.DefaultTimeToLive;
            if (containerProperties.IndexingPolicy is not null)
                container.IndexingPolicy = containerProperties.IndexingPolicy;
        }
        var response = CosmosResponseFactory.CreateContainerResponse(
            container,
            containerProperties,
            created ? HttpStatusCode.Created : HttpStatusCode.OK);
        return Task.FromResult(response);
    }

    public override Task<ContainerResponse> CreateContainerIfNotExistsAsync(
        ContainerProperties containerProperties,
        ThroughputProperties throughputProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        return CreateContainerIfNotExistsAsync(containerProperties, throughputProperties?.Throughput, requestOptions, cancellationToken);
    }

    public override Task<ContainerResponse> CreateContainerAsync(
        string id,
        string partitionKeyPath,
        int? throughput = null,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrEmpty(id);
        CosmosResourceNameValidator.ValidateResourceName(id, "Container");
        ArgumentNullException.ThrowIfNull(partitionKeyPath);
        var container = new InMemoryContainer(id, partitionKeyPath)
        {
            OnDeleted = () => _containers.TryRemove(id, out _),
            ExplicitlyCreated = true
        };
        container.SetParentDatabase(Id);
        if (throughput.HasValue)
            container._throughput = throughput.Value;
        if (!_containers.TryAdd(id, container))
        {
            if (_containers.TryGetValue(id, out var existing) && !existing.ExplicitlyCreated)
            {
                _containers[id] = container;
            }
            else
            {
                throw InMemoryCosmosException.Create("Container already exists.", HttpStatusCode.Conflict, 0, string.Empty, 0);
            }
        }
        _explicitlyCreatedContainers.TryAdd(id, true);
        var response = CosmosResponseFactory.CreateContainerResponse(
            container,
            new ContainerProperties(container.Id, partitionKeyPath),
            HttpStatusCode.Created);
        return Task.FromResult(response);
    }

    public override Task<ContainerResponse> CreateContainerAsync(
        ContainerProperties containerProperties,
        int? throughput = null,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        var id = containerProperties.Id;
        CosmosResourceNameValidator.ValidateResourceName(id, "Container");
        if (string.IsNullOrEmpty(containerProperties.PartitionKeyPath) && containerProperties.PartitionKeyPaths is null)
            containerProperties.PartitionKeyPath = "/id";
        var container = new InMemoryContainer(containerProperties)
        {
            OnDeleted = () => _containers.TryRemove(id, out _),
            ExplicitlyCreated = true,
            DefaultTimeToLive = containerProperties.DefaultTimeToLive
        };
        container.SetParentDatabase(Id);
        if (throughput.HasValue)
            container._throughput = throughput.Value;
        if (containerProperties.IndexingPolicy is not null)
            container.IndexingPolicy = containerProperties.IndexingPolicy;
        if (!_containers.TryAdd(id, container))
        {
            if (_containers.TryGetValue(id, out var existing) && !existing.ExplicitlyCreated)
            {
                _containers[id] = container;
            }
            else
            {
                throw InMemoryCosmosException.Create("Container already exists.", HttpStatusCode.Conflict, 0, string.Empty, 0);
            }
        }
        _explicitlyCreatedContainers.TryAdd(id, true);
        var response = CosmosResponseFactory.CreateContainerResponse(container, containerProperties, HttpStatusCode.Created);
        return Task.FromResult(response);
    }

    public override Task<ContainerResponse> CreateContainerAsync(
        ContainerProperties containerProperties,
        ThroughputProperties throughputProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        return CreateContainerAsync(containerProperties, throughputProperties?.Throughput, requestOptions, cancellationToken);
    }

    public override Task<ResponseMessage> CreateContainerStreamAsync(
        ContainerProperties containerProperties,
        int? throughput = null,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        var id = containerProperties.Id;
        if (string.IsNullOrEmpty(containerProperties.PartitionKeyPath) && containerProperties.PartitionKeyPaths is null)
            containerProperties.PartitionKeyPath = "/id";
        var container = new InMemoryContainer(containerProperties)
        {
            OnDeleted = () => _containers.TryRemove(id, out _),
            ExplicitlyCreated = true,
            DefaultTimeToLive = containerProperties.DefaultTimeToLive
        };
        container.SetParentDatabase(Id);
        if (throughput.HasValue)
            container._throughput = throughput.Value;
        if (containerProperties.IndexingPolicy is not null)
            container.IndexingPolicy = containerProperties.IndexingPolicy;
        if (!_containers.TryAdd(id, container))
        {
            if (_containers.TryGetValue(id, out var existing) && !existing.ExplicitlyCreated)
            {
                _containers[id] = container;
            }
            else
            {
                return Task.FromResult(CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.Conflict, "0:0#0"));
            }
        }
        _explicitlyCreatedContainers.TryAdd(id, true);
        return Task.FromResult(CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.Created, "0:0#0"));
    }

    public override Task<ResponseMessage> CreateContainerStreamAsync(
        ContainerProperties containerProperties,
        ThroughputProperties throughputProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        return CreateContainerStreamAsync(containerProperties, throughputProperties?.Throughput, requestOptions, cancellationToken);
    }

    public override Container GetContainer(string id) => GetOrCreateContainer(id);

    internal bool IsContainerExplicitlyCreated(string id) => _explicitlyCreatedContainers.ContainsKey(id);

    internal IEnumerable<InMemoryContainer> GetAllContainers() => _containers.Values;

    public override FeedIterator<T> GetContainerQueryIterator<T>(
        string? queryText = null,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
    {
        var offset = int.TryParse(continuationToken, out var o) ? o : 0;
        var items = _containers.Values.Select(c => new ContainerProperties(c.Id, c.PartitionKeyPaths));
        var results = Application.Query.MetadataSqlQueryExecutor.Execute<T, ContainerProperties>(items, queryText);
        return new InMemoryFeedIterator<T>(results, requestOptions?.MaxItemCount, offset);
    }

    public override FeedIterator<T> GetContainerQueryIterator<T>(
        QueryDefinition queryDefinition,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
    {
        var offset = int.TryParse(continuationToken, out var o) ? o : 0;
        var items = _containers.Values.Select(c => new ContainerProperties(c.Id, c.PartitionKeyPaths));
        var parameters = queryDefinition is not null
            ? Infrastructure.SdkInterop.CosmosSdkIntrospector.ExtractQueryParameters(queryDefinition)
            : null;
        var results = Application.Query.MetadataSqlQueryExecutor.Execute<T, ContainerProperties>(
            items, queryDefinition?.QueryText, parameters);
        return new InMemoryFeedIterator<T>(results, requestOptions?.MaxItemCount, offset);
    }

    public override Task<DatabaseResponse> ReadAsync(
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (_client is not null && !_client.IsDatabaseExplicitlyCreated(Id))
        {
            throw InMemoryCosmosException.Create(
                $"Database '{Id}' not found.",
                HttpStatusCode.NotFound,
                1003,
                Guid.NewGuid().ToString(),
                0);
        }
        return Task.FromResult(CosmosResponseFactory.CreateDatabaseResponse(
            this,
            HttpStatusCode.OK,
            new DatabaseProperties(Id)));
    }

    public override Task<ResponseMessage> ReadStreamAsync(
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.OK, "0:0#0"));
    }

    public override Task<DatabaseResponse> DeleteAsync(
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var container in _containers.Values)
            container.DeleteContainerAsync().GetAwaiter().GetResult();
        _containers.Clear();
        _explicitlyCreatedContainers.Clear();
        _users.Clear();
        _client?.RemoveDatabase(Id);
        return Task.FromResult(CosmosResponseFactory.CreateDatabaseResponse(this, HttpStatusCode.NoContent));
    }

    public override Task<ResponseMessage> DeleteStreamAsync(
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var container in _containers.Values)
            container.DeleteContainerAsync().GetAwaiter().GetResult();
        _containers.Clear();
        _explicitlyCreatedContainers.Clear();
        _users.Clear();
        _client?.RemoveDatabase(Id);
        return Task.FromResult(CosmosResponseFactory.CreateResponseMessage(HttpStatusCode.NoContent, "0:0#0"));
    }

    public override Task<int?> ReadThroughputAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(_throughput);

    public override Task<ThroughputResponse> ReadThroughputAsync(
        RequestOptions requestOptions,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CosmosResponseFactory.CreateThroughputResponse(
            ThroughputProperties.CreateManualThroughput(_throughput),
            HttpStatusCode.OK));
    }

    public override Task<ThroughputResponse> ReplaceThroughputAsync(
        int throughput,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        _throughput = throughput;
        return Task.FromResult(CosmosResponseFactory.CreateThroughputResponse(
            ThroughputProperties.CreateManualThroughput(throughput),
            HttpStatusCode.OK));
    }

    public override Task<ThroughputResponse> ReplaceThroughputAsync(
        ThroughputProperties throughputProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (throughputProperties?.Throughput.HasValue == true)
            _throughput = throughputProperties.Throughput.Value;
        return Task.FromResult(CosmosResponseFactory.CreateThroughputResponse(
            throughputProperties!,
            HttpStatusCode.OK));
    }

    public override FeedIterator GetContainerQueryStreamIterator(
        QueryDefinition queryDefinition,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => GetContainerQueryStreamIterator((string?)null, continuationToken, requestOptions);

    public override FeedIterator GetContainerQueryStreamIterator(
        string? queryText = null,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
    {
        return new InMemoryStreamFeedIterator(
            () => _containers.Values
                .Select(c => (object)new { id = c.Id })
                .ToList(),
            "DocumentCollections");
    }

    public override ContainerBuilder DefineContainer(string name, string partitionKeyPath)
        => new ContainerBuilder(this, name, partitionKeyPath);

    public override User GetUser(string id)
    {
        if (_users.TryGetValue(id, out var existing))
            return existing;

        return new InMemoryUser(id, () => _users.TryRemove(id, out _), _users);
    }

    public override Task<UserResponse> CreateUserAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        var user = new InMemoryUser(id, () => _users.TryRemove(id, out _));
        if (!_users.TryAdd(id, user))
            throw InMemoryCosmosException.Create($"User '{id}' already exists.", HttpStatusCode.Conflict, 0, string.Empty, 0);

        return Task.FromResult(CosmosResponseFactory.CreateUserResponse(user, new UserProperties(id), HttpStatusCode.Created));
    }

    public override Task<UserResponse> UpsertUserAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        var created = false;
        var user = _users.GetOrAdd(id, uid =>
        {
            created = true;
            return new InMemoryUser(uid, () => _users.TryRemove(uid, out _));
        });

        return Task.FromResult(CosmosResponseFactory.CreateUserResponse(
            user,
            new UserProperties(id),
            created ? HttpStatusCode.Created : HttpStatusCode.OK));
    }

    public override FeedIterator<T> GetUserQueryIterator<T>(
        string? queryText = null,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
    {
        return new InMemoryFeedIterator<T>(
            () => _users.Values
                .Select(u => (T)(object)new UserProperties(u.Id))
                .ToList());
    }

    public override FeedIterator<T> GetUserQueryIterator<T>(
        QueryDefinition queryDefinition,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
    {
        return GetUserQueryIterator<T>((string?)null, continuationToken, requestOptions);
    }

    public override ClientEncryptionKey GetClientEncryptionKey(string id) => throw new NotImplementedException();
    public override FeedIterator<ClientEncryptionKeyProperties> GetClientEncryptionKeyQueryIterator(
        QueryDefinition queryDefinition,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => throw new NotImplementedException();
    public override Task<ClientEncryptionKeyResponse> CreateClientEncryptionKeyAsync(
        ClientEncryptionKeyProperties clientEncryptionKeyProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
