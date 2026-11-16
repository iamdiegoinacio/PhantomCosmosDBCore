#nullable enable
using System.Globalization;
using System.Net;
using System.Text;
using Phantom.CosmosDB.Core.Domain.Primitives;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;
using Newtonsoft.Json;
using NSubstitute;
using static Phantom.CosmosDB.Core.Domain.Primitives.ContainerConstants;

namespace Phantom.CosmosDB.Core.Application.Scripts;

/// <summary>
/// Executes C# delegate and JavaScript stored procedures against an <see cref="InMemoryContainer"/>,
/// enforcing timeouts, response size limits, and script log headers.
/// </summary>
internal static class StoredProcedureExecutionService
{
    private const int MaxResponseSizeBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(10);

    public static Task<StoredProcedureExecuteResponse<TOutput>> ExecuteAsync<TOutput>(
        InMemoryContainer container,
        ScriptCatalog catalog,
        string storedProcedureId,
        PartitionKey partitionKey,
        dynamic[] parameters)
    {
        var (hasHandler, handlerResult) = ExecuteCore(container, catalog, storedProcedureId, partitionKey, parameters);

        var response = Substitute.For<StoredProcedureExecuteResponse<TOutput>>();
        response.StatusCode.Returns(HttpStatusCode.OK);
        response.RequestCharge.Returns(SyntheticRequestCharge);
        if (hasHandler)
        {
            if (handlerResult is not null)
            {
                TOutput deserialized = typeof(TOutput) == typeof(string)
                    ? (TOutput)(object)handlerResult
                    : JsonConvert.DeserializeObject<TOutput>(handlerResult)!;
                response.Resource.Returns(deserialized);
            }
            else
            {
                response.Resource.Returns(default(TOutput)!);
            }
        }

        PopulateScriptLogHeaders(container, response);
        return Task.FromResult(response);
    }

    public static Task<ResponseMessage> ExecuteStreamAsync(
        InMemoryContainer container,
        ScriptCatalog catalog,
        string storedProcedureId,
        PartitionKey partitionKey,
        dynamic[] parameters)
    {
        var (_, handlerResult) = ExecuteCore(container, catalog, storedProcedureId, partitionKey, parameters);

        var msg = container.CreateResponseMessage(HttpStatusCode.OK, handlerResult);
        if (container.SprocEngine?.CapturedLogs is { Count: > 0 } logs)
        {
            msg.Headers["x-ms-documentdb-script-log-results"] = Uri.EscapeDataString(string.Join("\n", logs));
        }

        return Task.FromResult(msg);
    }

    private static (bool HasHandler, string? Result) ExecuteCore(
        InMemoryContainer container,
        ScriptCatalog catalog,
        string storedProcedureId,
        PartitionKey partitionKey,
        dynamic[] parameters)
    {
        if (!catalog.StoredProcedures.ContainsKey(storedProcedureId) &&
            !catalog.StoredProcedureProperties.ContainsKey(storedProcedureId))
        {
            throw InMemoryCosmosException.Create(
                $"StoredProcedure '{storedProcedureId}' not found.",
                HttpStatusCode.NotFound,
                0,
                Guid.NewGuid().ToString(),
                SyntheticRequestCharge);
        }

        if (catalog.StoredProcedures.TryGetValue(storedProcedureId, out var handler))
        {
            return (true, ExecuteDelegate(storedProcedureId, () => handler(partitionKey, parameters)));
        }

        if (container.SprocEngine is not null
            && catalog.StoredProcedureProperties.TryGetValue(storedProcedureId, out var sprocProps)
            && sprocProps.Body is not null)
        {
            return (true, ExecuteJsEngine(container, storedProcedureId, sprocProps.Body, partitionKey, parameters));
        }

        return (false, null);
    }

    private static string? ExecuteDelegate(string sprocId, Func<string> invoker)
    {
        string? result;
        try
        {
            var task = Task.Run(invoker);
            if (!task.Wait(ExecutionTimeout))
            {
                throw InMemoryCosmosException.Create(
                    $"Stored procedure '{sprocId}' exceeded the 10-second execution timeout.",
                    HttpStatusCode.RequestTimeout,
                    CosmosSubStatusCodes.StoredProcedureScriptTimeout,
                    Guid.NewGuid().ToString(),
                    SyntheticRequestCharge);
            }

            result = task.Result;
        }
        catch (CosmosException)
        {
            throw;
        }
        catch (AggregateException ae) when (ae.InnerException is CosmosException cosmosEx)
        {
            throw cosmosEx;
        }
        catch (AggregateException ae)
        {
            var inner = ae.InnerException ?? ae;
            throw InMemoryCosmosException.Create(
                $"Stored procedure '{sprocId}' failed: {inner.Message}",
                HttpStatusCode.BadRequest,
                CosmosSubStatusCodes.ScriptError,
                Guid.NewGuid().ToString(),
                SyntheticRequestCharge);
        }
        catch (Exception ex)
        {
            throw InMemoryCosmosException.Create(
                $"Stored procedure '{sprocId}' failed: {ex.Message}",
                HttpStatusCode.BadRequest,
                CosmosSubStatusCodes.ScriptError,
                Guid.NewGuid().ToString(),
                SyntheticRequestCharge);
        }

        if (result != null && Encoding.UTF8.GetByteCount(result) > MaxResponseSizeBytes)
        {
            throw InMemoryCosmosException.Create(
                $"Stored procedure '{sprocId}' response exceeds the 2MB size limit.",
                HttpStatusCode.RequestEntityTooLarge,
                0,
                Guid.NewGuid().ToString(),
                SyntheticRequestCharge);
        }

        return result;
    }

    private static string? ExecuteJsEngine(
        InMemoryContainer container,
        string sprocId,
        string jsBody,
        PartitionKey pk,
        dynamic[] args)
    {
        try
        {
            var context = new PartitionScopedCollectionContext(container, pk);
            return container.SprocEngine!.Execute(jsBody, pk, args, context);
        }
        catch (CosmosException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw InMemoryCosmosException.Create(
                $"Stored procedure '{sprocId}' failed: {ex.Message}",
                HttpStatusCode.BadRequest,
                CosmosSubStatusCodes.ScriptError,
                Guid.NewGuid().ToString(),
                SyntheticRequestCharge);
        }
    }

    private static void PopulateScriptLogHeaders<TOutput>(
        InMemoryContainer container,
        StoredProcedureExecuteResponse<TOutput> response)
    {
        if (container.SprocEngine?.CapturedLogs is { Count: > 0 } logs)
        {
            var logString = Uri.EscapeDataString(string.Join("\n", logs));
            var headers = new Headers
            {
                ["x-ms-request-charge"] = SyntheticRequestCharge.ToString(CultureInfo.InvariantCulture),
                ["x-ms-documentdb-script-log-results"] = logString
            };
            response.Headers.Returns(headers);
            response.ScriptLog.Returns(Uri.UnescapeDataString(logString));
        }
    }
}
