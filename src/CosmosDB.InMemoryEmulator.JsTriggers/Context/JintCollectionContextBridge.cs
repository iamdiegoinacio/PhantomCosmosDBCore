#nullable enable
using Jint;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.JsTriggers.Context;

/// <summary>
/// Bridge exposing an <see cref="ICollectionContext"/> to JavaScript as the
/// <c>__collection</c> object returned by <c>getContext().getCollection()</c>.
/// </summary>
internal static class JintCollectionContextBridge
{
    private const string NullCollectionScript = """
        var __collection = { getSelfLink: function() { return ""; } };
        """;

    private const string CollectionScript = """
        var __collection = {
            getSelfLink: function() { return __selfLink; },
            createDocument: function(link, doc, opts, cb) {
                var result = JSON.parse(__createDocument(JSON.stringify(doc)));
                if (typeof opts === 'function') { opts(null, result); }
                else if (cb) { cb(null, result); }
                return true;
            },
            readDocument: function(link, opts, cb) {
                var docId = link.split('/').pop();
                var result = JSON.parse(__readDocument(docId));
                if (typeof opts === 'function') { opts(null, result); }
                else if (cb) { cb(null, result); }
                return true;
            },
            queryDocuments: function(link, query, opts, cb) {
                var results = JSON.parse(__queryDocuments(query));
                var responseOptions = { continuation: null };
                if (typeof opts === 'function') { opts(null, results, responseOptions); }
                else if (cb) { cb(null, results, responseOptions); }
                return true;
            },
            replaceDocument: function(link, doc, opts, cb) {
                var docId = doc.id || link.split('/').pop();
                var result = JSON.parse(__replaceDocument(docId, JSON.stringify(doc)));
                if (typeof opts === 'function') { opts(null, result); }
                else if (cb) { cb(null, result); }
                return true;
            },
            deleteDocument: function(link, opts, cb) {
                var docId = link.split('/').pop();
                __deleteDocument(docId);
                if (typeof opts === 'function') { opts(null); }
                else if (cb) { cb(null); }
                return true;
            }
        };
        """;

    /// <summary>
    /// Defines <c>__collection</c> inside <paramref name="engine"/>. A <c>null</c> context yields
    /// a minimal collection that only supports <c>getSelfLink()</c>.
    /// </summary>
    public static void Wire(Engine engine, ICollectionContext? context)
    {
        if (context is null)
        {
            engine.Execute(NullCollectionScript);
            return;
        }

        engine.SetValue("__selfLink", context.SelfLink ?? "");
        engine.SetValue("__createDocument", new Func<string, string>(jsonDoc =>
            context.CreateDocument(JObject.Parse(jsonDoc)).ToString(Formatting.None)));
        engine.SetValue("__readDocument", new Func<string, string>(docId =>
            context.ReadDocument(docId).ToString(Formatting.None)));
        engine.SetValue("__queryDocuments", new Func<string, string>(sql =>
            JsonConvert.SerializeObject(context.QueryDocuments(sql), Formatting.None)));
        engine.SetValue("__replaceDocument", new Func<string, string, string>((docId, jsonDoc) =>
            context.ReplaceDocument(docId, JObject.Parse(jsonDoc)).ToString(Formatting.None)));
        engine.SetValue("__deleteDocument", new Action<string>(context.DeleteDocument));

        engine.Execute(CollectionScript);
    }
}
