using System.Text;

namespace Horizon.Horlang.Lexxing;

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
        };
    }

    public static Token[] Tokenize(in string source)
    {
        // final currentToken array
        List<Token> tokens = [];

        // flag for if a currentToken has been identified yet
        bool foundTokenFlag;

        // flag for parsing strings
        bool inString = false;

        // helper function to construct currentToken and set flag
        void AddToken(in TokenType type, in string value)
        {
            tokens.Add(new Token(type, value));
            foundTokenFlag = true;
        }

        Queue<char> characters = new(source.ToCharArray());

        char prev = '0';
        while (characters.Count != 0)
        {
            char character = characters.Dequeue();
            foundTokenFlag = false;

            if (inString)
            {
                StringBuilder sb = new();
                while (inString)
                {
                    if (character == '"')
                    {
                        inString = false;
                        break;
                    }
                    sb.Append(character);
                    character = characters.Dequeue();
                    if (character == '"')
                    {
                        inString = false;
                        break;
                    }
                }
                AddToken(TokenType.TextLiteral, sb.ToString());
            }
            else
            {
                // match skippable characters
                if (char.IsWhiteSpace(character) || character == '\t' || character == '\n' || character == '\r')
                    continue;

                // match basic single character Tokens
                switch (character)
                {
                    case '(':
                        AddToken(TokenType.OpenParenthesis, character.ToString());
                        break;

                    case ')':
                        AddToken(TokenType.CloseParenthesis, character.ToString());
                        break;

                    case ';':
                        AddToken(TokenType.Semicolon, character.ToString());
                        break;

                    case ':':
                        AddToken(TokenType.Colon, character.ToString());
                        break;

                    case ',':
                        AddToken(TokenType.Comma, character.ToString());
                        break;

                    case '.':
                        AddToken(TokenType.Dot, character.ToString());
                        break;

                    case '!':
                        if (characters.Peek() != '=')
                            AddToken(TokenType.Exclamation, character.ToString());
                        break;

                    case '{':
                        AddToken(TokenType.OpenBracket, character.ToString());
                        break;

                    case '"':
                        inString = true;
                        continue;
                    case '}':
                        AddToken(TokenType.CloseBracket, character.ToString());
                        break;

                    case '[':
                        AddToken(TokenType.OpenBrace, character.ToString());
                        break;

                    case ']':
                        AddToken(TokenType.CloseBrace, character.ToString());
                        break;

                    case '+':
                    case '-':
                    case '*':
                    case '/':
                    case '%':
                    case '<':
                    case '>':
                    case '|':
                    case '&':
                        AddToken(TokenType.BinaryOperation, character.ToString());
                        break;

                    case '=':
                        if (characters.Peek() != '=' && prev != '!')
                            AddToken(TokenType.Equals, character.ToString());
                        break;
                }

                // match multicharacter Tokens
                if (!foundTokenFlag)
                {
                    // try matching numbers
                    if (char.IsNumber(character))
                    {
                        StringBuilder sb = new();

                        // append initial character
                        sb.Append(character);

                        // add numbers and progress queue until next char isn't a number
                        while (characters.Count != 0 && (char.IsNumber(characters.Peek()) || characters.Peek() == '.'))
                            sb.Append(characters.Dequeue());

                        AddToken(TokenType.Number, sb.ToString());
                    }
                    // match assignee
                    else if (char.IsLetter(character) || character == '_' || (character == '=' && characters.Peek() == '=') || (character == '!' && characters.Peek() == '='))
                    {
                        StringBuilder sb = new();

                        // append initial character
                        sb.Append(character);

                        // add numbers and progress queue until next char isnt a letter or special op
                        while (characters.Count != 0 && (char.IsLetter(characters.Peek()) || characters.Peek() == '_' || characters.Peek() == '=') || (character == '!' && characters.Peek() == '='))
                            sb.Append(characters.Dequeue());

                        string finalValue = sb.ToString();

                        TokenType type = TokenType.Identifier;
                        if (Keywords.TryGetValue(finalValue, out TokenType newType))
                            type = newType;

                        AddToken(type, finalValue);
                    }
                }

                // if we haven't found a known currentToken, panic
                if (!foundTokenFlag)
                    Console.WriteLine($"Failed to tokenize character '{character}'!");

                prev = character;
            }
        }

        // push EOF currentToken
        tokens.Add(new(TokenType.EndOfFile, string.Empty));
        return [.. tokens];
    }
}