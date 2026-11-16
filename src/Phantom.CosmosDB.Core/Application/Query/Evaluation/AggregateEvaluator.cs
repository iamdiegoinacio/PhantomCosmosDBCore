#nullable enable
using System.Globalization;
using Phantom.CosmosDB.Core.Application.Query.Functions;
using Phantom.CosmosDB.Core.Domain.Comparison;
using Phantom.CosmosDB.Core.Domain.Primitives;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Application.Query.Evaluation;

/// <summary>
/// Unified evaluator for Cosmos SQL aggregate expressions (<c>COUNT</c>, <c>COUNTIF</c>,
/// <c>SUM</c>, <c>AVG</c>, <c>MIN</c>, <c>MAX</c>) across <c>SELECT</c>, <c>GROUP BY</c>, and <c>HAVING</c>.
/// </summary>
internal static class AggregateEvaluator
{
    public static bool IsComplexExpression(string arg)
        => arg.Contains('(') || arg.Contains('*') || arg.Contains('+') ||
           arg.Contains('/') || arg.Contains('%') ||
           (arg.Contains('-') && !arg.StartsWith("-", StringComparison.Ordinal) && arg.IndexOf('-') != arg.IndexOf('.', StringComparison.Ordinal) + 1);

    public static int CountDefinedResults(
        SqlExpression innerExpr,
        List<string> items,
        string fromAlias,
        QueryExecutionContext context)
    {
        var count = 0;
        foreach (var json in items)
        {
            var jObj = JsonParseHelpers.ParseJson(json);
            var result = SqlExpressionEvaluator.Evaluate(innerExpr, jObj, fromAlias, context);
            if (result is null or UndefinedValue)
                continue;
            if (result is JToken jt && jt.Type == JTokenType.Null)
                continue;
            count++;
        }
        return count;
    }

    public static int EvaluateCountIf(
        FunctionCallExpression func,
        List<string> items,
        string fromAlias,
        QueryExecutionContext context)
    {
        if (func.Arguments.Length < 1) return 0;
        var boolExpr = func.Arguments[0];
        return items.Count(json =>
        {
            var jObj = JsonParseHelpers.ParseJson(json);
            var val = SqlExpressionEvaluator.Evaluate(boolExpr, jObj, fromAlias, context);
            return CosmosValueComparer.IsTruthy(val);
        });
    }

    public static List<double> ExtractNumericValues(
        IEnumerable<string> items,
        string innerArg,
        string fromAlias,
        QueryExecutionContext context)
    {
        var values = new List<double>();

        if (double.TryParse(innerArg, NumberStyles.Any, CultureInfo.InvariantCulture, out var literalValue))
        {
            values.AddRange(Enumerable.Repeat(literalValue, items.Count()));
            return values;
        }

        var isExpr = IsComplexExpression(innerArg);
        SqlExpression? parsedInnerExpr = null;
        if (isExpr)
        {
            CosmosSqlParser.TryParse($"SELECT VALUE {innerArg} FROM {fromAlias}", out var innerParsed);
            if (innerParsed?.SelectFields.Length > 0)
                parsedInnerExpr = innerParsed.SelectFields[0].SqlExpr;
        }

        foreach (var json in items)
        {
            var jObj = JsonParseHelpers.ParseJson(json);
            double? val = null;

            if (parsedInnerExpr is not null)
            {
                var result = SqlExpressionEvaluator.Evaluate(parsedInnerExpr, jObj, fromAlias, context);
                if (result is double d)
                    val = d;
                else if (result is long l)
                    val = l;
                else if (result != null && double.TryParse(result.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed2))
                    val = parsed2;
            }
            else
            {
                var path = SqlExpressionEvaluator.StripAliasPrefix(innerArg, fromAlias);
                var token = jObj.SelectToken(path);
                if (token != null && double.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed3))
                    val = parsed3;
            }

            if (val.HasValue)
                values.Add(val.Value);
        }

