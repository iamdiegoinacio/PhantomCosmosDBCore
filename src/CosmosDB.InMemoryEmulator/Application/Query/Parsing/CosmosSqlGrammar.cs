#nullable enable
using System.Globalization;
using Superpower;
using Superpower.Model;
using Superpower.Parsers;

namespace CosmosDB.InMemoryEmulator.Application.Query.Parsing;

/// <summary>
/// Token-based Superpower parser combinator grammar for Cosmos DB SQL queries.
/// </summary>
internal static class CosmosSqlGrammar
{
    private static string TokenSpanToString(Token<CosmosSqlToken> token) =>
        token.Span.ToStringValue();

    private static TokenListParser<CosmosSqlToken, string> AnyIdentifierOrKeyword =>
        Token.EqualTo(CosmosSqlToken.Identifier).Select(TokenSpanToString)
            .Or(Token.EqualTo(CosmosSqlToken.Value).Select(TokenSpanToString))
            .Or(Token.EqualTo(CosmosSqlToken.Array).Select(TokenSpanToString))
            .Or(Token.EqualTo(CosmosSqlToken.Defined).Select(TokenSpanToString))
            .Or(Token.EqualTo(CosmosSqlToken.Asc).Select(TokenSpanToString))
            .Or(Token.EqualTo(CosmosSqlToken.Desc).Select(TokenSpanToString))
            .Or(Token.EqualTo(CosmosSqlToken.Top).Select(TokenSpanToString))
            .Or(Token.EqualTo(CosmosSqlToken.Limit).Select(TokenSpanToString))
            .Or(Token.EqualTo(CosmosSqlToken.Offset).Select(TokenSpanToString))
            .Or(Token.EqualTo(CosmosSqlToken.Null).Select(TokenSpanToString))
            .Or(Token.EqualTo(CosmosSqlToken.True).Select(TokenSpanToString))
            .Or(Token.EqualTo(CosmosSqlToken.False).Select(TokenSpanToString));

    // ── Dotted path: ident.ident.ident ──

    private static readonly TokenListParser<CosmosSqlToken, string> DottedPath =
        from first in AnyIdentifierOrKeyword
        from rest in (
            from dot in Token.EqualTo(CosmosSqlToken.Dot)
            from next in AnyIdentifierOrKeyword
            select "." + next
        ).Many()
        select first + string.Concat(rest);

    // ── Dotted path with optional array indexing: ident.ident[0].ident ──

    private static readonly TokenListParser<CosmosSqlToken, string> BracketedStringContent =
        Token.EqualTo(CosmosSqlToken.StringLiteral).Select(t => t.Span.ToStringValue()[1..^1])
            .Or(Token.EqualTo(CosmosSqlToken.DoubleQuotedString).Select(t => t.Span.ToStringValue()[1..^1]));

    private static readonly TokenListParser<CosmosSqlToken, string> DottedPathWithIndex =
        from first in AnyIdentifierOrKeyword
        from rest in (
            from dot in Token.EqualTo(CosmosSqlToken.Dot)
            from next in AnyIdentifierOrKeyword
            select "." + next
        ).Or(
            (from open in Token.EqualTo(CosmosSqlToken.OpenBracket)
             from idx in Token.EqualTo(CosmosSqlToken.NumberLiteral).Select(TokenSpanToString)
             from close in Token.EqualTo(CosmosSqlToken.CloseBracket)
             select "[" + idx + "]").Try()
        ).Or(
            (from open in Token.EqualTo(CosmosSqlToken.OpenBracket)
             from str in BracketedStringContent
             from close in Token.EqualTo(CosmosSqlToken.CloseBracket)
             select "." + str).Try()
        ).Many()
        select first + string.Concat(rest);

