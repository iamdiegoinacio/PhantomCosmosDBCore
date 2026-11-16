#nullable enable
using System.Net;
using Phantom.CosmosDB.Core.Application.Query.Evaluation;
using Phantom.CosmosDB.Core.Application.Query.Stages;
using Phantom.CosmosDB.Core.Domain.Comparison;
using Phantom.CosmosDB.Core.Domain.Validation;
using Microsoft.Azure.Cosmos;

namespace Phantom.CosmosDB.Core.Application.Query;

/// <summary>
/// Orchestrates the end-to-end execution of a Cosmos DB SQL query across all pipeline stages:
/// Computed Properties -&gt; FROM/JOIN Expansion -&gt; WHERE -&gt; GROUP BY/HAVING -&gt;
/// ORDER BY -&gt; SELECT Projection -&gt; DISTINCT -&gt; TOP/OFFSET/LIMIT.
/// </summary>
internal static class CosmosQueryPipeline
{
    public static List<string> ExecuteSafe(Func<List<string>> queryFunc)
    {
        try
        {
            return queryFunc();
        }
        catch (CosmosException)
        {
            throw;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FormatException)
        {
            throw InMemoryCosmosException.Create(
                ex.Message, HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), 0);
        }
    }

    public static List<string> Execute(
        string queryText,
        IEnumerable<string> sourceItems,
        QueryExecutionContext context,
        (string Name, string FromAlias, SqlExpression? Expr)[] computedProps,
        IndexingPolicy? indexingPolicy = null,
        bool enableScanInQuery = false)
    {
        var parsed = CosmosSqlParser.Parse(queryText);
        CosmosIndexingPolicyValidator.ValidateIndexingModeForQuery(
            indexingPolicy, parsed, enableScanInQuery);
        CosmosIndexingPolicyValidator.ValidateCompositeIndexesForOrderBy(
            indexingPolicy, parsed.OrderByFields, parsed.FromAlias);

        IEnumerable<string> items = sourceItems;

        if (computedProps.Length > 0)
        {
            items = SelectProjectionStage.AugmentWithComputedProperties(items, computedProps, context);
        }

        if (parsed.FromSource is not null)
        {
            items = JoinExpansionStage.ExpandFromSource(items, parsed);
        }

        if (parsed.Joins is { Length: > 0 })
        {
            items = JoinExpansionStage.ExpandAllJoins(items, parsed);
        }
        else if (parsed.Join is not null)
        {
            items = JoinExpansionStage.ExpandJoinedItems(items, parsed);
        }

        if (parsed.Where is not null)
        {
            items = items.Where(json =>
            {
                var jObj = JsonParseHelpers.ParseJson(json);
                return WhereExpressionEvaluator.Evaluate(parsed.Where, jObj, parsed.FromAlias, context, parsed.Join);
            });
        }

        var groupByApplied = false;
        if (parsed.GroupByFields is { Length: > 0 })
        {
            items = GroupByStage.ApplyGroupBy(items, parsed, context);
            groupByApplied = true;
        }

        if (parsed.OrderByFields is { Length: > 0 })
        {
            var effectiveOrderBy = groupByApplied
                ? OrderByStage.ResolveOrderByAliasesAfterGroupBy(parsed.OrderByFields, parsed.SelectFields)
                : parsed.OrderByFields;
            items = OrderByStage.ApplyOrderByFields(items, effectiveOrderBy, parsed.FromAlias, context);
        }
        else if (parsed.OrderBy is not null)
        {
            items = OrderByStage.ApplyOrderBy(items, parsed.OrderBy, parsed.FromAlias);
        }
        else if (parsed.RankExpression is not null)
        {
            items = OrderByStage.ApplyOrderByRank(items, parsed.RankExpression, parsed.FromAlias, context);
        }

        if (parsed.TopCount.HasValue && !parsed.IsDistinct)
        {
            items = items.Take(parsed.TopCount.Value);
        }

        if (!parsed.IsDistinct && parsed.Offset.HasValue)
        {
            items = items.Skip(parsed.Offset.Value);
        }

        if (!parsed.IsDistinct && parsed.Limit.HasValue)
        {
            items = items.Take(parsed.Limit.Value);
        }

        if (!groupByApplied && !parsed.IsSelectAll && parsed.SelectFields.Length > 0)
        {
            items = SelectProjectionStage.ProjectFields(items, parsed, context);
        }

        if (parsed.IsSelectAll && computedProps.Length > 0)
        {
            items = SelectProjectionStage.StripComputedProperties(items, computedProps);
        }

        if (parsed.IsDistinct)
        {
            items = items.Distinct(JsonStructuralStringComparer.Instance)!;
        }

        if (parsed.IsDistinct && parsed.Offset.HasValue)
        {
            items = items.Skip(parsed.Offset.Value);
        }

        if (parsed.IsDistinct && parsed.Limit.HasValue)
        {
            items = items.Take(parsed.Limit.Value);
        }

        if (parsed.TopCount.HasValue && parsed.IsDistinct)
        {
            items = items.Take(parsed.TopCount.Value);
        }

        if (parsed.IsValueSelect && parsed.SelectFields.Length == 1)
        {
            items = SelectProjectionStage.UnwrapValueSelect(items);
        }

        return items as List<string> ?? items.ToList();
    }
}
