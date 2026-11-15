#nullable enable
using System.Globalization;
using System.Text.RegularExpressions;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.Query.Functions;

/// <summary>
/// Evaluates Cosmos SQL string and type-conversion functions (<c>STARTSWITH</c>, <c>ENDSWITH</c>,
/// <c>CONTAINS</c>, <c>CONCAT</c>, <c>LENGTH</c>, <c>LOWER</c>, <c>UPPER</c>, <c>TRIM</c>,
/// <c>SUBSTRING</c>, <c>REPLACE</c>, <c>INDEX_OF</c>, <c>REGEXMATCH</c>, <c>STRINGTO*</c>, <c>TO*</c>, etc.).
/// </summary>
internal static class StringFunctions
{
    public static bool TryEvaluate(string functionName, object?[] args, out object? result)
    {
        switch (functionName)
        {
            case "STARTSWITH":
                if (args.Length < 2) { result = false; return true; }
                if (args[0] is not string swStr || args[1] is not string swPrefix) { result = UndefinedValue.Instance; return true; }
                var swIc = args.Length >= 3 && string.Equals(args[2]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                result = swStr.StartsWith(swPrefix, swIc ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                return true;

            case "ENDSWITH":
                if (args.Length < 2) { result = false; return true; }
                if (args[0] is not string ewStr || args[1] is not string ewSuffix) { result = UndefinedValue.Instance; return true; }
                var ewIc = args.Length >= 3 && string.Equals(args[2]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                result = ewStr.EndsWith(ewSuffix, ewIc ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                return true;

            case "CONTAINS":
                if (args.Length < 2) { result = false; return true; }
                if (args[0] is not string ctStr || args[1] is not string ctSub) { result = UndefinedValue.Instance; return true; }
                var ctIc = args.Length >= 3 && string.Equals(args[2]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                result = ctStr.Contains(ctSub, ctIc ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                return true;

            case "CONCAT":
                if (args.Any(static a => a is null or UndefinedValue or not string))
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                result = string.Concat(args.Cast<string>());
                return true;

            case "LENGTH":
                if (args.Length == 0) { result = null; return true; }
                result = args[0] is string lenStr ? (object)(long)lenStr.Length : UndefinedValue.Instance;
                return true;

            case "LOWER":
                result = args.Length > 0 ? (args[0] is string sl ? (object)sl.ToLowerInvariant() : UndefinedValue.Instance) : null;
                return true;

            case "UPPER":
                result = args.Length > 0 ? (args[0] is string su ? (object)su.ToUpperInvariant() : UndefinedValue.Instance) : null;
                return true;

            case "TRIM":
                result = args.Length > 0 ? (args[0] is string st ? (object)st.Trim() : UndefinedValue.Instance) : null;
                return true;

            case "LTRIM":
                result = args.Length > 0 ? (args[0] is string slt ? (object)slt.TrimStart() : UndefinedValue.Instance) : null;
                return true;

            case "RTRIM":
                result = args.Length > 0 ? (args[0] is string srt ? (object)srt.TrimEnd() : UndefinedValue.Instance) : null;
                return true;

            case "REVERSE":
                result = args.Length > 0 ? (args[0] is string rs ? (object)new string(rs.Reverse().ToArray()) : UndefinedValue.Instance) : null;
                return true;

            case "LEFT":
                if (args.Length < 2) { result = null; return true; }
                if (args[0] is not string leftStr) { result = UndefinedValue.Instance; return true; }
                var leftCount = SqlMathHelpers.ToLong(args[1]);
                if (!leftCount.HasValue || leftCount.Value < 0) { result = UndefinedValue.Instance; return true; }
                result = leftStr[..(int)Math.Min(leftCount.Value, leftStr.Length)];
                return true;

            case "RIGHT":
                if (args.Length < 2) { result = null; return true; }
                if (args[0] is not string rightStr) { result = UndefinedValue.Instance; return true; }
                var rightCount = SqlMathHelpers.ToLong(args[1]);
                if (!rightCount.HasValue || rightCount.Value < 0) { result = UndefinedValue.Instance; return true; }
                result = rightStr[Math.Max(0, rightStr.Length - (int)rightCount.Value)..];
                return true;

            case "SUBSTRING":
                if (args.Length < 3 || args[0] is not string subStr)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                var subStart = SqlMathHelpers.ToLong(args[1]);
                var subLen = SqlMathHelpers.ToLong(args[2]);
                if (!subStart.HasValue || !subLen.HasValue)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                var si = (int)Math.Max(0, Math.Min(subStart.Value, subStr.Length));
                var li = (int)Math.Max(0, Math.Min(subLen.Value, subStr.Length - si));
                result = subStr.Substring(si, li);
                return true;

            case "REPLACE":
                if (args.Length < 3) { result = null; return true; }
                var repSource = args[0] as string;
                var repFind = args[1] as string;
                var repWith = args[2] as string;
                if (repSource is null || repFind is null || args[2] is not (null or UndefinedValue or string))
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                result = repFind.Length == 0 ? repSource : repSource.Replace(repFind, repWith ?? "");
                return true;

            case "INDEX_OF":
                if (args.Length < 2 || args[0] is not string idxStr || args[1] is not string idxSub)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                if (args.Length >= 3 && double.TryParse(args[2]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var startPos))
                {
                    var sp = (int)startPos;
                    if (sp < 0 || sp > idxStr.Length)
                    {
                        result = UndefinedValue.Instance;
                        return true;
                    }
                    result = (object)(long)idxStr.IndexOf(idxSub, sp, StringComparison.Ordinal);
                    return true;
                }
                result = (object)(long)idxStr.IndexOf(idxSub, StringComparison.Ordinal);
                return true;

            case "REGEXMATCH":
                if (args.Length < 2) { result = false; return true; }
                if (args[0] is not string rgxInput || args[1] is not string rgxPattern)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                var options = RegexOptions.None;
                if (args.Length >= 3)
                {
                    var modifiers = args[2]?.ToString() ?? "";
                    foreach (var ch in modifiers)
                    {
                        options |= ch switch
                        {
                            'i' => RegexOptions.IgnoreCase,
                            'm' => RegexOptions.Multiline,
                            's' => RegexOptions.Singleline,
                            'x' => RegexOptions.IgnorePatternWhitespace,
                            _ => RegexOptions.None,
                        };
                    }
                }
                try
                {
                    result = SqlMathHelpers.GetOrCreateRegex(rgxPattern, options).IsMatch(rgxInput);
                }
                catch (ArgumentException)
                {
                    result = UndefinedValue.Instance;
                }
                return true;

            case "REPLICATE":
                if (args.Length < 2) { result = null; return true; }
                if (args[0] is not string repStr) { result = UndefinedValue.Instance; return true; }
                var count = SqlMathHelpers.ToLong(args[1]);
                if (!count.HasValue || count.Value < 0 || count.Value > 10000)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                result = count.Value == 0 ? "" : string.Concat(Enumerable.Repeat(repStr, (int)count.Value));
                return true;

            case "STRING_EQUALS":
            case "STRINGEQUALS":
                if (args.Length < 2 || args[0] is not string se1 || args[1] is not string se2)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                var seIc = args.Length >= 3 && string.Equals(args[2]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                result = string.Equals(se1, se2, seIc ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                return true;

            case "STRINGTOARRAY":
                if (args.Length == 0) { result = null; return true; }
                if (args[0] is null or UndefinedValue) { result = UndefinedValue.Instance; return true; }
                var staStr = args[0]?.ToString();
                if (staStr is null) { result = UndefinedValue.Instance; return true; }
                try
                {
                    var token = JToken.Parse(staStr);
                    result = token is JArray ? token : UndefinedValue.Instance;
                }
                catch
                {
                    result = UndefinedValue.Instance;
                }
                return true;

            case "STRINGTOBOOLEAN":
                if (args.Length == 0) { result = null; return true; }
                if (args[0] is null or UndefinedValue) { result = UndefinedValue.Instance; return true; }
                result = args[0]?.ToString()?.Trim() switch
                {
                    "true" => true,
                    "false" => (object)false,
                    _ => UndefinedValue.Instance,
                };
                return true;

            case "STRINGTONULL":
                if (args.Length == 0) { result = null; return true; }
                if (args[0] is null or UndefinedValue) { result = UndefinedValue.Instance; return true; }
                result = args[0]?.ToString()?.Trim() == "null" ? null : UndefinedValue.Instance;
                return true;

            case "STRINGTONUMBER":
                if (args.Length == 0) { result = null; return true; }
                if (args[0] is null or UndefinedValue) { result = UndefinedValue.Instance; return true; }
                var stnStr = args[0]?.ToString()?.Trim();
                if (stnStr is null) { result = UndefinedValue.Instance; return true; }
                if (long.TryParse(stnStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var longVal))
                {
                    result = longVal;
                    return true;
                }
                if (double.TryParse(stnStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var doubleVal))
                {
                    result = double.IsNaN(doubleVal) || double.IsInfinity(doubleVal) ? UndefinedValue.Instance : doubleVal;
                    return true;
                }
                result = UndefinedValue.Instance;
                return true;

            case "STRINGTOOBJECT":
                if (args.Length == 0) { result = null; return true; }
                if (args[0] is null or UndefinedValue) { result = UndefinedValue.Instance; return true; }
                var stoStr = args[0]?.ToString();
                if (stoStr is null) { result = UndefinedValue.Instance; return true; }
                try
                {
                    var token = JToken.Parse(stoStr);
                    result = token is JObject ? token : UndefinedValue.Instance;
                }
                catch
                {
                    result = UndefinedValue.Instance;
                }
                return true;

            case "TOSTRING":
            case "ToString":
                if (args.Length == 0) { result = null; return true; }
                if (args[0] is null or UndefinedValue) { result = UndefinedValue.Instance; return true; }
                if (args[0] is bool boolVal) { result = boolVal ? "true" : "false"; return true; }
                if (args[0] is string or long or int or double or float or decimal) { result = args[0]?.ToString(); return true; }
                if (args[0] is JArray or JObject) { result = ((JToken)args[0]!).ToString(Formatting.None); return true; }
                if (args[0] is JValue jv && jv.Type == JTokenType.Null) { result = UndefinedValue.Instance; return true; }
                result = args[0]?.ToString();
                return true;

            case "TONUMBER":
            case "ToNumber":
                if (args.Length == 0) { result = null; return true; }
                if (args[0] is null) { result = UndefinedValue.Instance; return true; }
                if (args[0] is long or double) { result = args[0]; return true; }
                if (double.TryParse(args[0]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var numResult))
                {
                    result = numResult;
                    return true;
                }
                result = UndefinedValue.Instance;
                return true;

            case "TOBOOLEAN":
            case "ToBoolean":
                if (args.Length == 0) { result = null; return true; }
                if (args[0] is bool) { result = args[0]; return true; }
                if (args[0] != null && bool.TryParse(args[0]?.ToString(), out var boolRes))
                {
                    result = boolRes;
                    return true;
                }
                result = UndefinedValue.Instance;
                return true;

            case "STRINGJOIN":
                if (args.Length < 2) { result = null; return true; }
                var separator = args[1]?.ToString();
                if (separator is null) { result = null; return true; }
                JArray joinArr;
                if (args[0] is JArray ja) joinArr = ja;
                else if (args[0] is string s) { try { joinArr = JArray.Parse(s); } catch { result = null; return true; } }
                else { result = null; return true; }
                result = string.Join(separator, joinArr.Select(static t => t.Value<string>()));
                return true;

            case "STRINGSPLIT":
                if (args.Length < 2) { result = null; return true; }
                var splitInput = args[0]?.ToString();
                var delimiter = args[1]?.ToString();
                if (splitInput is null || delimiter is null || delimiter.Length == 0)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                var parts = splitInput.Split(delimiter);
                result = new JArray(parts.Select(static p => (JToken)p));
                return true;

            default:
                result = null;
                return false;
        }
    }
}
