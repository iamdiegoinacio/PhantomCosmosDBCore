#nullable enable
using System.Text.RegularExpressions;
using CosmosDB.InMemoryEmulator.Infrastructure.HttpGateway;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// Default implementation of <see cref="IQueryPlanStrategy"/> that builds
/// <c>PartitionedQueryExecutionInfo</c> using the current Cosmos SDK wire format (v2).
/// </summary>
public sealed class DefaultQueryPlanStrategy : IQueryPlanStrategy
{
    /// <inheritdoc />
    public JObject BuildQueryPlan(string sqlQuery, CosmosSqlQuery? parsed, string collectionRid)
    {
        var queryInfo = new JObject
        {
            ["distinctType"] = "None",
            ["top"] = null,
            ["offset"] = null,
            ["limit"] = null,
            ["orderBy"] = new JArray(),
            ["orderByExpressions"] = new JArray(),
            ["groupByExpressions"] = new JArray(),
            ["groupByAliases"] = new JArray(),
            ["aggregates"] = new JArray(),
            ["groupByAliasToAggregateType"] = new JObject(),
            ["rewrittenQuery"] = "",
            ["hasSelectValue"] = false,
            ["hasNonStreamingOrderBy"] = false
        };

        if (parsed is not null)
        {
            BuildFromParsedQuery(queryInfo, parsed, sqlQuery);
        }
        else
        {
            queryInfo["rewrittenQuery"] = sqlQuery;
        }

        AddCountDistinctInfo(queryInfo, sqlQuery);

        return new JObject
        {
            ["partitionedQueryExecutionInfoVersion"] = FakeCosmosHandler.QueryPlanVersion,
            ["queryInfo"] = queryInfo,
            ["queryRanges"] = new JArray(new JObject
            {
                ["min"] = "",
                ["max"] = "FF",
                ["isMinInclusive"] = true,
                ["isMaxInclusive"] = false
            })
        };
    }

