#nullable enable
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using static CosmosDB.InMemoryEmulator.Domain.Primitives.ContainerConstants;

namespace CosmosDB.InMemoryEmulator.Application.Scripts;

/// <summary>
/// In-memory implementation of <see cref="Microsoft.Azure.Cosmos.Scripts.Scripts"/> for stored procedures, triggers, and UDFs.
/// </summary>
internal sealed class InMemoryScripts : Microsoft.Azure.Cosmos.Scripts.Scripts
{
    private static readonly Regex IdFilterRegex =
        new(@"WHERE\s+c\.id\s*=\s*(?:'([^']+)'|""([^""]+)""|(@\w+))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly InMemoryContainer _c;
    private readonly ScriptCatalog _catalog;

    public InMemoryScripts(InMemoryContainer container, ScriptCatalog catalog)
    {
        _c = container;
        _catalog = catalog;
    }

    // ── Stored Procedure CRUD (typed) ─────────────────────────────────

    public override Task<StoredProcedureResponse> CreateStoredProcedureAsync(
        StoredProcedureProperties storedProcedureProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (_catalog.StoredProcedureProperties.ContainsKey(storedProcedureProperties.Id))
            throw InMemoryCosmosException.Create(
                $"StoredProcedure '{storedProcedureProperties.Id}' already exists.",
                HttpStatusCode.Conflict, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.StoredProcedureProperties[storedProcedureProperties.Id] = storedProcedureProperties;
        EnrichStoredProcedureSystemProperties(storedProcedureProperties);
        var r = Substitute.For<StoredProcedureResponse>();
        r.StatusCode.Returns(HttpStatusCode.Created);
        r.Resource.Returns(storedProcedureProperties);
        return Task.FromResult(r);
    }

    public override Task<StoredProcedureResponse> ReadStoredProcedureAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.StoredProcedureProperties.TryGetValue(id, out var props))
            throw InMemoryCosmosException.Create(
                $"StoredProcedure '{id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        EnrichStoredProcedureSystemProperties(props);
        var r = Substitute.For<StoredProcedureResponse>();
        r.StatusCode.Returns(HttpStatusCode.OK);
        r.Resource.Returns(props);
        return Task.FromResult(r);
    }

    public override Task<StoredProcedureResponse> ReplaceStoredProcedureAsync(
        StoredProcedureProperties storedProcedureProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.StoredProcedureProperties.ContainsKey(storedProcedureProperties.Id))
            throw InMemoryCosmosException.Create(
                $"StoredProcedure '{storedProcedureProperties.Id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.StoredProcedureProperties[storedProcedureProperties.Id] = storedProcedureProperties;
        EnrichStoredProcedureSystemProperties(storedProcedureProperties);
        var r = Substitute.For<StoredProcedureResponse>();
        r.StatusCode.Returns(HttpStatusCode.OK);
        r.Resource.Returns(storedProcedureProperties);
        return Task.FromResult(r);
    }

    public override Task<StoredProcedureResponse> DeleteStoredProcedureAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.StoredProcedureProperties.Remove(id))
            throw InMemoryCosmosException.Create(
                $"StoredProcedure '{id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.StoredProcedures.Remove(id);
        var r = Substitute.For<StoredProcedureResponse>();
        r.StatusCode.Returns(HttpStatusCode.NoContent);
        return Task.FromResult(r);
    }

    // ── Stored Procedure CRUD (stream) ────────────────────────────────

