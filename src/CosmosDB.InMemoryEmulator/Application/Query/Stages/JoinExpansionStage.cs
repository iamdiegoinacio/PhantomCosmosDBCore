#nullable enable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.Query.Stages;

/// <summary>
/// Query pipeline stage that expands <c>FROM alias IN c.field</c> and <c>JOIN</c> clauses into flattened rows.
/// </summary>
internal static class JoinExpansionStage
{
    public static List<string> ExpandFromSource(IEnumerable<string> items, CosmosSqlQuery parsed)
    {
        var sourcePath = parsed.FromSource ?? string.Empty;
        var dotIndex = sourcePath.IndexOf('.');
        var arrayPath = dotIndex >= 0 ? sourcePath[(dotIndex + 1)..] : sourcePath;

        var expanded = new List<string>();
        foreach (var json in items)
        {
            var jObj = JsonParseHelpers.ParseJson(json);
            var arrayToken = jObj.SelectToken(arrayPath);
            if (arrayToken is not JArray jArray)
                continue;

            foreach (var element in jArray)
            {
                var combined = element is JObject elementObj
                    ? new JObject(elementObj.Properties()) { [parsed.FromAlias] = element.DeepClone() }
                    : new JObject { [parsed.FromAlias] = element };
                expanded.Add(combined.ToString(Formatting.None));
            }
        }
        return expanded;
    }

    public static List<string> ExpandJoinedItems(IEnumerable<string> items, CosmosSqlQuery parsed)
    {
        if (parsed.Join is null)
        {
            return items.ToList();
        }

        var expanded = new List<string>();
        foreach (var json in items)
        {
            var jObj = JsonParseHelpers.ParseJson(json);
            var arrayToken = jObj.SelectToken(parsed.Join.ArrayField);
            if (arrayToken is JArray jArray)
            {
                foreach (var element in jArray)
                {
                    var combined = new JObject(jObj.Properties())
                    {
                        [parsed.Join.Alias] = element
                    };
                    expanded.Add(combined.ToString(Formatting.None));
                }
            }
        }
        return expanded;
    }

    public static List<string> ExpandAllJoins(IEnumerable<string> items, CosmosSqlQuery parsed)
    {
        var current = items;
        foreach (var join in parsed.Joins ?? [])
        {
            var expanded = new List<string>();
            foreach (var json in current)
            {
                var jObj = JsonParseHelpers.ParseJson(json);
                var sourcePath = join.SourceAlias == parsed.FromAlias
                    ? join.ArrayField
                    : $"{join.SourceAlias}.{join.ArrayField}";
                var arrayToken = jObj.SelectToken(sourcePath);
                if (arrayToken is JArray jArray)
                {
                    foreach (var element in jArray)
                    {
                        var combined = new JObject(jObj.Properties())
                        {
                            [join.Alias] = element
                        };
                        expanded.Add(combined.ToString(Formatting.None));
                    }
                }
            }
            current = expanded;
        }
        return current as List<string> ?? current.ToList();
    }
}
