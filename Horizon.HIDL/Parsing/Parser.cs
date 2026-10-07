using System.Globalization;

using Horizon.HIDL.Lexing;

namespace Horizon.HIDL.Parsing;

/// <summary>
/// Turns the tokens of a HIDL program into a syntax tree. Anything wrong with the program throws a
/// <see cref="ParseException"/> that says what was expected and where.
/// <para>
/// The language has let and const, functions (named and anonymous) with return, if / else if / else, while,
/// do while, for (x in something), break and continue, objects and lists (with spreads of other ones and trailing
/// commas), vec(x, y[, z[, w]]), the usual arithmetic and comparisons, &amp;&amp; and ||, and compound assignment.
/// Semicolons are optional. Comments go anywhere.
/// </para>
/// </summary>
public class Parser
{
    private Queue<Token> Tokens { get; set; } = new();

    private Token Peek() => Tokens.Peek();

    private Token Consume() => Tokens.Dequeue();

    private Token Consume(TokenType expected, string? what = null)
    {
        Token token = Peek();
        if (token.Type != expected)
            throw Unexpected(token, what ?? Describe(expected));

        return Tokens.Dequeue();
    }

    private bool Next(TokenType type) => Peek().Type == type;

    private bool NextOperator(params string[] operators) =>
        Peek().Type == TokenType.BinaryOperation && Array.IndexOf(operators, Peek().Value) >= 0;

    private static ParseException Unexpected(Token token, string expected)
    {
        string got = token.Type == TokenType.EndOfFile ? "the end of the file" : $"'{token.Value}'";
        return new ParseException($"Expected {expected} but found {got} at line {token.Line}, column {token.Col}.", token.Line, token.Col, Math.Max(1, token.Value.Length));
    }

    private static string Describe(TokenType type) => type switch
    {
        TokenType.Identifier => "a name",
        TokenType.OpenParenthesis => "'('",
        TokenType.CloseParenthesis => "')'",
        TokenType.OpenBracket => "'{'",
        TokenType.CloseBracket => "'}'",
        TokenType.OpenBrace => "'['",
        TokenType.CloseBrace => "']'",
        TokenType.Colon => "':'",
        TokenType.Equals => "'='",
        TokenType.In => "'in'",
        TokenType.While => "'while'",
        TokenType.Semicolon => "';'",
        _ => type.ToString()
    };

    public ProgramStatement ProduceSyntaxTree(in Token[] inputTokens)
    {
        // Comments are for people, the parser never sees them
        Tokens = new Queue<Token>(inputTokens.Where(token => token.Type != TokenType.Comment));
        List<IStatement> statements = [];

        while (!Next(TokenType.EndOfFile))
        {
            if (ParseStatement() is { } statement)
                statements.Add(statement);
        }

        return new ProgramStatement { Body = statements };
    }

    /* Statements */

    private IStatement? ParseStatement()
    {
        if (Next(TokenType.Semicolon))
        {
            Consume();
            return null;
        }

        IStatement statement = Peek().Type switch
        {
            TokenType.Let or TokenType.Const => ParseVariableDeclaration(),
            TokenType.Delete => ParseDeleteStatement(),
            TokenType.Return => ParseReturnStatement(),
            TokenType.Break => ParseKeywordStatement(TokenType.Break, new BreakStatement()),
            TokenType.Continue => ParseKeywordStatement(TokenType.Continue, new ContinueStatement()),
            TokenType.For => ParseForStatement(),
            TokenType.Function when PeekAhead(1).Type == TokenType.Identifier => ParseFunctionDeclaration(),
            _ => ParseExpression(),
        };

        // A semicolon after a statement is welcome and never needed
        if (Next(TokenType.Semicolon))
            Consume();

        return statement;
    }

    private Token PeekAhead(int offset)
    {
        int i = 0;
        foreach (Token token in Tokens)
        {
            if (i++ == offset)
                return token;
        }

        return new Token(TokenType.EndOfFile, string.Empty);
    }

    private IStatement ParseKeywordStatement(TokenType keyword, IStatement statement)
    {
        Consume(keyword);
        return statement;
    }

    private IStatement ParseDeleteStatement()
    {
        Consume(TokenType.Delete);
        string identifier = Consume(TokenType.Identifier, "the name of the variable to delete").Value;
        return new DeleteStatement(identifier);
    }

