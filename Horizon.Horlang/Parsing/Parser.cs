﻿using Bogz.Logging.Loggers;

using Horizon.Horlang.Lexxing;

namespace Horizon.Horlang.Parsing;

public class Parser
{
    private Queue<Token> Tokens { get; set; }

    private Token Consume(in TokenType expect)
    {
        var val = Tokens.Dequeue();
        if (val.Type != expect)
        {
            throw new Exception($"[Parser] Expected '{expect} but got '{val.Type}'!");
        }
        return val;
    }
    private Token Consume() => Tokens.Dequeue();
    private Token Peek() => Tokens.Peek();

    public ProgramStatement ProduceSyntaxTree(in Token[] inputTokens)
    {
        // store Tokens in a queue
        Tokens = new(inputTokens);
        List<IStatement> statements = [];

        // parse until we reach the end of the file
        while (Tokens.Peek().Type != TokenType.EndOfFile)
        {
            var statement = ParseStatement();
            if (statement != null)
                statements.Add(statement);
        }

        return new()
        {
            Body = statements
        };
    }

    private IStatement ParseStatement()
    {
        switch (Peek().Type)
        {
            case TokenType.Let:
                return ParseVariableDeclaration();
            case TokenType.Function:
                return ParseFunctionDeclaration();
            default:
                return ParseExpression();
        }
    }

    private IExpression ParseFunctionDeclaration()
    {
        Consume(TokenType.Function);

        var name = Consume(TokenType.Identifier);
        var args = ParseArguments();

        List<string> arguments = [];
        foreach (var arg in args)
        {
            if (arg.Type != NodeType.Identifier)
                throw new Exception("Func declaration parameters expected to be strings");
            arguments.Add(((IdentifierExpression)arg).Symbol);
        }


        // consume the {
        Consume(TokenType.OpenBracket);

        List<IStatement> body = [];

        while (Peek().Type != TokenType.EndOfFile && this.Peek().Type != TokenType.CloseBracket)
        {
            body.Add(ParseStatement());
        }

        // consume the }
        Consume(TokenType.CloseBracket);

        return new FunctionDeclarationExpression(name.Value, [.. arguments], [.. body]);
    }

    private IStatement ParseVariableDeclaration()
    {
        /* let NAME = VALUE;
         * let NAME;      */

        Consume(); // consume the LET keyword
        var identifier = Consume(TokenType.Identifier).Value;

        if (Peek().Type == TokenType.Semicolon)
        {
            Consume(); // consume the semicolon
            return new VariableDeclarationExpression(identifier, null);
        }

        Consume(TokenType.Equals); // expect an equals
        var expression = new VariableDeclarationExpression(identifier, ParseExpression());
        return expression;
    }

    /* Orders of precedence:
     * 
     * 9. AssignmentExpression
     * 8. MemberExpression
     * 7. FunctionCall
     * 6. LogicExpression
     * 5. ComparativeExpression
     * 4. AdditiveExpression
     * 3. MultiplicativeExpression
     * 2. UnaryExpression
     * 1. PrimaryExpression
     */


    private IExpression ParseExpression()
    {
        // least precedence
        return ParseAssignmentExpression();
    }

    private IExpression ParseAssignmentExpression()
    {
        var left = ParseObjectExpression(); // will be objects later

        if (Peek().Type == TokenType.Equals)
        {
            Consume(TokenType.Equals); // Consume the equals
            var value = ParseAssignmentExpression(); // allow chaining
            return new AssignmentExpression(left, value);
        }

        return left;
    }

    private IExpression ParseObjectExpression()
    {
        // { Prop[] }
        if (Peek().Type != TokenType.OpenBracket) return ParseAdditiveExpression();

        Consume(TokenType.OpenBracket); // consume the opening brace
        List<PropertyExpression> props = [];

        while (Peek().Type != TokenType.EndOfFile && Peek().Type != TokenType.CloseBracket)
        {
            // expect a key 
            Token key = Consume(TokenType.Identifier);

            // handle shorthand declaration in a list { key, key1 }
            if (Peek().Type == TokenType.Comma)
            {
                Consume(TokenType.Comma); //eat the comma
                props.Add(new PropertyExpression(key.Value, null));
                continue;
            }
            // handle shorthand as a free standing { key }
            else if (Peek().Type == TokenType.CloseBracket)
            {
                Consume(TokenType.CloseBracket); //eat the comma
                props.Add(new PropertyExpression(key.Value, null));
                continue;
            }

            Consume(TokenType.Colon); // we expect a value
            IExpression value = ParseExpression();

            // push key-value pair
            props.Add(new PropertyExpression(key.Value, value));

            if (Peek().Type != TokenType.CloseBracket)
            {
                Consume(TokenType.Comma);
            }
            else Consume(TokenType.CloseBracket);

        }
        return new ObjectLiteralExpression(props);
    }

