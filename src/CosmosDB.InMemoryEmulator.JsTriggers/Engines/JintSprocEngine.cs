#nullable enable
using CosmosDB.InMemoryEmulator.JsTriggers.Context;
using CosmosDB.InMemoryEmulator.JsTriggers.Runtime;
using Jint.Native;
using Jint.Runtime;
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator.JsTriggers;

/// <summary>
/// Jint-based implementation of <see cref="ISprocEngine"/>.
/// Executes Cosmos DB stored procedure JavaScript bodies using the Jint interpreter.
/// Provides the Cosmos DB server-side API: getContext(), getResponse().setBody(), and argument passing.
/// </summary>
public class JintSprocEngine : ISprocEngine
{
    private List<string> _capturedLogs = new();

    /// <inheritdoc />
    public IReadOnlyList<string> CapturedLogs => _capturedLogs;

    /// <inheritdoc />
    public string? Execute(string jsBody, PartitionKey partitionKey, dynamic[] args)
        => Execute(jsBody, partitionKey, args, null!);

    /// <inheritdoc />
    public string? Execute(string jsBody, PartitionKey partitionKey, dynamic[] args, ICollectionContext context)
    {
        string? result = null;
        var logs = new List<string>();
        _capturedLogs = logs;

        var engine = JintEngineFactory.Create();

        CosmosJsContextBootstrapper.InstallStoredProcedure(
            engine,
            context,
            setBody: value => result = JintValueMarshaller.ToStoredProcedureResponse(engine, value),
            log: message => logs.Add(message.ToString()));

        var jsArgs = JintValueMarshaller.ToJsValues(engine, args);

        try
        {
            JintFunctionInvoker.InvokeStoredProcedure(engine, jsBody, jsArgs);
        }
        catch (JavaScriptException ex)
        {
            throw ScriptFailure.From(ScriptFailure.StoredProcedure, ex);
        }

        return result;
    }
}