    private IStatement ParseReturnStatement()
    {
        Consume(TokenType.Return);

        // A bare return hands back null. The value, if there is one, is on the same line as far as the grammar cares
        if (Next(TokenType.Semicolon) || Next(TokenType.CloseBracket) || Next(TokenType.EndOfFile))
            return new ReturnStatement(null);

        return new ReturnStatement(ParseExpression());
    }

    private IStatement ParseForStatement()
    {
        Consume(TokenType.For);
        Consume(TokenType.OpenParenthesis);

        // for (let x in list) and for (x in list) both do
        if (Next(TokenType.Let) || Next(TokenType.Const))
            Consume();

        string variable = Consume(TokenType.Identifier, "the name of the loop variable").Value;
        Consume(TokenType.In);
        IExpression source = ParseExpression();
        Consume(TokenType.CloseParenthesis);

        return new ForInStatement(variable, source, ParseBlock());
    }

    private IStatement ParseVariableDeclaration()
    {
        bool isConst = Consume().Type == TokenType.Const;
        Token name = Consume(TokenType.Identifier, "the name of the variable");

        if (Lexer.IsKeyword(name.Value))
            throw new ParseException($"'{name.Value}' is a word of the language, a variable can't be called that (line {name.Line}).", name.Line, name.Col);

        if (!Next(TokenType.Equals))
        {
            if (isConst)
                throw new ParseException($"The constant '{name.Value}' needs a value (line {name.Line}).", name.Line, name.Col);

            return new VariableDeclarationExpression(name.Value, null, false);
        }

        Consume(TokenType.Equals);
        return new VariableDeclarationExpression(name.Value, ParseExpression(), isConst);
    }

    private IExpression ParseFunctionDeclaration()
    {
        Consume(TokenType.Function);

        if (Next(TokenType.Identifier))
        {
            string name = Consume().Value;
            var (parameters, body) = ParseParametersAndBody();
            return new FunctionDeclarationExpression(name, parameters, body);
        }

        var (anonymousParameters, anonymousBody) = ParseParametersAndBody();
        return new AnonymousFunctionDeclarationExpression(anonymousParameters, anonymousBody);
    }

    private (string[] Parameters, IStatement[] Body) ParseParametersAndBody()
    {
        Consume(TokenType.OpenParenthesis);
        List<string> parameters = [];

        while (!Next(TokenType.CloseParenthesis))
        {
            parameters.Add(Consume(TokenType.Identifier, "the name of a parameter").Value);
            if (Next(TokenType.Comma))
                Consume();
            else
                break;
        }

        Consume(TokenType.CloseParenthesis);
        return ([.. parameters], ParseBlock());
    }

    /// <summary>
    /// Helper method to read <c>{ statements }</c>.
    /// </summary>
    private IStatement[] ParseBlock()
    {
        Consume(TokenType.OpenBracket, "'{'");
        List<IStatement> body = [];

        while (!Next(TokenType.CloseBracket))
        {
            if (Next(TokenType.EndOfFile))
                throw Unexpected(Peek(), "'}' to close the block");

            if (ParseStatement() is { } statement)
                body.Add(statement);
        }

        Consume(TokenType.CloseBracket);
        return [.. body];
    }

    private IExpression ParseIf()
    {
        Consume(TokenType.If);
        Consume(TokenType.OpenParenthesis);
        IExpression condition = ParseExpression();
        Consume(TokenType.CloseParenthesis);

        IStatement[] body = ParseBlock();
        IStatement[]? otherwise = null;

        if (Next(TokenType.Else))
        {
            Consume();

            // else if is an if inside of the else
            otherwise = Next(TokenType.If) ? [ParseIf()] : ParseBlock();
        }

        return new IfDeclarationExpression(condition, body, otherwise);
    }

    private IExpression ParseWhile()
    {
        Consume(TokenType.While);
        Consume(TokenType.OpenParenthesis);
        IExpression condition = ParseExpression();
        Consume(TokenType.CloseParenthesis);

        return new WhileDeclarationExpression(condition, ParseBlock());
    }

