namespace Horizon.Horlang.Lexxing;

public enum TokenType : byte
{
    Let,
    Number,
    Equals,
    Dot,
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
}
