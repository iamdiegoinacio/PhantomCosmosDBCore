#nullable enable
using Phantom.CosmosDB.Core.Application.Query.Evaluation;
using Phantom.CosmosDB.Core.Domain.Comparison;
using Phantom.CosmosDB.Core.Domain.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Application.Query.Stages;

/// <summary>
/// Query pipeline stage handling computed property augmentation/stripping, <c>SELECT</c> field
/// projection, aggregate projection, and <c>SELECT VALUE</c> unwrapping.
/// </summary>
internal static class SelectProjectionStage
{
    public static List<string> AugmentWithComputedProperties(
        IEnumerable<string> items,
        (string Name, string FromAlias, SqlExpression? Expr)[] cps,
        QueryExecutionContext context)
    {
        if (cps.Length == 0) return items.ToList();

        return items.Select(json =>
        {
            var jObj = JsonParseHelpers.ParseJson(json);
            foreach (var (name, fromAlias, expr) in cps)
            {
                if (expr is null) continue;
                var value = SqlExpressionEvaluator.Evaluate(expr, jObj, fromAlias, context);
                if (value is UndefinedValue)
                    continue;
                jObj[name] = value is not null ? JToken.FromObject(value) : JValue.CreateNull();
            }
            return jObj.ToString(Formatting.None);
        }).ToList();
    }

    public static List<string> StripComputedProperties(
        IEnumerable<string> items,
        (string Name, string FromAlias, SqlExpression? Expr)[] cps)
    {
        if (cps.Length == 0) return items.ToList();
        var names = new HashSet<string>(cps.Select(static cp => cp.Name));
        return items.Select(json =>
        {
            var jObj = JsonParseHelpers.ParseJson(json);
            foreach (var name in names)
                jObj.Remove(name);
            return jObj.ToString(Formatting.None);
        }).ToList();
    }