    // ── Primary expression ──

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> StringLiteral =
        Token.EqualTo(CosmosSqlToken.StringLiteral)
            .Select(t =>
            {
                var raw = t.Span.ToStringValue();
                var unquoted = raw[1..^1].Replace("''", "'");
                return (SqlExpression)new LiteralExpression(unquoted);
            })
        .Or(Token.EqualTo(CosmosSqlToken.DoubleQuotedString)
            .Select(t =>
            {
                var raw = t.Span.ToStringValue();
                var unquoted = raw[1..^1];
                return (SqlExpression)new LiteralExpression(unquoted);
            }));

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> NumberLiteral =
        Token.EqualTo(CosmosSqlToken.NumberLiteral)
            .Select(t =>
            {
                var raw = t.Span.ToStringValue();
                if (long.TryParse(raw, out var longVal))
                {
                    return (SqlExpression)new LiteralExpression(longVal);
                }

                return (SqlExpression)new LiteralExpression(double.Parse(raw, CultureInfo.InvariantCulture));
            });

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> TrueLiteral =
        Token.EqualTo(CosmosSqlToken.True).Select(_ => (SqlExpression)new LiteralExpression(true));

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> FalseLiteral =
        Token.EqualTo(CosmosSqlToken.False).Select(_ => (SqlExpression)new LiteralExpression(false));

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> NullLiteral =
        Token.EqualTo(CosmosSqlToken.Null).Select(_ => (SqlExpression)new LiteralExpression(null));

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> UndefinedLiteral =
        Token.EqualTo(CosmosSqlToken.Undefined).Select(_ => (SqlExpression)new UndefinedLiteralExpression());

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> ParameterExpr =
        Token.EqualTo(CosmosSqlToken.Parameter)
            .Select(t => (SqlExpression)new ParameterExpression(t.Span.ToStringValue()));

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> FunctionCall =
        from name in AnyIdentifierOrKeyword
        from open in Token.EqualTo(CosmosSqlToken.OpenParen)
        from args in Token.EqualTo(CosmosSqlToken.Star).Select(_ => (SqlExpression)new IdentifierExpression("*"))
            .Or(Superpower.Parse.Ref(() => Expr!))
            .ManyDelimitedBy(Token.EqualTo(CosmosSqlToken.Comma))
        from close in Token.EqualTo(CosmosSqlToken.CloseParen)
        select (SqlExpression)new FunctionCallExpression(name.ToUpperInvariant(), args);

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> DottedFunctionCall =
        from first in AnyIdentifierOrKeyword
        from dot in Token.EqualTo(CosmosSqlToken.Dot)
        from second in AnyIdentifierOrKeyword
        from open in Token.EqualTo(CosmosSqlToken.OpenParen)
        from args in Superpower.Parse.Ref(() => Expr!).ManyDelimitedBy(Token.EqualTo(CosmosSqlToken.Comma))
        from close in Token.EqualTo(CosmosSqlToken.CloseParen)
        select (SqlExpression)new FunctionCallExpression(
            first.Equals("udf", StringComparison.OrdinalIgnoreCase)
                ? "UDF." + second
                : (first + "." + second).ToUpperInvariant(), args);

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> ExistsExpr =
        from kw in Token.EqualTo(CosmosSqlToken.Exists)
        from open in Token.EqualTo(CosmosSqlToken.OpenParen)
        from inner in CaptureBalancedParens()
        from close in Token.EqualTo(CosmosSqlToken.CloseParen)
        select (SqlExpression)new ExistsExpression(inner);

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> SubqueryParens =
        from open in Token.EqualTo(CosmosSqlToken.OpenParen)
        from subquery in Superpower.Parse.Ref(() => QueryParser!)
        from close in Token.EqualTo(CosmosSqlToken.CloseParen)
        select (SqlExpression)new SubqueryExpression(subquery);

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> Parens =
        SubqueryParens.Try()
        .Or(
            from open in Token.EqualTo(CosmosSqlToken.OpenParen)
            from expr in Superpower.Parse.Ref(() => Expr!)
            from close in Token.EqualTo(CosmosSqlToken.CloseParen)
            select expr);

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> ObjectLiteral =
        from open in Token.EqualTo(CosmosSqlToken.OpenBrace)
        from props in (
            from key in AnyIdentifierOrKeyword
                .Or(Token.EqualTo(CosmosSqlToken.DoubleQuotedString).Select(t => t.Span.ToStringValue()[1..^1]))
                .Or(Token.EqualTo(CosmosSqlToken.StringLiteral).Select(t => t.Span.ToStringValue()[1..^1]))
            from colon in Token.EqualTo(CosmosSqlToken.Colon)
            from value in Superpower.Parse.Ref(() => Expr!)
            select new KeyValuePair<string, SqlExpression>(key, value)
        ).ManyDelimitedBy(Token.EqualTo(CosmosSqlToken.Comma))
        from close in Token.EqualTo(CosmosSqlToken.CloseBrace)
        select (SqlExpression)new ObjectLiteralExpression(props);

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> ArrayLiteral =
        from open in Token.EqualTo(CosmosSqlToken.OpenBracket)
        from elements in Superpower.Parse.Ref(() => Expr!).ManyDelimitedBy(Token.EqualTo(CosmosSqlToken.Comma))
        from close in Token.EqualTo(CosmosSqlToken.CloseBracket)
        select (SqlExpression)new ArrayLiteralExpression(elements);

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> IdentExpr =
        DottedPathWithIndex.Select(path => (SqlExpression)new IdentifierExpression(path));

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> ArraySubqueryCall =
        from name in Token.EqualTo(CosmosSqlToken.Array)
        from open in Token.EqualTo(CosmosSqlToken.OpenParen)
        from subquery in Superpower.Parse.Ref(() => QueryParser!)
        from close in Token.EqualTo(CosmosSqlToken.CloseParen)
        select (SqlExpression)new FunctionCallExpression("ARRAY", [new SubqueryExpression(subquery)]);

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> Primary =
        StringLiteral
            .Or(NumberLiteral)
            .Or(TrueLiteral)
            .Or(FalseLiteral)
            .Or(NullLiteral)
            .Or(UndefinedLiteral)
            .Or(ParameterExpr)
            .Or(ExistsExpr.Try())
            .Or(ArraySubqueryCall.Try())
            .Or(DottedFunctionCall.Try())
            .Or(FunctionCall.Try())
            .Or(Parens)
            .Or(ObjectLiteral.Try())
            .Or(ArrayLiteral.Try())
            .Or(IdentExpr);

