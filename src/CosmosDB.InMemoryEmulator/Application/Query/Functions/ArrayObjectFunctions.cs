#nullable enable
using CosmosDB.InMemoryEmulator.Domain.Comparison;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.Query.Functions;

/// <summary>
/// Evaluates Cosmos SQL array, set, and object-conversion functions (<c>ARRAY_CONTAINS</c>,
/// <c>ARRAY_LENGTH</c>, <c>ARRAY_SLICE</c>, <c>ARRAY_CONCAT</c>, <c>ARRAY_CONTAINS_ANY</c>,
/// <c>ARRAY_CONTAINS_ALL</c>, <c>SETINTERSECT</c>, <c>SETUNION</c>, <c>SETDIFFERENCE</c>,
/// <c>CHOOSE</c>, <c>OBJECTTOARRAY</c>, and <c>ARRAYTOOBJECT</c>).
/// </summary>
internal static class ArrayObjectFunctions
{
    public static bool TryEvaluate(
        FunctionCallExpression func,
        object?[] args,
        JObject item,
        string fromAlias,
        Func<SqlExpression, object?> evaluateExpr,
        out object? result)
    {
        switch (func.FunctionName)
        {
            case "ARRAY_CONTAINS":
                if (func.Arguments.Length < 2)
                {
                    result = false;
                    return true;
                }

                JArray? jArray = null;
                if (func.Arguments[0] is IdentifierExpression ident)
                {
                    var arrayPath = ident.Name;
                    if (arrayPath.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        arrayPath = arrayPath[(fromAlias.Length + 1)..];
                    }

                    jArray = item.SelectToken(arrayPath) as JArray;
                }
                else if (args[0] is JArray evalArr)
                {
                    jArray = evalArr;
                }

                if (jArray is null)
                {
                    result = false;
                    return true;
                }

                var partial = args.Length >= 3 && string.Equals(args[2]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                var searchValue = args[1];
                if (searchValue is JObject searchObj)
                {
                    result = ArrayContainsMatchJObject(jArray, searchObj, partial);
                    return true;
                }

                if (searchValue is null)
                {
                    result = jArray.Any(static t => t.Type == JTokenType.Null);
                    return true;
                }

                result = ArrayContainsMatch(jArray, searchValue.ToString() ?? string.Empty, partial);
                return true;

            case "ARRAY_LENGTH":
                if (func.Arguments.Length < 1)
                {
                    result = null;
                    return true;
                }

                if (func.Arguments[0] is IdentifierExpression lenIdent)
                {
                    var path = lenIdent.Name;
                    if (path.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        path = path[(fromAlias.Length + 1)..];
                    }

                    var token = item.SelectToken(path);
                    result = token is JArray arr ? (object)(long)arr.Count : UndefinedValue.Instance;
                    return true;
                }

                var evaluated = evaluateExpr(func.Arguments[0]);
                result = evaluated is JArray evalLenArr ? (object)(long)evalLenArr.Count : UndefinedValue.Instance;
                return true;

            case "ARRAY_SLICE":
                if (func.Arguments.Length < 2)
                {
                    result = null;
                    return true;
                }

                if (func.Arguments[0] is IdentifierExpression sliceIdent)
                {
                    var path = sliceIdent.Name;
                    if (path.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        path = path[(fromAlias.Length + 1)..];
                    }

                    if (item.SelectToken(path) is not JArray arr)
                    {
                        result = null;
                        return true;
                    }

                    var start = (int)(SqlMathHelpers.ToLong(args[1]) ?? 0);
                    if (start < 0) start = Math.Max(0, arr.Count + start);
                    var length = args.Length >= 3 ? (int)(SqlMathHelpers.ToLong(args[2]) ?? arr.Count) : arr.Count;
                    result = new JArray(arr.Skip(start).Take(length));
                    return true;
                }

                if (evaluateExpr(func.Arguments[0]) is JArray evalArr2)
                {
                    var start2 = (int)(SqlMathHelpers.ToLong(args[1]) ?? 0);
                    if (start2 < 0) start2 = Math.Max(0, evalArr2.Count + start2);
                    var length2 = args.Length >= 3 ? (int)(SqlMathHelpers.ToLong(args[2]) ?? evalArr2.Count) : evalArr2.Count;
                    result = new JArray(evalArr2.Skip(start2).Take(length2));
                    return true;
                }

                result = null;
                return true;

            case "ARRAY_CONCAT":
                var concatResult = new JArray();
                foreach (var argExpr in func.Arguments)
                {
                    var concatArr = ResolveJArray(argExpr, item, fromAlias, evaluateExpr);
                    if (concatArr is null)
                    {
                        result = UndefinedValue.Instance;
                        return true;
                    }

                    foreach (var el in concatArr)
                    {
                        concatResult.Add(el.DeepClone());
                    }
                }
                result = concatResult;
                return true;

            case "ARRAY_CONTAINS_ANY":
                if (func.Arguments.Length < 2)
                {
                    result = false;
                    return true;
                }

                var anySourceArray = ResolveJArray(func.Arguments[0], item, fromAlias, evaluateExpr);
                if (anySourceArray is null || anySourceArray.Count == 0)
                {
                    result = false;
                    return true;
                }

                var anySearchArray = ResolveJArray(func.Arguments[1], item, fromAlias, evaluateExpr);
                if (anySearchArray is null && func.Arguments.Length >= 2)
                {
                    anySearchArray = new JArray();
                    for (int i = 1; i < func.Arguments.Length; i++)
                    {
                        var val = evaluateExpr(func.Arguments[i]);
                        if (val is UndefinedValue) continue;
                        anySearchArray.Add(val is JToken jt ? jt : (val is null ? JValue.CreateNull() : JToken.FromObject(val)));
                    }
                }

                if (anySearchArray is null || anySearchArray.Count == 0)
                {
                    result = false;
                    return true;
                }

                var anySourceValues = new HashSet<JToken?>(anySourceArray, JTokenValueComparer.Instance);
                result = anySearchArray.Any(t => anySourceValues.Contains(t));
                return true;

            case "ARRAY_CONTAINS_ALL":
                if (func.Arguments.Length < 2)
                {
                    result = false;
                    return true;
                }

                var allSourceArray = ResolveJArray(func.Arguments[0], item, fromAlias, evaluateExpr);
                if (allSourceArray is null)
                {
                    result = false;
                    return true;
                }

                var allSearchArray = ResolveJArray(func.Arguments[1], item, fromAlias, evaluateExpr);
                if (allSearchArray is null && func.Arguments.Length >= 2)
                {
                    allSearchArray = new JArray();
                    for (int i = 1; i < func.Arguments.Length; i++)
                    {
                        var val = evaluateExpr(func.Arguments[i]);
                        if (val is UndefinedValue) continue;
                        allSearchArray.Add(val is JToken jt ? jt : (val is null ? JValue.CreateNull() : JToken.FromObject(val)));
                    }
                }

                if (allSearchArray is null || allSearchArray.Count == 0)
                {
                    result = true;
                    return true;
                }

                var allSourceValues = new HashSet<JToken?>(allSourceArray, JTokenValueComparer.Instance);
                result = allSearchArray.All(t => allSourceValues.Contains(t));
                return true;

            case "SETINTERSECT":
                if (func.Arguments.Length < 2)
                {
                    result = new JArray();
                    return true;
                }

                var interArr1 = ResolveJArray(func.Arguments[0], item, fromAlias, evaluateExpr);
                var interArr2 = ResolveJArray(func.Arguments[1], item, fromAlias, evaluateExpr);
                if (interArr1 is null || interArr2 is null)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }

                var interSet2 = new HashSet<JToken?>(interArr2, JTokenValueComparer.Instance);
                var interResult = new JArray();
                var interSeen = new HashSet<JToken?>(JTokenValueComparer.Instance);
                foreach (var element in interArr1)
                {
                    if (interSet2.Contains(element) && interSeen.Add(element))
                    {
                        interResult.Add(element.DeepClone());
                    }
                }
                result = interResult;
                return true;

            case "SETUNION":
                if (func.Arguments.Length < 2)
                {
                    result = new JArray();
                    return true;
                }

                var unionArr1 = ResolveJArray(func.Arguments[0], item, fromAlias, evaluateExpr);
                var unionArr2 = ResolveJArray(func.Arguments[1], item, fromAlias, evaluateExpr);
                if (unionArr1 is null || unionArr2 is null)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }

                var unionResult = new JArray();
                var unionSeen = new HashSet<JToken?>(JTokenValueComparer.Instance);
                foreach (var element in unionArr1)
                {
                    if (unionSeen.Add(element)) unionResult.Add(element.DeepClone());
                }
                foreach (var element in unionArr2)
                {
                    if (unionSeen.Add(element)) unionResult.Add(element.DeepClone());
                }
                result = unionResult;
                return true;

            case "SETDIFFERENCE":
                if (func.Arguments.Length < 2)
                {
                    result = new JArray();
                    return true;
                }

                var diffArr1 = ResolveJArray(func.Arguments[0], item, fromAlias, evaluateExpr);
                var diffArr2 = ResolveJArray(func.Arguments[1], item, fromAlias, evaluateExpr);
                if (diffArr1 is null || diffArr2 is null)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }

                var diffExclude = new HashSet<JToken?>(diffArr2, JTokenValueComparer.Instance);
                var diffResult = new JArray();
                var diffSeen = new HashSet<JToken?>(JTokenValueComparer.Instance);
                foreach (var element in diffArr1)
                {
                    if (!diffExclude.Contains(element) && diffSeen.Add(element))
                    {
                        diffResult.Add(element.DeepClone());
                    }
                }
                result = diffResult;
                return true;

            case "CHOOSE":
                if (args.Length < 2)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                var idx = SqlMathHelpers.ToLong(args[0]);
                if (!idx.HasValue || idx.Value < 1 || idx.Value >= args.Length)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                result = args[(int)idx.Value];
                return true;

            case "OBJECTTOARRAY":
                if (args.Length < 1) { result = null; return true; }
                JObject objToArr;
                if (args[0] is JObject jo) objToArr = jo;
                else if (args[0] is string s) { try { objToArr = JObject.Parse(s); } catch { result = UndefinedValue.Instance; return true; } }
                else { result = UndefinedValue.Instance; return true; }
                var otaResult = new JArray();
                foreach (var prop in objToArr.Properties())
                {
                    otaResult.Add(new JObject { ["k"] = prop.Name, ["v"] = prop.Value.DeepClone() });
                }
                result = otaResult;
                return true;

