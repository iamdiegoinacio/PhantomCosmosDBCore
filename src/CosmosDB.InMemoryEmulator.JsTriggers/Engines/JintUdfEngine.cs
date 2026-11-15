#nullable enable
using CosmosDB.InMemoryEmulator.JsTriggers.Runtime;
using Jint.Runtime;

namespace CosmosDB.InMemoryEmulator.JsTriggers;

/// <summary>
/// Jint-based implementation of <see cref="IJsUdfEngine"/>.
/// Executes Cosmos DB user-defined function JavaScript bodies in an isolated, sandboxed engine.
/// </summary>
public class JintUdfEngine : IJsUdfEngine
{
    /// <inheritdoc />
    public object? ExecuteUdf(string jsBody, object[] args)
    {
        var engine = JintEngineFactory.Create();

        try
        {
            var jsArgs = JintValueMarshaller.ToJsValues(engine, args);
            var result = JintFunctionInvoker.InvokeUdf(engine, jsBody, jsArgs);
            return JintValueMarshaller.ToClr(engine, result);
        }
        catch (JavaScriptException ex)
        {
            throw ScriptFailure.From(ScriptFailure.Udf, ex);
        }
    }
}
