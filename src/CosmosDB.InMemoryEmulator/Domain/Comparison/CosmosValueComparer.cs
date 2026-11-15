#nullable enable
using System.Globalization;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Domain.Comparison;

/// <summary>
/// Implements Cosmos DB value equality, total type-ranked ordering, boolean truthiness,
/// and MIN/MAX type-ranked aggregation rules.
/// </summary>
internal static class CosmosValueComparer
{
    /// <summary>
    /// Sentinel for undefined (missing field) in <c>ORDER BY</c> — distinct from <c>null</c>.
    /// </summary>
    public static readonly object UndefinedSortSentinel = new();

    /// <summary>
    /// Cosmos DB <c>WHERE</c> requires strict boolean — non-boolean values evaluate to <c>false</c>.
    /// </summary>
    public static bool IsTruthy(object? value) => value switch
    {
        null => false,
        UndefinedValue => false,
        bool b => b,
        _ => false
    };

    public static bool ValuesEqual(object? left, object? right)
    {
        if (left is UndefinedValue || right is UndefinedValue)
        {
            return false;
        }

        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if ((left is double or long) && (right is double or long))
        {
            if (double.TryParse(left.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var l) &&
                double.TryParse(right.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r))
            {
                return l == r;
            }
        }

        var leftJ = left is JToken ? left : NormalizeToJTokenIfComplex(left);
        var rightJ = right is JToken ? right : NormalizeToJTokenIfComplex(right);

        if (GetTypeRank(leftJ) != GetTypeRank(rightJ))
        {
            return false;
        }

        if (leftJ is JToken jtLeft && rightJ is JToken jtRight)
        {
            return JToken.DeepEquals(jtLeft, jtRight);
        }

        return string.Equals(leftJ?.ToString(), rightJ?.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the Cosmos DB type rank for ordering:
    /// undefined(0) &lt; null(1) &lt; bool(2) &lt; number(3) &lt; string(4) &lt; array(5) &lt; object(6).
    /// </summary>
    public static int GetTypeRank(object? value)
    {
        if (ReferenceEquals(value, UndefinedSortSentinel)) return 0;
        if (value is UndefinedValue) return 0;
        if (value is null) return 1;
        if (value is bool) return 2;
        if (value is int or long or double or float or decimal) return 3;
        if (value is string) return 4;
        if (value is JToken jt)
        {
            return jt.Type switch
            {
                JTokenType.Undefined => 0,
                JTokenType.Null => 1,
                JTokenType.Boolean => 2,
                JTokenType.Integer or JTokenType.Float => 3,
                JTokenType.String => 4,
                JTokenType.Array => 5,
                JTokenType.Object => 6,
                _ => 7
            };
        }
        return 7;
    }

    public static int CompareValues(object? left, object? right)
    {
        var leftRank = GetTypeRank(left);
        var rightRank = GetTypeRank(right);
        if (leftRank != rightRank) return leftRank.CompareTo(rightRank);

        if (leftRank <= 1) return 0;

        if (left is bool lb && right is bool rb)
            return lb.CompareTo(rb);

        if (double.TryParse(left?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var l) &&
            double.TryParse(right?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r))
        {
            return l.CompareTo(r);
        }

        if (leftRank == 5)
        {
            var leftArr = left is JArray la ? la : (left is JToken lt ? new JArray(lt) : null);
            var rightArr = right is JArray ra ? ra : (right is JToken rt ? new JArray(rt) : null);
            if (leftArr is not null && rightArr is not null)
            {
                for (var i = 0; i < Math.Min(leftArr.Count, rightArr.Count); i++)
                {
                    var cmp = CompareValues(
                        JTokenToObject(leftArr[i]),
                        JTokenToObject(rightArr[i]));
                    if (cmp != 0) return cmp;
                }
                return leftArr.Count.CompareTo(rightArr.Count);
            }
        }

        if (leftRank == 6)
        {
            var leftObj = left as JObject ?? (left is JToken lk ? lk as JObject : null);
            var rightObj = right as JObject ?? (right is JToken rk ? rk as JObject : null);
            if (leftObj is not null && rightObj is not null)
            {
                var leftProps = leftObj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                var rightProps = rightObj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                for (var i = 0; i < Math.Min(leftProps.Count, rightProps.Count); i++)
                {
                    var nameCmp = string.Compare(leftProps[i].Name, rightProps[i].Name, StringComparison.Ordinal);
                    if (nameCmp != 0) return nameCmp;
                    var valCmp = CompareValues(
                        JTokenToObject(leftProps[i].Value),
                        JTokenToObject(rightProps[i].Value));
                    if (valCmp != 0) return valCmp;
                }
                return leftProps.Count.CompareTo(rightProps.Count);
            }
        }

        return string.Compare(left?.ToString(), right?.ToString(), StringComparison.Ordinal);
    }

    public static object? JTokenToObject(JToken token)
    {
        return token.Type switch
        {
            JTokenType.Integer => token.Value<long>(),
            JTokenType.Float => token.Value<double>(),
            JTokenType.Boolean => token.Value<bool>(),
            JTokenType.String => token.Value<string>(),
            JTokenType.Null => null,
            JTokenType.Undefined => UndefinedValue.Instance,
            _ => token
        };
    }

    /// <summary>
    /// Computes MIN or MAX across a list of <see cref="JToken"/> values, respecting Cosmos DB type ordering:
    /// boolean(0) &lt; number(1) &lt; string(2). Booleans compare as false &lt; true.
    /// Returns <see cref="UndefinedValue.Instance"/> when <paramref name="tokens"/> is empty.
    /// </summary>
    public static object? AggregateMinMax(List<JToken> tokens, bool isMin)
    {
        if (tokens.Count == 0)
            return UndefinedValue.Instance;

        static int MinMaxTypeRank(JToken t) => t.Type switch
        {
            JTokenType.Boolean => 0,
            JTokenType.Integer or JTokenType.Float => 1,
            JTokenType.String => 2,
            _ => 3
        };

        var eligible = tokens.Where(t => MinMaxTypeRank(t) <= 2).ToList();
        if (eligible.Count == 0)
            return UndefinedValue.Instance;

        JToken best = eligible[0];
        foreach (var t in eligible.Skip(1))
        {
            var rankBest = MinMaxTypeRank(best);
            var rankT = MinMaxTypeRank(t);
            bool tIsBetter;
            if (rankBest != rankT)
            {
                tIsBetter = isMin ? rankT < rankBest : rankT > rankBest;
            }
            else
            {
                tIsBetter = rankBest switch
                {
                    0 => isMin
                        ? !t.Value<bool>() && best.Value<bool>()
                        : t.Value<bool>() && !best.Value<bool>(),
                    1 => isMin
                        ? t.Value<double>() < best.Value<double>()
                        : t.Value<double>() > best.Value<double>(),
                    2 => isMin
                        ? string.Compare(t.Value<string>(), best.Value<string>(), StringComparison.Ordinal) < 0
                        : string.Compare(t.Value<string>(), best.Value<string>(), StringComparison.Ordinal) > 0,
                    _ => false
                };
            }
            if (tIsBetter) best = t;
        }

        return best.Type switch
        {
            JTokenType.Boolean => best.Value<bool>(),
            JTokenType.Integer => best.Value<long>(),
            JTokenType.Float => best.Value<double>(),
            JTokenType.String => best.Value<string>(),
            _ => UndefinedValue.Instance
        };
    }

    private static object? NormalizeToJTokenIfComplex(object? value)
    {
        if (value is null or string or bool or int or long or double or float or decimal or UndefinedValue)
            return value;
        try { return JToken.FromObject(value); }
        catch { return value; }
    }
}
