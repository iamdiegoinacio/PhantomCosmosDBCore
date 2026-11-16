#nullable enable
using Phantom.CosmosDB.Core.Infrastructure.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core;

/// <summary>
/// JSON parsing and numeric normalization utilities matching Cosmos DB RFC 8259 semantics.
/// </summary>
internal static class JsonParseHelpers
{
    internal static JObject ParseJson(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json))
        {
            DateParseHandling = DateParseHandling.None
        };
        var obj = JObject.Load(reader);

        var normalized = ToCosmosJson(obj);
        using var reader2 = new JsonTextReader(new StringReader(normalized))
        {
            DateParseHandling = DateParseHandling.None
        };
        return JObject.Load(reader2);
    }

    internal static JToken ParseJsonToken(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json))
        {
            DateParseHandling = DateParseHandling.None
        };
        var token = JToken.Load(reader);

        if (token is JObject or JArray)
        {
            var normalized = ToCosmosJson(token);
            using var reader2 = new JsonTextReader(new StringReader(normalized))
            {
                DateParseHandling = DateParseHandling.None
            };
            return JToken.Load(reader2);
        }

        return token;
    }

    /// <summary>
    /// Serializes a <see cref="JToken"/> to JSON using Cosmos DB's minimal numeric representation.
    /// Whole numbers are written without .0, trailing fractional zeros are stripped.
    /// </summary>
    internal static string ToCosmosJson(JToken token)
    {
        using var sw = new StringWriter();
        using var writer = new CosmosJsonWriter(sw) { Formatting = Formatting.None };
        token.WriteTo(writer);
        return sw.ToString();
    }

    /// <summary>
    /// Normalizes a numeric aggregate result to match Cosmos DB's output format.
    /// Whole-number results are returned as long; fractional results as double.
    /// </summary>
    internal static object NormalizeNumericResult(double value)
    {
        if (value == Math.Truncate(value) && value >= long.MinValue && value < (double)long.MaxValue)
            return (long)value;
        return value;
    }
}
