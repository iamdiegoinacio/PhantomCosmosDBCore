#nullable enable
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Domain.Comparison;

/// <summary>
/// Compares JSON strings using structural equality (property-order-insensitive).
/// Used by <c>DISTINCT</c> to deduplicate projected results where objects may have
/// identical values but different property ordering (e.g. after Patch operations).
/// </summary>
internal sealed class JsonStructuralStringComparer : IEqualityComparer<string?>
{
    public static readonly JsonStructuralStringComparer Instance = new();

    private JsonStructuralStringComparer()
    {
    }

    public bool Equals(string? x, string? y)
    {
        if (x == y) return true;
        if (x is null || y is null) return false;
        try { return JToken.DeepEquals(JToken.Parse(x), JToken.Parse(y)); }
        catch { return false; }
    }

    public int GetHashCode(string? obj)
    {
        if (obj is null) return 0;
        try { return JTokenValueComparer.Instance.GetHashCode(JToken.Parse(obj)); }
        catch { return obj.GetHashCode(); }
    }
}