    private IExpression ParseDoWhile()
    {
        Consume(TokenType.Do);
        IStatement[] body = ParseBlock();

        Consume(TokenType.While);
        Consume(TokenType.OpenParenthesis);
        IExpression condition = ParseExpression();
        Consume(TokenType.CloseParenthesis);

        return new DoWhileDeclarationExpression(condition, body);
    }

    /* Expressions, from the loosest binding to the tightest */

    private IExpression ParseExpression() => ParseAssignment();

    private IExpression ParseAssignment()
    {
        IExpression left = ParseConditional();

        if (Next(TokenType.Equals))
        {
            Consume();
            return new AssignmentExpression(left, ParseAssignment());
        }

        if (Next(TokenType.CompoundEquals))
        {
            string op = Consume().Value;
            return new AssignmentExpression(left, ParseAssignment(), op);
        }

        return left;
    }

    // a ? b : c, with either side allowed to be another one
    private IExpression ParseConditional()
    {
        IExpression condition = ParseOr();
        if (!Next(TokenType.Question))
            return condition;

        Consume();
        IExpression then = ParseAssignment();
        Consume(TokenType.Colon, "':' between the two sides of the ?");
        IExpression otherwise = ParseAssignment();

        return new ConditionalExpression(condition, then, otherwise);
    }

    private IExpression ParseOr()
    {
        IExpression left = ParseAnd();
        while (NextOperator("||", "|"))
        {
            string op = Consume().Value;
            left = new BinaryExpression(left, ParseAnd(), op);
        }

        return left;
    }

    private IExpression ParseAnd()
    {
        IExpression left = ParseEquality();
        while (NextOperator("&&", "&"))
        {
            string op = Consume().Value;
            left = new BinaryExpression(left, ParseEquality(), op);
        }

        return left;
    }

    private IExpression ParseEquality()
    {
        IExpression left = ParseComparison();
        while (Next(TokenType.Equality) || Next(TokenType.NotEquality))
        {
            string op = Consume().Value;
            left = new BinaryExpression(left, ParseComparison(), op);
        }

        return left;
    }

    private IExpression ParseComparison()
    {
        IExpression left = ParseAdditive();
        while (NextOperator("<", ">", "<=", ">="))
        {
            string op = Consume().Value;
            left = new BinaryExpression(left, ParseAdditive(), op);
        }

        return left;
    }

    private IExpression ParseAdditive()
    {
        IExpression left = ParseMultiplicative();
        while (NextOperator("+", "-"))
        {
            string op = Consume().Value;
            left = new BinaryExpression(left, ParseMultiplicative(), op);
        }

        return left;
    }

    private IExpression ParseMultiplicative()
    {
        IExpression left = ParseUnary();
        while (NextOperator("*", "/", "%"))
        {
            string op = Consume().Value;
            left = new BinaryExpression(left, ParseUnary(), op);
        }

        return left;
    }

    private IExpression ParseUnary()
    {
        if (Next(TokenType.Exclamation))
        {
            Consume();
            return new BinaryExpression(ParseUnary(), null, "!");
        }

        if (NextOperator("-", "+"))
        {
            string op = Consume().Value;
            return new BinaryExpression(ParseUnary(), null, op);
        }

        return ParseCallMember();
    }

    /// <summary>
    /// Helper method for everything that hangs off a value: <c>a.b</c>, <c>a[b]</c> and <c>a(b)</c>, in any order and as many as there are.
    /// </summary>
    private IExpression ParseCallMember()
    {
        IExpression expression = ParsePrimary();

        while (true)
        {
            if (Next(TokenType.Dot))
            {
                Consume();
                Token name = Consume(TokenType.Identifier, "a name after the dot");
                expression = new MemberExpression(expression, new IdentifierExpression(name.Value), false);
            }
            else if (Next(TokenType.OpenBrace))
            {
                Consume();
                IExpression index = ParseExpression();
                Consume(TokenType.CloseBrace);
                expression = new MemberExpression(expression, index, true);
            }
            else if (Next(TokenType.OpenParenthesis))
            {
                expression = new CallExpression(ParseArguments(), expression);
            }
            else
            {
                return expression;
            }
        }
    }

