#nullable enable
using Phantom.CosmosDB.JsTriggers.Context;
using Phantom.CosmosDB.JsTriggers.Runtime;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.JsTriggers;

/// <summary>
/// Jint-based implementation of <see cref="IJsTriggerEngine"/>.
/// Executes Cosmos DB trigger JavaScript bodies using the Jint interpreter.
/// Provides the Cosmos DB server-side API: getContext(), getRequest()/getResponse(), getBody(), setBody().
/// </summary>
/// <remarks>
/// Also implements <see cref="IJsUdfEngine"/> for backward compatibility with
/// <c>container.JsTriggerEngine as IJsUdfEngine</c>; UDF execution is delegated to <see cref="JintUdfEngine"/>.
/// </remarks>
public class JintTriggerEngine : IJsTriggerEngine, IJsUdfEngine
{
    private readonly IJsUdfEngine _udfEngine = new JintUdfEngine();

    /// <inheritdoc />
    public JObject ExecutePreTrigger(string jsBody, JObject document)
        => ExecutePreTrigger(jsBody, document, null!);

    /// <inheritdoc />
    public JObject ExecutePreTrigger(string jsBody, JObject document, ICollectionContext context)
        => RunTrigger(
            jsBody, document, context,
            CosmosJsContextBootstrapper.InstallPreTrigger,
            ScriptFailure.PreTrigger) ?? document;

    /// <inheritdoc />
    public JObject? ExecutePostTrigger(string jsBody, JObject document)
        => ExecutePostTrigger(jsBody, document, null!);

    /// <inheritdoc />
    public JObject? ExecutePostTrigger(string jsBody, JObject document, ICollectionContext context)
        => RunTrigger(
            jsBody, document, context,
            CosmosJsContextBootstrapper.InstallPostTrigger,
            ScriptFailure.PostTrigger);

    /// <inheritdoc />
    public object? ExecuteUdf(string jsBody, object[] args)
        => _udfEngine.ExecuteUdf(jsBody, args);

    /// <summary>
    /// Template for pre/post trigger execution: parse the document into JS, install the
    /// trigger-specific context, run the body and return the body set by the script (if any).
    /// </summary>
    private static JObject? RunTrigger(
        string jsBody,
        JObject document,
        ICollectionContext? context,
        Action<Engine, ICollectionContext?, JsValue, Action<JsValue>> installContext,
        string failureKind)
    {
        JsValue? updatedBody = null;
        var engine = JintEngineFactory.Create();

        var jsDocument = JintValueMarshaller.FromJson(engine, document.ToString(Formatting.None));
        installContext(engine, context, jsDocument, value => updatedBody = value);

        try
        {
            JintFunctionInvoker.InvokeTrigger(engine, jsBody);
        }
        catch (JavaScriptException ex)
        {
            throw ScriptFailure.From(failureKind, ex);
        }

        if (updatedBody is null)
        {
            return null;
        }

        var json = JintValueMarshaller.ToJson(engine, updatedBody);
        return json is null ? null : JObject.Parse(json);
    }
}
