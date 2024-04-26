﻿namespace Horizon.Horlang.Lexxing;

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
}