    // ── Unary ──

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> UnaryExpr =
        (from op in Token.EqualTo(CosmosSqlToken.Not)
         from operand in Superpower.Parse.Ref(() => UnaryExpr!)
         select (SqlExpression)new UnaryExpression(UnaryOp.Not, operand))
        .Or(
            from op in Token.EqualTo(CosmosSqlToken.Minus)
            from operand in Primary
            select (SqlExpression)new UnaryExpression(UnaryOp.Negate, operand))
        .Or(
            from op in Token.EqualTo(CosmosSqlToken.Tilde)
            from operand in Primary
            select (SqlExpression)new UnaryExpression(UnaryOp.BitwiseNot, operand))
        .Or(Primary);

    // ── Multiplicative: *, /, % ──

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> Multiplicative =
        Superpower.Parse.Chain(
            Token.EqualTo(CosmosSqlToken.Star).Select(_ => BinaryOp.Multiply)
                .Or(Token.EqualTo(CosmosSqlToken.Slash).Select(_ => BinaryOp.Divide))
                .Or(Token.EqualTo(CosmosSqlToken.Percent).Select(_ => BinaryOp.Modulo)),
            UnaryExpr,
            (op, left, right) => new BinaryExpression(left, op, right));

    // ── Additive: +, - ──

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> Additive =
        Superpower.Parse.Chain(
            Token.EqualTo(CosmosSqlToken.Plus).Select(_ => BinaryOp.Add)
                .Or(Token.EqualTo(CosmosSqlToken.Minus).Select(_ => BinaryOp.Subtract)),
            Multiplicative,
            (op, left, right) => new BinaryExpression(left, op, right));

    // ── Bitwise AND: & ──

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> BitwiseAndExpr =
        Superpower.Parse.Chain(
            Token.EqualTo(CosmosSqlToken.Ampersand).Select(_ => BinaryOp.BitwiseAnd),
            Additive,
            (op, left, right) => new BinaryExpression(left, op, right));

    // ── Bitwise XOR: ^ ──

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> BitwiseXorExpr =
        Superpower.Parse.Chain(
            Token.EqualTo(CosmosSqlToken.Caret).Select(_ => BinaryOp.BitwiseXor),
            BitwiseAndExpr,
            (op, left, right) => new BinaryExpression(left, op, right));

    // ── Bitwise OR: | ──

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> BitwiseOrExpr =
        Superpower.Parse.Chain(
            Token.EqualTo(CosmosSqlToken.Pipe).Select(_ => BinaryOp.BitwiseOr),
            BitwiseXorExpr,
            (op, left, right) => new BinaryExpression(left, op, right));

