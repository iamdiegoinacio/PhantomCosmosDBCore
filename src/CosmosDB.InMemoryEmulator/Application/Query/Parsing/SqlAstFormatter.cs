#nullable enable
using System.Text;

namespace CosmosDB.InMemoryEmulator.Application.Query.Parsing;

/// <summary>
/// Serializes <see cref="SqlExpression"/> and <see cref="CosmosSqlQuery"/> AST nodes
/// back into canonical Cosmos DB SQL strings.
/// </summary>
internal static class SqlAstFormatter
{
    public static string ExprToString(SqlExpression? expr)
    {
        if (expr is null)
        {
            return "null";
        }

        return expr switch
        {
            LiteralExpression { Value: null } => "null",
            LiteralExpression { Value: string s } => $"'{s}'",
            LiteralExpression { Value: bool b } => b ? "true" : "false",
            LiteralExpression lit => lit.Value?.ToString() ?? "null",
            UndefinedLiteralExpression => "undefined",
            IdentifierExpression ident => ident.Name,
            ParameterExpression param => param.Name,
            PropertyAccessExpression prop => $"{ExprToString(prop.Object)}.{prop.Property}",
            IndexAccessExpression idx => $"{ExprToString(idx.Object)}[{ExprToString(idx.Index)}]",
            FunctionCallExpression func => $"{func.FunctionName}({string.Join(", ", func.Arguments.Select(ExprToString))})",
            BinaryExpression { Operator: BinaryOp.And or BinaryOp.Or } bin =>
                $"({WrapIfLowPrecedence(bin.Left)} {BinaryOpToString(bin.Operator)} {WrapIfLowPrecedence(bin.Right)})",
            BinaryExpression bin => $"{WrapIfLowPrecedence(bin.Left)} {BinaryOpToString(bin.Operator)} {WrapIfLowPrecedence(bin.Right)}",
            UnaryExpression unary => $"{UnaryOpToString(unary.Operator)} {WrapIfLowPrecedence(unary.Operand)}",
            BetweenExpression betw => $"{WrapIfLowPrecedence(betw.Value)} BETWEEN {ExprToString(betw.Low)} AND {ExprToString(betw.High)}",
            InExpression inExpr => $"{WrapIfLowPrecedence(inExpr.Value)} IN ({string.Join(", ", inExpr.List.Select(ExprToString))})",
            LikeExpression like => like.EscapeChar is not null
                ? $"{WrapIfLowPrecedence(like.Value)} LIKE {ExprToString(like.Pattern)} ESCAPE '{like.EscapeChar}'"
                : $"{WrapIfLowPrecedence(like.Value)} LIKE {ExprToString(like.Pattern)}",
            ExistsExpression exists => $"EXISTS({exists.RawSubquery})",
            SubqueryExpression sub => $"({SubqueryToString(sub.Subquery)})",
            TernaryExpression tern => $"{ExprToString(tern.Condition)} ? {ExprToString(tern.IfTrue)} : {ExprToString(tern.IfFalse)}",
            CoalesceExpression coal => $"{ExprToString(coal.Left)} ?? {ExprToString(coal.Right)}",
            ObjectLiteralExpression obj => "{" + string.Join(", ", obj.Properties.Select(p => $"{p.Key}: {ExprToString(p.Value)}")) + "}",
            ArrayLiteralExpression arr => "[" + string.Join(", ", arr.Elements.Select(ExprToString)) + "]",
            _ => expr.ToString() ?? string.Empty,
        };
    }

    /// <summary>
    /// Wraps the expression in parentheses if it has lower precedence than binary/unary operators
    /// (i.e., ternary or coalesce expressions). This ensures that re-parsing the serialized string
    /// produces the same AST.
    /// </summary>
    private static string WrapIfLowPrecedence(SqlExpression expr) =>
        expr is TernaryExpression or CoalesceExpression
            ? $"({ExprToString(expr)})"
            : ExprToString(expr);

    public static string SubqueryToString(CosmosSqlQuery sub)
    {
        var sb = new StringBuilder("SELECT ");
        if (sub.IsDistinct)
        {
            sb.Append("DISTINCT ");
        }

        if (sub.TopCount.HasValue)
        {
            sb.Append($"TOP {sub.TopCount.Value} ");
        }

        if (sub.IsValueSelect)
        {
            sb.Append("VALUE ");
        }

        if (sub.IsSelectAll)
        {
            sb.Append('*');
        }
        else
        {
            var selectParts = sub.SelectFields.Select(field =>
            {
                var expr = field.SqlExpr is not null ? ExprToString(field.SqlExpr) : field.Expression;
                return field.Alias is not null ? $"{expr} AS {field.Alias}" : expr;
            });
            sb.Append(string.Join(", ", selectParts));
        }

        if (sub.FromSource is not null)
        {
            sb.Append($" FROM {sub.FromAlias} IN {sub.FromSource}");
        }
        else
        {
            sb.Append($" FROM {sub.FromAlias}");
        }

        if (sub.Joins is { Length: > 0 })
        {
            foreach (var join in sub.Joins)
            {
                sb.Append($" JOIN {join.Alias} IN {join.SourceAlias}.{join.ArrayField}");
            }
        }

        if (sub.WhereExpr is not null)
        {
            sb.Append($" WHERE {ExprToString(sub.WhereExpr)}");
        }

        if (sub.GroupByFields is { Length: > 0 })
        {
            sb.Append($" GROUP BY {string.Join(", ", sub.GroupByFields)}");
        }

        if (sub.HavingExpr is not null)
        {
            sb.Append($" HAVING {ExprToString(sub.HavingExpr)}");
        }

        if (sub.OrderByFields is { Length: > 0 })
        {
            var orderByStr = string.Join(", ", sub.OrderByFields.Select(field =>
                $"{field.Field} {(field.Ascending ? "ASC" : "DESC")}"));
            sb.Append($" ORDER BY {orderByStr}");
        }

        if (sub.Offset.HasValue)
        {
            sb.Append($" OFFSET {sub.Offset.Value}");
        }

        if (sub.Limit.HasValue)
        {
            sb.Append($" LIMIT {sub.Limit.Value}");
        }

        return sb.ToString();
    }

    public static string BinaryOpToString(BinaryOp op) => op switch
    {
        BinaryOp.Equal => "=",
        BinaryOp.NotEqual => "!=",
        BinaryOp.LessThan => "<",
        BinaryOp.GreaterThan => ">",
        BinaryOp.LessThanOrEqual => "<=",
        BinaryOp.GreaterThanOrEqual => ">=",
        BinaryOp.And => "AND",
        BinaryOp.Or => "OR",
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        BinaryOp.Modulo => "%",
        BinaryOp.Like => "LIKE",
        BinaryOp.StringConcat => "||",
        BinaryOp.BitwiseAnd => "&",
        BinaryOp.BitwiseOr => "|",
        BinaryOp.BitwiseXor => "^",
        _ => op.ToString(),
    };

    public static string UnaryOpToString(UnaryOp op) => op switch
    {
        UnaryOp.Not => "NOT",
        UnaryOp.Negate => "-",
        UnaryOp.BitwiseNot => "~",
        _ => op.ToString(),
    };
}
