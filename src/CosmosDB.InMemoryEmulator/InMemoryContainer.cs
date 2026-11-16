#nullable enable
using System.Collections.Concurrent;
using System.Net;
using CosmosDB.InMemoryEmulator.Application.Account;
using CosmosDB.InMemoryEmulator.Application.ChangeFeed;
using CosmosDB.InMemoryEmulator.Application.Crud;
using CosmosDB.InMemoryEmulator.Application.Patch;
using CosmosDB.InMemoryEmulator.Application.Query;
using CosmosDB.InMemoryEmulator.Application.Scripts;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using CosmosDB.InMemoryEmulator.Domain.Validation;
using CosmosDB.InMemoryEmulator.Infrastructure.Persistence;
using CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop;
using CosmosDB.InMemoryEmulator.Infrastructure.Storage;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NSubstitute;
using static CosmosDB.InMemoryEmulator.Domain.Primitives.ContainerConstants;
using static CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop.CosmosSdkIntrospector;
using CosmosResponseFactory = CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop.CosmosResponseFactory;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// In-memory implementation of <see cref="Container"/> for testing. Supports the full
/// breadth of the Cosmos DB SDK surface area: CRUD, SQL queries (40+ built-in functions),
/// LINQ, transactional batches, change feed, patch operations, ETags, TTL, ReadMany,
/// stream APIs, stored procedures, UDFs, state persistence, and more.
/// </summary>
/// <remarks>
/// <para>
/// Create an instance directly for simple unit tests, or use the DI extension methods
/// (<see cref="ServiceCollectionExtensions"/>) for integration tests.
/// For HTTP-level interception with zero production code changes, wrap this container
/// in a <see cref="FakeCosmosHandler"/>.
/// </para>
/// <para>
/// Thread-safe for concurrent reads and writes. All data is stored in
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> collections keyed by (id, partitionKey).
/// </para>
/// </remarks>
internal class InMemoryContainer : Container, IContainerTestSetup
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.None,
        DateParseHandling = DateParseHandling.None,
        ContractResolver = new DefaultContractResolver(),
        Converters = { new StringEnumConverter { AllowIntegerValues = true } }
    };

    private const string JsTriggersNotInstalled =
        "JavaScript stored procedure execution requires the CosmosDB.InMemoryEmulator.JsTriggers package. " +
        "Install it and call container.UseJsTriggers(), or use the C# delegate overload instead.";

    private const string JsUdfNotInstalled =
        "JavaScript UDF execution requires the CosmosDB.InMemoryEmulator.JsTriggers package. " +
        "Install it and call container.UseJsTriggers(), or use the C# delegate overload instead.";

    private const string JsTriggerNotInstalled =
        "JavaScript trigger execution requires the CosmosDB.InMemoryEmulator.JsTriggers package. " +
        "Install it and call container.UseJsTriggers(), or use the C# delegate overload instead.";

    private readonly InMemoryItemStore _store = new();
    private readonly InMemoryChangeFeedLog _changeFeedLog = new();
    private readonly ScriptCatalog _scriptCatalog = new();
    private readonly Scripts _scripts;

    internal int _throughput = 400;
    internal bool _isDeleted;

    private ContainerProperties _containerProperties;
    private volatile (string Name, string FromAlias, SqlExpression? Expr)[]? _parsedComputedProperties;
    private Database? _cachedDatabase;
    private string? _parentDatabaseId;
    private int? _defaultTimeToLive;

    private IndexingPolicy _indexingPolicy = new()
    {
        Automatic = true,
        IndexingMode = IndexingMode.Consistent,
        IncludedPaths = { new IncludedPath { Path = "/*" } },
        ExcludedPaths = { new ExcludedPath { Path = "/\"_etag\"/?" } },
    };

    /// <summary>
    /// Optional JavaScript trigger engine. When set, triggers that have a <see cref="TriggerProperties.Body"/>
    /// but no C# handler will be executed via this engine. Set by calling <c>UseJsTriggers()</c>
    /// from the <c>CosmosDB.InMemoryEmulator.JsTriggers</c> package.
    /// </summary>
    public IJsTriggerEngine JsTriggerEngine { get; set; } = null!;

    /// <summary>
    /// Optional JavaScript stored procedure engine. When set, stored procedures that have a
    /// <see cref="StoredProcedureProperties.Body"/> but no C# handler will be executed via this engine.
    /// Set by calling <c>UseJsStoredProcedures()</c> from the <c>CosmosDB.InMemoryEmulator.JsTriggers</c> package.
    /// </summary>
    public ISprocEngine SprocEngine { get; set; } = null!;

    internal Action? OnDeleted { get; set; }

    /// <summary>
    /// Creates a new <see cref="InMemoryContainer"/> with a single partition key path.
    /// </summary>
    /// <param name="id">The container identifier. Defaults to <c>"in-memory-container"</c>.</param>
    /// <param name="partitionKeyPath">The JSON path to the partition key field (e.g. <c>/partitionKey</c>). Defaults to <c>/id</c>.</param>
    public InMemoryContainer(string id = "in-memory-container", string partitionKeyPath = "/id")
    {
        Id = id;
        _containerProperties = new ContainerProperties(id, partitionKeyPath);
        PartitionKeyPaths = new[] { partitionKeyPath };
        _scripts = new InMemoryScripts(this, _scriptCatalog);
    }

    /// <summary>
    /// Creates a new <see cref="InMemoryContainer"/> with composite (hierarchical) partition key paths.
    /// </summary>
    /// <param name="id">The container identifier.</param>
    /// <param name="partitionKeyPaths">One or more JSON paths for the composite partition key.</param>
    public InMemoryContainer(string id, IReadOnlyList<string> partitionKeyPaths)
    {
        Id = id;
        PartitionKeyPaths = partitionKeyPaths;
        _containerProperties = new ContainerProperties(id, partitionKeyPaths);
        _scripts = new InMemoryScripts(this, _scriptCatalog);
    }

    /// <summary>
    /// Creates a new <see cref="InMemoryContainer"/> from a <see cref="ContainerProperties"/> instance.
    /// This allows specifying advanced settings such as <see cref="UniqueKeyPolicy"/>.
    /// </summary>
    /// <param name="containerProperties">The container properties to use.</param>
    public InMemoryContainer(ContainerProperties containerProperties)
    {
        DocumentValidator.ValidateComputedProperties(containerProperties);
        _containerProperties = containerProperties;
        Id = containerProperties.Id;
        DefaultTimeToLive = containerProperties.DefaultTimeToLive;
        var paths = containerProperties.PartitionKeyPaths;
        if (paths is null || paths.Count == 0)
        {
            var singlePath = containerProperties.PartitionKeyPath ?? "/id";
            PartitionKeyPaths = new[] { singlePath };
        }
        else
        {
            PartitionKeyPaths = paths;
        }
        _scripts = new InMemoryScripts(this, _scriptCatalog);
    }

    // ─── Properties ───────────────────────────────────────────────────────────

    /// <summary>The container identifier.</summary>
    public override string Id { get; } = default!;

    /// <summary>Returns a stubbed <see cref="Microsoft.Azure.Cosmos.Database"/> instance.</summary>
    public override Database Database
    {
        get
        {
            if (_cachedDatabase is null)
            {
                var db = Substitute.For<Database>();
                db.Id.Returns(_parentDatabaseId ?? Id);
                _cachedDatabase = db;
            }
            return _cachedDatabase;
        }
    }

    internal void SetParentDatabase(string databaseId) => _parentDatabaseId = databaseId;

    internal bool ExplicitlyCreated { get; set; } = true;

    /// <summary>Returns a stubbed <see cref="Microsoft.Azure.Cosmos.Conflicts"/> instance.</summary>
    public override Conflicts Conflicts => Substitute.For<Conflicts>();

    /// <summary>The <see cref="Microsoft.Azure.Cosmos.Scripts.Scripts"/> instance for executing stored procedures and UDFs.</summary>
    public override Scripts Scripts => _scripts;

    /// <summary>
    /// Container-level default TTL in seconds. When set, items expire after this duration
    /// unless overridden by a per-item <c>_ttl</c> property. Set to <c>null</c> to disable.
    /// Items are lazily evicted on the next read attempt.
    /// Setting to 0 throws BadRequest as in real Cosmos DB — use -1 for "enabled, no default expiry".
    /// </summary>
    public int? DefaultTimeToLive
    {
        get => _defaultTimeToLive;
        set
        {
            if (value == 0)
            {
                throw InMemoryCosmosException.Create(
                    "The value of DefaultTimeToLive must be either null, -1, or a positive integer.",
                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
            }
            _defaultTimeToLive = value;
        }
    }

    /// <inheritdoc />
    public UniqueKeyPolicy UniqueKeyPolicy
    {
        get => _containerProperties.UniqueKeyPolicy;
        set => _containerProperties.UniqueKeyPolicy = value ?? new UniqueKeyPolicy();
    }

    internal ContainerProperties ContainerPropertiesInternal => _containerProperties;

    internal void ResetComputedPropertiesCache() => _parsedComputedProperties = null;

    /// <summary>The partition key path(s) for this container.</summary>
    public IReadOnlyList<string> PartitionKeyPaths { get; }

    /// <summary>
    /// Maximum number of entries retained in the change feed log. When a new entry
    /// would exceed this limit, the oldest entries are evicted. Defaults to <c>1000</c>.
    /// Set to <c>0</c> to disable eviction (unbounded growth).
    /// </summary>
    public int MaxChangeFeedSize
    {
        get => _changeFeedLog.MaxChangeFeedSize;
        set => _changeFeedLog.MaxChangeFeedSize = value;
    }

    /// <summary>
    /// Gets the current session token for this container.
    /// The token advances each time a write operation succeeds.
    /// </summary>
    internal string CurrentSessionToken => _changeFeedLog.CurrentSessionToken;

    /// <summary>
    /// Number of feed ranges returned by <see cref="GetFeedRangesAsync"/>. Defaults to 1.
    /// Set to a higher value to simulate multiple physical partitions so that
    /// FeedRange-scoped queries and change feed iterators return subsets of data.
    /// </summary>
    public int FeedRangeCount { get; set; } = 1;

    /// <summary>
    /// When set, the container will automatically save its state to this file path on
    /// <see cref="Dispose"/> and can load state from it via <see cref="LoadPersistedState"/>.
    /// The directory will be created automatically if it does not exist.
    /// </summary>
    public string StateFilePath { get; set; } = null!;

    /// <summary>
    /// The indexing policy for this container. Accepted and stored but does not affect
    /// query performance — all queries scan all items regardless of indexing settings.
    /// When set, automatically ensures <c>/_etag/?</c> is present in ExcludedPaths
    /// to match real Cosmos DB behaviour.
    /// </summary>
    public IndexingPolicy IndexingPolicy
    {
        get => _indexingPolicy;
        set
        {
            _indexingPolicy = value;
            DocumentValidator.EnsureEtagExcludedPath(_indexingPolicy);
        }
    }

    // ─── Public helpers for test infrastructure ───────────────────────────────

    /// <summary>
    /// Removes all items, ETags, timestamps, and change feed entries from the container.
    /// </summary>
    public void ClearItems()
    {
        _store.Clear();
        _changeFeedLog.Clear();
    }

    /// <summary>Returns the number of non-expired items currently stored in the container.</summary>
    public int ItemCount => _store.GetActiveItemCount(DefaultTimeToLive);

    /// <summary>
    /// Saves the current container state to the file specified by <see cref="StateFilePath"/>.
    /// Does nothing if <see cref="StateFilePath"/> is null. Creates the directory if it does not exist.
    /// </summary>
    public void Dispose()
    {
        if (StateFilePath is not null)
        {
            var dir = Path.GetDirectoryName(StateFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            ExportStateToFile(StateFilePath);
        }
    }

    /// <summary>
    /// Loads container state from the file specified by <see cref="StateFilePath"/>.
    /// If the file does not exist, the container starts empty (no-op).
    /// If the file exists, its contents are imported via <see cref="ImportState"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StateFilePath"/> is null.</exception>
    public void LoadPersistedState()
    {
        if (StateFilePath is null)
            throw new InvalidOperationException("StateFilePath must be set before calling LoadPersistedState().");

        if (File.Exists(StateFilePath))
            ImportStateFromFile(StateFilePath);
    }

    // ─── State persistence & Point-in-time restore ────────────────────────────

    /// <summary>Exports the current container state as a JSON string.</summary>
    public string ExportState()
        => ContainerStateSerializer.ExportState(
            _store.GetOrderedKeysSnapshot(),
            _store.Items,
            key => _store.IsExpired(key, DefaultTimeToLive));

    /// <summary>Imports container state from a JSON string, replacing all existing data.</summary>
    public void ImportState(string json)
        => ContainerStateSerializer.ImportState(
            json,
            ClearItems,
            jObj => PartitionKeyProcessor.ExtractPartitionKeyValue(null, jObj, PartitionKeyPaths),
            (jObj, pk) => UniqueKeyValidator.ValidateUniqueKeys(_containerProperties.UniqueKeyPolicy, _store.Items, jObj, pk),
            (docJson, etag, ts) => InMemoryItemStore.EnrichWithSystemProperties(Id, docJson, etag, ts),
            (key, enriched, etag, now) =>
            {
                _store.Etags[key] = etag;
                _store.Timestamps[key] = now;
                _store.Items[key] = enriched;
                _store.AddInsertionOrder(key);
            });

    /// <summary>Exports the current container state to a file.</summary>
    public void ExportStateToFile(string filePath) => File.WriteAllText(filePath, ExportState());

    /// <summary>Imports container state from a file, replacing all existing data.</summary>
    public void ImportStateFromFile(string filePath) => ImportState(File.ReadAllText(filePath));

    /// <summary>
    /// Restores the container to its state at the specified point in time by replaying
    /// the change feed. All current data is replaced with the reconstructed state.
    /// </summary>
    public void RestoreToPointInTime(DateTimeOffset pointInTime)
        => ContainerStateSerializer.RestoreToPointInTime(
            pointInTime,
            _changeFeedLog,
            () => _store.Clear(includeLocks: true),
            (docJson, etag, ts) => InMemoryItemStore.EnrichWithSystemProperties(Id, docJson, etag, ts),
            (key, enriched, etag, ts) =>
            {
                _store.Items[key] = enriched;
                _store.Etags[key] = etag;
                _store.Timestamps[key] = ts;
            },
            insertionOrderKeys =>
            {
                lock (_store.InsertionOrderLock)
                {
                    foreach (var key in insertionOrderKeys)
                    {
                        if (_store.Items.ContainsKey(key))
                            _store.InsertionOrder.Add(key);
                    }
                }
            });

    // ─── Script & Trigger Registrations ───────────────────────────────────────

    /// <summary>
    /// Registers a user-defined function that can be called in SQL queries as <c>udf.name(args)</c>.
    /// </summary>
    public void RegisterUdf(string name, Func<object[], object?> implementation)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(implementation);
        _scriptCatalog.RegisterUdf(name, args => implementation(args!));
    }

    void IContainerTestSetup.RegisterUdf(string name, Func<object[], object> handler)
        => RegisterUdf(name, (Func<object[], object?>)handler);

    /// <summary>Removes a previously registered user-defined function.</summary>
    public void DeregisterUdf(string name) => _scriptCatalog.DeregisterUdf(name);

    /// <summary>
    /// Registers a stored procedure handler that is invoked when <c>ExecuteStoredProcedureAsync</c> is called.
    /// </summary>
    public void RegisterStoredProcedure(string sprocId, Func<PartitionKey, dynamic[], string> handler)
        => _scriptCatalog.RegisterStoredProcedure(sprocId, handler);

    /// <summary>Removes a previously registered stored procedure handler.</summary>
    public void DeregisterStoredProcedure(string sprocId) => _scriptCatalog.DeregisterStoredProcedure(sprocId);

    /// <summary>
    /// Registers a pre-trigger handler invoked when <c>ItemRequestOptions.PreTriggers</c> includes this trigger's ID.
    /// </summary>
    public void RegisterTrigger(
        string triggerId,
        TriggerType triggerType,
        TriggerOperation triggerOperation,
        Func<JObject, JObject> preHandler)
        => _scriptCatalog.RegisterTrigger(triggerId, triggerType, triggerOperation, preHandler);

    /// <summary>
    /// Registers a post-trigger handler invoked when <c>ItemRequestOptions.PostTriggers</c> includes this trigger's ID.
    /// </summary>
    public void RegisterTrigger(
        string triggerId,
        TriggerType triggerType,
        TriggerOperation triggerOperation,
        Action<JObject> postHandler)
        => _scriptCatalog.RegisterTrigger(triggerId, triggerType, triggerOperation, postHandler);

    /// <summary>Removes a previously registered trigger handler.</summary>
    public void DeregisterTrigger(string triggerId) => _scriptCatalog.DeregisterTrigger(triggerId);

    void IContainerTestSetup.RegisterStoredProcedure(string id, string jsBody)
    {
        if (SprocEngine is null)
            throw new NotImplementedException(JsTriggersNotInstalled);

        RegisterStoredProcedure(id, (pk, args) =>
        {
            var result = SprocEngine.Execute(jsBody, pk, args, new PartitionScopedCollectionContext(this, pk));
            return result ?? "null";
        });

        _scriptCatalog.StoredProcedureProperties[id] = new StoredProcedureProperties { Id = id, Body = jsBody };
    }

    void IContainerTestSetup.RegisterUdf(string name, string jsBody)
    {
        if (JsTriggerEngine is not IJsUdfEngine)
            throw new NotImplementedException(JsUdfNotInstalled);

        _scriptCatalog.UserDefinedFunctions["UDF." + name.TrimStart('.')] = ScriptCatalog.UdfPlaceholder;
        _scriptCatalog.UdfProperties[name] = new UserDefinedFunctionProperties { Id = name, Body = jsBody };
    }

    void IContainerTestSetup.RegisterTrigger(
        string id,
        TriggerType type,
        TriggerOperation operation,
        string jsBody)
    {
        if (JsTriggerEngine is null)
            throw new NotImplementedException(JsTriggerNotInstalled);

        _scriptCatalog.TriggerProperties[id] = new TriggerProperties
        {
            Id = id,
            TriggerType = type,
            TriggerOperation = operation,
            Body = jsBody
        };

        if (type == TriggerType.Pre)
        {
            _scriptCatalog.RegisterTrigger(id, type, operation, doc => JsTriggerEngine.ExecutePreTrigger(jsBody, doc));
        }
        else
        {
            _scriptCatalog.RegisterTrigger(id, type, operation, (Action<JObject>)(doc => JsTriggerEngine.ExecutePostTrigger(jsBody, doc)));
        }
    }

    /// <summary>
    /// Returns a checkpoint value representing the current position in the change feed.
    /// Pass this to <see cref="GetChangeFeedIterator{T}(long)"/> to read changes since this point.
    /// </summary>
    public long GetChangeFeedCheckpoint() => _changeFeedLog.Count;

    // ═══════════════════════════════════════════════════════════════════════════
    //  Item CRUD & Patch — Typed & Stream
    // ═══════════════════════════════════════════════════════════════════════════

    public override Task<ItemResponse<T>> CreateItemAsync<T>(
        T item,
        PartitionKey? partitionKey = null,
        ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => TypedDocumentCrudService.CreateItemAsync(
            CreateOperationContext(), item, partitionKey, requestOptions, cancellationToken);

    public override Task<ItemResponse<T>> ReadItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => TypedDocumentCrudService.ReadItemAsync<T>(
            CreateOperationContext(), id, partitionKey, requestOptions, cancellationToken);

    public override Task<ItemResponse<T>> UpsertItemAsync<T>(
        T item,
        PartitionKey? partitionKey = null,
        ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => TypedDocumentCrudService.UpsertItemAsync(
            CreateOperationContext(), item, partitionKey, requestOptions, cancellationToken);

    public override Task<ItemResponse<T>> ReplaceItemAsync<T>(
        T item,
        string id,
        PartitionKey? partitionKey = null,
        ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => TypedDocumentCrudService.ReplaceItemAsync(
            CreateOperationContext(), item, id, partitionKey, requestOptions, cancellationToken);

    public override Task<ItemResponse<T>> DeleteItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => TypedDocumentCrudService.DeleteItemAsync<T>(
            CreateOperationContext(), id, partitionKey, requestOptions, cancellationToken);

    public override Task<ItemResponse<T>> PatchItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        IReadOnlyList<PatchOperation> patchOperations,
        PatchItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => DocumentPatchEngine.PatchItemAsync<T>(
            id, partitionKey, patchOperations, requestOptions, _store, _changeFeedLog, Id,
            PartitionKeyPaths, _containerProperties, DefaultTimeToLive, JsonSettings,
            CreateQueryContext, ExecutePreTriggers, ExecutePostTriggers, cancellationToken);

    public override Task<ResponseMessage> CreateItemStreamAsync(
        Stream streamPayload,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => StreamDocumentCrudService.CreateItemStreamAsync(
            CreateOperationContext(), streamPayload, partitionKey, requestOptions, cancellationToken);

    public override Task<ResponseMessage> ReadItemStreamAsync(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => StreamDocumentCrudService.ReadItemStreamAsync(
            CreateOperationContext(), id, partitionKey, requestOptions, cancellationToken);

    public override Task<ResponseMessage> UpsertItemStreamAsync(
        Stream streamPayload,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => StreamDocumentCrudService.UpsertItemStreamAsync(
            CreateOperationContext(), streamPayload, partitionKey, requestOptions, cancellationToken);

    public override Task<ResponseMessage> ReplaceItemStreamAsync(
        Stream streamPayload,
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => StreamDocumentCrudService.ReplaceItemStreamAsync(
            CreateOperationContext(), streamPayload, id, partitionKey, requestOptions, cancellationToken);

    public override Task<ResponseMessage> DeleteItemStreamAsync(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => StreamDocumentCrudService.DeleteItemStreamAsync(
            CreateOperationContext(), id, partitionKey, requestOptions, cancellationToken);

    public override Task<ResponseMessage> PatchItemStreamAsync(
        string id,
        PartitionKey partitionKey,
        IReadOnlyList<PatchOperation> patchOperations,
        PatchItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => DocumentPatchEngine.PatchItemStreamAsync(
            id, partitionKey, patchOperations, requestOptions, _store, _changeFeedLog, Id,
            PartitionKeyPaths, _containerProperties, DefaultTimeToLive,
            CreateQueryContext, ExecutePreTriggers, ExecutePostTriggers, cancellationToken);

    public override Task<FeedResponse<T>> ReadManyItemsAsync<T>(
        IReadOnlyList<(string id, PartitionKey partitionKey)> items,
        ReadManyRequestOptions? readManyRequestOptions = null,
        CancellationToken cancellationToken = default)
        => BulkDocumentReadDeleteService.ReadManyItemsAsync<T>(
            CreateOperationContext(), items, readManyRequestOptions, cancellationToken);

    public override Task<ResponseMessage> ReadManyItemsStreamAsync(
        IReadOnlyList<(string id, PartitionKey partitionKey)> items,
        ReadManyRequestOptions? readManyRequestOptions = null,
        CancellationToken cancellationToken = default)
        => BulkDocumentReadDeleteService.ReadManyItemsStreamAsync(
            CreateOperationContext(), items, readManyRequestOptions, cancellationToken);

    public override Task<ResponseMessage> DeleteAllItemsByPartitionKeyStreamAsync(
        PartitionKey partitionKey,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => BulkDocumentReadDeleteService.DeleteAllItemsByPartitionKeyStreamAsync(
            CreateOperationContext(), partitionKey, cancellationToken);

    // ═══════════════════════════════════════════════════════════════════════════
    //  Query — Typed & Stream FeedIterators
    // ═══════════════════════════════════════════════════════════════════════════

    public override FeedIterator<T> GetItemQueryIterator<T>(
        QueryDefinition queryDefinition,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => ContainerQueryIteratorFactory.CreateTypedIterator<T>(
            queryDefinition, continuationToken, requestOptions, FilterItemsByQuery, JsonSettings);

    public override FeedIterator<T> GetItemQueryIterator<T>(
        string? queryText = null,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => ContainerQueryIteratorFactory.CreateTypedIterator<T>(
            queryText, continuationToken, requestOptions, GetAllItemsForPartition, FilterItemsByQuery, JsonSettings);

    public override FeedIterator<T> GetItemQueryIterator<T>(
        FeedRange feedRange,
        QueryDefinition queryDefinition,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => ContainerQueryIteratorFactory.CreateTypedFeedRangeIterator<T>(
            feedRange, queryDefinition, continuationToken, requestOptions,
            PartitionKeyPaths, GetAllItemsForPartition, FilterItemsByQuery, JsonSettings);

    public override FeedIterator GetItemQueryStreamIterator(
        QueryDefinition queryDefinition,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => ContainerQueryIteratorFactory.CreateStreamIterator(
            queryDefinition, continuationToken, requestOptions, FilterItemsByQuery, () => CurrentSessionToken);

    public override FeedIterator GetItemQueryStreamIterator(
        string? queryText = null,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => ContainerQueryIteratorFactory.CreateStreamIterator(
            queryText, continuationToken, requestOptions, GetAllItemsForPartition, FilterItemsByQuery, () => CurrentSessionToken);

    public override FeedIterator GetItemQueryStreamIterator(
        FeedRange feedRange,
        QueryDefinition queryDefinition,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null)
        => ContainerQueryIteratorFactory.CreateStreamFeedRangeIterator(
            feedRange, queryDefinition, continuationToken, requestOptions,
            PartitionKeyPaths, GetAllItemsForPartition, FilterItemsByQuery, JsonSettings, () => CurrentSessionToken);

    // ═══════════════════════════════════════════════════════════════════════════
    //  LINQ & TransactionalBatch
    // ═══════════════════════════════════════════════════════════════════════════

    public override IOrderedQueryable<T> GetItemLinqQueryable<T>(
        bool allowSynchronousQueryExecution = false,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null,
        CosmosLinqSerializerOptions? linqSerializerOptions = null)
    {
        InMemoryFeedIteratorSetup.LastMaxItemCount = requestOptions?.MaxItemCount;
        return GetAllItemsForPartition(requestOptions)
            .Select(json => JsonConvert.DeserializeObject<T>(json, JsonSettings)!)
            .AsQueryable()
            .OrderBy(item => 0);
    }

    public override TransactionalBatch CreateTransactionalBatch(PartitionKey partitionKey)
        => new InMemoryTransactionalBatch(this, partitionKey);

    // ═══════════════════════════════════════════════════════════════════════════
    //  Change Feed — Iterators & Processor Builders
    // ═══════════════════════════════════════════════════════════════════════════

    public override FeedIterator<T> GetChangeFeedIterator<T>(
        ChangeFeedStartFrom changeFeedStartFrom,
        ChangeFeedMode changeFeedMode,
        ChangeFeedRequestOptions? changeFeedRequestOptions = null)
        => ContainerChangeFeedIteratorFactory.CreateTypedIterator<T>(
            _changeFeedLog, changeFeedStartFrom, changeFeedMode, changeFeedRequestOptions, JsonSettings);

    /// <summary>
    /// Returns a change feed iterator that reads changes from the given checkpoint position.
    /// Obtain a checkpoint via <see cref="GetChangeFeedCheckpoint"/>.
    /// </summary>
    public FeedIterator<T> GetChangeFeedIterator<T>(long checkpoint)
        => ContainerChangeFeedIteratorFactory.CreateCheckpointIterator<T>(
            _changeFeedLog, checkpoint, JsonSettings);

    public override FeedIterator GetChangeFeedStreamIterator(
        ChangeFeedStartFrom changeFeedStartFrom,
        ChangeFeedMode changeFeedMode,
        ChangeFeedRequestOptions? changeFeedRequestOptions = null)
        => ContainerChangeFeedIteratorFactory.CreateStreamIterator(
            _changeFeedLog, changeFeedStartFrom, changeFeedMode, changeFeedRequestOptions, () => CurrentSessionToken);

    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilder<T>(
        string processorName, Container.ChangesHandler<T> onChangesDelegate)
        => ChangeFeedProcessorBuilderFactory.Create(processorName,
            new InMemoryChangeFeedProcessor<T>(this, onChangesDelegate));

    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilder<T>(
        string processorName, Container.ChangeFeedHandler<T> onChangesDelegate)
        => ChangeFeedProcessorBuilderFactory.Create(processorName,
            new InMemoryChangeFeedProcessor<T>(this, onChangesDelegate));

    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilder(
        string processorName, Container.ChangeFeedStreamHandler onChangesDelegate)
        => ChangeFeedProcessorBuilderFactory.Create(processorName,
            new InMemoryChangeFeedStreamProcessor(this, onChangesDelegate));

    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilderWithManualCheckpoint<T>(
        string processorName, Container.ChangeFeedHandlerWithManualCheckpoint<T> onChangesDelegate)
        => ChangeFeedProcessorBuilderFactory.Create(processorName,
            new InMemoryManualCheckpointChangeFeedProcessor<T>(this, onChangesDelegate));

    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilderWithManualCheckpoint(
        string processorName, Container.ChangeFeedStreamHandlerWithManualCheckpoint onChangesDelegate)
        => ChangeFeedProcessorBuilderFactory.Create(processorName,
            new InMemoryManualCheckpointStreamChangeFeedProcessor(this, onChangesDelegate));

    public override ChangeFeedEstimator GetChangeFeedEstimator(
        string processorName, Container leaseContainer)
        => Substitute.For<ChangeFeedEstimator>();

    public override ChangeFeedProcessorBuilder GetChangeFeedEstimatorBuilder(
        string processorName,
        Container.ChangesEstimationHandler estimationDelegate,
        TimeSpan? estimationPeriod = null)
        => ChangeFeedProcessorBuilderFactory.Create(processorName, new NoOpChangeFeedProcessor());

    public override Task<IReadOnlyList<FeedRange>> GetFeedRangesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(FeedRangeCalculator.GetFeedRanges(FeedRangeCount));

    // ═══════════════════════════════════════════════════════════════════════════
    //  Container & Throughput Management
    // ═══════════════════════════════════════════════════════════════════════════

    public override Task<ContainerResponse> ReadContainerAsync(
        ContainerRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => ContainerLifecycleService.ReadContainerAsync(
            this, _store, _containerProperties, JsonSettings, cancellationToken);

    public override Task<ResponseMessage> ReadContainerStreamAsync(
        ContainerRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => ContainerLifecycleService.ReadContainerStreamAsync(
            this, _store, _containerProperties, JsonSettings, cancellationToken);

    public override Task<ContainerResponse> ReplaceContainerAsync(
        ContainerProperties containerProperties,
        ContainerRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (containerProperties.Id != Id)
        {
            throw InMemoryCosmosException.Create(
                $"Container id '{containerProperties.Id}' does not match the existing container id '{Id}'.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        _containerProperties = ContainerLifecycleService.ApplyReplaceContainer(
            this, _containerProperties, containerProperties);
        return Task.FromResult(CosmosResponseFactory.CreateContainerResponse(this, containerProperties, HttpStatusCode.OK));
    }

    public override Task<ResponseMessage> ReplaceContainerStreamAsync(
        ContainerProperties containerProperties,
        ContainerRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (containerProperties.Id != Id)
            return Task.FromResult(CreateResponseMessage(HttpStatusCode.BadRequest));
        try
        {
            _containerProperties = ContainerLifecycleService.ApplyReplaceContainer(
                this, _containerProperties, containerProperties);
        }
        catch (CosmosException)
        {
            return Task.FromResult(CreateResponseMessage(HttpStatusCode.BadRequest));
        }

        return Task.FromResult(CreateResponseMessage(
            HttpStatusCode.OK, JsonConvert.SerializeObject(containerProperties, JsonSettings)));
    }

    public override Task<ContainerResponse> DeleteContainerAsync(
        ContainerRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isDeleted = true;
        _store.Clear();
        _changeFeedLog.Clear();
        _scriptCatalog.Clear();
        OnDeleted?.Invoke();
        return Task.FromResult(CosmosResponseFactory.CreateContainerResponse(this, null, HttpStatusCode.NoContent));
    }

    public override Task<ResponseMessage> DeleteContainerStreamAsync(
        ContainerRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isDeleted = true;
        _store.Clear();
        _changeFeedLog.Clear();
        _scriptCatalog.Clear();
        OnDeleted?.Invoke();
        return Task.FromResult(CreateResponseMessage(HttpStatusCode.NoContent));
    }

    public override Task<int?> ReadThroughputAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(_throughput);

    public override Task<ThroughputResponse> ReadThroughputAsync(
        RequestOptions requestOptions,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CosmosResponseFactory.CreateThroughputResponse(
            ThroughputProperties.CreateManualThroughput(_throughput)));

    public override Task<ThroughputResponse> ReplaceThroughputAsync(
        int throughput,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => ContainerLifecycleService.ReplaceThroughputAsync(this, throughput);

    public override Task<ThroughputResponse> ReplaceThroughputAsync(
        ThroughputProperties throughputProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => ContainerLifecycleService.ReplaceThroughputAsync(this, throughputProperties);

    // ═══════════════════════════════════════════════════════════════════════════
    //  Internal & Private Helpers (Batch Snapshots, Triggers, Query Context)
    // ═══════════════════════════════════════════════════════════════════════════

    internal static string JTokenToTypedKey(JToken token)
        => PartitionKeyProcessor.JTokenToTypedKey(token)!;

    internal static string BuildSelectTokenPath(IEnumerable<string> segments)
        => PatchOperationApplier.BuildSelectTokenPath(segments);

    internal ResponseMessage CreateResponseMessage(
        HttpStatusCode statusCode,
        string? json = null,
        string? etag = null,
        int subStatusCode = 0)
        => CosmosResponseFactory.CreateResponseMessage(statusCode, CurrentSessionToken, json, etag, subStatusCode);

    internal Dictionary<(string Id, string PartitionKey), string> SnapshotItems()
        => _store.SnapshotItems();

    internal Dictionary<(string Id, string PartitionKey), string> SnapshotEtags()
        => _store.SnapshotEtags();

    internal Dictionary<(string Id, string PartitionKey), DateTimeOffset> SnapshotTimestamps()
        => _store.SnapshotTimestamps();

    internal int GetChangeFeedCount() => _changeFeedLog.Count;

    internal List<(DateTimeOffset Timestamp, string Id, string PartitionKey, string Json, bool IsDelete)> GetChangeFeedEntries()
        => _changeFeedLog.GetSnapshot();

    internal void BeginBatchTracking() => InMemoryItemStore.BeginBatchTracking();

    internal HashSet<(string Id, string PartitionKey)> EndBatchTracking() => InMemoryItemStore.EndBatchTracking();

    internal void RestoreSnapshot(
        Dictionary<(string Id, string PartitionKey), string> itemsSnapshot,
        Dictionary<(string Id, string PartitionKey), string> etagsSnapshot,
        Dictionary<(string Id, string PartitionKey), DateTimeOffset> timestampsSnapshot,
        int changeFeedCount,
        IReadOnlySet<(string Id, string PartitionKey)> touchedKeys)
        => _store.RestoreSnapshot(itemsSnapshot, etagsSnapshot, timestampsSnapshot, changeFeedCount, touchedKeys, _changeFeedLog);

    private PartitionScopedCollectionContext CreateCollectionContextFromDoc(JObject jObj)
    {
        var pkValue = PartitionKeyProcessor.ExtractPartitionKeyValueCore(null, jObj, PartitionKeyPaths);
        var pk = PartitionKeyProcessor.TypedKeyToPartitionKey(pkValue);
        return new PartitionScopedCollectionContext(this, pk);
    }

    private JObject ExecutePreTriggers(ItemRequestOptions? requestOptions, JObject jObj, string operationName)
        => TriggerExecutionService.ExecutePreTriggers(
            requestOptions, jObj, operationName, _scriptCatalog, PartitionKeyPaths, JsTriggerEngine, CreateCollectionContextFromDoc);

    private JObject? ExecutePostTriggers(ItemRequestOptions? requestOptions, JObject committedDoc, string operationName)
        => TriggerExecutionService.ExecutePostTriggers(
            requestOptions, committedDoc, operationName, _scriptCatalog, JsTriggerEngine, CreateCollectionContextFromDoc);

    private IEnumerable<string> GetAllItemsForPartition(QueryRequestOptions? requestOptions)
        => _store.GetAllItemsForPartition(requestOptions, PartitionKeyPaths, DefaultTimeToLive);

    private DocumentOperationContext CreateOperationContext()
        => new(
            _store,
            _changeFeedLog,
            Id,
            PartitionKeyPaths,
            _containerProperties,
            DefaultTimeToLive,
            JsonSettings,
            ExecutePreTriggers,
            ExecutePostTriggers);

    private QueryExecutionContext CreateQueryContext(IDictionary<string, object?> parameters)
        => new(
            parameters,
            _scriptCatalog.UserDefinedFunctions,
            _scriptCatalog.UdfProperties,
            JsTriggerEngine as IJsUdfEngine,
            ScriptCatalog.UdfPlaceholder);

    private (string Name, string FromAlias, SqlExpression? Expr)[] GetParsedComputedProperties()
    {
        var cached = _parsedComputedProperties;
        if (cached is not null) return cached;

        var cps = _containerProperties.ComputedProperties;
        if (cps is null or { Count: 0 })
        {
            _parsedComputedProperties = Array.Empty<(string, string, SqlExpression?)>();
            return _parsedComputedProperties;
        }

        var result = new (string Name, string FromAlias, SqlExpression? Expr)[cps.Count];
        for (var i = 0; i < cps.Count; i++)
        {
            var parsed = CosmosSqlParser.Parse(cps[i].Query);
            if (parsed.IsValueSelect && parsed.SelectFields.Length == 1 && parsed.SelectFields[0].SqlExpr is not null)
            {
                result[i] = (cps[i].Name, parsed.FromAlias, parsed.SelectFields[0].SqlExpr);
            }
            else
            {
                result[i] = (cps[i].Name, parsed.FromAlias, null);
            }
        }

        _parsedComputedProperties = result;
        return result;
    }

    private List<string> FilterItemsByQuery(
        string queryText,
        IDictionary<string, object?> parameters,
        QueryRequestOptions? requestOptions,
        IEnumerable<string>? preFilteredItems = null)
    {
        IEnumerable<string> items = preFilteredItems ?? GetAllItemsForPartition(requestOptions);
        var context = CreateQueryContext(parameters);
        return CosmosQueryPipeline.Execute(
            queryText,
            items,
            context,
            GetParsedComputedProperties(),
            _containerProperties.IndexingPolicy,
            requestOptions?.EnableScanInQuery ?? false);
    }
}
