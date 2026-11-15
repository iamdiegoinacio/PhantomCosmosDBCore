#nullable enable
using CosmosDB.InMemoryEmulator.Application.Query.Functions;
using CosmosDB.InMemoryEmulator.Domain.Comparison;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.Query.Evaluation;

/// <summary>
/// Evaluates <see cref="WhereExpression"/> AST nodes using Cosmos DB three-value logic
/// (including <c>undefined</c> propagation for <c>NOT</c> and <c>FilterPredicate</c> null-coercion).
/// </summary>
internal static class WhereExpressionEvaluator
{
    public static bool Evaluate(
        WhereExpression expression,
        JObject item,
        string fromAlias,
        QueryExecutionContext context,
        JoinClause? join,
        bool treatUndefinedAsNull = false)
    {
        return expression switch
        {
            ComparisonCondition c => EvaluateComparison(c, item, fromAlias, context.Parameters, treatUndefinedAsNull),
            AndCondition a => Evaluate(a.Left, item, fromAlias, context, join, treatUndefinedAsNull)
                              && Evaluate(a.Right, item, fromAlias, context, join, treatUndefinedAsNull),
            OrCondition o => Evaluate(o.Left, item, fromAlias, context, join, treatUndefinedAsNull)
                             || Evaluate(o.Right, item, fromAlias, context, join, treatUndefinedAsNull),
            NotCondition n => !EvaluateIncludesUndefined(n.Inner, item, fromAlias, context, join, treatUndefinedAsNull)
                              && !Evaluate(n.Inner, item, fromAlias, context, join, treatUndefinedAsNull),
            FunctionCondition f => EvaluateLegacyFunction(f, item, fromAlias, context.Parameters),
            ExistsCondition e => SubqueryEvaluator.EvaluateExists(e, item, fromAlias, context, join),
            SqlExpressionCondition s => CosmosValueComparer.IsTruthy(SqlExpressionEvaluator.Evaluate(s.Expression, item, fromAlias, context)),
            _ => true
        };
    }

    private static bool EvaluateComparison(
        ComparisonCondition comparison,
        JObject item,
        string fromAlias,
        IDictionary<string, object?> parameters,
        bool treatUndefinedAsNull = false)
    {
        var leftValue = SqlExpressionEvaluator.ResolveValue(comparison.Left, item, fromAlias, parameters);
        var rightValue = SqlExpressionEvaluator.ResolveValue(comparison.Right, item, fromAlias, parameters);

        if (treatUndefinedAsNull)
        {
            if (leftValue is UndefinedValue) leftValue = null;
            if (rightValue is UndefinedValue) rightValue = null;
        }

        if (leftValue is UndefinedValue || rightValue is UndefinedValue)
            return false;

        return comparison.Operator switch
        {
            ComparisonOp.Equal => CosmosValueComparer.ValuesEqual(leftValue, rightValue),
            ComparisonOp.NotEqual => !CosmosValueComparer.ValuesEqual(leftValue, rightValue),
            ComparisonOp.LessThan => CosmosValueComparer.CompareValues(leftValue, rightValue) < 0,
            ComparisonOp.GreaterThan => CosmosValueComparer.CompareValues(leftValue, rightValue) > 0,
            ComparisonOp.LessThanOrEqual => CosmosValueComparer.CompareValues(leftValue, rightValue) <= 0,
            ComparisonOp.GreaterThanOrEqual => CosmosValueComparer.CompareValues(leftValue, rightValue) >= 0,
            ComparisonOp.Like => CosmosValueComparer.IsTruthy(SqlMathHelpers.EvaluateLike(leftValue, rightValue)),
            _ => false
        };
    }

    private static bool EvaluateIncludesUndefined(
        WhereExpression expression,
        JObject item,
        string fromAlias,
        QueryExecutionContext context,
        JoinClause? join,
        bool treatUndefinedAsNull = false)
    {
        return expression switch
        {
            ComparisonCondition c => ComparisonIncludesUndefined(c, item, fromAlias, context.Parameters, treatUndefinedAsNull),
            AndCondition a =>
                EvaluateIncludesUndefined(a.Left, item, fromAlias, context, join, treatUndefinedAsNull) ||
                EvaluateIncludesUndefined(a.Right, item, fromAlias, context, join, treatUndefinedAsNull),
            OrCondition o =>
                EvaluateIncludesUndefined(o.Left, item, fromAlias, context, join, treatUndefinedAsNull) ||
                EvaluateIncludesUndefined(o.Right, item, fromAlias, context, join, treatUndefinedAsNull),
            NotCondition n =>
                EvaluateIncludesUndefined(n.Inner, item, fromAlias, context, join, treatUndefinedAsNull),
            SqlExpressionCondition s =>
                SqlExpressionEvaluator.Evaluate(s.Expression, item, fromAlias, context) is UndefinedValue,
            _ => false,
        };
    }

