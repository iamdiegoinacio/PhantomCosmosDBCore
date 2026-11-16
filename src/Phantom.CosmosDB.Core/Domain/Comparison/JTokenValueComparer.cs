#nullable enable
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Domain.Comparison;

/// <summary>
/// Type-aware equality comparer for <see cref="JToken"/> values, matching Cosmos DB semantics.
/// Different types (number vs string) are never equal, even if their string representations match.
/// </summary>
internal sealed class JTokenValueComparer : IEqualityComparer<JToken?>
{
    public static readonly JTokenValueComparer Instance = new();

    private JTokenValueComparer()
    {
    }

    public bool Equals(JToken? x, JToken? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return x is null && y is null;
        return JToken.DeepEquals(x, y);
    }

    public int GetHashCode(JToken? obj)
    {
        if (obj is null) return 0;
        return obj.Type switch
        {
            JTokenType.Integer => HashCode.Combine(obj.Type, obj.Value<long>()),
            JTokenType.Float => HashCode.Combine(obj.Type, obj.Value<double>()),
            JTokenType.String => HashCode.Combine(obj.Type, obj.Value<string>()?.GetHashCode() ?? 0),
            JTokenType.Boolean => HashCode.Combine(obj.Type, obj.Value<bool>()),
            JTokenType.Null => HashCode.Combine(obj.Type),
            JTokenType.Object => HashObjectOrderInsensitive((JObject)obj),
            JTokenType.Array => HashArrayElements((JArray)obj),
            _ => HashCode.Combine(obj.Type, obj.ToString().GetHashCode())
        };
    }

    private static int HashObjectOrderInsensitive(JObject obj)
    {
        int hash = 0;
        foreach (var prop in obj.Properties())
        {
            hash ^= HashCode.Combine(prop.Name.GetHashCode(), Instance.GetHashCode(prop.Value));
        }
        return HashCode.Combine(JTokenType.Object, hash);
    }

    private static int HashArrayElements(JArray arr)
    {
        var hash = new HashCode();
        hash.Add(JTokenType.Array);
        foreach (var element in arr)
        {
            hash.Add(Instance.GetHashCode(element));
        }
        return hash.ToHashCode();
    }
}