    // ── String concat: || ──

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> StringConcatExpr =
        Superpower.Parse.Chain(
            Token.EqualTo(CosmosSqlToken.DoublePipe).Select(_ => BinaryOp.StringConcat),
            BitwiseOrExpr,
            (op, left, right) => new BinaryExpression(left, op, right));

    // ── Comparison: =, !=, <, >, <=, >=, LIKE ──

    private static readonly TokenListParser<CosmosSqlToken, BinaryOp> CompOp =
        Token.EqualTo(CosmosSqlToken.Equals).Select(_ => BinaryOp.Equal)
            .Or(Token.EqualTo(CosmosSqlToken.NotEquals).Select(_ => BinaryOp.NotEqual))
            .Or(Token.EqualTo(CosmosSqlToken.LessThanOrEqual).Select(_ => BinaryOp.LessThanOrEqual))
            .Or(Token.EqualTo(CosmosSqlToken.GreaterThanOrEqual).Select(_ => BinaryOp.GreaterThanOrEqual))
            .Or(Token.EqualTo(CosmosSqlToken.LessThan).Select(_ => BinaryOp.LessThan))
            .Or(Token.EqualTo(CosmosSqlToken.GreaterThan).Select(_ => BinaryOp.GreaterThan))
            .Or(Token.EqualTo(CosmosSqlToken.Like).Select(_ => BinaryOp.Like));

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> Comparison =
        from left in StringConcatExpr
        from rest in (
            (from kw in Token.EqualTo(CosmosSqlToken.Between)
             from low in StringConcatExpr
             from and in Token.EqualTo(CosmosSqlToken.And)
             from high in StringConcatExpr
             select (Func<SqlExpression, SqlExpression>)(l => new BetweenExpression(l, low, high)))
            .Or(
                (from not in Token.EqualTo(CosmosSqlToken.Not)
                 from kw in Token.EqualTo(CosmosSqlToken.Between)
                 from low in StringConcatExpr
                 from and in Token.EqualTo(CosmosSqlToken.And)
                 from high in StringConcatExpr
                 select (Func<SqlExpression, SqlExpression>)(l => new UnaryExpression(UnaryOp.Not, new BetweenExpression(l, low, high))))
                .Try())
            .Or(
                from kw in Token.EqualTo(CosmosSqlToken.In)
                from open in Token.EqualTo(CosmosSqlToken.OpenParen)
                from vals in Superpower.Parse.Ref(() => Expr!).ManyDelimitedBy(Token.EqualTo(CosmosSqlToken.Comma))
                from close in Token.EqualTo(CosmosSqlToken.CloseParen)
                select (Func<SqlExpression, SqlExpression>)(l => new InExpression(l, vals)))
            .Or(
                (from not in Token.EqualTo(CosmosSqlToken.Not)
                 from kw in Token.EqualTo(CosmosSqlToken.In)
                 from open in Token.EqualTo(CosmosSqlToken.OpenParen)
                 from vals in Superpower.Parse.Ref(() => Expr!).ManyDelimitedBy(Token.EqualTo(CosmosSqlToken.Comma))
                 from close in Token.EqualTo(CosmosSqlToken.CloseParen)
                 select (Func<SqlExpression, SqlExpression>)(l => new UnaryExpression(UnaryOp.Not, new InExpression(l, vals))))
                .Try())
            .Or(
                (from kw in Token.EqualTo(CosmosSqlToken.Like)
                 from pattern in StringConcatExpr
                 from esc in Token.EqualTo(CosmosSqlToken.Escape)
                 from escChar in Token.EqualTo(CosmosSqlToken.StringLiteral)
                 select (Func<SqlExpression, SqlExpression>)(l => new LikeExpression(l, pattern, escChar.Span.ToStringValue()[1..^1])))
                .Try())
            .Or(
                (from not in Token.EqualTo(CosmosSqlToken.Not)
                 from kw in Token.EqualTo(CosmosSqlToken.Like)
                 from pattern in StringConcatExpr
                 from esc in Token.EqualTo(CosmosSqlToken.Escape)
                 from escChar in Token.EqualTo(CosmosSqlToken.StringLiteral)
                 select (Func<SqlExpression, SqlExpression>)(l => new UnaryExpression(UnaryOp.Not, new LikeExpression(l, pattern, escChar.Span.ToStringValue()[1..^1]))))
                .Try())
            .Or(
                (from not in Token.EqualTo(CosmosSqlToken.Not)
                 from kw in Token.EqualTo(CosmosSqlToken.Like)
                 from pattern in StringConcatExpr
                 select (Func<SqlExpression, SqlExpression>)(l => new UnaryExpression(UnaryOp.Not, new BinaryExpression(l, BinaryOp.Like, pattern))))
                .Try())
            .Or(
                (from is_ in Token.EqualTo(CosmosSqlToken.Is)
                 from not_ in Token.EqualTo(CosmosSqlToken.Not)
                 from null_ in Token.EqualTo(CosmosSqlToken.Null)
                 select (Func<SqlExpression, SqlExpression>)(l => new BinaryExpression(l, BinaryOp.NotEqual, new LiteralExpression(null))))
                .Try())
            .Or(
                from is_ in Token.EqualTo(CosmosSqlToken.Is)
                from null_ in Token.EqualTo(CosmosSqlToken.Null)
                select (Func<SqlExpression, SqlExpression>)(l => new BinaryExpression(l, BinaryOp.Equal, new LiteralExpression(null))))
            .Or(
                from op in CompOp
                from right in StringConcatExpr
                select (Func<SqlExpression, SqlExpression>)(l => new BinaryExpression(l, op, right)))
        ).OptionalOrDefault(null)
        select rest != null ? rest(left) : left;

