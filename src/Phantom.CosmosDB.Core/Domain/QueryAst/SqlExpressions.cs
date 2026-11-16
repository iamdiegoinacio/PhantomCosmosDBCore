#nullable enable

namespace Phantom.CosmosDB.Core;

public abstract record SqlExpression;

public sealed record LiteralExpression(object? Value) : SqlExpression;

/// <summary>
/// Represents the Cosmos SQL <c>undefined</c> keyword. Distinguished from the
/// string literal <c>'undefined'</c> so the evaluator can map it to the runtime
/// undefined sentinel rather than a defined string value.
/// </summary>
public sealed record UndefinedLiteralExpression : SqlExpression;

public sealed record IdentifierExpression(string Name) : SqlExpression;

public sealed record ParameterExpression(string Name) : SqlExpression;

public sealed record PropertyAccessExpression(SqlExpression Object, string Property) : SqlExpression;

public sealed record IndexAccessExpression(SqlExpression Object, SqlExpression Index) : SqlExpression;

public sealed record BinaryExpression(SqlExpression Left, BinaryOp Operator, SqlExpression Right) : SqlExpression;

public sealed record UnaryExpression(UnaryOp Operator, SqlExpression Operand) : SqlExpression;

public sealed record FunctionCallExpression(string FunctionName, SqlExpression[] Arguments) : SqlExpression;

public sealed record BetweenExpression(SqlExpression Value, SqlExpression Low, SqlExpression High) : SqlExpression;

public sealed record InExpression(SqlExpression Value, SqlExpression[] List) : SqlExpression;

public sealed record LikeExpression(SqlExpression Value, SqlExpression Pattern, string? EscapeChar) : SqlExpression;

public sealed record ExistsExpression(string RawSubquery) : SqlExpression;

public sealed record SubqueryExpression(CosmosSqlQuery Subquery) : SqlExpression;

public sealed record TernaryExpression(SqlExpression Condition, SqlExpression IfTrue, SqlExpression IfFalse) : SqlExpression;

public sealed record CoalesceExpression(SqlExpression Left, SqlExpression Right) : SqlExpression;

public sealed record ObjectLiteralExpression(KeyValuePair<string, SqlExpression>[] Properties) : SqlExpression;

public sealed record ArrayLiteralExpression(SqlExpression[] Elements) : SqlExpression;

public enum BinaryOp
{
    Equal,
    NotEqual,
    LessThan,
    GreaterThan,
    LessThanOrEqual,
    GreaterThanOrEqual,
    And,
    Or,
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    Like,
    StringConcat,
}

public enum UnaryOp
{
    Not,
    Negate,
    BitwiseNot,
}
