using System.Text;
using System.Collections.Generic;

namespace Horizon.HIDL.Lexxing;

public static class Lexer
{
    private static readonly Dictionary<string, TokenType> Keywords;

    static Lexer()
    {
        // define reserved keywords and match to currentToken type.
        Keywords = new()
        {
            {"let", TokenType.Let },
            {"const", TokenType.Const },
            {"func", TokenType.Function },
            {"if", TokenType.If },
            {"while", TokenType.While },
            {"do", TokenType.Do},
            {"delete", TokenType.Delete},
            {"break", TokenType.Break},
            {"==", TokenType.Equality},
            {"!=", TokenType.NotEquality},
            {"vec", TokenType.Vector},
        };
    }

    public static Token[] Tokenize(in string source)
    {
        string src = source;
        List<Token> tokens = [];
        int index = 0;
        int line = 1;
        int col = 1;

        char Peek(int offset = 0)
        {
            if (index + offset >= src.Length) return '\0';
            return src[index + offset];
        }

        char Consume()
        {
            if (index >= src.Length) return '\0';
            char c = src[index];
            index++;
            if (c == '\n')
            {
                line++;
                col = 1;
            }
            else
            {
                col++;
            }
            return c;
        }

        void AddToken(TokenType type, string value, int startLine, int startCol)
        {
            tokens.Add(new Token(type, value, startLine, startCol));
        }

        while (index < src.Length)
        {
            char current = Peek();

            // Skip whitespace
            if (char.IsWhiteSpace(current))
            {
                Consume();
                continue;
            }

            int startLine = line;
            int startCol = col;

            // Comments
            if (current == '/' && Peek(1) == '/')
            {
                StringBuilder sb = new();
                sb.Append(Consume()); // '/'
                sb.Append(Consume()); // '/'
                while (Peek() != '\0' && Peek() != '\n' && Peek() != '\r')
                {
                    sb.Append(Consume());
                }
                AddToken(TokenType.Comment, sb.ToString(), startLine, startCol);
                continue;
            }
            if (current == '/' && Peek(1) == '*')
            {
                StringBuilder sb = new();
                sb.Append(Consume()); // '/'
                sb.Append(Consume()); // '*'
                while (Peek() != '\0')
                {
                    if (Peek() == '*' && Peek(1) == '/')
                    {
                        sb.Append(Consume()); // '*'
                        sb.Append(Consume()); // '/'
                        break;
                    }
                    sb.Append(Consume());
                }
                AddToken(TokenType.Comment, sb.ToString(), startLine, startCol);
                continue;
            }

            // Strings
            if (current == '"')
            {
                Consume(); // '"'
                StringBuilder sb = new();
                while (Peek() != '\0' && Peek() != '"')
                {
                    sb.Append(Consume());
                }
                if (Peek() == '"')
                {
                    Consume(); // '"'
                }
                AddToken(TokenType.TextLiteral, sb.ToString(), startLine, startCol);
                continue;
            }

            // Equality operators
            if (current == '=' && Peek(1) == '=')
            {
                Consume(); // '='
                Consume(); // '='
                AddToken(TokenType.Equality, "==", startLine, startCol);
                continue;
            }
            if (current == '!' && Peek(1) == '=')
            {
                Consume(); // '!'
                Consume(); // '='
                AddToken(TokenType.NotEquality, "!=", startLine, startCol);
                continue;
            }

            // Single character operators and punctuation
            bool singleMatch = true;
            switch (current)
            {
                case '(':
                    Consume();
                    AddToken(TokenType.OpenParenthesis, "(", startLine, startCol);
                    break;

                case ')':
                    Consume();
                    AddToken(TokenType.CloseParenthesis, ")", startLine, startCol);
                    break;

                case ';':
                    Consume();
                    AddToken(TokenType.Semicolon, ";", startLine, startCol);
                    break;

                case ':':
                    Consume();
                    AddToken(TokenType.Colon, ":", startLine, startCol);
                    break;

                case ',':
                    Consume();
                    AddToken(TokenType.Comma, ",", startLine, startCol);
                    break;

                case '.':
                    Consume();
                    AddToken(TokenType.Dot, ".", startLine, startCol);
                    break;

                case '!':
                    Consume();
                    AddToken(TokenType.Exclamation, "!", startLine, startCol);
                    break;

                case '{':
                    Consume();
                    AddToken(TokenType.OpenBracket, "{", startLine, startCol);
                    break;

                case '}':
                    Consume();
                    AddToken(TokenType.CloseBracket, "}", startLine, startCol);
                    break;

                case '[':
                    Consume();
                    AddToken(TokenType.OpenBrace, "[", startLine, startCol);
                    break;

                case ']':
                    Consume();
                    AddToken(TokenType.CloseBrace, "]", startLine, startCol);
                    break;

                case '/':
                case '+':
                case '-':
                case '*':
                case '%':
                case '<':
                case '>':
                case '|':
                case '&':
                    Consume();
                    AddToken(TokenType.BinaryOperation, current.ToString(), startLine, startCol);
                    break;

                case '=':
                    Consume();
                    AddToken(TokenType.Equals, "=", startLine, startCol);
                    break;

                default:
                    singleMatch = false;
                    break;
            }

            if (singleMatch) continue;

            // Numbers
            if (char.IsDigit(current))
            {
                StringBuilder sb = new();
                while (char.IsDigit(Peek()) || Peek() == '.')
                {
                    sb.Append(Consume());
                }
                AddToken(TokenType.Number, sb.ToString(), startLine, startCol);
                continue;
            }

            // Identifiers / Keywords
            if (char.IsLetter(current) || current == '_')
            {
                StringBuilder sb = new();
                while (char.IsLetterOrDigit(Peek()) || Peek() == '_')
                {
                    sb.Append(Consume());
                }
                string finalValue = sb.ToString();
                TokenType type = TokenType.Identifier;
                if (Keywords.TryGetValue(finalValue, out TokenType keywordType))
                {
                    type = keywordType;
                }
                AddToken(type, finalValue, startLine, startCol);
                continue;
            }

            // Fallback for unexpected characters
            throw new Parsing.ParseException($"Failed to tokenize character '{current}' at line {startLine}, column {startCol}!", startLine, startCol, 1);
        }

        tokens.Add(new Token(TokenType.EndOfFile, string.Empty, line, col));
        return [.. tokens];
    }
}