    // ── Logical AND ──

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> AndExpr =
        Superpower.Parse.Chain(
            Token.EqualTo(CosmosSqlToken.And).Select(_ => BinaryOp.And),
            Comparison,
            (op, left, right) => new BinaryExpression(left, op, right));

    // ── Logical OR ──

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> OrExpr =
        Superpower.Parse.Chain(
            Token.EqualTo(CosmosSqlToken.Or).Select(_ => BinaryOp.Or),
            AndExpr,
            (op, left, right) => new BinaryExpression(left, op, right));

    // ── Null coalesce: ?? ──

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> CoalesceExpr =
        from left in OrExpr
        from rest in (
            from op in Token.EqualTo(CosmosSqlToken.QuestionQuestion)
            from right in Superpower.Parse.Ref(() => CoalesceExpr!)
            select right
        ).OptionalOrDefault(null)
        select rest != null ? new CoalesceExpression(left, rest) : left;

    // ── Ternary: cond ? then : else ──

    private static readonly TokenListParser<CosmosSqlToken, SqlExpression> TernaryExpr =
        from cond in CoalesceExpr
        from rest in (
            from q in Token.EqualTo(CosmosSqlToken.Question)
            from ifTrue in Superpower.Parse.Ref(() => Expr!)
            from colon in Token.EqualTo(CosmosSqlToken.Colon)
            from ifFalse in Superpower.Parse.Ref(() => Expr!)
            select new { ifTrue, ifFalse }
        ).OptionalOrDefault(null)
        select rest != null ? (SqlExpression)new TernaryExpression(cond, rest.ifTrue, rest.ifFalse) : cond;

    // ── Top-level expression ──

    internal static readonly TokenListParser<CosmosSqlToken, SqlExpression> Expr = TernaryExpr;

    // ── SELECT field parsing ──

    private static readonly TokenListParser<CosmosSqlToken, SelectField> StarField =
        Token.EqualTo(CosmosSqlToken.Star).Select(_ => new SelectField("*", null));

    private static readonly TokenListParser<CosmosSqlToken, SelectField> AliasDotStarField =
        from ident in AnyIdentifierOrKeyword
        from dot in Token.EqualTo(CosmosSqlToken.Dot)
        from star in Token.EqualTo(CosmosSqlToken.Star)
        select new SelectField("*", null);

    private static readonly TokenListParser<CosmosSqlToken, SelectField> ExpressionField =
        from expr in Expr
        from alias in (
            from as_ in Token.EqualTo(CosmosSqlToken.As)
            from name in AnyIdentifierOrKeyword
            select (string?)name
        ).OptionalOrDefault(null)
        select new SelectField(SqlAstFormatter.ExprToString(expr), alias, expr);

    private static readonly TokenListParser<CosmosSqlToken, SelectField> SingleSelectField =
        StarField.Try().Or(AliasDotStarField.Try()).Or(ExpressionField);

