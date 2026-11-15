#nullable enable
using CosmosDB.InMemoryEmulator.Application.Query.Evaluation;
using CosmosDB.InMemoryEmulator.Domain.Comparison;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.Query.Stages;

/// <summary>
/// Query pipeline stage that applies <c>ORDER BY</c>, <c>ORDER BY RANK</c>, and post-<c>GROUP BY</c> alias resolution.
/// </summary>
internal static class OrderByStage
{
    public static List<string> ApplyOrderBy(IEnumerable<string> items, OrderByClause orderBy, string fromAlias)
        => ApplyOrderByFields(items, [new OrderByField(orderBy.Field, orderBy.Ascending)], fromAlias);

    public static List<string> ApplyOrderByRank(
        IEnumerable<string> items,
        SqlExpression rankExpr,
        string fromAlias,
        QueryExecutionContext context)
    {
        return items
            .OrderByDescending(json =>
            {
                var jObj = JsonParseHelpers.ParseJson(json);
                var score = SqlExpressionEvaluator.Evaluate(rankExpr, jObj, fromAlias, context);
                return score switch
                {
                    double d => d,
                    long l => (double)l,
                    int i => (double)i,
                    _ => 0.0
                };
            })
            .ToList();
    }

    public static List<string> ApplyOrderByFields(
        IEnumerable<string> items,
        OrderByField[] orderByFields,
        string fromAlias,
        QueryExecutionContext? context = null)
    {
        if (orderByFields.Length == 0)
        {
            return items.ToList();
        }

        var effectiveContext = context ?? new QueryExecutionContext();
        IOrderedEnumerable<string>? ordered = null;
        foreach (var field in orderByFields)
        {
            object? KeySelector(string json)
            {
                var jObj = JsonParseHelpers.ParseJson(json);

                if (field.Expression is not null)
                {
                    var value = SqlExpressionEvaluator.Evaluate(field.Expression, jObj, fromAlias, effectiveContext);
                    return value switch
                    {
                        double d => d,
                        long l => (double)l,
                        int i => (double)i,
                        decimal dec => (double)dec,
                        bool b => b,
                        string s => s,
                        UndefinedValue => CosmosValueComparer.UndefinedSortSentinel,
                        null => null,
                        JToken jt => jt,
                        _ => value.ToString()
                    };
                }

                var fieldPath = field.Field ?? string.Empty;
                if (fieldPath.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                {
                    fieldPath = fieldPath[(fromAlias.Length + 1)..];
                }

                var token = jObj.SelectToken(fieldPath);
                if (token is null)
                {
                    return CosmosValueComparer.UndefinedSortSentinel;
                }

                return token.Type switch
                {
                    JTokenType.Null => null,
                    JTokenType.Boolean => token.Value<bool>(),
                    JTokenType.Integer => token.Value<long>(),
                    JTokenType.Float => token.Value<double>(),
                    JTokenType.String => token.Value<string>(),
                    _ => token
                };
            }

            var asc = field.Ascending;
            var comparer = Comparer<object?>.Create((l, r) =>
            {
                var result = CosmosValueComparer.CompareValues(l, r);
                return asc ? result : -result;
            });
            ordered = ordered == null ? items.OrderBy(KeySelector, comparer) : ordered.ThenBy(KeySelector, comparer);
        }

        return ordered?.ToList() ?? items.ToList();
    }

    public static OrderByField[] ResolveOrderByAliasesAfterGroupBy(
        OrderByField[] orderByFields,
        SelectField[] selectFields)
    {
        var resolved = new OrderByField[orderByFields.Length];
        for (var i = 0; i < orderByFields.Length; i++)
        {
            var obf = orderByFields[i];
            if (obf.Expression is FunctionCallExpression func
                && SqlExpressionEvaluator.AggregateFunctions.Contains(func.FunctionName))
            {
                var orderByText = CosmosSqlParser.ExprToString(obf.Expression);
                OrderByField? matched = null;
                foreach (var sf in selectFields)
                {
                    if (sf.Alias is not null && sf.Expression.Trim().Equals(
                            orderByText, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = new OrderByField(sf.Alias, obf.Ascending);
                        break;
                    }
                }

                resolved[i] = matched ?? obf;
            }
            else
            {
                resolved[i] = obf;
            }
        }

        return resolved;
    }
}