    private static void BuildFromParsedQuery(JObject queryInfo, CosmosSqlQuery parsed, string sqlQuery)
    {
        if (parsed.OrderByFields is { Length: > 0 })
        {
            var orderByArr = new JArray();
            var orderByExprArr = new JArray();
            foreach (var field in parsed.OrderByFields)
            {
                orderByArr.Add(field.Ascending ? "Ascending" : "Descending");
                orderByExprArr.Add(field.Field ?? CosmosSqlParser.ExprToString(field.Expression));
            }

            queryInfo["orderBy"] = orderByArr;
            queryInfo["orderByExpressions"] = orderByExprArr;
            queryInfo["hasNonStreamingOrderBy"] = true;
        }

        if (parsed.TopCount.HasValue)
        {
            queryInfo["top"] = parsed.TopCount.Value;
        }

        if (parsed.Offset.HasValue)
        {
            queryInfo["offset"] = parsed.Offset.Value;
        }

        if (parsed.Limit.HasValue)
        {
            queryInfo["limit"] = parsed.Limit.Value;
        }

        if (parsed.IsDistinct)
        {
            var isOrdered = false;
            if (parsed.IsValueSelect && parsed.OrderByFields is { Length: > 0 } && parsed.SelectFields.Length == 1)
            {
                var selectExpr = CosmosSqlParser.ExprToString(parsed.SelectFields[0].SqlExpr);
                var orderExpr = parsed.OrderByFields[0].Field ?? CosmosSqlParser.ExprToString(parsed.OrderByFields[0].Expression);
                isOrdered = string.Equals(selectExpr, orderExpr, StringComparison.OrdinalIgnoreCase);
            }
            queryInfo["distinctType"] = isOrdered ? "Ordered" : "Unordered";
        }

        if (parsed.GroupByFields is { Length: > 0 })
        {
            queryInfo["groupByExpressions"] = new JArray(parsed.GroupByFields);
            var aliases = new JArray();
            foreach (var sf in parsed.SelectFields)
            {
                if (sf.Alias is not null)
                    aliases.Add(sf.Alias);
                else if (sf.Expression is not null)
                    aliases.Add(sf.Expression);
            }
            queryInfo["groupByAliases"] = aliases;
        }

        var aggregates = new JArray();
        var groupByAliasToAgg = new JObject();
        foreach (var field in parsed.SelectFields)
        {
            DetectAggregates(field.SqlExpr, aggregates, groupByAliasToAgg, field.Alias);
        }

        if (aggregates.Count > 0)
        {
            queryInfo["aggregates"] = aggregates;
        }

        if (groupByAliasToAgg.Count > 0)
        {
            queryInfo["groupByAliasToAggregateType"] = groupByAliasToAgg;
        }

        if (parsed.IsValueSelect)
        {
            queryInfo["hasSelectValue"] = true;
        }

        var isGroupByBypass = parsed.GroupByFields is { Length: > 0 };
        var aggregateFieldCount = parsed.SelectFields.Count(f => ContainsAggregate(f.SqlExpr));
        var isAggregateBypass = !isGroupByBypass && aggregateFieldCount > 0;
        var isCountIfBypass = !isGroupByBypass
            && parsed.SelectFields.Any(f => ContainsCountIf(f.SqlExpr));
        var isLiteralWithAggregateBypass = !isGroupByBypass && !parsed.IsValueSelect
            && aggregateFieldCount > 0
            && parsed.SelectFields.Any(f => !ContainsAggregate(f.SqlExpr)
                && f.SqlExpr is not null and not IdentifierExpression and not PropertyAccessExpression);

        if (isGroupByBypass || isAggregateBypass || isCountIfBypass || isLiteralWithAggregateBypass)
        {
            queryInfo["groupByExpressions"] = new JArray();
            queryInfo["groupByAliases"] = new JArray();
            queryInfo["groupByAliasToAggregateType"] = new JObject();
            queryInfo["aggregates"] = new JArray();
            if (isGroupByBypass)
            {
                queryInfo["hasNonStreamingOrderBy"] = false;
                queryInfo["orderBy"] = new JArray();
                queryInfo["orderByExpressions"] = new JArray();
            }
        }

        if (parsed.OrderByFields is { Length: > 0 })
        {
            queryInfo["rewrittenQuery"] = GatewayOrderByQueryRewriter.BuildOrderByRewrittenQuery(parsed);
        }
        else if (parsed.Offset.HasValue || parsed.Limit.HasValue)
        {
            queryInfo["rewrittenQuery"] = GatewayOrderByQueryRewriter.StripOffsetLimit(sqlQuery);
        }
        else
        {
            queryInfo["rewrittenQuery"] = sqlQuery;
        }
    }

    internal static void DetectAggregates(
        SqlExpression? expr, JArray aggregates, JObject groupByAliasToAgg, string? alias)
    {
        if (expr is FunctionCallExpression func)
        {
            var name = func.FunctionName.ToUpperInvariant();
            string? aggType = name switch
            {
                "COUNT" => "Count",
                "COUNTIF" => "CountIf",
                "SUM" => "Sum",
                "MIN" => "Min",
                "MAX" => "Max",
                "AVG" => "Average",
                _ => null
            };

            if (aggType is not null)
            {
                if (!aggregates.Any(a => a.ToString() == aggType))
                {
                    aggregates.Add(aggType);
                }

                if (alias is not null)
                {
                    groupByAliasToAgg[alias] = aggType;
                }
            }
            else
            {
                foreach (var arg in func.Arguments)
                {
                    DetectAggregates(arg, aggregates, groupByAliasToAgg, alias);
                }
            }
        }
        else if (expr is BinaryExpression bin)
        {
            DetectAggregates(bin.Left, aggregates, groupByAliasToAgg, alias);
            DetectAggregates(bin.Right, aggregates, groupByAliasToAgg, alias);
        }
        else if (expr is UnaryExpression unary)
        {
            DetectAggregates(unary.Operand, aggregates, groupByAliasToAgg, alias);
        }
        else if (expr is TernaryExpression ternary)
        {
            DetectAggregates(ternary.Condition, aggregates, groupByAliasToAgg, alias);
            DetectAggregates(ternary.IfTrue, aggregates, groupByAliasToAgg, alias);
            DetectAggregates(ternary.IfFalse, aggregates, groupByAliasToAgg, alias);
        }
        else if (expr is CoalesceExpression coalesce)
        {
            DetectAggregates(coalesce.Left, aggregates, groupByAliasToAgg, alias);
            DetectAggregates(coalesce.Right, aggregates, groupByAliasToAgg, alias);
        }
    }

