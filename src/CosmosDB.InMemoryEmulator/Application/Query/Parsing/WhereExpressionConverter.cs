#nullable enable

namespace CosmosDB.InMemoryEmulator.Application.Query.Parsing;

/// <summary>
/// Converts <see cref="SqlExpression"/> AST trees into backward-compatible <see cref="WhereExpression"/> nodes.
/// </summary>
internal static class WhereExpressionConverter
{
    private static readonly HashSet<string> LegacyFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "STARTSWITH", "ENDSWITH", "CONTAINS", "ARRAY_CONTAINS", "IS_DEFINED", "IS_NULL",
    };

    public static WhereExpression? ToWhereExpression(SqlExpression? expr)
    {
        if (expr is null)
        {
            return null;
        }

        switch (expr)
        {
            case BinaryExpression bin when bin.Operator == BinaryOp.And:
                return new AndCondition(ToWhereExpression(bin.Left)!, ToWhereExpression(bin.Right)!);

            case BinaryExpression bin when bin.Operator == BinaryOp.Or:
                return new OrCondition(ToWhereExpression(bin.Left)!, ToWhereExpression(bin.Right)!);

            case UnaryExpression { Operator: UnaryOp.Not } unary:
                return new NotCondition(ToWhereExpression(unary.Operand)!);

            case ExistsExpression exists:
                return new ExistsCondition(exists.RawSubquery);

            case FunctionCallExpression func:
                if (func.FunctionName.StartsWith("UDF.", StringComparison.OrdinalIgnoreCase) ||
                    func.FunctionName.StartsWith("ST_", StringComparison.OrdinalIgnoreCase))
                {
                    return new SqlExpressionCondition(func);
                }

                var hasComplexArgs = func.Arguments.Any(a => a is ObjectLiteralExpression or ArrayLiteralExpression or FunctionCallExpression);
                if (hasComplexArgs)
                {
                    return new SqlExpressionCondition(func);
                }

                if (LegacyFunctionNames.Contains(func.FunctionName))
                {
                    var args = func.Arguments.Select(SqlAstFormatter.ExprToString).ToArray();
                    return new FunctionCondition(func.FunctionName, args);
                }

                return new SqlExpressionCondition(func);

            case BinaryExpression bin when IsComparisonOp(bin.Operator):
                if (ContainsFunctionCall(bin.Left) || ContainsFunctionCall(bin.Right) ||
                    ContainsArithmetic(bin.Left) || ContainsArithmetic(bin.Right) ||
                    ContainsComplexExpression(bin.Left) || ContainsComplexExpression(bin.Right))
                {
                    return new SqlExpressionCondition(bin);
                }

                var compOp = bin.Operator switch
                {
                    BinaryOp.Equal => ComparisonOp.Equal,
                    BinaryOp.NotEqual => ComparisonOp.NotEqual,
                    BinaryOp.LessThan => ComparisonOp.LessThan,
                    BinaryOp.GreaterThan => ComparisonOp.GreaterThan,
                    BinaryOp.LessThanOrEqual => ComparisonOp.LessThanOrEqual,
                    BinaryOp.GreaterThanOrEqual => ComparisonOp.GreaterThanOrEqual,
                    BinaryOp.Like => ComparisonOp.Like,
                    _ => ComparisonOp.Equal,
                };
                return new ComparisonCondition(
                    SqlAstFormatter.ExprToString(bin.Left),
                    compOp,
                    SqlAstFormatter.ExprToString(bin.Right));

            default:
                return new SqlExpressionCondition(expr);
        }
    }

    public static bool IsComparisonOp(BinaryOp op) =>
        op is BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.LessThan or BinaryOp.GreaterThan
            or BinaryOp.LessThanOrEqual or BinaryOp.GreaterThanOrEqual or BinaryOp.Like;

    private static bool ContainsFunctionCall(SqlExpression expr) =>
        expr switch
        {
            FunctionCallExpression => true,
            BinaryExpression bin => ContainsFunctionCall(bin.Left) || ContainsFunctionCall(bin.Right),
            UnaryExpression unary => ContainsFunctionCall(unary.Operand),
            _ => false
        };

    private static bool ContainsArithmetic(SqlExpression expr) =>
        expr switch
        {
            BinaryExpression bin when !IsComparisonOp(bin.Operator) => true,
            BinaryExpression bin => ContainsArithmetic(bin.Left) || ContainsArithmetic(bin.Right),
            UnaryExpression unary => ContainsArithmetic(unary.Operand),
            _ => false
        };

    /// <summary>
    /// Returns true when the expression contains a subquery, coalesce, or ternary —
    /// any of which require full expression evaluation rather than string-based ResolveValue.
    /// </summary>
    private static bool ContainsComplexExpression(SqlExpression expr) =>
        expr switch
        {
            SubqueryExpression => true,
            CoalesceExpression => true,
            TernaryExpression => true,
            BinaryExpression bin => ContainsComplexExpression(bin.Left) || ContainsComplexExpression(bin.Right),
            UnaryExpression unary => ContainsComplexExpression(unary.Operand),
            _ => false
        };
}
