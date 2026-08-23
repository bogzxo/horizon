using System.Text;
using System.Collections.Generic;

using Egui;
using Egui.Widgets;

using Horizon.HIDL.Lexxing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;
using ValueType = Horizon.HIDL.Runtime.ValueType;

namespace Horizon.Engine.Debugging.Debuggers;

public static class SyntaxEditorExtension
{
    private static void TraverseAST(IStatement statement, HashSet<string> declaredFunctions, HashSet<string> declaredVariables)
    {
        if (statement == null) return;

        if (statement is ProgramStatement program)
        {
            foreach (var stmt in program.Body)
            {
                TraverseAST(stmt, declaredFunctions, declaredVariables);
            }
        }
        else if (statement is FunctionDeclarationExpression funcDecl)
        {
            declaredFunctions.Add(funcDecl.Name);
            foreach (var param in funcDecl.Parameters)
            {
                declaredVariables.Add(param);
            }
            foreach (var stmt in funcDecl.Body)
            {
                TraverseAST(stmt, declaredFunctions, declaredVariables);
            }
        }
        else if (statement is AnonymousFunctionDeclarationExpression anonDecl)
        {
            foreach (var param in anonDecl.Parameters)
            {
                declaredVariables.Add(param);
            }
            foreach (var stmt in anonDecl.Body)
            {
                TraverseAST(stmt, declaredFunctions, declaredVariables);
            }
        }
        else if (statement is VariableDeclarationExpression varDecl)
        {
            declaredVariables.Add(varDecl.Identifier);
            if (varDecl.Value != null)
            {
                TraverseAST(varDecl.Value, declaredFunctions, declaredVariables);
            }
        }
        else if (statement is AssignmentExpression assign)
        {
            TraverseAST(assign.Assignee, declaredFunctions, declaredVariables);
            TraverseAST(assign.Value, declaredFunctions, declaredVariables);
        }
        else if (statement is IfDeclarationExpression ifExpr)
        {
            TraverseAST(ifExpr.Condition, declaredFunctions, declaredVariables);
            foreach (var stmt in ifExpr.Body)
            {
                TraverseAST(stmt, declaredFunctions, declaredVariables);
            }
        }
        else if (statement is WhileDeclarationExpression whileExpr)
        {
            TraverseAST(whileExpr.Condition, declaredFunctions, declaredVariables);
            foreach (var stmt in whileExpr.Body)
            {
                TraverseAST(stmt, declaredFunctions, declaredVariables);
            }
        }
        else if (statement is DoWhileDeclarationExpression dowhileExpr)
        {
            TraverseAST(dowhileExpr.Condition, declaredFunctions, declaredVariables);
            foreach (var stmt in dowhileExpr.Body)
            {
                TraverseAST(stmt, declaredFunctions, declaredVariables);
            }
        }
        else if (statement is CallExpression call)
        {
            TraverseAST(call.Caller, declaredFunctions, declaredVariables);
            foreach (var arg in call.Arguments)
            {
                TraverseAST(arg, declaredFunctions, declaredVariables);
            }
        }
        else if (statement is MemberExpression member)
        {
            TraverseAST(member.Object, declaredFunctions, declaredVariables);
            TraverseAST(member.Property, declaredFunctions, declaredVariables);
        }
        else if (statement is BinaryExpression binary)
        {
            TraverseAST(binary.Left, declaredFunctions, declaredVariables);
            if (binary.Right != null)
            {
                TraverseAST(binary.Right, declaredFunctions, declaredVariables);
            }
        }
        else if (statement is ObjectLiteralExpression objLiteral)
        {
            foreach (var prop in objLiteral.Properties)
            {
                if (prop.Value != null)
                {
                    TraverseAST(prop.Value, declaredFunctions, declaredVariables);
                }
            }
        }
        else if (statement is VectorDeclarationExpression vecDecl)
        {
            foreach (var expr in vecDecl.Expressions)
            {
                TraverseAST(expr, declaredFunctions, declaredVariables);
            }
        }
    }