    internal static bool ContainsAggregate(SqlExpression? expr)
    {
        return expr switch
        {
            FunctionCallExpression func =>
                func.FunctionName.ToUpperInvariant() is "COUNT" or "COUNTIF" or "SUM" or "MIN" or "MAX" or "AVG"
                || func.Arguments.Any(ContainsAggregate),
            BinaryExpression bin => ContainsAggregate(bin.Left) || ContainsAggregate(bin.Right),
            UnaryExpression unary => ContainsAggregate(unary.Operand),
            TernaryExpression ternary => ContainsAggregate(ternary.Condition) || ContainsAggregate(ternary.IfTrue) || ContainsAggregate(ternary.IfFalse),
            CoalesceExpression coalesce => ContainsAggregate(coalesce.Left) || ContainsAggregate(coalesce.Right),
            _ => false
        };
    }

    private static bool ContainsCountIf(SqlExpression? expr)
    {
        return expr switch
        {
            FunctionCallExpression func =>
                func.FunctionName.Equals("COUNTIF", StringComparison.OrdinalIgnoreCase)
                || func.Arguments.Any(ContainsCountIf),
            BinaryExpression bin => ContainsCountIf(bin.Left) || ContainsCountIf(bin.Right),
            UnaryExpression unary => ContainsCountIf(unary.Operand),
            TernaryExpression ternary => ContainsCountIf(ternary.Condition) || ContainsCountIf(ternary.IfTrue) || ContainsCountIf(ternary.IfFalse),
            CoalesceExpression coalesce => ContainsCountIf(coalesce.Left) || ContainsCountIf(coalesce.Right),
            _ => false
        };
    }

    private static void AddCountDistinctInfo(JObject queryInfo, string sqlQuery)
    {
        var countDistinctMatch = Regex.Match(sqlQuery,
            @"COUNT\s*\(\s*DISTINCT\s+(.+?)\s*\)", RegexOptions.IgnoreCase);
        if (!countDistinctMatch.Success) return;

        var distinctExpr = countDistinctMatch.Groups[1].Value.Trim();

        var aliasMatch = Regex.Match(sqlQuery,
            @"\bFROM\s+\w+\s+(?:AS\s+)?(\w+)", RegexOptions.IgnoreCase);
        if (!aliasMatch.Success)
            aliasMatch = Regex.Match(sqlQuery, @"\bFROM\s+(\w+)", RegexOptions.IgnoreCase);
        var fromAlias = aliasMatch.Success ? aliasMatch.Groups[1].Value : "c";

        var distinctPath = distinctExpr;
        if (distinctPath.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
            distinctPath = distinctPath[(fromAlias.Length + 1)..];

        var isSimplePath = Regex.IsMatch(distinctPath, @"^[\w.]+$");

        queryInfo["dCountInfo"] = new JObject
        {
            ["dCountAlias"] = "$1",
            ["dCountExpressionBase"] = isSimplePath
                ? new JObject { ["kind"] = "PropertyRef", ["propertyPath"] = distinctPath }
                : new JObject { ["kind"] = "Expression", ["expression"] = distinctExpr }
        };
    }
}
