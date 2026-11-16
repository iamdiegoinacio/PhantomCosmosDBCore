#nullable enable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Application.Query;

/// <summary>
/// Evaluates arbitrary Cosmos DB SQL queries against in-memory metadata collections
/// (Databases, Containers, Stored Procedures, Triggers, UDFs) by routing their JSON
/// representations through <see cref="CosmosQueryPipeline"/>.
/// </summary>
internal static class MetadataSqlQueryExecutor
{
    public static List<T> Execute<T, TSource>(
        IEnumerable<TSource> sources,
        string? queryText,
        IDictionary<string, object?>? parameters = null)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            if (typeof(T) == typeof(TSource))
            {
                return sources.Cast<T>().ToList();
            }

            return sources
                .Select(s => DeserializeResult<T>(JsonConvert.SerializeObject(s)))
                .ToList();
        }

        var jsonItems = sources.Select(s => JsonConvert.SerializeObject(s)).ToList();
        if (jsonItems.Count == 0)
        {
            return new List<T>();
        }

        var context = new QueryExecutionContext(parameters);
        var executed = CosmosQueryPipeline.ExecuteSafe(
            () => CosmosQueryPipeline.Execute(
                queryText,
                jsonItems,
                context,
                Array.Empty<(string Name, string FromAlias, SqlExpression? Expr)>()));

        return executed.Select(DeserializeResult<T>).ToList();
    }

    private static T DeserializeResult<T>(string json)
    {
        if (typeof(T) == typeof(string))
        {
            var token = JToken.Parse(json);
            return (T)(object)(token.Type == JTokenType.String ? token.Value<string>()! : json);
        }

        if (typeof(T) == typeof(JObject))
        {
            return (T)(object)JObject.Parse(json);
        }

        if (typeof(T) == typeof(JToken))
        {
            return (T)(object)JToken.Parse(json);
        }

        return JsonConvert.DeserializeObject<T>(json)!;
    }
}