    // ── JOIN clause parsing ──

    private static readonly TokenListParser<CosmosSqlToken, JoinClause> JoinParser =
        from kw in Token.EqualTo(CosmosSqlToken.Join)
        from alias in AnyIdentifierOrKeyword
        from in_ in Token.EqualTo(CosmosSqlToken.In)
        from source in DottedPathWithIndex
        select ParseJoinSource(alias, source);

    // ── ORDER BY parsing ──

    private static readonly TokenListParser<CosmosSqlToken, OrderByField> OrderByFieldParser =
        (from expr in Superpower.Parse.Ref(() => Expr!)
         from dir in Token.EqualTo(CosmosSqlToken.Asc).Select(_ => true)
             .Or(Token.EqualTo(CosmosSqlToken.Desc).Select(_ => false))
             .OptionalOrDefault(true)
         select expr is IdentifierExpression ident
             ? new OrderByField(ident.Name, dir)
             : new OrderByField(null, dir, expr)).Try()
        .Or(
        from field in DottedPathWithIndex
        from dir in Token.EqualTo(CosmosSqlToken.Asc).Select(_ => true)
            .Or(Token.EqualTo(CosmosSqlToken.Desc).Select(_ => false))
            .OptionalOrDefault(true)
        select new OrderByField(field, dir));

    // ── Full query parsing ──

    public static readonly TokenListParser<CosmosSqlToken, CosmosSqlQuery> QueryParser =
        from select_ in Token.EqualTo(CosmosSqlToken.Select)
        from distinct in Token.EqualTo(CosmosSqlToken.Distinct).OptionalOrDefault()
        from top in (
            from topKw in Token.EqualTo(CosmosSqlToken.Top)
            from n in Token.EqualTo(CosmosSqlToken.NumberLiteral).Select(t => int.Parse(t.Span.ToStringValue()))
            select (int?)n
        ).OptionalOrDefault(null)
        from value_ in Token.EqualTo(CosmosSqlToken.Value).OptionalOrDefault()
        from fields in SingleSelectField.ManyDelimitedBy(Token.EqualTo(CosmosSqlToken.Comma))
        from fromKw in Token.EqualTo(CosmosSqlToken.From)
        from fromFirstIdent in AnyIdentifierOrKeyword
        from fromClause in (
            from in_ in Token.EqualTo(CosmosSqlToken.In)
            from source in DottedPath
            select (Alias: fromFirstIdent, Source: (string?)source)
        ).Try().Or(
            from as_ in Token.EqualTo(CosmosSqlToken.As)
            from alias in AnyIdentifierOrKeyword
            select (Alias: alias, Source: (string?)null)
        ).Try().Or(
            from alias in Token.EqualTo(CosmosSqlToken.Identifier).Select(TokenSpanToString)
            select (Alias: alias, Source: (string?)null)
        ).Try().OptionalOrDefault((Alias: fromFirstIdent, Source: (string?)null))
        from joins in JoinParser.Many()
        from where_ in (
            from whereKw in Token.EqualTo(CosmosSqlToken.Where)
            from expr in Expr
            select (SqlExpression?)expr
        ).OptionalOrDefault(null)
        from groupBy in (
            from groupKw in Token.EqualTo(CosmosSqlToken.Group)
            from byKw in Token.EqualTo(CosmosSqlToken.By)
            from groupFields in Expr.ManyDelimitedBy(Token.EqualTo(CosmosSqlToken.Comma))
            select (SqlExpression[]?)groupFields
        ).OptionalOrDefault(null)
        from having in (
            from havingKw in Token.EqualTo(CosmosSqlToken.Having)
            from expr in Expr
            select (SqlExpression?)expr
        ).OptionalOrDefault(null)
        from orderByResult in (
            from orderKw in Token.EqualTo(CosmosSqlToken.Order)
            from byKw in Token.EqualTo(CosmosSqlToken.By)
            from result in (
                from rankKw in Token.EqualTo(CosmosSqlToken.Identifier)
                    .Where(t => string.Equals(t.Span.ToStringValue(), "RANK", StringComparison.OrdinalIgnoreCase))
                from expr in Expr
                select (Fields: (OrderByField[]?)null, RankExpr: (SqlExpression?)expr)
            ).Try().Or(
                from orderFields in OrderByFieldParser.ManyDelimitedBy(Token.EqualTo(CosmosSqlToken.Comma))
                select (Fields: (OrderByField[]?)orderFields, RankExpr: (SqlExpression?)null)
            )
            select result
        ).OptionalOrDefault(default)
        from offset in (
            from offsetKw in Token.EqualTo(CosmosSqlToken.Offset)
            from n in Token.EqualTo(CosmosSqlToken.NumberLiteral).Select(t => int.Parse(t.Span.ToStringValue()))
            select (int?)n
        ).OptionalOrDefault(null)
        from limit in (
            from limitKw in Token.EqualTo(CosmosSqlToken.Limit)
            from n in Token.EqualTo(CosmosSqlToken.NumberLiteral).Select(t => int.Parse(t.Span.ToStringValue()))
            select (int?)n
        ).OptionalOrDefault(null)
        select BuildQuery(
            distinct.HasValue, top, value_.HasValue, fields,
            fromClause.Alias, fromClause.Source, joins, where_,
            groupBy?.Select(SqlAstFormatter.ExprToString).ToArray(),
            groupBy?.ToArray(),
            having, orderByResult.Fields, offset, limit, orderByResult.RankExpr);

