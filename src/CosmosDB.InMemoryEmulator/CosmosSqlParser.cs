#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using CosmosDB.InMemoryEmulator.Application.Query.Parsing;
using Superpower;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// Clean Architecture facade for parsing, formatting, and simplifying Cosmos DB SQL queries.
/// Delegates tokenization to <see cref="CosmosSqlTokenizer"/>, combinator parsing to
/// <see cref="CosmosSqlGrammar"/>, AST formatting to <see cref="SqlAstFormatter"/>,
/// and SDK gateway rewriting to <see cref="SdkQuerySimplifier"/>.
/// </summary>
public static class CosmosSqlParser
{
    private const int ParseCacheMaxSize = 5000;
    private static readonly ConcurrentDictionary<string, CosmosSqlQuery> ParseCache = new();

    public static CosmosSqlQuery Parse(string sql)
    {
        if (ParseCache.TryGetValue(sql, out var cached))
        {
            return cached;
        }

        CosmosSqlQuery parsed;
        try
        {
            var tokens = CosmosSqlTokenizer.Tokenize(sql);
            parsed = CosmosSqlGrammar.QueryParser.Parse(tokens);
        }
        catch (Exception ex)
        {
            throw new NotSupportedException($"Failed to parse Cosmos SQL query: {sql}", ex);
        }

        if (ParseCache.Count < ParseCacheMaxSize)
        {
            ParseCache.TryAdd(sql, parsed);
        }

        return parsed;
    }

    public static bool TryParse(string sql, [NotNullWhen(true)] out CosmosSqlQuery? result)
    {
        try
        {
            result = Parse(sql);
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
    }

    public static WhereExpression? ToWhereExpression(SqlExpression? expr) =>
        WhereExpressionConverter.ToWhereExpression(expr);

    public static SqlExpression? SimplifySdkWhereExpression(SqlExpression? expr, string? fromAlias = null) =>
        SdkQuerySimplifier.SimplifySdkWhereExpression(expr, fromAlias);

    public static string SimplifySdkQuery(CosmosSqlQuery parsed) =>
        SdkQuerySimplifier.SimplifySdkQuery(parsed);

    public static string ExprToString(SqlExpression? expr) =>
        SqlAstFormatter.ExprToString(expr);

    internal static WhereExpression? ParseWhereExpression(string expression)
    {
        var wrappedSql = $"SELECT * FROM c WHERE {expression}";
        var query = Parse(wrappedSql);
        return query.Where;
    }
}
