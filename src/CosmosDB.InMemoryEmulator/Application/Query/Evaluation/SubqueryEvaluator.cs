#nullable enable
using System.Net;
using CosmosDB.InMemoryEmulator.Domain.Comparison;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.Query.Evaluation;

/// <summary>
/// Evaluates <c>EXISTS(...)</c>, <c>ARRAY(...)</c>, and scalar correlated/non-correlated subqueries.
/// </summary>
internal static class SubqueryEvaluator
{
    public static bool EvaluateExists(
        ExistsCondition exists,
        JObject item,
        string fromAlias,
        QueryExecutionContext context,
        JoinClause? join)
    {
        try
        {
            var raw = exists.RawSubquery.Trim();
            var queryToParse = raw.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                ? raw
                : "SELECT * FROM " + raw;

            CosmosSqlQuery subquery;
            try
            {
                subquery = CosmosSqlParser.Parse(queryToParse);
            }
            catch (NotSupportedException)
            {
                throw InMemoryCosmosException.Create(
                    $"Syntax error in EXISTS subquery: {raw}",
                    HttpStatusCode.BadRequest, 0, string.Empty, 0);
            }

            if (subquery.FromSource is not null)
            {
                var sourcePath = subquery.FromSource;
                if (sourcePath.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                {
                    sourcePath = sourcePath[(fromAlias.Length + 1)..];
                }

                var arrayToken = item.SelectToken(sourcePath);
                if (arrayToken is not JArray jArray || jArray.Count == 0)
                {
                    return false;
                }

                if (subquery.Where is not null)
                {
                    var iterAlias = subquery.FromAlias;
                    foreach (var element in jArray)
                    {
                        var combined = new JObject(item.Properties());
                        combined[iterAlias] = element is JObject elementObj
                            ? elementObj.DeepClone()
                            : new JValue(element.ToObject<object>());
                        if (WhereExpressionEvaluator.Evaluate(subquery.Where, combined, iterAlias, context, null))
                        {
                            return true;
                        }
                    }
                    return false;
                }
                return true;
            }

            if (subquery.Join is not null)
            {
                var arrayPath = subquery.Join.ArrayField;
                var sourceAlias = subquery.Join.SourceAlias;
                if (sourceAlias.Equals(fromAlias, StringComparison.OrdinalIgnoreCase))
                {
                    var arrayToken = item.SelectToken(arrayPath);
                    if (arrayToken is not JArray jArray || jArray.Count == 0)
                    {
                        return false;
                    }

                    if (subquery.Where is not null)
                    {
                        foreach (var element in jArray)
                        {
                            if (element is JObject elementObj)
                            {
                                var combined = new JObject(item.Properties())
                                {
                                    [subquery.Join.Alias] = elementObj
                                };
                                if (WhereExpressionEvaluator.Evaluate(subquery.Where, combined, subquery.FromAlias, context, subquery.Join))
                                {
                                    return true;
                                }
                            }
                        }
                        return false;
                    }
                    return true;
                }
            }

            if (subquery.Where is not null)
            {
                return WhereExpressionEvaluator.Evaluate(subquery.Where, item, fromAlias, context, join);
            }

            return true;
        }
        catch (CosmosException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public static object? EvaluateSubquery(
        CosmosSqlQuery subquery,
        JObject item,
        string fromAlias,
        QueryExecutionContext context)
    {
        var results = ExecuteSubqueryAgainstItem(subquery, item, fromAlias, context);
        return results.Count > 0 ? JsonParseHelpers.ParseJsonToken(results[0]).ToObject<object>() : null;
    }

    public static object EvaluateArraySubquery(
        CosmosSqlQuery subquery,
        JObject item,
        string fromAlias,
        QueryExecutionContext context)
    {
        var results = ExecuteSubqueryAgainstItem(subquery, item, fromAlias, context);
        var jArray = new JArray();
        foreach (var resultJson in results)
        {
            jArray.Add(JsonParseHelpers.ParseJsonToken(resultJson));
        }
        return jArray;
    }

    private static List<string> ExecuteSubqueryAgainstItem(
        CosmosSqlQuery subquery,
        JObject item,
        string fromAlias,
        QueryExecutionContext context)
    {
        List<JObject> sourceItems;

        if (subquery.FromSource is not null)
        {
            var sourcePath = subquery.FromSource;
            if (sourcePath.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
            {
                sourcePath = sourcePath[(fromAlias.Length + 1)..];
            }

            var arrayToken = item.SelectToken(sourcePath);
            if (arrayToken is not JArray sourceArray)
            {
                return [];
            }

            sourceItems = [];
            foreach (var element in sourceArray)
            {
                var combined = new JObject(item.Properties())
                {
                    [subquery.FromAlias] = element
                };
                sourceItems.Add(combined);
            }
        }
        else if (subquery.Join is not null)
        {
            var arrayPath = subquery.Join.ArrayField;
            var sourceAlias = subquery.Join.SourceAlias;
            JToken? arrayToken;

            if (sourceAlias.Equals(fromAlias, StringComparison.OrdinalIgnoreCase) ||
                sourceAlias.Equals(subquery.FromAlias, StringComparison.OrdinalIgnoreCase))
            {
                arrayToken = item.SelectToken(arrayPath);
            }
            else
            {
                arrayToken = item.SelectToken($"{sourceAlias}.{arrayPath}");
            }

            if (arrayToken is not JArray jArray)
            {
                return [];
            }

            sourceItems = [];
            foreach (var element in jArray)
            {
                var combined = new JObject(item.Properties())
                {
                    [subquery.Join.Alias] = element
                };
                sourceItems.Add(combined);
            }
        }
        else
        {
            sourceItems = [item];
        }

        if (subquery.Where is not null)
        {
            sourceItems = sourceItems.Where(sourceItem =>
                WhereExpressionEvaluator.Evaluate(subquery.Where, sourceItem, subquery.FromAlias, context, subquery.Join)
            ).ToList();
        }

        var hasAggregateSelect = !subquery.IsSelectAll &&
            subquery.SelectFields.Any(static f => f.SqlExpr is not null && SqlExpressionEvaluator.ContainsAggregateCall(f.SqlExpr));

        var results = new List<string>();
        if (hasAggregateSelect)
        {
            var projected = new JObject();
            foreach (var field in subquery.SelectFields)
            {
                var outputName = field.Alias ?? field.Expression.Split('.').Last();
                if (field.SqlExpr is FunctionCallExpression func && SqlExpressionEvaluator.AggregateFunctions.Contains(func.FunctionName))
                {
                    var aggValue = EvaluateSubqueryAggregate(func, sourceItems, subquery.FromAlias, context);
                    if (aggValue is not null and not UndefinedValue)
                        projected[outputName] = JToken.FromObject(aggValue);
                }
                else if (field.SqlExpr is not null)
                {
                    if (sourceItems.Count > 0)
                    {
                        var value = SqlExpressionEvaluator.Evaluate(field.SqlExpr, sourceItems[0], subquery.FromAlias, context);
                        if (value is not null and not UndefinedValue)
                            projected[outputName] = JToken.FromObject(value);
                    }
                }
            }

            if (subquery.IsValueSelect)
            {
                var first = projected.Properties().FirstOrDefault();
                if (first?.Value is not null)
                {
                    results.Add(first.Value.Type == JTokenType.String
                        ? JsonConvert.SerializeObject(first.Value.Value<string>())
                        : first.Value.ToString(Formatting.None));
                }
            }
            else
            {
                results.Add(projected.ToString(Formatting.None));
            }
        }
        else
        {
            foreach (var sourceItem in sourceItems)
            {
                if (subquery.IsSelectAll)
                {
                    results.Add(sourceItem.ToString(Formatting.None));
                }
                else
                {
                    var projected = new JObject();
                    foreach (var field in subquery.SelectFields)
                    {
                        var outputName = field.Alias ?? field.Expression.Split('.').Last();
                        if (field.SqlExpr is not null and not IdentifierExpression)
                        {
                            var value = SqlExpressionEvaluator.Evaluate(field.SqlExpr, sourceItem, subquery.FromAlias, context);
                            if (value is not UndefinedValue)
                                projected[outputName] = value is not null ? JToken.FromObject(value) : JValue.CreateNull();
                        }
                        else
                        {
                            var path = field.Expression;
                            if (string.Equals(path, subquery.FromAlias, StringComparison.OrdinalIgnoreCase))
                            {
                                var aliasToken = sourceItem[subquery.FromAlias];
                                projected[outputName] = aliasToken is not null
                                    ? aliasToken.DeepClone()
                                    : sourceItem.DeepClone();
                                continue;
                            }
                            if (path.StartsWith(subquery.FromAlias + ".", StringComparison.OrdinalIgnoreCase))
                            {
                                path = path[(subquery.FromAlias.Length + 1)..];
                            }
                            var token = sourceItem.SelectToken(path);
                            outputName = field.Alias ?? path.Split('.').Last();
                            projected[outputName] = token?.DeepClone();
                        }
                    }

                    if (subquery.IsValueSelect)
                    {
                        var first = projected.Properties().FirstOrDefault();
                        if (first?.Value is not null)
                        {
                            results.Add(first.Value.Type == JTokenType.String
                                ? JsonConvert.SerializeObject(first.Value.Value<string>())
                                : first.Value.ToString(Formatting.None));
                        }
                    }
                    else
                    {
                        results.Add(projected.ToString(Formatting.None));
                    }
                }
            }
        }

        if (subquery.IsDistinct)
        {
            results = results.Distinct(JsonStructuralStringComparer.Instance).ToList()!;
        }

        if (subquery.OrderByFields is { Length: > 0 })
        {
            IOrderedEnumerable<string>? ordered = null;
            foreach (var field in subquery.OrderByFields)
            {
                var fieldPath = field.Field;
                if (fieldPath.StartsWith(subquery.FromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    fieldPath = fieldPath[(subquery.FromAlias.Length + 1)..];

                object? KeySelector(string json)
                {
                    var token = JsonParseHelpers.ParseJsonToken(json);
                    if (token is not JObject obj)
                    {
                        return token.Type switch
                        {
                            JTokenType.Integer => token.Value<long>(),
                            JTokenType.Float => token.Value<double>(),
                            _ => token.ToString()
                        };
                    }
                    var selected = obj.SelectToken(fieldPath);
                    if (selected is null) return null;
                    return selected.Type switch
                    {
                        JTokenType.Integer => selected.Value<long>(),
                        JTokenType.Float => selected.Value<double>(),
                        _ => selected.ToString()
                    };
                }

                var asc = field.Ascending;
                var comparer = Comparer<object?>.Create((l, r) =>
                {
                    var cmp = CosmosValueComparer.CompareValues(l, r);
                    return asc ? cmp : -cmp;
                });
                ordered = ordered == null ? results.OrderBy(KeySelector, comparer) : ordered.ThenBy(KeySelector, comparer);
            }
            results = ordered?.ToList() ?? results;
        }

        if (subquery.TopCount.HasValue)
        {
            results = results.Take(subquery.TopCount.Value).ToList();
        }

        if (subquery.Offset.HasValue)
        {
            results = results.Skip(subquery.Offset.Value).ToList();
        }

        if (subquery.Limit.HasValue)
        {
            results = results.Take(subquery.Limit.Value).ToList();
        }

        return results;
    }

    private static object? EvaluateSubqueryAggregate(
        FunctionCallExpression func,
        List<JObject> sourceItems,
        string fromAlias,
        QueryExecutionContext context)
    {
        var name = func.FunctionName.ToUpperInvariant();
        if (name is "COUNT")
        {
            if (func.Arguments.Length == 1 && func.Arguments[0] is IdentifierExpression ident)
            {
                var fieldPath = ident.Name;
                if (fieldPath.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    fieldPath = fieldPath[(fromAlias.Length + 1)..];
                return (double)sourceItems.Count(si => si.SelectToken(fieldPath) is not null);
            }
            return (double)sourceItems.Count;
        }

        if (name is "COUNTIF")
        {
            if (func.Arguments.Length < 1) return 0.0;
            var boolExpr = func.Arguments[0];
            return (double)sourceItems.Count(si =>
            {
                var val = SqlExpressionEvaluator.Evaluate(boolExpr, si, fromAlias, context);
                return CosmosValueComparer.IsTruthy(val);
            });
        }

        var argExpr = func.Arguments.Length > 0 ? func.Arguments[0] : null;
        if (argExpr is null) return null;

        var values = new List<JToken>();
        foreach (var si in sourceItems)
        {
            var val = SqlExpressionEvaluator.Evaluate(argExpr, si, fromAlias, context);
            if (val is not null and not UndefinedValue)
                values.Add(JToken.FromObject(val));
        }

        return name switch
        {
            "SUM" => values.Count > 0 ? JsonParseHelpers.NormalizeNumericResult(values.Sum(static v => v.Value<double>())) : UndefinedValue.Instance,
            "AVG" => values.Count > 0 ? JsonParseHelpers.NormalizeNumericResult(values.Average(static v => v.Value<double>())) : UndefinedValue.Instance,
            "MIN" => CosmosValueComparer.AggregateMinMax(values, true),
            "MAX" => CosmosValueComparer.AggregateMinMax(values, false),
            _ => null
        };
    }
}
