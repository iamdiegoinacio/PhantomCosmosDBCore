#nullable enable
using System.Globalization;
using Phantom.CosmosDB.Core.Application.Query.Functions;
using Phantom.CosmosDB.Core.Domain.Comparison;
using Phantom.CosmosDB.Core.Domain.Primitives;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Application.Query.Evaluation;

/// <summary>
/// Resolves JSON paths, parameters, and literals, and evaluates the <see cref="SqlExpression"/> AST.
/// </summary>
internal static class SqlExpressionEvaluator
{
    public static readonly HashSet<string> AggregateFunctions =
        new(StringComparer.OrdinalIgnoreCase) { "COUNT", "COUNTIF", "SUM", "AVG", "MIN", "MAX" };

    /// <summary>
    /// Recursively checks whether a <see cref="SqlExpression"/> tree contains any aggregate function call
    /// (<c>COUNT</c>, <c>COUNTIF</c>, <c>SUM</c>, <c>AVG</c>, <c>MIN</c>, <c>MAX</c>).
    /// </summary>
    public static bool ContainsAggregateCall(SqlExpression expr) => expr switch
    {
        FunctionCallExpression func => AggregateFunctions.Contains(func.FunctionName),
        ObjectLiteralExpression obj => obj.Properties.Any(static p => ContainsAggregateCall(p.Value)),
        ArrayLiteralExpression arr => arr.Elements.Any(ContainsAggregateCall),
        BinaryExpression bin => ContainsAggregateCall(bin.Left) || ContainsAggregateCall(bin.Right),
        UnaryExpression unary => ContainsAggregateCall(unary.Operand),
        TernaryExpression tern => ContainsAggregateCall(tern.Condition) || ContainsAggregateCall(tern.IfTrue) || ContainsAggregateCall(tern.IfFalse),
        _ => false
    };

