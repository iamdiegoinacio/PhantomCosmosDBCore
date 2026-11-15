#nullable enable

namespace CosmosDB.InMemoryEmulator;

public sealed record SelectField(string Expression, string? Alias, SqlExpression? SqlExpr = null);

public sealed record JoinClause(string Alias, string SourceAlias, string ArrayField);

public sealed record OrderByField(string? Field, bool Ascending, SqlExpression? Expression = null);

public sealed record OrderByClause(string? Field, bool Ascending);

public sealed record CosmosSqlQuery(
    SelectField[] SelectFields,
    bool IsSelectAll,
    int? TopCount,
    string FromAlias,
    JoinClause? Join,
    WhereExpression? Where,
    int? Offset,
    int? Limit,
    OrderByClause? OrderBy = null,
    bool IsDistinct = false,
    bool IsValueSelect = false,
    JoinClause[]? Joins = null,
    OrderByField[]? OrderByFields = null,
    string[]? GroupByFields = null,
    SqlExpression[]? GroupByExpressions = null,
    WhereExpression? Having = null,
    SqlExpression? WhereExpr = null,
    SqlExpression? HavingExpr = null,
    string? FromSource = null,
    SqlExpression? RankExpression = null);