    private static bool ComparisonIncludesUndefined(
        ComparisonCondition c,
        JObject item,
        string fromAlias,
        IDictionary<string, object?> parameters,
        bool treatUndefinedAsNull = false)
    {
        var left = SqlExpressionEvaluator.ResolveValue(c.Left, item, fromAlias, parameters);
        var right = SqlExpressionEvaluator.ResolveValue(c.Right, item, fromAlias, parameters);

        if (treatUndefinedAsNull)
        {
            if (left is UndefinedValue) left = null;
            if (right is UndefinedValue) right = null;
        }

        if (left is UndefinedValue || right is UndefinedValue)
            return true;

        if (c.Operator == ComparisonOp.Like && (left is null || right is null))
            return true;

        return false;
    }

    private static bool EvaluateLegacyFunction(
        FunctionCondition func,
        JObject item,
        string fromAlias,
        IDictionary<string, object?> parameters)
    {
        switch (func.FunctionName)
        {
            case "STARTSWITH":
                if (func.Arguments.Length < 2) return false;
                var swField = SqlExpressionEvaluator.ResolveValue(func.Arguments[0], item, fromAlias, parameters)?.ToString();
                var swPrefix = SqlExpressionEvaluator.ResolveValue(func.Arguments[1], item, fromAlias, parameters)?.ToString();
                if (swField is null || swPrefix is null) return false;
                var swIc = func.Arguments.Length >= 3 &&
                    string.Equals(SqlExpressionEvaluator.ResolveValue(func.Arguments[2], item, fromAlias, parameters)?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                return swField.StartsWith(swPrefix, swIc ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

            case "ENDSWITH":
                if (func.Arguments.Length < 2) return false;
                var ewField = SqlExpressionEvaluator.ResolveValue(func.Arguments[0], item, fromAlias, parameters)?.ToString();
                var ewSuffix = SqlExpressionEvaluator.ResolveValue(func.Arguments[1], item, fromAlias, parameters)?.ToString();
                if (ewField is null || ewSuffix is null) return false;
                var ewIc = func.Arguments.Length >= 3 &&
                    string.Equals(SqlExpressionEvaluator.ResolveValue(func.Arguments[2], item, fromAlias, parameters)?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                return ewField.EndsWith(ewSuffix, ewIc ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

            case "CONTAINS":
                if (func.Arguments.Length < 2) return false;
                var ctField = SqlExpressionEvaluator.ResolveValue(func.Arguments[0], item, fromAlias, parameters)?.ToString();
                var ctSearch = SqlExpressionEvaluator.ResolveValue(func.Arguments[1], item, fromAlias, parameters)?.ToString();
                if (ctField is null || ctSearch is null) return false;
                var ctIc = func.Arguments.Length >= 3 &&
                    string.Equals(SqlExpressionEvaluator.ResolveValue(func.Arguments[2], item, fromAlias, parameters)?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                return ctField.Contains(ctSearch, ctIc ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

            case "ARRAY_CONTAINS":
                if (func.Arguments.Length < 2) return false;
                JArray? jArray;
                var firstArg = func.Arguments[0].Trim();
                if (firstArg.StartsWith("@", StringComparison.Ordinal) && parameters.TryGetValue(firstArg, out var paramVal))
                {
                    jArray = paramVal switch
                    {
                        JArray ja => ja,
                        System.Collections.IEnumerable enumerable when paramVal is not string =>
                            new JArray(enumerable.Cast<object>().Select(JToken.FromObject)),
                        _ => null
                    };
                }
                else
                {
                    var arrayPath = firstArg;
                    if (arrayPath.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        arrayPath = arrayPath[(fromAlias.Length + 1)..];
                    }
                    jArray = item.SelectToken(arrayPath) as JArray;
                }

                if (jArray is null) return false;
                var searchValue = SqlExpressionEvaluator.ResolveValue(func.Arguments[1], item, fromAlias, parameters);
                if (searchValue is null)
                {
                    return jArray.Any(static t => t.Type == JTokenType.Null);
                }

                var searchStr = searchValue.ToString() ?? string.Empty;
                var partial = func.Arguments.Length >= 3 &&
                    string.Equals(func.Arguments[2].Trim(), "true", StringComparison.OrdinalIgnoreCase);
                return ArrayObjectFunctions.ArrayContainsMatch(jArray, searchStr, partial);

            case "IS_DEFINED":
                if (func.Arguments.Length < 1) return false;
                var defPath = func.Arguments[0].Trim();
                if (defPath.StartsWith("@", StringComparison.Ordinal))
                    return parameters.ContainsKey(defPath);
                if (defPath.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    defPath = defPath[(fromAlias.Length + 1)..];
                var defToken = item.SelectToken(defPath);
                return defToken is not null && defToken.Type != JTokenType.Undefined;

            case "IS_NULL":
                if (func.Arguments.Length < 1) return false;
                var nullPath = func.Arguments[0].Trim();
                if (nullPath.StartsWith("@", StringComparison.Ordinal))
                {
                    if (parameters.TryGetValue(nullPath, out var nullParamVal))
                        return nullParamVal is null || (nullParamVal is JToken jt && jt.Type == JTokenType.Null);
                    return true;
                }
                if (nullPath.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    nullPath = nullPath[(fromAlias.Length + 1)..];
                var nullToken = item.SelectToken(nullPath);
                return nullToken is not null && nullToken.Type is JTokenType.Null;

            default:
                return true;
        }
    }
}
