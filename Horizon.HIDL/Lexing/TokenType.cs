namespace Horizon.HIDL.Lexing;

public enum TokenType : byte
{
    Let,
    Const,
    Number,
    Equals,
    Dot,
    If,
    While,
    Do,
    Break,
    Delete,
    OpenParenthesis,
    CloseParenthesis,
    BinaryOperation,
    Identifier,
    Semicolon,
    Comma,
    EndOfFile,
    Null,
    OpenBracket,
    CloseBracket,
    OpenBrace,
    CloseBrace,
    Colon,
    Function,
    Quote,
    TextLiteral,
    Exclamation,
    Equality,
    NotEquality,
    Vector,
    Comment,

    // else, for (x in list), return, continue
    Else,
    For,
    In,
    Return,
    Continue,

    // The three dots that spread an object or a list into another: { ...base, damage: 8 }
    Spread,

    // +=, -=, *=, /=, %=, with the operator as the value
    CompoundEquals,

    // The ? of a ? b : c
    Question,

    // A character the language has no use for, which only the lenient lexing of an editor hands out
    Unknown,
}