    private IExpression[] ParseArguments()
    {
        Consume(TokenType.OpenParenthesis);
        List<IExpression> arguments = [];

        while (!Next(TokenType.CloseParenthesis))
        {
            arguments.Add(ParseExpression());

            if (Next(TokenType.Comma))
                Consume();
            else if (!Next(TokenType.CloseParenthesis))
                throw Unexpected(Peek(), "',' or ')' in the arguments");
        }

        Consume(TokenType.CloseParenthesis);
        return [.. arguments];
    }

    private IExpression ParsePrimary()
    {
        Token token = Peek();

        switch (token.Type)
        {
            case TokenType.Identifier:
                return new IdentifierExpression(Consume().Value);

            case TokenType.Number:
                Consume();
                if (!float.TryParse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                    throw new ParseException($"'{token.Value}' isn't a number (line {token.Line}).", token.Line, token.Col, token.Value.Length);
                return new NumericLiteralExpression(number);

            case TokenType.TextLiteral:
                return new StringLiteralExpression(Consume().Value);

            case TokenType.Function:
                return ParseFunctionDeclaration();

            case TokenType.Vector:
                return ParseVector();

            case TokenType.If:
                return ParseIf();

            case TokenType.While:
                return ParseWhile();

            case TokenType.Do:
                return ParseDoWhile();

            case TokenType.OpenBracket:
                return ParseObjectLiteral();

            case TokenType.OpenBrace:
                return ParseListLiteral();

            case TokenType.OpenParenthesis:
            {
                Consume();
                IExpression inner = ParseExpression();
                Consume(TokenType.CloseParenthesis);
                return inner;
            }

            case TokenType.Null:
                Consume();
                return new NullLiteral();

            default:
                throw Unexpected(token, "a value");
        }
    }

    private IExpression ParseVector()
    {
        Token keyword = Consume(TokenType.Vector);
        IExpression[] components = ParseArguments();

        if (components.Length is < 1 or > 4)
            throw new ParseException($"vec takes 1 to 4 numbers, not {components.Length} (line {keyword.Line}).", keyword.Line, keyword.Col, 3);

        return new VectorDeclarationExpression(components);
    }

    /// <summary>
    /// Helper method to read <c>{ key: value, "other key": value, shorthand, ...spread, }</c>.
    /// </summary>
    private IExpression ParseObjectLiteral()
    {
        Consume(TokenType.OpenBracket);
        List<PropertyExpression> properties = [];

        while (!Next(TokenType.CloseBracket))
        {
            if (Next(TokenType.EndOfFile))
                throw Unexpected(Peek(), "'}' to close the object");

            if (Next(TokenType.Spread))
            {
                Consume();
                properties.Add(new PropertyExpression(null, new SpreadExpression(ParseExpression())));
            }
            else
            {
                Token key = Peek();
                if (key.Type is not (TokenType.Identifier or TokenType.TextLiteral))
                    throw Unexpected(key, "the name of a property");
                Consume();

                if (Next(TokenType.Colon))
                {
                    Consume();
                    properties.Add(new PropertyExpression(key.Value, ParseExpression()));
                }
                else
                {
                    // { name } is { name: name }
                    properties.Add(new PropertyExpression(key.Value, null));
                }
            }

            if (Next(TokenType.Comma))
                Consume();
            else if (!Next(TokenType.CloseBracket))
                throw Unexpected(Peek(), "',' or '}' after the property");
        }

        Consume(TokenType.CloseBracket);
        return new ObjectLiteralExpression(properties);
    }

    /// <summary>
    /// Helper method to read <c>[a, b, ...others, ]</c>.
    /// </summary>
    private IExpression ParseListLiteral()
    {
        Consume(TokenType.OpenBrace);
        List<IExpression> items = [];

        while (!Next(TokenType.CloseBrace))
        {
            if (Next(TokenType.EndOfFile))
                throw Unexpected(Peek(), "']' to close the list");

            if (Next(TokenType.Spread))
            {
                Consume();
                items.Add(new SpreadExpression(ParseExpression()));
            }
            else
            {
                items.Add(ParseExpression());
            }

            if (Next(TokenType.Comma))
                Consume();
            else if (!Next(TokenType.CloseBrace))
                throw Unexpected(Peek(), "',' or ']' after the item");
        }

        Consume(TokenType.CloseBrace);
        return new ListLiteralExpression([.. items]);
    }
}