    private static JoinClause ParseJoinSource(string alias, string source)
    {
        var dotIdx = source.IndexOf('.');
        if (dotIdx < 0)
        {
            return new JoinClause(alias, source, source);
        }

        return new JoinClause(alias, source[..dotIdx], source[(dotIdx + 1)..]);
    }

    private static CosmosSqlQuery BuildQuery(
        bool isDistinct, int? top, bool isValue,
        SelectField[] fields, string fromAlias, string? fromSource,
        JoinClause[] joins, SqlExpression? whereExpr,
        string[]? groupBy, SqlExpression[]? groupByExpressions, SqlExpression? havingExpr,
        OrderByField[]? orderByFields,
        int? offset, int? limit, SqlExpression? rankExpr = null)
    {
        var isSelectAll = fields.Length == 1 && fields[0].Expression == "*";
        var where = whereExpr != null ? WhereExpressionConverter.ToWhereExpression(whereExpr) : null;
        var having = havingExpr != null ? new SqlExpressionCondition(havingExpr) : null;
        var rawHavingExpr = havingExpr;

        var firstJoin = joins.Length > 0 ? joins[0] : null;
        OrderByClause? legacyOrderBy = null;
        if (orderByFields is { Length: > 0 })
        {
            legacyOrderBy = new OrderByClause(orderByFields[0].Field, orderByFields[0].Ascending);
        }

        return new CosmosSqlQuery(
            SelectFields: fields,
            IsSelectAll: isSelectAll,
            TopCount: top,
            FromAlias: fromAlias,
            Join: firstJoin,
            Where: where,
            Offset: offset,
            Limit: limit,
            OrderBy: legacyOrderBy,
            IsDistinct: isDistinct,
            IsValueSelect: isValue,
            Joins: joins,
            OrderByFields: orderByFields,
            GroupByFields: groupBy,
            GroupByExpressions: groupByExpressions,
            Having: having,
            WhereExpr: whereExpr,
            HavingExpr: rawHavingExpr,
            FromSource: fromSource,
            RankExpression: rankExpr);
    }

    private static TokenListParser<CosmosSqlToken, string> CaptureBalancedParens()
    {
        return input =>
        {
            var depth = 0;
            var current = input;
            var parts = new List<string>();

            while (!current.IsAtEnd)
            {
                var token = current.ConsumeToken();
                if (!token.HasValue)
                {
                    break;
                }

                if (token.Value.Kind == CosmosSqlToken.OpenParen)
                {
                    depth++;
                    parts.Add("(");
                    current = token.Remainder;
                }
                else if (token.Value.Kind == CosmosSqlToken.CloseParen)
                {
                    if (depth == 0)
                    {
                        return TokenListParserResult.Value(string.Join(" ", parts), input, current);
                    }
                    depth--;
                    parts.Add(")");
                    current = token.Remainder;
                }
                else
                {
                    parts.Add(token.Value.Span.ToStringValue());
                    current = token.Remainder;
                }
            }

            return TokenListParserResult.Value(string.Join(" ", parts), input, current);
        };
    }
}
