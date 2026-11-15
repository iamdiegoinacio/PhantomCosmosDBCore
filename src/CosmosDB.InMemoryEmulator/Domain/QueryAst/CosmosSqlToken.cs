#nullable enable

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// Lexical token kinds recognized by the Cosmos DB SQL tokenizer and parser.
/// </summary>
public enum CosmosSqlToken
{
    // Keywords
    Select,
    Distinct,
    Top,
    Value,
    As,
    From,
    Join,
    In,
    Where,
    And,
    Or,
    Not,
    Between,
    Like,
    Is,
    Null,
    Defined,
    Undefined,
    True,
    False,
    Exists,
    Order,
    By,
    Asc,
    Desc,
    Group,
    Having,
    Offset,
    Limit,
    Array,
    Escape,

    // Literals & identifiers
    Identifier,
    StringLiteral,
    DoubleQuotedString,
    NumberLiteral,
    Parameter,

    // Operators & punctuation
    Star,
    Comma,
    Dot,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    Equals,
    NotEquals,
    LessThanOrEqual,
    GreaterThanOrEqual,
    LessThan,
    GreaterThan,
    Plus,
    Minus,
    Slash,
    Percent,
    Ampersand,
    Pipe,
    Caret,
    Tilde,
    QuestionQuestion,
    Question,
    Colon,
    DoublePipe,
    OpenBrace,
    CloseBrace,
}