    public override Task<ResponseMessage> CreateStoredProcedureStreamAsync(
        StoredProcedureProperties storedProcedureProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (_catalog.StoredProcedureProperties.ContainsKey(storedProcedureProperties.Id))
            throw InMemoryCosmosException.Create(
                $"StoredProcedure '{storedProcedureProperties.Id}' already exists.",
                HttpStatusCode.Conflict, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.StoredProcedureProperties[storedProcedureProperties.Id] = storedProcedureProperties;
        EnrichStoredProcedureSystemProperties(storedProcedureProperties);
        return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.Created,
            JsonConvert.SerializeObject(storedProcedureProperties)));
    }

    public override Task<ResponseMessage> ReadStoredProcedureStreamAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.StoredProcedureProperties.TryGetValue(id, out var props))
            throw InMemoryCosmosException.Create(
                $"StoredProcedure '{id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        EnrichStoredProcedureSystemProperties(props);
        return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.OK,
            JsonConvert.SerializeObject(props)));
    }

    public override Task<ResponseMessage> ReplaceStoredProcedureStreamAsync(
        StoredProcedureProperties storedProcedureProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.StoredProcedureProperties.ContainsKey(storedProcedureProperties.Id))
            throw InMemoryCosmosException.Create(
                $"StoredProcedure '{storedProcedureProperties.Id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.StoredProcedureProperties[storedProcedureProperties.Id] = storedProcedureProperties;
        EnrichStoredProcedureSystemProperties(storedProcedureProperties);
        return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.OK,
            JsonConvert.SerializeObject(storedProcedureProperties)));
    }

    public override Task<ResponseMessage> DeleteStoredProcedureStreamAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.StoredProcedureProperties.Remove(id))
            throw InMemoryCosmosException.Create(
                $"StoredProcedure '{id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.StoredProcedures.Remove(id);
        return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.NoContent));
    }

    // ── Stored Procedure Execute ──────────────────────────────────────

    public override Task<StoredProcedureExecuteResponse<TOutput>> ExecuteStoredProcedureAsync<TOutput>(
        string storedProcedureId,
        PartitionKey partitionKey,
        dynamic[] parameters,
        StoredProcedureRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.StoredProcedures.ContainsKey(storedProcedureId) &&
            !_catalog.StoredProcedureProperties.ContainsKey(storedProcedureId))
        {
            throw InMemoryCosmosException.Create(
                $"StoredProcedure '{storedProcedureId}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        string? handlerResult = null;
        var hasHandler = false;
        if (_catalog.StoredProcedures.TryGetValue(storedProcedureId, out var handler))
        {
            handlerResult = ExecuteHandler(storedProcedureId, () => handler(partitionKey, parameters));
            hasHandler = true;
        }
        else if (_c.SprocEngine is not null
            && _catalog.StoredProcedureProperties.TryGetValue(storedProcedureId, out var sprocProps)
            && sprocProps.Body is not null)
        {
            handlerResult = ExecuteJsEngine(storedProcedureId, sprocProps.Body, partitionKey, parameters);
            hasHandler = true;
        }

        var r = Substitute.For<StoredProcedureExecuteResponse<TOutput>>();
        r.StatusCode.Returns(HttpStatusCode.OK);
        r.RequestCharge.Returns(SyntheticRequestCharge);
        if (hasHandler)
        {
            if (handlerResult is not null)
            {
                TOutput deserialized = typeof(TOutput) == typeof(string)
                    ? (TOutput)(object)handlerResult
                    : JsonConvert.DeserializeObject<TOutput>(handlerResult)!;
                r.Resource.Returns(deserialized);
            }
            else
            {
                r.Resource.Returns(default(TOutput)!);
            }
        }

        PopulateScriptLogHeaders(r);
        return Task.FromResult(r);
    }

    public override Task<ResponseMessage> ExecuteStoredProcedureStreamAsync(
        string storedProcedureId,
        PartitionKey partitionKey,
        dynamic[] parameters,
        StoredProcedureRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.StoredProcedures.ContainsKey(storedProcedureId) &&
            !_catalog.StoredProcedureProperties.ContainsKey(storedProcedureId))
        {
            throw InMemoryCosmosException.Create(
                $"StoredProcedure '{storedProcedureId}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        string? handlerResult = null;
        if (_catalog.StoredProcedures.TryGetValue(storedProcedureId, out var handler))
        {
            handlerResult = ExecuteHandler(storedProcedureId, () => handler(partitionKey, parameters));
        }
        else if (_c.SprocEngine is not null
            && _catalog.StoredProcedureProperties.TryGetValue(storedProcedureId, out var sprocProps)
            && sprocProps.Body is not null)
        {
            handlerResult = ExecuteJsEngine(storedProcedureId, sprocProps.Body, partitionKey, parameters);
        }

        var msg = _c.CreateResponseMessage(HttpStatusCode.OK, handlerResult);
        if (_c.SprocEngine?.CapturedLogs is { Count: > 0 } logs)
            msg.Headers["x-ms-documentdb-script-log-results"] = Uri.EscapeDataString(string.Join("\n", logs));
        return Task.FromResult(msg);
    }

    public override Task<ResponseMessage> ExecuteStoredProcedureStreamAsync(
        string storedProcedureId,
        Stream streamPayload,
        PartitionKey partitionKey,
        StoredProcedureRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(streamPayload);
        var json = reader.ReadToEnd();
        var parameters = JsonConvert.DeserializeObject<dynamic[]>(json) ?? Array.Empty<dynamic>();
        return ExecuteStoredProcedureStreamAsync(storedProcedureId, partitionKey, parameters, requestOptions, cancellationToken);
    }

    // ── Trigger CRUD (typed) ──────────────────────────────────────────

    public override Task<TriggerResponse> CreateTriggerAsync(
        TriggerProperties triggerProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (_catalog.TriggerProperties.ContainsKey(triggerProperties.Id))
            throw InMemoryCosmosException.Create(
                $"Trigger '{triggerProperties.Id}' already exists.",
                HttpStatusCode.Conflict, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.TriggerProperties[triggerProperties.Id] = triggerProperties;
        var r = Substitute.For<TriggerResponse>();
        r.StatusCode.Returns(HttpStatusCode.Created);
        r.Resource.Returns(triggerProperties);
        return Task.FromResult(r);
    }

    public override Task<TriggerResponse> ReadTriggerAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.TriggerProperties.TryGetValue(id, out var props))
            throw InMemoryCosmosException.Create(
                $"Trigger '{id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        var r = Substitute.For<TriggerResponse>();
        r.StatusCode.Returns(HttpStatusCode.OK);
        r.Resource.Returns(props);
        return Task.FromResult(r);
    }

    public override Task<TriggerResponse> ReplaceTriggerAsync(
        TriggerProperties triggerProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.TriggerProperties.ContainsKey(triggerProperties.Id))
            throw InMemoryCosmosException.Create(
                $"Trigger '{triggerProperties.Id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.TriggerProperties[triggerProperties.Id] = triggerProperties;
        var r = Substitute.For<TriggerResponse>();
        r.StatusCode.Returns(HttpStatusCode.OK);
        r.Resource.Returns(triggerProperties);
        return Task.FromResult(r);
    }

    public override Task<TriggerResponse> DeleteTriggerAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.TriggerProperties.Remove(id))
            throw InMemoryCosmosException.Create(
                $"Trigger '{id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.Triggers.Remove(id);
        var r = Substitute.For<TriggerResponse>();
        r.StatusCode.Returns(HttpStatusCode.NoContent);
        return Task.FromResult(r);
    }

    // ── Trigger CRUD (stream) ─────────────────────────────────────────

    public override Task<ResponseMessage> CreateTriggerStreamAsync(
        TriggerProperties triggerProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (_catalog.TriggerProperties.ContainsKey(triggerProperties.Id))
            return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.Conflict));

        _catalog.TriggerProperties[triggerProperties.Id] = triggerProperties;
        return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.Created,
            JsonConvert.SerializeObject(triggerProperties)));
    }

    public override Task<ResponseMessage> ReadTriggerStreamAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.TriggerProperties.TryGetValue(id, out var props))
            return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.NotFound));

        return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.OK,
            JsonConvert.SerializeObject(props)));
    }

    public override Task<ResponseMessage> ReplaceTriggerStreamAsync(
        TriggerProperties triggerProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.TriggerProperties.ContainsKey(triggerProperties.Id))
            return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.NotFound));

        _catalog.TriggerProperties[triggerProperties.Id] = triggerProperties;
        return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.OK,
            JsonConvert.SerializeObject(triggerProperties)));
    }

    public override Task<ResponseMessage> DeleteTriggerStreamAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.TriggerProperties.Remove(id))
            return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.NotFound));

        _catalog.Triggers.Remove(id);
        return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.NoContent));
    }

    // ── UDF CRUD (typed) ──────────────────────────────────────────────

    public override Task<UserDefinedFunctionResponse> CreateUserDefinedFunctionAsync(
        UserDefinedFunctionProperties userDefinedFunctionProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (_catalog.UdfProperties.ContainsKey(userDefinedFunctionProperties.Id))
            throw InMemoryCosmosException.Create(
                $"UDF '{userDefinedFunctionProperties.Id}' already exists.",
                HttpStatusCode.Conflict, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.UdfProperties[userDefinedFunctionProperties.Id] = userDefinedFunctionProperties;
        if (!_catalog.UserDefinedFunctions.ContainsKey("UDF." + userDefinedFunctionProperties.Id))
            _catalog.UserDefinedFunctions["UDF." + userDefinedFunctionProperties.Id] = ScriptCatalog.UdfPlaceholder;

        var r = Substitute.For<UserDefinedFunctionResponse>();
        r.StatusCode.Returns(HttpStatusCode.Created);
        r.Resource.Returns(userDefinedFunctionProperties);
        return Task.FromResult(r);
    }

    public override Task<UserDefinedFunctionResponse> ReadUserDefinedFunctionAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.UdfProperties.TryGetValue(id, out var props))
            throw InMemoryCosmosException.Create(
                $"UDF '{id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        var r = Substitute.For<UserDefinedFunctionResponse>();
        r.StatusCode.Returns(HttpStatusCode.OK);
        r.Resource.Returns(props);
        return Task.FromResult(r);
    }

    public override Task<UserDefinedFunctionResponse> ReplaceUserDefinedFunctionAsync(
        UserDefinedFunctionProperties userDefinedFunctionProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.UdfProperties.ContainsKey(userDefinedFunctionProperties.Id))
            throw InMemoryCosmosException.Create(
                $"UDF '{userDefinedFunctionProperties.Id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.UdfProperties[userDefinedFunctionProperties.Id] = userDefinedFunctionProperties;
        var r = Substitute.For<UserDefinedFunctionResponse>();
        r.StatusCode.Returns(HttpStatusCode.OK);
        r.Resource.Returns(userDefinedFunctionProperties);
        return Task.FromResult(r);
    }

    public override Task<UserDefinedFunctionResponse> DeleteUserDefinedFunctionAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.UdfProperties.Remove(id))
            throw InMemoryCosmosException.Create(
                $"UDF '{id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.UserDefinedFunctions.Remove("UDF." + id);
        var r = Substitute.For<UserDefinedFunctionResponse>();
        r.StatusCode.Returns(HttpStatusCode.NoContent);
        return Task.FromResult(r);
    }

    // ── UDF CRUD (stream) ─────────────────────────────────────────────

    public override Task<ResponseMessage> CreateUserDefinedFunctionStreamAsync(
        UserDefinedFunctionProperties userDefinedFunctionProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (_catalog.UdfProperties.ContainsKey(userDefinedFunctionProperties.Id))
            throw InMemoryCosmosException.Create(
                $"UDF '{userDefinedFunctionProperties.Id}' already exists.",
                HttpStatusCode.Conflict, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.UdfProperties[userDefinedFunctionProperties.Id] = userDefinedFunctionProperties;
        if (!_catalog.UserDefinedFunctions.ContainsKey("UDF." + userDefinedFunctionProperties.Id))
            _catalog.UserDefinedFunctions["UDF." + userDefinedFunctionProperties.Id] = ScriptCatalog.UdfPlaceholder;

        return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.Created,
            JsonConvert.SerializeObject(userDefinedFunctionProperties)));
    }

    public override Task<ResponseMessage> ReadUserDefinedFunctionStreamAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.UdfProperties.TryGetValue(id, out var props))
            throw InMemoryCosmosException.Create(
                $"UDF '{id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.OK,
            JsonConvert.SerializeObject(props)));
    }

    public override Task<ResponseMessage> ReplaceUserDefinedFunctionStreamAsync(
        UserDefinedFunctionProperties userDefinedFunctionProperties,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.UdfProperties.ContainsKey(userDefinedFunctionProperties.Id))
            throw InMemoryCosmosException.Create(
                $"UDF '{userDefinedFunctionProperties.Id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.UdfProperties[userDefinedFunctionProperties.Id] = userDefinedFunctionProperties;
        return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.OK,
            JsonConvert.SerializeObject(userDefinedFunctionProperties)));
    }

    public override Task<ResponseMessage> DeleteUserDefinedFunctionStreamAsync(
        string id,
        RequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.UdfProperties.Remove(id))
            throw InMemoryCosmosException.Create(
                $"UDF '{id}' not found.",
                HttpStatusCode.NotFound, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);

        _catalog.UserDefinedFunctions.Remove("UDF." + id);
        return Task.FromResult(_c.CreateResponseMessage(HttpStatusCode.NoContent));
    }

    // ── Query iterators (typed) ───────────────────────────────────────

    public override FeedIterator<T> GetStoredProcedureQueryIterator<T>(
        string? queryText = null, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => new InMemoryFeedIterator<T>(() => FilterById(_catalog.StoredProcedureProperties, queryText).Cast<T>().ToList());

    public override FeedIterator<T> GetStoredProcedureQueryIterator<T>(
        QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => new InMemoryFeedIterator<T>(() => FilterById(_catalog.StoredProcedureProperties, queryDefinition).Cast<T>().ToList());

    public override FeedIterator<T> GetTriggerQueryIterator<T>(
        string? queryText = null, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => new InMemoryFeedIterator<T>(() => FilterById(_catalog.TriggerProperties, queryText).Cast<T>().ToList());

    public override FeedIterator<T> GetTriggerQueryIterator<T>(
        QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => new InMemoryFeedIterator<T>(() => FilterById(_catalog.TriggerProperties, queryDefinition).Cast<T>().ToList());

    public override FeedIterator<T> GetUserDefinedFunctionQueryIterator<T>(
        string? queryText = null, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => new InMemoryFeedIterator<T>(() => FilterById(_catalog.UdfProperties, queryText).Cast<T>().ToList());

    public override FeedIterator<T> GetUserDefinedFunctionQueryIterator<T>(
        QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => new InMemoryFeedIterator<T>(() => FilterById(_catalog.UdfProperties, queryDefinition).Cast<T>().ToList());

    // ── Query iterators (stream) ──────────────────────────────────────

    public override FeedIterator GetStoredProcedureQueryStreamIterator(
        string? queryText = null, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => new InMemoryStreamFeedIterator(
            () => FilterById(_catalog.StoredProcedureProperties, queryText).Cast<object>().ToList(), "StoredProcedures", () => _c.CurrentSessionToken);

    public override FeedIterator GetStoredProcedureQueryStreamIterator(
        QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => new InMemoryStreamFeedIterator(
            () => FilterById(_catalog.StoredProcedureProperties, queryDefinition).Cast<object>().ToList(), "StoredProcedures", () => _c.CurrentSessionToken);

    public override FeedIterator GetTriggerQueryStreamIterator(
        string? queryText = null, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => new InMemoryStreamFeedIterator(
            () => FilterById(_catalog.TriggerProperties, queryText).Cast<object>().ToList(), "Triggers", () => _c.CurrentSessionToken);

    public override FeedIterator GetTriggerQueryStreamIterator(
        QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => new InMemoryStreamFeedIterator(
            () => FilterById(_catalog.TriggerProperties, queryDefinition).Cast<object>().ToList(), "Triggers", () => _c.CurrentSessionToken);

    public override FeedIterator GetUserDefinedFunctionQueryStreamIterator(
        string? queryText = null, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => new InMemoryStreamFeedIterator(
            () => FilterById(_catalog.UdfProperties, queryText).Cast<object>().ToList(), "UserDefinedFunctions", () => _c.CurrentSessionToken);

    public override FeedIterator GetUserDefinedFunctionQueryStreamIterator(
        QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => new InMemoryStreamFeedIterator(
            () => FilterById(_catalog.UdfProperties, queryDefinition).Cast<object>().ToList(), "UserDefinedFunctions", () => _c.CurrentSessionToken);

    // ── Private helpers ───────────────────────────────────────────────

    public static void EnrichStoredProcedureSystemProperties(StoredProcedureProperties props)
    {
        var json = JsonConvert.SerializeObject(props);
        var jObj = JObject.Parse(json);
        if (jObj["_etag"] == null || string.IsNullOrEmpty(jObj["_etag"]?.ToString()))
            jObj["_etag"] = $"\"{Guid.NewGuid()}\"";
        if (jObj["_self"] == null || string.IsNullOrEmpty(jObj["_self"]?.ToString()))
            jObj["_self"] = $"dbs/db/colls/col/sprocs/{props.Id}";
        if (jObj["_ts"] == null || jObj["_ts"]!.Type == JTokenType.Null)
            jObj["_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (jObj["_rid"] == null || string.IsNullOrEmpty(jObj["_rid"]?.ToString()))
            jObj["_rid"] = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=');
        JsonConvert.PopulateObject(jObj.ToString(), props);
    }

    private static IEnumerable<TValue> FilterById<TValue>(
        Dictionary<string, TValue> dict, string? queryText)
    {
        var id = ExtractIdFromQueryText(queryText);
        return id != null && dict.TryGetValue(id, out var match)
            ? new[] { match }
            : dict.Values;
    }

    private static IEnumerable<TValue> FilterById<TValue>(
        Dictionary<string, TValue> dict, QueryDefinition? queryDefinition)
    {
        if (queryDefinition == null) return dict.Values;
        var id = ExtractIdFromQueryText(queryDefinition.QueryText);
        if (id != null && id.StartsWith("@"))
        {
            var parameters = CosmosSdkIntrospector.ExtractQueryParameters(queryDefinition);
            if (parameters.TryGetValue(id, out var paramValue))
                id = paramValue?.ToString();
            else
                return dict.Values;
        }
        return id != null && dict.TryGetValue(id, out var match)
            ? new[] { match }
            : dict.Values;
    }

    private static string? ExtractIdFromQueryText(string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return null;
        var m = IdFilterRegex.Match(queryText);
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value
             : m.Groups[2].Success ? m.Groups[2].Value
             : m.Groups[3].Success ? m.Groups[3].Value
             : null;
    }

    private static string? ExecuteHandler(string sprocId, Func<string> invoker)
    {
        string? result;
        try
        {
            var task = Task.Run(invoker);
            if (!task.Wait(TimeSpan.FromSeconds(10)))
                throw InMemoryCosmosException.Create(
                    $"Stored procedure '{sprocId}' exceeded the 10-second execution timeout.",
                    HttpStatusCode.RequestTimeout, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
            result = task.Result;
        }
        catch (CosmosException) { throw; }
        catch (AggregateException ae) when (ae.InnerException is CosmosException) { throw ae.InnerException; }
        catch (AggregateException ae)
        {
            var inner = ae.InnerException ?? ae;
            throw InMemoryCosmosException.Create($"Stored procedure '{sprocId}' failed: {inner.Message}",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }
        catch (Exception ex)
        {
            throw InMemoryCosmosException.Create($"Stored procedure '{sprocId}' failed: {ex.Message}",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        const int MaxResponseSize = 2 * 1024 * 1024;
        if (result != null && Encoding.UTF8.GetByteCount(result) > MaxResponseSize)
            throw InMemoryCosmosException.Create($"Stored procedure '{sprocId}' response exceeds the 2MB size limit.",
                (HttpStatusCode)413, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        return result;
    }

    private string? ExecuteJsEngine(string sprocId, string jsBody, PartitionKey pk, dynamic[] args)
    {
        try
        {
            var context = new PartitionScopedCollectionContext(_c, pk);
            return _c.SprocEngine!.Execute(jsBody, pk, args, context);
        }
        catch (CosmosException) { throw; }
        catch (Exception ex)
        {
            throw InMemoryCosmosException.Create($"Stored procedure '{sprocId}' failed: {ex.Message}",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }
    }

    private void PopulateScriptLogHeaders<TOutput>(StoredProcedureExecuteResponse<TOutput> r)
    {
        if (_c.SprocEngine?.CapturedLogs is { Count: > 0 } logs)
        {
            var logString = Uri.EscapeDataString(string.Join("\n", logs));
            var headers = new Headers
            {
                ["x-ms-request-charge"] = SyntheticRequestCharge.ToString(CultureInfo.InvariantCulture),
                ["x-ms-documentdb-script-log-results"] = logString
            };
            r.Headers.Returns(headers);
            r.ScriptLog.Returns(Uri.UnescapeDataString(logString));
        }
    }
}