    public static Response SyntaxHighlightedCodeEditor(this Ui ui, ref string code, Horizon.HIDL.HIDLRuntime? runtime = null)
    {
        // Create a CodeEditor with transparent native text
        var font = new FontId(14, new FontFamily.Monospace());
        var codeEditor = TextEdit.Multiline(ref code)
            .CodeEditor()
            .DesiredWidth(float.PositiveInfinity)
            .DesiredRows(24)
            // Hide native text using the built-in Transparent constant
            .TextColor(Color32.Transparent);

        var response = ui.Add(codeEditor);

        // draw our custom syntax highlighting on top
        var painter = ui.PainterAt(response.Rect);

        // Egui's default text margin inside a TextEdit is typically 4px.
        var textPos = new EVec2(response.Rect.Min.X, response.Rect.Min.Y );

        // approximate character dimensions for standard egui monospace font at size 14, thanks rust /s
        float charWidth = 8.4f;
        float lineHeight = 16.0f;

        // draw the full text in a base color. 
        // this ensures whitespace, punctuation, and comments skipped by the lexer remain visible.
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            painter.Text(
                new EPos2(textPos.X, textPos.Y + (i * lineHeight)),
                Align2.LeftTop,
                lines[i],
                font,
                Color32.FromRgb(100, 100, 100) // Base Gray
            );
        }

        // Parse symbols if AST can be produced
        HashSet<string> declaredFunctions = new();
        HashSet<string> declaredVariables = new();
        try
        {
            var ast = new Parser().ProduceSyntaxTree(Lexer.Tokenize(code));
            
            TraverseAST(ast, declaredFunctions, declaredVariables);
        }
        catch
        {
            // @bogz ???
        }

        // Gather all external active variables/objects/functions from the runtime environment
        Dictionary<string, IRuntimeValue> externalValues = [];
        if (runtime != null)
        {
            try
            {
                externalValues = runtime.UserScope.GetAllDeclaredValues(true);
            }
            catch
            {
                // @bogz ???
            }
        }

        // Tokenize and overlay the highlighted colors
        var tokens = Lexer.Tokenize(code);

        for (int tokenIdx = 0; tokenIdx < tokens.Length; tokenIdx++)
        {
            var token = tokens[tokenIdx];
            if (token.Type == TokenType.EndOfFile) break;

            if (token.Type == TokenType.Comment)
            {
                var commentLines = token.Value.Split('\n');
                for (int i = 0; i < commentLines.Length; i++)
                {
                    int cmtLineNum = token.Line - 1 + i;
                    int cmtColNum = (i == 0) ? (token.Col - 1) : 0;
                    string lineText = commentLines[i].TrimEnd('\r');

                    float cmtX = textPos.X + (cmtColNum * charWidth);
                    float cmtY = textPos.Y + (cmtLineNum * lineHeight);

                    painter.Text(
                        new EPos2(cmtX, cmtY),
                        Align2.LeftTop,
                        lineText,
                        font,
                        Color32.FromRgb(106, 153, 85) // Green comments
                    );
                }
                continue;
            }

            int lineNum = token.Line - 1;
            int colNum = token.Col - 1;

            float tokenX = textPos.X + (colNum * charWidth);
            float tokenY = textPos.Y + (lineNum * lineHeight);

            // Draw the highlighted token exactly over the base text
            string textToDraw = token.Type == TokenType.TextLiteral ? $"\"{token.Value}\"" : token.Value;
            painter.Text(
                new EPos2(tokenX, tokenY),
                Align2.LeftTop,
                textToDraw,
                font,
                GetTokenColor(token, tokenIdx, tokens, declaredFunctions, declaredVariables, externalValues)
            );
        }

