using System.Text;

using Horizon.HIDL.Parsing;

namespace Horizon.HIDL.Lexing;

/// <summary>
/// Cuts HIDL source into tokens. Every token knows the line and column it started on, so whoever trips over one
/// can say where. Comments come out as tokens of their own (the editor colours them in), the parser throws them away.
/// </summary>
public static class Lexer
{
    private static readonly Dictionary<string, TokenType> Keywords = new()
    {
        { "let", TokenType.Let },
        { "const", TokenType.Const },
        { "func", TokenType.Function },
        { "if", TokenType.If },
        { "else", TokenType.Else },
        { "while", TokenType.While },
        { "do", TokenType.Do },
        { "for", TokenType.For },
        { "in", TokenType.In },
        { "return", TokenType.Return },
        { "break", TokenType.Break },
        { "continue", TokenType.Continue },
        { "delete", TokenType.Delete },
        { "vec", TokenType.Vector },
    };

    /// <summary>
    /// Whether a word is one the language keeps to itself, which no variable can be called.
    /// </summary>
    public static bool IsKeyword(string word) => Keywords.ContainsKey(word);

    public static Token[] Tokenize(in string source)
    {
        string src = source;
        List<Token> tokens = [];
        int index = 0;
        int line = 1;
        int col = 1;

        char Peek(int offset = 0) => index + offset < src.Length ? src[index + offset] : '\0';

        char Consume()
        {
            if (index >= src.Length) return '\0';

            char c = src[index++];
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

        void Add(TokenType type, string value, int startLine, int startCol) => tokens.Add(new Token(type, value, startLine, startCol));

        while (index < src.Length)
        {
            char current = Peek();

            if (char.IsWhiteSpace(current))
            {
                Consume();
                continue;
            }

            int startLine = line;
            int startCol = col;

            // Comments, to the end of the line or to the closing star
            if (current == '/' && Peek(1) == '/')
            {
                StringBuilder sb = new();
                while (Peek() != '\0' && Peek() != '\n' && Peek() != '\r')
                    sb.Append(Consume());

                Add(TokenType.Comment, sb.ToString(), startLine, startCol);
                continue;
            }

            if (current == '/' && Peek(1) == '*')
            {
                StringBuilder sb = new();
                sb.Append(Consume()).Append(Consume());
                while (Peek() != '\0')
                {
                    if (Peek() == '*' && Peek(1) == '/')
                    {
                        sb.Append(Consume()).Append(Consume());
                        break;
                    }

                    sb.Append(Consume());
                }

                Add(TokenType.Comment, sb.ToString(), startLine, startCol);
                continue;
            }

            // Strings, with the usual escapes: \" \\ \n \t
            if (current == '"')
            {
                Consume();
                StringBuilder sb = new();
                while (Peek() != '\0' && Peek() != '"')
                {
                    char c = Consume();
                    if (c == '\\')
                    {
                        char escaped = Consume();
                        sb.Append(escaped switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            '0' => '\0',
                            _ => escaped
                        });
                        continue;
                    }

                    sb.Append(c);
                }

                if (Peek() != '"')
                    throw new ParseException($"The string starting at line {startLine}, column {startCol} is never closed.", startLine, startCol);

                Consume();
                Add(TokenType.TextLiteral, sb.ToString(), startLine, startCol);
                continue;
            }

            // Two and three character operators first, or they come out as two of the single ones
            if (current == '.' && Peek(1) == '.' && Peek(2) == '.')
            {
                Consume(); Consume(); Consume();
                Add(TokenType.Spread, "...", startLine, startCol);
                continue;
            }

            if (current == '=' && Peek(1) == '=') { Consume(); Consume(); Add(TokenType.Equality, "==", startLine, startCol); continue; }
            if (current == '!' && Peek(1) == '=') { Consume(); Consume(); Add(TokenType.NotEquality, "!=", startLine, startCol); continue; }
            if (current == '>' && Peek(1) == '=') { Consume(); Consume(); Add(TokenType.BinaryOperation, ">=", startLine, startCol); continue; }
            if (current == '<' && Peek(1) == '=') { Consume(); Consume(); Add(TokenType.BinaryOperation, "<=", startLine, startCol); continue; }
            if (current == '&' && Peek(1) == '&') { Consume(); Consume(); Add(TokenType.BinaryOperation, "&&", startLine, startCol); continue; }
            if (current == '|' && Peek(1) == '|') { Consume(); Consume(); Add(TokenType.BinaryOperation, "||", startLine, startCol); continue; }

            if (current is '+' or '-' or '*' or '/' or '%' && Peek(1) == '=')
            {
                Consume(); Consume();
                Add(TokenType.CompoundEquals, current.ToString(), startLine, startCol);
                continue;
            }

            bool single = true;
            switch (current)
            {
                case '(': Consume(); Add(TokenType.OpenParenthesis, "(", startLine, startCol); break;
                case ')': Consume(); Add(TokenType.CloseParenthesis, ")", startLine, startCol); break;
                case ';': Consume(); Add(TokenType.Semicolon, ";", startLine, startCol); break;
                case ':': Consume(); Add(TokenType.Colon, ":", startLine, startCol); break;
                case ',': Consume(); Add(TokenType.Comma, ",", startLine, startCol); break;
                // .5 is a number, see below
                case '.' when !char.IsDigit(Peek(1)): Consume(); Add(TokenType.Dot, ".", startLine, startCol); break;
                case '!': Consume(); Add(TokenType.Exclamation, "!", startLine, startCol); break;
                case '{': Consume(); Add(TokenType.OpenBracket, "{", startLine, startCol); break;
                case '}': Consume(); Add(TokenType.CloseBracket, "}", startLine, startCol); break;
                case '[': Consume(); Add(TokenType.OpenBrace, "[", startLine, startCol); break;
                case ']': Consume(); Add(TokenType.CloseBrace, "]", startLine, startCol); break;
                case '=': Consume(); Add(TokenType.Equals, "=", startLine, startCol); break;

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
                    Add(TokenType.BinaryOperation, current.ToString(), startLine, startCol);
                    break;

                default:
                    single = false;
                    break;
            }

            if (single) continue;

            // Numbers, digits with one dot at most. 1.5 and .5 and 10
            if (char.IsDigit(current) || (current == '.' && char.IsDigit(Peek(1))))
            {
                StringBuilder sb = new();
                bool dotted = false;
                while (char.IsDigit(Peek()) || (Peek() == '.' && !dotted && char.IsDigit(Peek(1))))
                {
                    if (Peek() == '.') dotted = true;
                    sb.Append(Consume());
                }

                Add(TokenType.Number, sb.ToString(), startLine, startCol);
                continue;
            }

            // Identifiers and keywords
            if (char.IsLetter(current) || current == '_')
            {
                StringBuilder sb = new();
                while (char.IsLetterOrDigit(Peek()) || Peek() == '_')
                    sb.Append(Consume());

                string word = sb.ToString();
                Add(Keywords.TryGetValue(word, out TokenType keyword) ? keyword : TokenType.Identifier, word, startLine, startCol);
                continue;
            }

            throw new ParseException($"There is no making sense of '{current}' at line {startLine}, column {startCol}.", startLine, startCol, 1);
        }

        tokens.Add(new Token(TokenType.EndOfFile, string.Empty, line, col));
        return [.. tokens];
    }
}