    public static List<string> ProjectFields(
        IEnumerable<string> items,
        CosmosSqlQuery parsed,
        QueryExecutionContext context)
    {
        var hasAggregate = parsed.SelectFields.Any(static f =>
        {
            var expr = f.Expression.TrimStart();
            if (SqlExpressionEvaluator.AggregateFunctions.Any(fn => expr.StartsWith(fn + "(", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (f.SqlExpr is not null)
                return SqlExpressionEvaluator.ContainsAggregateCall(f.SqlExpr);
            return false;
        });

        if (hasAggregate)
        {
            return ProjectAggregateFields(items, parsed, context);
        }

        var projected = new List<string>();
        foreach (var json in items)
        {
            var jObj = JsonParseHelpers.ParseJson(json);
            var resultObj = new JObject();
            foreach (var field in parsed.SelectFields)
            {
                if (field.Expression == "*")
                {
                    foreach (var prop in jObj.Properties())
                        resultObj[prop.Name] = prop.Value.DeepClone();
                    continue;
                }

                var outputName = field.Alias ?? field.Expression.Split('.').Last();

                if (field.SqlExpr is not null and not IdentifierExpression)
                {
                    var value = SqlExpressionEvaluator.Evaluate(field.SqlExpr, jObj, parsed.FromAlias, context);
                    if (value is not UndefinedValue)
                        resultObj[outputName] = value is not null ? JToken.FromObject(value) : JValue.CreateNull();
                }
                else
                {
                    var path = field.Expression;

                    if (string.Equals(path, parsed.FromAlias, StringComparison.OrdinalIgnoreCase))
                    {
                        var aliasToken = jObj[parsed.FromAlias];
                        if (parsed.FromSource is not null && aliasToken is not null)
                        {
                            resultObj[outputName] = aliasToken.DeepClone();
                        }
                        else
                        {
                            resultObj[outputName] = jObj.DeepClone();
                        }
                        continue;
                    }

                    if (path.StartsWith(parsed.FromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        path = path[(parsed.FromAlias.Length + 1)..];
                    }

                    var token = jObj.SelectToken(path);
                    outputName = field.Alias ?? path.Split('.').Last();
                    if (token is not null)
                        resultObj[outputName] = token.DeepClone();
                }
            }
            projected.Add(resultObj.ToString(Formatting.None));
        }
        return projected;
    }

    public static List<string> UnwrapValueSelect(IEnumerable<string> items)
    {
        var unwrapped = new List<string>();
        foreach (var json in items)
        {
            var jObj = JsonParseHelpers.ParseJson(json);
            if (!jObj.HasValues)
            {
                continue;
            }

            var first = jObj.Properties().FirstOrDefault();
            if (first?.Value is not null)
            {
                var val = first.Value;
                if (val.Type == JTokenType.String)
                {
                    unwrapped.Add(JsonConvert.SerializeObject(val.Value<string>()));
                }
                else
                {
                    unwrapped.Add(val.ToString(Formatting.None));
                }
            }
            else
            {
                unwrapped.Add("null");
            }
        }
        return unwrapped;
    }

    private static List<string> ProjectAggregateFields(
        IEnumerable<string> itemsEnumerable,
        CosmosSqlQuery parsed,
        QueryExecutionContext context)
    {
        var items = itemsEnumerable as List<string> ?? itemsEnumerable.ToList();
        var resultObj = new JObject();
        foreach (var field in parsed.SelectFields)
        {
            var expr = field.Expression.TrimStart();
            var outputName = field.Alias ?? field.Expression;

            if (field.SqlExpr is not null && SqlExpressionEvaluator.ContainsAggregateCall(field.SqlExpr)
                && field.SqlExpr is not FunctionCallExpression)
            {
                var val = AggregateEvaluator.EvaluateAggregateExpression(field.SqlExpr, items, parsed.FromAlias, context);
                if (val is not null and not UndefinedValue)
                    resultObj[outputName] = val is JToken jt ? jt.DeepClone() : JToken.FromObject(val);
                continue;
            }

            string? funcName = null;
            string? innerArg = null;

            foreach (var fn in SqlExpressionEvaluator.AggregateFunctions)
            {
                if (expr.StartsWith(fn + "(", StringComparison.OrdinalIgnoreCase))
                {
                    funcName = fn.ToUpperInvariant();
                    var open = expr.IndexOf('(');
                    var close = expr.LastIndexOf(')');
                    if (open >= 0 && close > open)
                    {
                        innerArg = expr.Substring(open + 1, close - open - 1).Trim();
                    }

                    break;
                }
            }

            if (funcName == "COUNT")
            {
                if (innerArg is "1" or "*" or null)
                {
                    resultObj[outputName] = items.Count;
                }
                else if (field.SqlExpr is FunctionCallExpression countFunc && countFunc.Arguments.Length > 0)
                {
                    resultObj[outputName] = AggregateEvaluator.CountDefinedResults(
                        countFunc.Arguments[0], items, parsed.FromAlias, context);
                }
                else
                {
                    var countPath = innerArg;
                    if (countPath.StartsWith(parsed.FromAlias + ".", StringComparison.OrdinalIgnoreCase))
                        countPath = countPath[(parsed.FromAlias.Length + 1)..];
                    resultObj[outputName] = items.Count(json =>
                    {
                        var jObj = JsonParseHelpers.ParseJson(json);
                        return jObj.SelectToken(countPath) is { Type: not JTokenType.Null };
                    });
                }
            }
            else if (funcName == "COUNTIF" && field.SqlExpr is FunctionCallExpression countIfFunc)
            {
                resultObj[outputName] = AggregateEvaluator.EvaluateCountIf(countIfFunc, items, parsed.FromAlias, context);
            }
            else if (funcName is "SUM" or "AVG" && innerArg != null)
            {
                var values = AggregateEvaluator.ExtractNumericValues(items, innerArg, parsed.FromAlias, context);
                if (funcName == "SUM")
                {
                    if (values.Count > 0)
                        resultObj[outputName] = JToken.FromObject(JsonParseHelpers.NormalizeNumericResult(values.Sum()));
                }
                else
                {
                    if (values.Count > 0)
                        resultObj[outputName] = JToken.FromObject(JsonParseHelpers.NormalizeNumericResult(values.Average()));
                }
            }
            else if (funcName is "MIN" or "MAX" && innerArg != null)
            {
                var tokens = AggregateEvaluator.ExtractTokenValues(items, innerArg, parsed.FromAlias, context);
                var minMaxResult = CosmosValueComparer.AggregateMinMax(tokens, funcName == "MIN");
                if (minMaxResult is not UndefinedValue && minMaxResult is not null)
                    resultObj[outputName] = JToken.FromObject(minMaxResult);
            }
            else if (field.SqlExpr is not null && SqlExpressionEvaluator.ContainsAggregateCall(field.SqlExpr))
            {
                var val = AggregateEvaluator.EvaluateAggregateExpression(field.SqlExpr, items, parsed.FromAlias, context);
                if (val is not null and not UndefinedValue)
                    resultObj[outputName] = val is JToken jt ? jt.DeepClone() : JToken.FromObject(val);
            }
            else if (field.SqlExpr is not null and not IdentifierExpression)
            {
                var jObj = items.Count > 0 ? JsonParseHelpers.ParseJson(items[0]) : new JObject();
                var val = SqlExpressionEvaluator.Evaluate(field.SqlExpr, jObj, parsed.FromAlias, context);
                if (val is not null and not UndefinedValue)
                    resultObj[outputName] = val is JToken jt ? jt.DeepClone() : JToken.FromObject(val);
                else if (val is null)
                    resultObj[outputName] = JValue.CreateNull();
            }
            else
            {
                var path = field.Expression;
                if (path.StartsWith(parsed.FromAlias + ".", StringComparison.OrdinalIgnoreCase))
                {
                    path = path[(parsed.FromAlias.Length + 1)..];
                }

                if (items.Count > 0)
                {
                    var jObj = JsonParseHelpers.ParseJson(items[0]);
                    resultObj[outputName] = jObj.SelectToken(path)?.DeepClone();
                }
            }
        }
        return [resultObj.ToString(Formatting.None)];
    }
}
