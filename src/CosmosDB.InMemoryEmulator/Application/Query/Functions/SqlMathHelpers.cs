#nullable enable
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CosmosDB.InMemoryEmulator.Domain.Primitives;

namespace CosmosDB.InMemoryEmulator.Application.Query.Functions;

/// <summary>
/// Shared mathematical, bitwise, regex, and string-matching helpers for Cosmos SQL expression evaluation.
/// </summary>
internal static class SqlMathHelpers
{
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex> RegexCache = new();

    public static object? ArithmeticOp(object? left, object? right, Func<double, double, double> op)
    {
        if (left is null or UndefinedValue || right is null or UndefinedValue)
        {
            return UndefinedValue.Instance;
        }

        if (double.TryParse(left.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var l) &&
            double.TryParse(right.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r))
        {
            var result = op(l, r);
            if (double.IsNaN(result) || double.IsInfinity(result))
                return UndefinedValue.Instance;

            if (left is long or int && right is long or int && result == Math.Floor(result)
                && result >= long.MinValue && result < 9.2233720368547758E+18)
            {
                return (long)result;
            }

            return result;
        }

        return null;
    }

    public static object BitwiseOp(object? left, object? right, Func<long, long, long> op)
    {
        if (left is null or UndefinedValue || right is null or UndefinedValue)
        {
            return UndefinedValue.Instance;
        }

        if (long.TryParse(left.ToString(), out var l) && long.TryParse(right.ToString(), out var r))
        {
            return op(l, r);
        }

        return UndefinedValue.Instance;
    }

    public static object BitwiseShiftOp(object? left, object? right, bool isLeft)
    {
        if (left is null or UndefinedValue || right is null or UndefinedValue)
        {
            return UndefinedValue.Instance;
        }

        if (long.TryParse(left.ToString(), out var l) && long.TryParse(right.ToString(), out var r))
        {
            if (r < 0 || r >= 64) return UndefinedValue.Instance;
            return isLeft ? l << (int)r : l >> (int)r;
        }

        return UndefinedValue.Instance;
    }

    public static object MathOp(object? value, Func<double, double> op)
    {
        if (value is null or UndefinedValue)
        {
            return UndefinedValue.Instance;
        }

        var isIntegerInput = value is long or int;

        if (double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
        {
            var result = op(n);
            if (double.IsNaN(result) || double.IsInfinity(result))
                return UndefinedValue.Instance;
            if (isIntegerInput && result == Math.Floor(result) && result is >= long.MinValue and <= long.MaxValue)
                return (long)result;
            return result;
        }

        return UndefinedValue.Instance;
    }

    public static long? ToLong(object? value) => value switch
    {
        long l => l,
        double d => (long)d,
        int i => i,
        _ when value != null && long.TryParse(value.ToString(), out var p) => p,
        _ => null
    };

    public static long FloorToUnit(DateTime dt, long ticksPerUnit)
        => dt.Ticks / ticksPerUnit;

    public static Regex GetOrCreateRegex(string pattern, RegexOptions options)
    {
        var key = (pattern, options);
        if (RegexCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var regex = new Regex(pattern, options | RegexOptions.Compiled);
        if (RegexCache.Count < ContainerConstants.RegexCacheMaxSize)
        {
            RegexCache.TryAdd(key, regex);
        }

        return regex;
    }

    public static object EvaluateLike(object? left, object? right, string? escapeChar = null)
    {
        if (left is UndefinedValue || right is UndefinedValue)
        {
            return UndefinedValue.Instance;
        }

        if (left is null || right is null)
        {
            return UndefinedValue.Instance;
        }

        if (left is not string leftStr)
        {
            return UndefinedValue.Instance;
        }

        var patternStr = right.ToString() ?? string.Empty;
        if (escapeChar is { Length: > 0 })
        {
            var esc = escapeChar[0];
            var sb = new StringBuilder();
            for (var i = 0; i < patternStr.Length; i++)
            {
                if (patternStr[i] == esc && i + 1 < patternStr.Length)
                {
                    sb.Append(Regex.Escape(patternStr[i + 1].ToString()));
                    i++;
                }
                else if (patternStr[i] == '%')
                    sb.Append(".*");
                else if (patternStr[i] == '_')
                    sb.Append('.');
                else
                    sb.Append(Regex.Escape(patternStr[i].ToString()));
            }
            var pattern = $"^{sb}$";
            return GetOrCreateRegex(pattern, RegexOptions.Singleline).IsMatch(leftStr);
        }

        var simplePattern = ConvertLikeToRegex(patternStr);
        return GetOrCreateRegex(simplePattern, RegexOptions.Singleline).IsMatch(leftStr);
    }

    public static int CountOccurrences(string text, string term)
    {
        if (term.Length == 0) return 0;
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += term.Length;
        }
        return count;
    }

    private static string ConvertLikeToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var ch in pattern)
        {
            if (ch == '%') sb.Append(".*");
            else if (ch == '_') sb.Append('.');
            else sb.Append(Regex.Escape(ch.ToString()));
        }
        sb.Append('$');
        return sb.ToString();
    }
}
