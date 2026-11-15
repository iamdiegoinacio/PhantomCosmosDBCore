#nullable enable
using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using Superpower.Tokenizers;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// Lexical analyzer for Cosmos DB SQL queries using Superpower combinators.
/// </summary>
public static class CosmosSqlTokenizer
{
    private static readonly TextParser<Unit> StringLiteralToken =
        from open in Character.EqualTo('\'')
        from content in Character.EqualTo('\'').Then(_ => Character.EqualTo('\'')).Try()
            .Or(Character.Except('\''))
            .Many()
        from close in Character.EqualTo('\'')
        select Unit.Value;

    private static readonly TextParser<Unit> DoubleQuotedStringToken =
        from open in Character.EqualTo('"')
        from content in Character.Except('"').Many()
        from close in Character.EqualTo('"')
        select Unit.Value;

    private static readonly TextParser<Unit> ParameterToken =
        from at in Character.EqualTo('@')
        from rest in Character.LetterOrDigit.Or(Character.EqualTo('_')).AtLeastOnce()
        select Unit.Value;

    private static readonly TextParser<Unit> NumberToken =
        from number in Numerics.Decimal
        from exp in (
            from e in Character.EqualTo('e').Or(Character.EqualTo('E'))
            from sign in Character.EqualTo('+').Or(Character.EqualTo('-')).OptionalOrDefault()
            from digits in Character.Digit.AtLeastOnce()
            select Unit.Value
        ).OptionalOrDefault()
        select Unit.Value;

    // Ref: EF Core Cosmos provider uses $type as a discriminator column.
    //   Allow $ as a valid identifier character to support queries like c.$type.
    private static readonly TextParser<Unit> IdentOrKeyword =
        from first in Character.Letter.Or(Character.EqualTo('_')).Or(Character.EqualTo('$'))
        from rest in Character.LetterOrDigit.Or(Character.EqualTo('_')).Or(Character.EqualTo('$')).Many()
        select Unit.Value;

    private static readonly Dictionary<string, CosmosSqlToken> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELECT"] = CosmosSqlToken.Select,
        ["DISTINCT"] = CosmosSqlToken.Distinct,
        ["TOP"] = CosmosSqlToken.Top,
        ["VALUE"] = CosmosSqlToken.Value,
        ["AS"] = CosmosSqlToken.As,
        ["FROM"] = CosmosSqlToken.From,
        ["JOIN"] = CosmosSqlToken.Join,
        ["IN"] = CosmosSqlToken.In,
        ["WHERE"] = CosmosSqlToken.Where,
        ["AND"] = CosmosSqlToken.And,
        ["OR"] = CosmosSqlToken.Or,
        ["NOT"] = CosmosSqlToken.Not,
        ["BETWEEN"] = CosmosSqlToken.Between,
        ["LIKE"] = CosmosSqlToken.Like,
        ["IS"] = CosmosSqlToken.Is,
        ["NULL"] = CosmosSqlToken.Null,
        ["DEFINED"] = CosmosSqlToken.Defined,
        ["UNDEFINED"] = CosmosSqlToken.Undefined,
        ["TRUE"] = CosmosSqlToken.True,
        ["FALSE"] = CosmosSqlToken.False,
        ["EXISTS"] = CosmosSqlToken.Exists,
        ["ORDER"] = CosmosSqlToken.Order,
        ["BY"] = CosmosSqlToken.By,
        ["ASC"] = CosmosSqlToken.Asc,
        ["DESC"] = CosmosSqlToken.Desc,
        ["GROUP"] = CosmosSqlToken.Group,
        ["HAVING"] = CosmosSqlToken.Having,
        ["OFFSET"] = CosmosSqlToken.Offset,
        ["LIMIT"] = CosmosSqlToken.Limit,
        ["ARRAY"] = CosmosSqlToken.Array,
        ["ESCAPE"] = CosmosSqlToken.Escape,
    };

    private static readonly Tokenizer<CosmosSqlToken> Inner = new TokenizerBuilder<CosmosSqlToken>()
        .Ignore(Span.WhiteSpace)
        .Match(StringLiteralToken, CosmosSqlToken.StringLiteral)
        .Match(DoubleQuotedStringToken, CosmosSqlToken.DoubleQuotedString)
        .Match(ParameterToken, CosmosSqlToken.Parameter)
        .Match(NumberToken, CosmosSqlToken.NumberLiteral)
        .Match(IdentOrKeyword, CosmosSqlToken.Identifier)
        .Match(Character.EqualTo('*'), CosmosSqlToken.Star)
        .Match(Character.EqualTo(','), CosmosSqlToken.Comma)
        .Match(Character.EqualTo('.'), CosmosSqlToken.Dot)
        .Match(Character.EqualTo('('), CosmosSqlToken.OpenParen)
        .Match(Character.EqualTo(')'), CosmosSqlToken.CloseParen)
        .Match(Character.EqualTo('['), CosmosSqlToken.OpenBracket)
        .Match(Character.EqualTo(']'), CosmosSqlToken.CloseBracket)
        .Match(Span.EqualTo("!="), CosmosSqlToken.NotEquals)
        .Match(Span.EqualTo("<>"), CosmosSqlToken.NotEquals)
        .Match(Span.EqualTo("<="), CosmosSqlToken.LessThanOrEqual)
        .Match(Span.EqualTo(">="), CosmosSqlToken.GreaterThanOrEqual)
        .Match(Span.EqualTo("??"), CosmosSqlToken.QuestionQuestion)
        .Match(Span.EqualTo("||"), CosmosSqlToken.DoublePipe)
        .Match(Character.EqualTo('='), CosmosSqlToken.Equals)
        .Match(Character.EqualTo('<'), CosmosSqlToken.LessThan)
        .Match(Character.EqualTo('>'), CosmosSqlToken.GreaterThan)
        .Match(Character.EqualTo('+'), CosmosSqlToken.Plus)
        .Match(Character.EqualTo('-'), CosmosSqlToken.Minus)
        .Match(Character.EqualTo('/'), CosmosSqlToken.Slash)
        .Match(Character.EqualTo('%'), CosmosSqlToken.Percent)
        .Match(Character.EqualTo('&'), CosmosSqlToken.Ampersand)
        .Match(Character.EqualTo('|'), CosmosSqlToken.Pipe)
        .Match(Character.EqualTo('^'), CosmosSqlToken.Caret)
        .Match(Character.EqualTo('~'), CosmosSqlToken.Tilde)
        .Match(Character.EqualTo('?'), CosmosSqlToken.Question)
        .Match(Character.EqualTo(':'), CosmosSqlToken.Colon)
        .Match(Character.EqualTo('{'), CosmosSqlToken.OpenBrace)
        .Match(Character.EqualTo('}'), CosmosSqlToken.CloseBrace)
        .Build();

    public static TokenList<CosmosSqlToken> Tokenize(string input)
    {
        var tokens = Inner.Tokenize(input);
        var remapped = tokens.Select(token =>
        {
            if (token.Kind == CosmosSqlToken.Identifier &&
                Keywords.TryGetValue(token.Span.ToStringValue(), out var keyword))
            {
                return new Token<CosmosSqlToken>(keyword, token.Span);
            }
            return token;
        }).ToArray();
        return new TokenList<CosmosSqlToken>(remapped);
    }
}