    private IExpression ParseMultiplicativeExpression()
    {
        var left = ParseCallMemberExpression();

        while (Peek().Value.CompareTo("/") == 0 || Peek().Value.CompareTo("*") == 0 || Peek().Value.CompareTo("%") == 0)
        {
            var op = Consume();
            var right = ParseCallMemberExpression();

            // recursive precedence
            left = new BinaryExpression(left, right, op.Value);
        }

        return left;
    }

    private IExpression ParseCallMemberExpression()
    {
        var member = ParseMemberExpression();

        // foo.bar ()
        if (Peek().Type == TokenType.OpenParenthesis)
            return ParseCallExpression(member);

        return member;
    }
    private IExpression ParseCallExpression(in IExpression caller)
    {
        IExpression callExpr = new CallExpression(ParseArguments(), caller);

        // allow chaining of function calls
        if (Peek().Type == TokenType.OpenParenthesis)
            callExpr = ParseCallExpression(callExpr);

        return callExpr;
    }
    private IExpression ParseMemberExpression()
    {
        var obj = ParsePrimaryExpression();

        while (Peek().Type == TokenType.Dot || Peek().Type == TokenType.OpenBrace)
        {
            var op = Consume();
            IExpression property;
            bool computed = false;

            // non computed property foo.bar
            if (op.Type == TokenType.Dot)
            {
                computed = false;

                // expected to be an identifier (ie, foo.BAR)
                property = ParsePrimaryExpression();
                if (property.Type != NodeType.Identifier)
                    throw new Exception("cannot use dot operator unless the rhs is a identifier.");
            }
            else // allows obj[math.random()]
            {
                computed = true;
                // allows chaning
                property = ParseExpression();
                Consume(TokenType.CloseBrace);
            }

            obj = new MemberExpression(obj, property, computed);
        }
        return obj;
    }
    private IExpression[] ParseArguments()
    {
        Consume(TokenType.OpenParenthesis); // already checked but here for consistency sakes
        IExpression[] arguments = Peek().Type == TokenType.CloseParenthesis ? [] : ParseArgumentsList();
        Consume(TokenType.CloseParenthesis); // consume closing parenthesis
        return arguments;

    }
    private IExpression[] ParseArgumentsList()
    {
        // we use ParseAssignmentExpression() as we want foo(x = 69) to first set x to 69, then return x
        List<IExpression> arguments = [ParseAssignmentExpression()];

        while (Peek().Type == TokenType.Comma && Peek().Type != TokenType.CloseParenthesis)
        {
            Consume(TokenType.Comma); // consume the comma
            arguments.Add(ParseAssignmentExpression());
        }
        return [.. arguments];
    }
    private IExpression ParseAdditiveExpression()
    {
        var left = ParseMultiplicativeExpression();

        while (Peek().Value.CompareTo("+") == 0 || Peek().Value.CompareTo("-") == 0)
        {
            var op = Consume();
            var right = ParseMultiplicativeExpression(); // higher precidence

            // recursive precedence
            left = new BinaryExpression(left, right, op.Value);
        }

        return left;
    }

    private IExpression ParsePrimaryExpression()
    {
        Token token = Peek();
        IExpression result = new NullLiteral(); ;

        switch (token.Type)
        {
            case TokenType.Identifier:
                result = new IdentifierExpression(Consume().Value);
                break;
            case TokenType.Number:
                result = new NumericLiteralExpression(int.Parse(Consume().Value));
                break;
            case TokenType.TextLiteral:
                result = new StringLiteralExpression(Consume().Value);
                break;
            case TokenType.Function:
                result = ParseFunctionDeclaration();
                break;
            case TokenType.OpenParenthesis:
                Consume(); // consume the opening parenthesis
                result = ParseExpression();
                Consume(TokenType.CloseParenthesis); // consume the closing parenthesis
                break;
            case TokenType.Null:
                Consume(); // consume null keyword
                break;
            case TokenType.Semicolon:
                Consume(); break;
            default:
                throw new Exception($"Unexpected token found during parsing: {token}");
        }

        return result;
    }
}
