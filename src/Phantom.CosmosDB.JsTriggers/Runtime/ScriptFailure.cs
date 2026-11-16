#nullable enable
using System.Net;
using Jint.Runtime;

namespace Phantom.CosmosDB.JsTriggers.Runtime;

/// <summary>
/// Translates JavaScript runtime errors into the <c>400 BadRequest</c> Cosmos exception
/// that the real service returns for failing server-side scripts.
/// </summary>
internal static class ScriptFailure
{
    public const string StoredProcedure = "Stored procedure failed";
    public const string PreTrigger = "Pre-trigger failed";
    public const string PostTrigger = "Post-trigger failed";
    public const string Udf = "UDF execution failed";

    /// <summary>Creates the exception for a failed script of the given <paramref name="kind"/>.</summary>
    public static Exception From(string kind, JavaScriptException error)
        => InMemoryCosmosException.Create(
            $"{kind}: {error.Message}",
            HttpStatusCode.BadRequest, 0, string.Empty, 0);
}
