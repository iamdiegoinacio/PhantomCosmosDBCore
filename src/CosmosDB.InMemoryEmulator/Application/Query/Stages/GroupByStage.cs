#nullable enable
using CosmosDB.InMemoryEmulator.Application.Query.Evaluation;
using CosmosDB.InMemoryEmulator.Domain.Comparison;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.Query.Stages;

/// <summary>
/// Query pipeline stage that executes <c>GROUP BY</c>, aggregate projections, and <c>HAVING</c> filtering.
/// </summary>
internal static class GroupByStage
{
    public static List<string> ApplyGroupBy(
        IEnumerable<string> items,
        CosmosSqlQuery parsed,
        QueryExecutionContext context)
    {
        var fromAlias = parsed.FromAlias;

        var groups = items.GroupBy(json =>
        {
            var jObj = JsonParseHelpers.ParseJson(json);
            var keyParts = new List<string>();
            var groupByExprs = parsed.GroupByExpressions;
            var groupByFields = parsed.GroupByFields ?? [];
            for (var i = 0; i < groupByFields.Length; i++)
            {
                if (groupByExprs != null && i < groupByExprs.Length
                    && groupByExprs[i] is not IdentifierExpression
                    && groupByExprs[i] is not null)
                {
                    var val = SqlExpressionEvaluator.Evaluate(groupByExprs[i], jObj, fromAlias, context);
                    keyParts.Add(val is UndefinedValue ? "\x00undefined" : val?.ToString() ?? "null");
                }
                else
                {
                    var path = SqlExpressionEvaluator.StripAliasPrefix(groupByFields[i], fromAlias);
                    var token = jObj.SelectToken(path);
                    if (token is null)
                        keyParts.Add("\x00undefined");
                    else if (token.Type == JTokenType.Null)
                        keyParts.Add("\x00null");
                    else
                        keyParts.Add(token.ToString());
                }
            }
            return string.Join("\x1F", keyParts.Select(static k => k.Replace("\x1F", "\x1F\x1F")));
        });

        var hasAggregate = parsed.SelectFields.Any(static f =>
        {
            var expr = f.Expression.TrimStart();
            if (SqlExpressionEvaluator.AggregateFunctions.Any(fn => expr.StartsWith(fn + "(", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (f.SqlExpr is not null)
                return SqlExpressionEvaluator.ContainsAggregateCall(f.SqlExpr);
            return false;
        });

        if (!hasAggregate)
        {
            return groups.Select(g =>
            {
                var jObj = JsonParseHelpers.ParseJson(g.First());
                var projected = new JObject();
                foreach (var field in parsed.SelectFields)
                {
                    var outputName = field.Alias ?? field.Expression.Split('.').Last();

                    if (field.SqlExpr is not null and not IdentifierExpression)
                    {
                        var val = SqlExpressionEvaluator.Evaluate(field.SqlExpr, jObj, fromAlias, context);
                        projected[outputName] = val is not null ? JToken.FromObject(val) : JValue.CreateNull();
                    }
                    else
                    {
                        var path = SqlExpressionEvaluator.StripAliasPrefix(field.Expression, fromAlias);
                        projected[outputName] = jObj.SelectToken(path)?.DeepClone();
                    }
                }
                return projected.ToString(Formatting.None);
            }).ToList();
        }

        var result = new List<string>();
        foreach (var group in groups)
        {
            var groupItems = group.ToList();
            var resultObj = new JObject();

            foreach (var field in parsed.SelectFields)
            {
                var expr = field.Expression.TrimStart();
                string? funcName = null;
                string? innerArg = null;

                foreach (var fn in SqlExpressionEvaluator.AggregateFunctions)
                {
                    if (expr.StartsWith(fn + "(", StringComparison.OrdinalIgnoreCase))
                    {
                        funcName = fn;
                        var open = expr.IndexOf('(');
                        var close = expr.LastIndexOf(')');
                        if (open >= 0 && close > open)
                        {
                            innerArg = expr.Substring(open + 1, close - open - 1).Trim();
                        }

                        break;
                    }
                }

                var outputName = field.Alias ?? field.Expression;

                if (funcName == "COUNT")
                {
                    if (innerArg is "1" or "*" or null)
                    {
                        resultObj[outputName] = groupItems.Count;
                    }
                    else
                    {
                        var countPath = innerArg;
                        if (countPath.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                            countPath = countPath[(fromAlias.Length + 1)..];
                        resultObj[outputName] = groupItems.Count(json =>
                        {
                            var jObj = JsonParseHelpers.ParseJson(json);
                            return jObj.SelectToken(countPath) is { Type: not JTokenType.Null };
                        });
                    }
                }
                else if (funcName == "COUNTIF" && field.SqlExpr is FunctionCallExpression countIfFunc)
                {
                    resultObj[outputName] = AggregateEvaluator.EvaluateCountIf(countIfFunc, groupItems, fromAlias, context);
                }
                else if (funcName is "SUM" or "AVG" && innerArg != null)
                {
                    var values = AggregateEvaluator.ExtractNumericValues(groupItems, innerArg, fromAlias, context);
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
                    var tokens = AggregateEvaluator.ExtractTokenValues(groupItems, innerArg, fromAlias, context);
                    var minMaxResult = CosmosValueComparer.AggregateMinMax(tokens, funcName == "MIN");
                    if (minMaxResult is not UndefinedValue && minMaxResult is not null)
                        resultObj[outputName] = JToken.FromObject(minMaxResult);
                }
                else
                {
                    var jObj = JsonParseHelpers.ParseJson(groupItems[0]);

                    if (field.SqlExpr is not null and not IdentifierExpression && funcName is null)
                    {
                        var fieldOutputName = field.Alias ?? field.Expression;
                        var val = SqlExpressionEvaluator.ContainsAggregateCall(field.SqlExpr)
                            ? AggregateEvaluator.EvaluateGroupByProjectionExpression(field.SqlExpr, groupItems, jObj, fromAlias, context)
                            : SqlExpressionEvaluator.Evaluate(field.SqlExpr, jObj, fromAlias, context);
                        resultObj[fieldOutputName] = val is not null ? JToken.FromObject(val) : JValue.CreateNull();
                    }
                    else
                    {
                        var path = SqlExpressionEvaluator.StripAliasPrefix(field.Expression, fromAlias);
                        var fieldOutputName = field.Alias ?? path.Split('.').Last();
                        resultObj[fieldOutputName] = jObj.SelectToken(path)?.DeepClone();
                    }
                }
            }

            if (parsed.Having is not null)
            {
                if (!AggregateEvaluator.EvaluateHavingCondition(parsed.Having, groupItems, resultObj, fromAlias, context))
                {
                    continue;
                }
            }

            result.Add(resultObj.ToString(Formatting.None));
        }

        return result;
    }
}