        return values;
    }

    public static List<JToken> ExtractTokenValues(
        IEnumerable<string> items,
        string innerArg,
        string fromAlias,
        QueryExecutionContext context)
    {
        var tokens = new List<JToken>();

        if (double.TryParse(innerArg, NumberStyles.Any, CultureInfo.InvariantCulture, out var literalValue))
        {
            var token = new JValue(literalValue);
            tokens.AddRange(Enumerable.Repeat((JToken)token, items.Count()));
            return tokens;
        }

        var isExpr = IsComplexExpression(innerArg);
        SqlExpression? parsedInnerExpr = null;
        if (isExpr)
        {
            CosmosSqlParser.TryParse($"SELECT VALUE {innerArg} FROM {fromAlias}", out var innerParsed);
            if (innerParsed?.SelectFields.Length > 0)
                parsedInnerExpr = innerParsed.SelectFields[0].SqlExpr;
        }

        foreach (var json in items)
        {
            var jObj = JsonParseHelpers.ParseJson(json);

            if (parsedInnerExpr is not null)
            {
                var result = SqlExpressionEvaluator.Evaluate(parsedInnerExpr, jObj, fromAlias, context);
                if (result is not null and not UndefinedValue)
                    tokens.Add(JToken.FromObject(result));
            }
            else
            {
                var path = SqlExpressionEvaluator.StripAliasPrefix(innerArg, fromAlias);
                var token = jObj.SelectToken(path);
                if (token != null && token.Type != JTokenType.Null)
                    tokens.Add(token);
            }
        }

        return tokens;
    }

    public static bool EvaluateHavingCondition(
        WhereExpression having,
        List<string> groupItems,
        JObject resultObj,
        string fromAlias,
        QueryExecutionContext context)
    {
        if (having is SqlExpressionCondition sec)
        {
            return CosmosValueComparer.IsTruthy(
                EvaluateHavingSqlExpression(sec.Expression, groupItems, resultObj, fromAlias, context));
        }

        return WhereExpressionEvaluator.Evaluate(having, resultObj, fromAlias, context, null);
    }

    public static object? EvaluateHavingAggregate(
        FunctionCallExpression func,
        List<string> groupItems,
        string fromAlias,
        QueryExecutionContext context)
    {
        switch (func.FunctionName)
        {
            case "COUNT":
            {
                if (func.Arguments.Length < 1)
                    return (double)groupItems.Count;
                var countArg = func.Arguments[0] is IdentifierExpression countIdent ? countIdent.Name : "1";
                if (countArg is "1" or "*")
                    return (double)groupItems.Count;
                var countPath = countArg;
                if (countPath.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    countPath = countPath[(fromAlias.Length + 1)..];
                return (double)groupItems.Count(json =>
                {
                    var jObj = JsonParseHelpers.ParseJson(json);
                    return jObj.SelectToken(countPath) is { Type: not JTokenType.Null };
                });
            }
            case "COUNTIF":
                return (double)EvaluateCountIf(func, groupItems, fromAlias, context);
            case "SUM":
            case "AVG":
            case "MIN":
            case "MAX":
            {
                if (func.Arguments.Length < 1)
                {
                    return 0.0;
                }

                var innerArg = func.Arguments[0] is IdentifierExpression ident ? ident.Name : "1";
                if (func.FunctionName is "MIN" or "MAX")
                {
                    var tokens = ExtractTokenValues(groupItems, innerArg, fromAlias, context);
                    return CosmosValueComparer.AggregateMinMax(tokens, func.FunctionName == "MIN");
                }

                var values = ExtractNumericValues(groupItems, innerArg, fromAlias, context);
                return func.FunctionName switch
                {
                    "SUM" => values.Count > 0 ? JsonParseHelpers.NormalizeNumericResult(values.Sum()) : UndefinedValue.Instance,
                    "AVG" => values.Count > 0 ? JsonParseHelpers.NormalizeNumericResult(values.Average()) : UndefinedValue.Instance,
                    _ => 0.0
                };
            }
            default:
                return null;
        }
    }

    public static object? EvaluateGroupByProjectionExpression(
        SqlExpression expr,
        List<string> groupItems,
        JObject sampleItem,
        string fromAlias,
        QueryExecutionContext context)
    {
        return expr switch
        {
            FunctionCallExpression func when SqlExpressionEvaluator.AggregateFunctions.Contains(func.FunctionName)
                => EvaluateHavingAggregate(func, groupItems, fromAlias, context),
            ObjectLiteralExpression obj => EvaluateGroupByObjectLiteral(obj, groupItems, sampleItem, fromAlias, context),
            ArrayLiteralExpression arr => arr.Elements
                .Select(e => EvaluateGroupByProjectionExpression(e, groupItems, sampleItem, fromAlias, context))
                .Where(static v => v is not UndefinedValue)
                .Select(static v => v is not null ? JToken.FromObject(v) : JValue.CreateNull())
                .ToArray(),
            _ => SqlExpressionEvaluator.Evaluate(expr, sampleItem, fromAlias, context)
        };
    }

    public static object? EvaluateAggregateExpression(
        SqlExpression expr,
        IEnumerable<string> itemsEnumerable,
        string fromAlias,
        QueryExecutionContext context)
    {
        var items = itemsEnumerable as List<string> ?? itemsEnumerable.ToList();
        switch (expr)
        {
            case FunctionCallExpression func when SqlExpressionEvaluator.AggregateFunctions.Contains(func.FunctionName):
            {
                var innerArg = func.Arguments.Length > 0 ? CosmosSqlParser.ExprToString(func.Arguments[0]) : "1";
                return func.FunctionName.ToUpperInvariant() switch
                {
                    "COUNT" when innerArg is "1" or "*" => items.Count,
                    "COUNT" => CountDefinedResults(func.Arguments[0], items, fromAlias, context),
                    "COUNTIF" => EvaluateCountIf(func, items, fromAlias, context),
                    "SUM" => ExtractNumericValues(items, innerArg, fromAlias, context) is var sv && sv.Count > 0 ? sv.Sum() : UndefinedValue.Instance,
                    "AVG" => ExtractNumericValues(items, innerArg, fromAlias, context) is var av && av.Count > 0 ? av.Average() : UndefinedValue.Instance,
                    "MIN" => CosmosValueComparer.AggregateMinMax(ExtractTokenValues(items, innerArg, fromAlias, context), true),
                    "MAX" => CosmosValueComparer.AggregateMinMax(ExtractTokenValues(items, innerArg, fromAlias, context), false),
                    _ => null
                };
            }
            case ObjectLiteralExpression obj:
            {
                var result = new JObject();
                foreach (var prop in obj.Properties)
                {
                    var val = EvaluateAggregateExpression(prop.Value, items, fromAlias, context);
                    if (val is not null and not UndefinedValue)
                        result[prop.Key] = val is JToken jt ? jt.DeepClone() : JToken.FromObject(val);
                }
                return result;
            }
            case ArrayLiteralExpression arr:
            {
                var result = new JArray();
                foreach (var element in arr.Elements)
                {
                    var val = EvaluateAggregateExpression(element, items, fromAlias, context);
                    result.Add(val is JToken jt ? jt.DeepClone() : val is not null and not UndefinedValue ? JToken.FromObject(val) : JValue.CreateNull());
                }
                return result;
            }
            case BinaryExpression bin:
            {
                var left = EvaluateAggregateExpression(bin.Left, items, fromAlias, context);
                var right = EvaluateAggregateExpression(bin.Right, items, fromAlias, context);
                return bin.Operator switch
                {
                    BinaryOp.GreaterThan => CosmosValueComparer.CompareValues(left, right) > 0,
                    BinaryOp.GreaterThanOrEqual => CosmosValueComparer.CompareValues(left, right) >= 0,
                    BinaryOp.LessThan => CosmosValueComparer.CompareValues(left, right) < 0,
                    BinaryOp.LessThanOrEqual => CosmosValueComparer.CompareValues(left, right) <= 0,
                    BinaryOp.Equal => CosmosValueComparer.ValuesEqual(left, right),
                    BinaryOp.NotEqual => !CosmosValueComparer.ValuesEqual(left, right),
                    BinaryOp.And => CosmosValueComparer.IsTruthy(left) && CosmosValueComparer.IsTruthy(right),
                    BinaryOp.Or => CosmosValueComparer.IsTruthy(left) || CosmosValueComparer.IsTruthy(right),
                    BinaryOp.Add => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => a + b),
                    BinaryOp.Subtract => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => a - b),
                    BinaryOp.Multiply => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => a * b),
                    BinaryOp.Divide => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => b != 0 ? a / b : double.NaN),
                    BinaryOp.Modulo => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => b != 0 ? a % b : double.NaN),
                    _ => null
                };
            }
            case TernaryExpression tern:
            {
                var condition = EvaluateAggregateExpression(tern.Condition, items, fromAlias, context);
                return condition is bool b && b
                    ? EvaluateAggregateExpression(tern.IfTrue, items, fromAlias, context)
                    : EvaluateAggregateExpression(tern.IfFalse, items, fromAlias, context);
            }
            default:
                if (items.Count > 0)
                {
                    var jObj = JsonParseHelpers.ParseJson(items[0]);
                    return SqlExpressionEvaluator.Evaluate(expr, jObj, fromAlias, context);
                }
                return null;
        }
    }

    private static object? EvaluateHavingSqlExpression(
        SqlExpression expr,
        List<string> groupItems,
        JObject resultObj,
        string fromAlias,
        QueryExecutionContext context)
    {
        return expr switch
        {
            FunctionCallExpression func when SqlExpressionEvaluator.AggregateFunctions.Contains(func.FunctionName) =>
                EvaluateHavingAggregate(func, groupItems, fromAlias, context),
            BinaryExpression bin => EvaluateHavingBinaryExpression(bin, groupItems, resultObj, fromAlias, context),
            LiteralExpression lit => lit.Value,
            UndefinedLiteralExpression => UndefinedValue.Instance,
            IdentifierExpression ident => SqlExpressionEvaluator.ResolveValue(ident.Name, resultObj, fromAlias, context.Parameters),
            _ => SqlExpressionEvaluator.Evaluate(expr, resultObj, fromAlias, context)
        };
    }

    private static object? EvaluateHavingBinaryExpression(
        BinaryExpression bin,
        List<string> groupItems,
        JObject resultObj,
        string fromAlias,
        QueryExecutionContext context)
    {
        var left = EvaluateHavingSqlExpression(bin.Left, groupItems, resultObj, fromAlias, context);
        var right = EvaluateHavingSqlExpression(bin.Right, groupItems, resultObj, fromAlias, context);
        return bin.Operator switch
        {
            BinaryOp.GreaterThan => CosmosValueComparer.CompareValues(left, right) > 0,
            BinaryOp.GreaterThanOrEqual => CosmosValueComparer.CompareValues(left, right) >= 0,
            BinaryOp.LessThan => CosmosValueComparer.CompareValues(left, right) < 0,
            BinaryOp.LessThanOrEqual => CosmosValueComparer.CompareValues(left, right) <= 0,
            BinaryOp.Equal => CosmosValueComparer.ValuesEqual(left, right),
            BinaryOp.NotEqual => !CosmosValueComparer.ValuesEqual(left, right),
            BinaryOp.And => CosmosValueComparer.IsTruthy(left) && CosmosValueComparer.IsTruthy(right),
            BinaryOp.Or => CosmosValueComparer.IsTruthy(left) || CosmosValueComparer.IsTruthy(right),
            BinaryOp.Add => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => a + b),
            BinaryOp.Subtract => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => a - b),
            BinaryOp.Multiply => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => a * b),
            BinaryOp.Divide => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => b != 0 ? a / b : double.NaN),
            BinaryOp.Modulo => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => b != 0 ? a % b : double.NaN),
            _ => null
        };
    }

    private static JObject EvaluateGroupByObjectLiteral(
        ObjectLiteralExpression obj,
        List<string> groupItems,
        JObject sampleItem,
        string fromAlias,
        QueryExecutionContext context)
    {
        var result = new JObject();
        foreach (var prop in obj.Properties)
        {
            var val = EvaluateGroupByProjectionExpression(prop.Value, groupItems, sampleItem, fromAlias, context);
            if (val is not UndefinedValue)
                result[prop.Key] = val is not null ? JToken.FromObject(val) : JValue.CreateNull();
        }
        return result;
    }
}
