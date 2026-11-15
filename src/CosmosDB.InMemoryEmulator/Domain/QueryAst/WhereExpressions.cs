#nullable enable

namespace CosmosDB.InMemoryEmulator;

public enum ComparisonOp
{
    Equal,
    NotEqual,
    LessThan,
    GreaterThan,
    LessThanOrEqual,
    GreaterThanOrEqual,
    Like,
}

public abstract record WhereExpression;

public sealed record ComparisonCondition(string Left, ComparisonOp Operator, string Right) : WhereExpression;

public sealed record FunctionCondition(string FunctionName, string[] Arguments) : WhereExpression;

public sealed record AndCondition(WhereExpression Left, WhereExpression Right) : WhereExpression;

public sealed record OrCondition(WhereExpression Left, WhereExpression Right) : WhereExpression;

public sealed record NotCondition(WhereExpression Inner) : WhereExpression;

public sealed record ExistsCondition(string RawSubquery) : WhereExpression;

public sealed record SqlExpressionCondition(SqlExpression Expression) : WhereExpression;
