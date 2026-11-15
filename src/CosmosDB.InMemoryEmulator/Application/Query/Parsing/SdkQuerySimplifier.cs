#nullable enable
using System.Text;

namespace CosmosDB.InMemoryEmulator.Application.Query.Parsing;

/// <summary>
/// Simplifies and reconstructs Cosmos SDK gateway-rewritten queries into canonical SQL
/// suitable for execution on <see cref="InMemoryContainer"/>.
/// </summary>
internal static class SdkQuerySimplifier
{
    public static SqlExpression? SimplifySdkWhereExpression(
        SqlExpression? expr, string? fromAlias = null)
    {
        return SimplifyCore(expr, fromAlias);
    }

    private static SqlExpression? SimplifyCore(SqlExpression? expr, string? fromAlias)
    {
        if (expr is null)
        {
            return null;
        }

        if (expr is LiteralExpression { Value: true })
        {
            return null;
        }

        if (expr is FunctionCallExpression func &&
            string.Equals(func.FunctionName, "IS_DEFINED", StringComparison.OrdinalIgnoreCase) &&
            func.Arguments.Length == 1)
        {
            var argStr = SqlAstFormatter.ExprToString(func.Arguments[0]);
            // Only strip when: no alias specified (backward-compat), or arg IS the FROM alias exactly.
            // The SDK injects IS_DEFINED(root) — never IS_DEFINED(root.field) — so this
            // preserves any user-written IS_DEFINED on specific field paths.
            if (fromAlias is null || string.Equals(argStr, fromAlias, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        if (expr is BinaryExpression { Operator: BinaryOp.And } and)
        {
            var left = SimplifyCore(and.Left, fromAlias);
            var right = SimplifyCore(and.Right, fromAlias);
            if (left is null && right is null)
            {
                return null;
            }

            if (left is null)
            {
                return right;
            }

            if (right is null)
            {
                return left;
            }

            return new BinaryExpression(left, BinaryOp.And, right);
        }

        return expr;
    }

    public static string SimplifySdkQuery(CosmosSqlQuery parsed)
    {
        var fromAlias = parsed.FromAlias;

        var isOrderByQuery = parsed.SelectFields.Any(field =>
            string.Equals(field.Alias, "orderByItems", StringComparison.OrdinalIgnoreCase));

        // SELECT clause
        var sb = new StringBuilder("SELECT ");
        if (parsed.IsDistinct)
        {
            sb.Append("DISTINCT ");
        }

        if (parsed.TopCount.HasValue)
        {
            sb.Append($"TOP {parsed.TopCount.Value} ");
        }

        if (isOrderByQuery)
        {
            sb.Append($"VALUE {fromAlias}");
        }
        else if (parsed.IsValueSelect)
        {
            var selectExprs = parsed.SelectFields
                .Select(field => SqlAstFormatter.ExprToString(field.SqlExpr ?? new IdentifierExpression(field.Expression)))
                .ToArray();
            sb.Append("VALUE ");
            sb.Append(string.Join(", ", selectExprs));
        }
        else if (parsed.IsSelectAll)
        {
            sb.Append('*');
        }
        else
        {
            var selectParts = parsed.SelectFields.Select(field =>
            {
                var expr = field.SqlExpr is not null ? SqlAstFormatter.ExprToString(field.SqlExpr) : field.Expression;
                return field.Alias is not null ? $"{expr} AS {field.Alias}" : expr;
            });
            sb.Append(string.Join(", ", selectParts));
        }

        // FROM
        if (parsed.FromSource is not null)
        {
            sb.Append($" FROM {fromAlias} IN {parsed.FromSource}");
        }
        else
        {
            sb.Append($" FROM {fromAlias}");
        }

        // JOINs
        if (parsed.Joins is { Length: > 0 })
        {
            foreach (var join in parsed.Joins)
            {
                sb.Append($" JOIN {join.Alias} IN {join.SourceAlias}.{join.ArrayField}");
            }
        }

        // WHERE — strip SDK-injected nodes
        var simplifiedWhere = SimplifySdkWhereExpression(parsed.WhereExpr, fromAlias);
        if (simplifiedWhere is not null)
        {
            sb.Append($" WHERE {SqlAstFormatter.ExprToString(simplifiedWhere)}");
        }

        // GROUP BY
        if (parsed.GroupByFields is { Length: > 0 })
        {
            sb.Append($" GROUP BY {string.Join(", ", parsed.GroupByFields)}");
        }

        // HAVING
        if (parsed.HavingExpr is not null)
        {
            sb.Append($" HAVING {SqlAstFormatter.ExprToString(parsed.HavingExpr)}");
        }

        // ORDER BY
        if (parsed.OrderByFields is { Length: > 0 })
        {
            var orderByStr = string.Join(", ", parsed.OrderByFields.Select(field =>
                $"{field.Field ?? SqlAstFormatter.ExprToString(field.Expression)} {(field.Ascending ? "ASC" : "DESC")}"));
            sb.Append($" ORDER BY {orderByStr}");
        }
        else if (parsed.RankExpression is not null)
        {
            sb.Append($" ORDER BY RANK {SqlAstFormatter.ExprToString(parsed.RankExpression)}");
        }

        // OFFSET / LIMIT
        if (parsed.Offset.HasValue)
        {
            sb.Append($" OFFSET {parsed.Offset.Value}");
        }

        if (parsed.Limit.HasValue)
        {
            sb.Append($" LIMIT {parsed.Limit.Value}");
        }

        return sb.ToString();
    }
}