        return response;
    }

    // thank u gemini
    private static Color32 GetTokenColor(
        Token token,
        int tokenIdx,
        Token[] tokens,
        HashSet<string> declaredFunctions,
        HashSet<string> declaredVariables,
        Dictionary<string, IRuntimeValue> externalValues)
    {
        switch (token.Type)
        {
            // Keywords (Blue)
            case TokenType.Let:
            case TokenType.Const:
            case TokenType.Function:
            case TokenType.If:
            case TokenType.While:
            case TokenType.Do:
            case TokenType.Delete:
            case TokenType.Break:
                return Color32.FromRgb(86, 156, 214);

            case TokenType.Vector:
                return Color32.FromRgb(78, 201, 176); // Teal for types

            // Variables / Identifiers
            case TokenType.Identifier:
                {
                    string name = token.Value;

                    // 1. Check external environment-aware active variables/objects/functions
                    if (externalValues.TryGetValue(name, out var runtimeValue))
                    {
                        if (runtimeValue.Type is ValueType.NativeFunction or ValueType.Function or ValueType.AnonymousFunction)
                        {
                            return Color32.FromRgb(220, 220, 170); // Yellow for functions
                        }
                        else if (runtimeValue.Type == ValueType.Object)
                        {
                            return Color32.FromRgb(78, 201, 176); // Teal for objects/types
                        }
                        else if (runtimeValue.Type is ValueType.Boolean or ValueType.Null)
                        {
                            return Color32.FromRgb(86, 156, 214); // Blue for constant-like values
                        }
                        else
                        {
                            return Color32.FromRgb(156, 220, 254); // Light Blue for regular variables
                        }
                    }

                    // 2. Built-in constants
                    if (name == "true" || name == "false" || name == "null" || name == "version")
                        return Color32.FromRgb(86, 156, 214);

                    // 4. Built-in functions (fallback)
                    if (name == "print" || name == "read" || name == "exit" || name == "clear" || name == "ld" || name == "reset")
                        return Color32.FromRgb(216, 160, 223);

                    // 5. Function name in definition: func NAME(...)
                    if (tokenIdx > 0 && tokens[tokenIdx - 1].Type == TokenType.Function)
                        return Color32.FromRgb(220, 220, 170);

                    // 6. Function/method call: NAME(...)
                    if (tokenIdx + 1 < tokens.Length && tokens[tokenIdx + 1].Type == TokenType.OpenParenthesis)
                        return Color32.FromRgb(220, 220, 170);

                    // 7. Property Key / Object Key in literal: { KEY: value }
                    // Or dot property accessor: obj.PROPERTY
                    if (tokenIdx > 0 && tokens[tokenIdx - 1].Type == TokenType.Dot)
                        return Color32.FromRgb(156, 220, 254);

                    if (tokenIdx + 1 < tokens.Length && tokens[tokenIdx + 1].Type == TokenType.Colon)
                        return Color32.FromRgb(156, 220, 254);

                    // 8. AST symbols
                    if (declaredFunctions.Contains(name))
                        return Color32.FromRgb(220, 220, 170);

                    if (declaredVariables.Contains(name))
                        return Color32.FromRgb(156, 220, 254);

                    // Default identifier (Light Gray)
                    return Color32.FromRgb(220, 220, 220);
                }

            // Numbers (Light Green)
            case TokenType.Number:
                return Color32.FromRgb(181, 206, 168);

            // Strings (Orange)
            case TokenType.TextLiteral:
                return Color32.FromRgb(214, 157, 133);

            // Operators and Punctuation (Light Gray/Silver)
            case TokenType.Equality:
            case TokenType.NotEquality:
            case TokenType.BinaryOperation:
            case TokenType.Equals:
            case TokenType.OpenParenthesis:
            case TokenType.CloseParenthesis:
            case TokenType.Semicolon:
            case TokenType.Colon:
            case TokenType.Comma:
            case TokenType.Dot:
            case TokenType.Exclamation:
            case TokenType.OpenBracket:
            case TokenType.CloseBracket:
            case TokenType.OpenBrace:
            case TokenType.CloseBrace:
                return Color32.FromRgb(180, 180, 180);

            default:
                return Color32.FromRgb(220, 220, 220);
        }
    }
}