            case "ARRAYTOOBJECT":
                if (args.Length < 1) { result = UndefinedValue.Instance; return true; }
                JArray arrToObj;
                if (args[0] is JArray ja) arrToObj = ja;
                else if (args[0] is string s) { try { arrToObj = JArray.Parse(s); } catch { result = UndefinedValue.Instance; return true; } }
                else { result = UndefinedValue.Instance; return true; }
                var atoResult = new JObject();
                foreach (var element in arrToObj)
                {
                    if (element is JObject kvObj && kvObj["k"] != null && kvObj["v"] != null)
                    {
                        var keyName = kvObj["k"]!.Value<string>();
                        if (keyName is not null)
                        {
                            atoResult[keyName] = kvObj["v"]!.DeepClone();
                        }
                    }
                    else
                    {
                        result = UndefinedValue.Instance;
                        return true;
                    }
                }
                result = atoResult;
                return true;

            default:
                result = null;
                return false;
        }
    }

    public static bool ArrayContainsMatch(JArray jArray, string searchStr, bool partial)
    {
        foreach (var element in jArray)
        {
            if (element.Type == JTokenType.Object && searchStr.StartsWith("{", StringComparison.Ordinal))
            {
                var searchObj = JsonParseHelpers.ParseJson(searchStr);
                if (partial)
                {
                    var allMatch = searchObj.Properties().All(prop =>
                    {
                        var elementValue = element[prop.Name];
                        return elementValue is not null && JToken.DeepEquals(elementValue, prop.Value);
                    });
                    if (allMatch) return true;
                }
                else if (JToken.DeepEquals(element, searchObj))
                {
                    return true;
                }
            }
            else if (string.Equals(element.ToString(), searchStr, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public static bool ArrayContainsMatchJObject(JArray jArray, JObject searchObj, bool partial)
    {
        foreach (var element in jArray)
        {
            if (element.Type != JTokenType.Object)
            {
                continue;
            }

            if (partial)
            {
                var allMatch = searchObj.Properties().All(prop =>
                {
                    var elementValue = element[prop.Name];
                    return elementValue is not null && JToken.DeepEquals(elementValue, prop.Value);
                });
                if (allMatch) return true;
            }
            else if (JToken.DeepEquals(element, searchObj))
            {
                return true;
            }
        }
        return false;
    }

    private static JArray? ResolveJArray(
        SqlExpression argument,
        JObject item,
        string fromAlias,
        Func<SqlExpression, object?> evaluateExpr)
    {
        if (argument is IdentifierExpression ident)
        {
            var path = ident.Name;
            if (path.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
            {
                path = path[(fromAlias.Length + 1)..];
            }

            return item.SelectToken(path) as JArray;
        }

        return evaluateExpr(argument) as JArray;
    }
}
