#nullable enable
using Jint;
using Jint.Native;

namespace Phantom.CosmosDB.JsTriggers.Context;

/// <summary>
/// Builds the Cosmos DB server-side JavaScript environment (<c>getContext()</c>) for each kind
/// of script: stored procedures, pre-triggers and post-triggers. Each method binds the CLR
/// callbacks the script needs and then installs the matching JavaScript facade.
/// </summary>
internal static class CosmosJsContextBootstrapper
{
    private const string StoredProcedureScript = """
        var console = { log: function(msg) { __log(msg); } };
        function getContext() {
            return {
                getResponse: function() {
                    return {
                        setBody: function(val) { __setBody(val); }
                    };
                },
                getCollection: function() { return __collection; }
            };
        }
        """;

    private const string PreTriggerScript = """
        function getContext() {
            return {
                getRequest: function() {
                    return {
                        getBody: function() { return __getBody(); },
                        setBody: function(doc) { __setBody(doc); }
                    };
                },
                getResponse: function() {
                    throw new Error("getResponse() is not available in pre-triggers.");
                },
                getCollection: function() { return __collection; }
            };
        }
        """;

    // Post-triggers have access to both getResponse() and getRequest().
    private const string PostTriggerScript = """
        function getContext() {
            return {
                getResponse: function() {
                    return {
                        getBody: function() { return __getBody(); },
                        setBody: function(doc) { __setBody(doc); }
                    };
                },
                getRequest: function() {
                    return {
                        getBody: function() { return __getBody(); }
                    };
                },
                getCollection: function() { return __collection; }
            };
        }
        """;

    /// <summary>Installs <c>getContext()</c>, <c>console.log</c> and <c>getCollection()</c> for a stored procedure.</summary>
    public static void InstallStoredProcedure(
        Engine engine,
        ICollectionContext? collection,
        Action<JsValue> setBody,
        Action<JsValue> log)
    {
        engine.SetValue("__setBody", setBody);
        engine.SetValue("__log", log);
        JintCollectionContextBridge.Wire(engine, collection);
        engine.Execute(StoredProcedureScript);
    }

    /// <summary>Installs <c>getContext()</c> for a pre-trigger (request body readable and writable).</summary>
    public static void InstallPreTrigger(
        Engine engine,
        ICollectionContext? collection,
        JsValue document,
        Action<JsValue> setBody)
        => InstallTrigger(engine, collection, document, setBody, PreTriggerScript);

    /// <summary>Installs <c>getContext()</c> for a post-trigger (response body writable, request body readable).</summary>
    public static void InstallPostTrigger(
        Engine engine,
        ICollectionContext? collection,
        JsValue document,
        Action<JsValue> setBody)
        => InstallTrigger(engine, collection, document, setBody, PostTriggerScript);

    private static void InstallTrigger(
        Engine engine,
        ICollectionContext? collection,
        JsValue document,
        Action<JsValue> setBody,
        string script)
    {
        engine.SetValue("__getBody", new Func<JsValue>(() => document));
        engine.SetValue("__setBody", setBody);
        JintCollectionContextBridge.Wire(engine, collection);
        engine.Execute(script);
    }
}