    /// <summary>
    /// Strips the <c>FROM</c> alias prefix from a property path, handling both
    /// dot notation (<c>c.name</c>) and bracket notation (<c>c["name"]</c>) from the LINQ provider.
    /// </summary>
    public static string StripAliasPrefix(string path, string fromAlias)
    {
        if (path.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
            return path[(fromAlias.Length + 1)..];
        if (path.StartsWith(fromAlias + "[", StringComparison.OrdinalIgnoreCase))
        {
            var bracketPart = path[fromAlias.Length..];
            if (bracketPart.StartsWith("[\"", StringComparison.Ordinal) && bracketPart.EndsWith("\"]", StringComparison.Ordinal))
                return bracketPart[2..^2];
            if (bracketPart.StartsWith("['", StringComparison.Ordinal) && bracketPart.EndsWith("']", StringComparison.Ordinal))
                return bracketPart[2..^2];
            return bracketPart;
        }
        return path;
    }

    public static object? ResolveValue(
        string expression,
        JObject item,
        string fromAlias,
        IDictionary<string, object?> parameters)
    {
        var trimmed = expression.Trim();
        if (trimmed.StartsWith("@", StringComparison.Ordinal))
        {
            return parameters.TryGetValue(trimmed, out var v) ? v : null;
        }

        if (trimmed.StartsWith("'", StringComparison.Ordinal) && trimmed.EndsWith("'", StringComparison.Ordinal))
        {
            return trimmed[1..^1];
        }

        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
        {
            return num;
        }

        var jsonPath = trimmed;
        if (jsonPath.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
        {
            jsonPath = jsonPath[(fromAlias.Length + 1)..];
        }
        else if (jsonPath.StartsWith(fromAlias + "[", StringComparison.OrdinalIgnoreCase))
        {
            jsonPath = jsonPath[fromAlias.Length..];
        }

        var token = item.SelectToken(jsonPath);
        if (token == null)
        {
            if (TryResolveStringPseudoProperty(jsonPath, item, out var pseudoResult))
            {
                return pseudoResult;
            }

            return UndefinedValue.Instance;
        }

        return token.Type switch
        {
            JTokenType.String => token.Value<string>(),
            JTokenType.Integer => token.Value<long>(),
            JTokenType.Float => token.Value<double>(),
            JTokenType.Boolean => token.Value<bool>(),
            JTokenType.Null => null,
            JTokenType.Undefined => UndefinedValue.Instance,
            JTokenType.Array => token,
            JTokenType.Object => token,
            _ => token.ToString()
        };
    }

    public static object? Evaluate(
        SqlExpression expr,
        JObject item,
        string fromAlias,
        QueryExecutionContext context)
    {
        return expr switch
        {
            LiteralExpression lit => lit.Value,
            UndefinedLiteralExpression => UndefinedValue.Instance,
            IdentifierExpression ident => ResolveValue(ident.Name, item, fromAlias, context.Parameters),
            ParameterExpression param => context.Parameters.TryGetValue(param.Name, out var v) ? UnwrapJToken(v) : null,
            FunctionCallExpression func => SqlFunctionEvaluator.Evaluate(
                func,
                item,
                fromAlias,
                context,
                argExpr => Evaluate(argExpr, item, fromAlias, context),
                subquery => SubqueryEvaluator.EvaluateArraySubquery(subquery, item, fromAlias, context)),
            BinaryExpression bin => EvaluateBinaryExpression(bin, item, fromAlias, context),
            UnaryExpression unary => EvaluateUnaryExpression(unary, item, fromAlias, context),
            BetweenExpression between => EvalBetween(between, item, fromAlias, context),
            InExpression inExpr => EvalIn(inExpr, item, fromAlias, context),
            LikeExpression like => SqlMathHelpers.EvaluateLike(
                Evaluate(like.Value, item, fromAlias, context),
                Evaluate(like.Pattern, item, fromAlias, context),
                like.EscapeChar),
            ExistsExpression exists => SubqueryEvaluator.EvaluateExists(new ExistsCondition(exists.RawSubquery), item, fromAlias, context, null),
            CoalesceExpression coal => EvalCoalesce(coal, item, fromAlias, context),
            TernaryExpression tern => Evaluate(tern.Condition, item, fromAlias, context) is bool tb && tb
                ? Evaluate(tern.IfTrue, item, fromAlias, context)
                : Evaluate(tern.IfFalse, item, fromAlias, context),
            ObjectLiteralExpression obj => EvaluateObjectLiteral(obj, item, fromAlias, context),
            ArrayLiteralExpression arr => EvaluateArrayLiteral(arr, item, fromAlias, context),
            PropertyAccessExpression prop => ResolveValue(
                $"{CosmosSqlParser.ExprToString(prop.Object)}.{prop.Property}", item, fromAlias, context.Parameters),
            IndexAccessExpression idx => ResolveValue(
                $"{CosmosSqlParser.ExprToString(idx.Object)}[{CosmosSqlParser.ExprToString(idx.Index)}]", item, fromAlias, context.Parameters),
            SubqueryExpression sub => SubqueryEvaluator.EvaluateSubquery(sub.Subquery, item, fromAlias, context),
            _ => null
        };
    }

    private static bool TryResolveStringPseudoProperty(string jsonPath, JObject item, out object? result)
    {
        result = null;

        string? parentPath = null;
        if (jsonPath.EndsWith("['Length']", StringComparison.OrdinalIgnoreCase))
            parentPath = jsonPath[..^"['Length']".Length];
        else if (jsonPath.EndsWith("[\"Length\"]", StringComparison.OrdinalIgnoreCase))
            parentPath = jsonPath[..^"[\"Length\"]".Length];
        else if (jsonPath.EndsWith(".Length", StringComparison.OrdinalIgnoreCase))
            parentPath = jsonPath[..^".Length".Length];

        if (parentPath is null || parentPath.Length == 0) return false;

        var parentToken = item.SelectToken(parentPath);
        if (parentToken is JValue { Type: JTokenType.String } sv)
        {
            result = (long)sv.Value<string>()!.Length;
            return true;
        }

        if (parentToken is JArray arr)
        {
            result = (long)arr.Count;
            return true;
        }

        return false;
    }

    private static object? UnwrapJToken(object? value)
    {
        if (value is JValue jv)
        {
            return jv.Type switch
            {
                JTokenType.String => jv.Value<string>(),
                JTokenType.Integer => jv.Value<long>(),
                JTokenType.Float => jv.Value<double>(),
                JTokenType.Boolean => jv.Value<bool>(),
                JTokenType.Null => null,
                _ => value
            };
        }
        return value;
    }

    private static object EvalBetween(BetweenExpression b, JObject item, string fromAlias, QueryExecutionContext context)
    {
        var value = Evaluate(b.Value, item, fromAlias, context);
        var low = Evaluate(b.Low, item, fromAlias, context);
        var high = Evaluate(b.High, item, fromAlias, context);
        if (value is UndefinedValue || low is UndefinedValue || high is UndefinedValue)
            return UndefinedValue.Instance;
        return CosmosValueComparer.CompareValues(value, low) >= 0 && CosmosValueComparer.CompareValues(value, high) <= 0;
    }

    private static object? EvalCoalesce(CoalesceExpression coal, JObject item, string fromAlias, QueryExecutionContext context)
    {
        var left = Evaluate(coal.Left, item, fromAlias, context);
        return left is not null and not UndefinedValue ? left : Evaluate(coal.Right, item, fromAlias, context);
    }

    private static object EvalIn(InExpression inExpr, JObject item, string fromAlias, QueryExecutionContext context)
    {
        var value = Evaluate(inExpr.Value, item, fromAlias, context);
        if (value is UndefinedValue)
            return UndefinedValue.Instance;
        return inExpr.List.Any(li => CosmosValueComparer.ValuesEqual(value, Evaluate(li, item, fromAlias, context)));
    }

    private static object? EvaluateBinaryExpression(
        BinaryExpression bin, JObject item, string fromAlias, QueryExecutionContext context)
    {
        var left = Evaluate(bin.Left, item, fromAlias, context);
        var right = Evaluate(bin.Right, item, fromAlias, context);

        if (bin.Operator is BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.LessThan or BinaryOp.GreaterThan
            or BinaryOp.LessThanOrEqual or BinaryOp.GreaterThanOrEqual or BinaryOp.Like)
        {
            if (left is UndefinedValue || right is UndefinedValue)
                return UndefinedValue.Instance;
        }

        return bin.Operator switch
        {
            BinaryOp.Equal => CosmosValueComparer.ValuesEqual(left, right),
            BinaryOp.NotEqual => !CosmosValueComparer.ValuesEqual(left, right),
            BinaryOp.LessThan => CosmosValueComparer.CompareValues(left, right) < 0,
            BinaryOp.GreaterThan => CosmosValueComparer.CompareValues(left, right) > 0,
            BinaryOp.LessThanOrEqual => CosmosValueComparer.CompareValues(left, right) <= 0,
            BinaryOp.GreaterThanOrEqual => CosmosValueComparer.CompareValues(left, right) >= 0,
            BinaryOp.And => left is UndefinedValue
                ? (CosmosValueComparer.IsTruthy(right) ? UndefinedValue.Instance : (object)false)
                : right is UndefinedValue
                    ? (CosmosValueComparer.IsTruthy(left) ? UndefinedValue.Instance : (object)false)
                    : CosmosValueComparer.IsTruthy(left) && CosmosValueComparer.IsTruthy(right),
            BinaryOp.Or => left is UndefinedValue
                ? (CosmosValueComparer.IsTruthy(right) ? (object)true : UndefinedValue.Instance)
                : right is UndefinedValue
                    ? (CosmosValueComparer.IsTruthy(left) ? (object)true : UndefinedValue.Instance)
                    : CosmosValueComparer.IsTruthy(left) || CosmosValueComparer.IsTruthy(right),
            BinaryOp.Like => SqlMathHelpers.EvaluateLike(left, right),
            BinaryOp.Add => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => a + b),
            BinaryOp.Subtract => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => a - b),
            BinaryOp.Multiply => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => a * b),
            BinaryOp.Divide => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => b != 0 ? a / b : double.NaN),
            BinaryOp.Modulo => SqlMathHelpers.ArithmeticOp(left, right, static (a, b) => b != 0 ? a % b : double.NaN),
            BinaryOp.StringConcat => left is null or UndefinedValue || right is null or UndefinedValue ? UndefinedValue.Instance : left.ToString() + right.ToString(),
            BinaryOp.BitwiseAnd => SqlMathHelpers.BitwiseOp(left, right, static (a, b) => a & b),
            BinaryOp.BitwiseOr => SqlMathHelpers.BitwiseOp(left, right, static (a, b) => a | b),
            BinaryOp.BitwiseXor => SqlMathHelpers.BitwiseOp(left, right, static (a, b) => a ^ b),
            _ => null
        };
    }

    private static object EvaluateUnaryExpression(
        UnaryExpression unary, JObject item, string fromAlias, QueryExecutionContext context)
    {
        var operand = Evaluate(unary.Operand, item, fromAlias, context);
        return unary.Operator switch
        {
            UnaryOp.Not => operand is UndefinedValue or null ? UndefinedValue.Instance : !CosmosValueComparer.IsTruthy(operand),
            UnaryOp.Negate => operand is double d ? -d : operand is long l ? -l : UndefinedValue.Instance,
            UnaryOp.BitwiseNot => operand is long lng ? ~lng : UndefinedValue.Instance,
            _ => UndefinedValue.Instance
        };
    }

    private static JObject EvaluateObjectLiteral(
        ObjectLiteralExpression obj, JObject item, string fromAlias, QueryExecutionContext context)
    {
        var result = new JObject();
        foreach (var prop in obj.Properties)
        {
            var value = Evaluate(prop.Value, item, fromAlias, context);
            if (value is not UndefinedValue)
                result[prop.Key] = value is not null ? JToken.FromObject(value) : JValue.CreateNull();
        }
        return result;
    }

    private static JArray EvaluateArrayLiteral(
        ArrayLiteralExpression arr, JObject item, string fromAlias, QueryExecutionContext context)
    {
        var result = new JArray();
        foreach (var element in arr.Elements)
        {
            var value = Evaluate(element, item, fromAlias, context);
            result.Add(value is not null and not UndefinedValue ? JToken.FromObject(value) : JValue.CreateNull());
        }
        return result;
    }
}
