using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using ScintillaNET;

using Horizon.HIDL.Lexxing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;
using ValueType = Horizon.HIDL.Runtime.ValueType;

namespace Horizon.HIDL.Editor
{
    public class SyntaxHighlighter
    {
        public const int STYLE_DEFAULT = 0;
        public const int STYLE_KEYWORD = 1;
        public const int STYLE_VECTOR = 2;
        public const int STYLE_FUNCTION = 3;
        public const int STYLE_BUILTIN = 4;
        public const int STYLE_IDENTIFIER = 5;
        public const int STYLE_NUMBER = 6;
        public const int STYLE_STRING = 7;
        public const int STYLE_COMMENT = 8;
        public const int STYLE_PUNCTUATION = 9;
        public const int STYLE_LITERAL_KEYWORD = 10;
        public const int STYLE_OBJECT = 11;

        public const int ERROR_INDICATOR = 8;

        private readonly Scintilla rtb;
        private string? _lastCode;
        private ParseException? _lastError;

        public SyntaxHighlighter(Scintilla rtb)
        {
            this.rtb = rtb;
        }

        public void InitializeStyles()
        {
            rtb.StyleResetDefault();
            rtb.Styles[Style.Default].Font = "Consolas";
            rtb.Styles[Style.Default].Size = 13;
            rtb.Styles[Style.Default].BackColor = Color.FromArgb(30, 30, 30);
            rtb.Styles[Style.Default].ForeColor = Color.FromArgb(220, 220, 220);
            rtb.StyleClearAll();

            rtb.CaretForeColor = Color.White;
            rtb.SelectionBackColor = Color.FromArgb(38, 79, 120);

            rtb.Styles[STYLE_DEFAULT].ForeColor = Color.FromArgb(220, 220, 220);
            rtb.Styles[STYLE_KEYWORD].ForeColor = Color.FromArgb(86, 156, 214);
            rtb.Styles[STYLE_VECTOR].ForeColor = Color.FromArgb(78, 201, 176);
            rtb.Styles[STYLE_FUNCTION].ForeColor = Color.FromArgb(220, 220, 170);
            rtb.Styles[STYLE_BUILTIN].ForeColor = Color.FromArgb(216, 160, 223);
            rtb.Styles[STYLE_IDENTIFIER].ForeColor = Color.FromArgb(156, 220, 254);
            rtb.Styles[STYLE_NUMBER].ForeColor = Color.FromArgb(181, 206, 168);
            rtb.Styles[STYLE_STRING].ForeColor = Color.FromArgb(214, 157, 133);
            rtb.Styles[STYLE_COMMENT].ForeColor = Color.FromArgb(106, 153, 85);
            rtb.Styles[STYLE_PUNCTUATION].ForeColor = Color.FromArgb(180, 180, 180);
            rtb.Styles[STYLE_LITERAL_KEYWORD].ForeColor = Color.FromArgb(86, 156, 214);
            rtb.Styles[STYLE_OBJECT].ForeColor = Color.FromArgb(78, 201, 176);

            rtb.Indicators[ERROR_INDICATOR].Style = IndicatorStyle.Squiggle;
            rtb.Indicators[ERROR_INDICATOR].ForeColor = Color.Red;

            // Setup line numbers margin
            rtb.Margins[0].Type = MarginType.Number;
            rtb.Margins[0].Width = 45;
            rtb.Styles[Style.LineNumber].BackColor = Color.FromArgb(37, 37, 38);
            rtb.Styles[Style.LineNumber].ForeColor = Color.FromArgb(110, 110, 110);
        }

        public ParseException? HighlightSyntax(HIDLRuntime? runtime)
        {
            string code = rtb.Text;
            if (string.IsNullOrEmpty(code))
            {
                _lastCode = null;
                _lastError = null;
                ClearHighlights();
                return null;
            }

            if (code == _lastCode)
            {
                return _lastError;
            }

            ParseException? parseError = null;

            // Tokenize
            Token[] tokens = Array.Empty<Token>();
            try
            {
                tokens = Horizon.HIDL.Lexxing.Lexer.Tokenize(code);
            }
            catch (ParseException ex)
            {
                parseError = ex;
            }
            catch (Exception ex)
            {
                parseError = TryExtractErrorLocation(ex, tokens, code);
            }

            // Parse AST for declared symbols
            HashSet<string> declaredFunctions = new();
            HashSet<string> declaredVariables = new();
            if (tokens.Length > 0 && parseError == null)
            {
                try
                {
                    var ast = new Parser().ProduceSyntaxTree(tokens);
                    TraverseAST(ast, declaredFunctions, declaredVariables);
                }
                catch (ParseException ex)
                {
                    parseError = ex;
                }
                catch (Exception ex)
                {
                    parseError = TryExtractErrorLocation(ex, tokens, code);
                }
            }

            Dictionary<string, IRuntimeValue> externalValues = new();

            if (runtime != null)
            {
                if (tokens.Length > 0 && parseError == null)
                {
                    try
                    {
                        runtime.GenerateValue(code, out externalValues);
                    }
                    catch (Exception ex)
                    {
                        parseError = TryExtractErrorLocation(ex, tokens, code);
                    }
                }

                if (externalValues.Count == 0)
                {
                    try
                    {
                        externalValues = runtime.UserScope.GetAllDeclaredValues(true);
                    }
                    catch
                    {
                    }
                }
            }

            int textLength = rtb.TextLength;

            // Clear previous styling and indicators
            rtb.StartStyling(0);
            rtb.SetStyling(textLength, STYLE_DEFAULT);

            rtb.IndicatorCurrent = ERROR_INDICATOR;
            rtb.IndicatorClearRange(0, textLength);

            // Apply token highlights
            for (int tokenIdx = 0; tokenIdx < tokens.Length; tokenIdx++)
            {
                var token = tokens[tokenIdx];
                if (token.Type == TokenType.EndOfFile) break;

                int lineNum = token.Line - 1;
                if (lineNum < 0 || lineNum >= rtb.Lines.Count) continue;

                int lineStart = rtb.Lines[lineNum].Position;
                int colNum = token.Col - 1;
                int tokenStart = lineStart + colNum;

                int tokenLength = token.Type == TokenType.TextLiteral
                    ? token.Value.Length + 2
                    : token.Value.Length;

                if (tokenStart >= 0 && tokenStart + tokenLength <= textLength)
                {
                    int style = GetTokenStyle(token, tokenIdx, tokens, declaredFunctions, declaredVariables, externalValues);
                    rtb.StartStyling(tokenStart);
                    rtb.SetStyling(tokenLength, style);
                }
            }

            // Highlight invalid code with red squiggle
            if (parseError != null)
            {
                int errLine = Math.Max(0, parseError.Line - 1);
                int errCol = Math.Max(0, parseError.Col - 1);

                if (errLine < rtb.Lines.Count)
                {
                    int lineStart = rtb.Lines[errLine].Position;
                    int errStart = lineStart + errCol;

                    if (errStart >= textLength && textLength > 0)
                    {
                        errStart = textLength - 1;
                    }

                    int errLen = Math.Max(1, parseError.Length);
                    if (errStart + errLen > textLength)
                    {
                        errLen = Math.Max(1, textLength - errStart);
                    }

                    if (errStart >= 0 && errLen > 0)
                    {
                        rtb.IndicatorCurrent = ERROR_INDICATOR;
                        rtb.IndicatorFillRange(errStart, errLen);
                    }
                }
            }

            _lastCode = code;
            _lastError = parseError;

            return parseError;
        }

        private void ClearHighlights()
        {
            int textLength = rtb.TextLength;
            if (textLength > 0)
            {
                rtb.StartStyling(0);
                rtb.SetStyling(textLength, STYLE_DEFAULT);

                rtb.IndicatorCurrent = ERROR_INDICATOR;
                rtb.IndicatorClearRange(0, textLength);
            }
        }

        private static ParseException? TryExtractErrorLocation(Exception ex, Token[] tokens, string code)
        {
            if (ex is ParseException pe) return pe;

            var matchLineCol = Regex.Match(ex.Message, @"at line (\d+), column (\d+)");
            if (matchLineCol.Success &&
                int.TryParse(matchLineCol.Groups[1].Value, out int line) &&
                int.TryParse(matchLineCol.Groups[2].Value, out int col))
            {
                return new ParseException(ex.Message, line, col, 1);
            }

            var matchSymbol = Regex.Matches(ex.Message, @"'([^']+)'");
            foreach (Match m in matchSymbol)
            {
                string symbolName = m.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(symbolName)) continue;

                for (int i = tokens.Length - 1; i >= 0; i--)
                {
                    var t = tokens[i];
                    if (t.Type != TokenType.EndOfFile && t.Value == symbolName)
                    {
                        return new ParseException(ex.Message, t.Line, t.Col, Math.Max(1, t.Value.Length));
                    }
                }
            }

            if (tokens.Length > 0)
            {
                for (int i = tokens.Length - 1; i >= 0; i--)
                {
                    if (tokens[i].Type != TokenType.EndOfFile)
                    {
                        return new ParseException(ex.Message, tokens[i].Line, tokens[i].Col, Math.Max(1, tokens[i].Value.Length));
                    }
                }
            }

            return new ParseException(ex.Message, 1, 1, 1);
        }

        private static void TraverseAST(IStatement? statement, HashSet<string> declaredFunctions, HashSet<string> declaredVariables)
        {
            switch (statement)
            {
                case null:
                    return;
                case ProgramStatement program:
                {
                    foreach (var stmt in program.Body)
                    {
                        TraverseAST(stmt, declaredFunctions, declaredVariables);
                    }

                    break;
                }
                case FunctionDeclarationExpression funcDecl:
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

                    break;
                }
                case AnonymousFunctionDeclarationExpression anonDecl:
                {
                    foreach (var param in anonDecl.Parameters)
                    {
                        declaredVariables.Add(param);
                    }
                    foreach (var stmt in anonDecl.Body)
                    {
                        TraverseAST(stmt, declaredFunctions, declaredVariables);
                    }

                    break;
                }
                case VariableDeclarationExpression varDecl:
                {
                    declaredVariables.Add(varDecl.Identifier);
                    if (varDecl.Value != null)
                    {
                        TraverseAST(varDecl.Value, declaredFunctions, declaredVariables);
                    }

                    break;
                }
                case AssignmentExpression assign:
                    TraverseAST(assign.Assignee, declaredFunctions, declaredVariables);
                    TraverseAST(assign.Value, declaredFunctions, declaredVariables);
                    break;
                case IfDeclarationExpression ifExpr:
                {
                    TraverseAST(ifExpr.Condition, declaredFunctions, declaredVariables);
                    foreach (var stmt in ifExpr.Body)
                    {
                        TraverseAST(stmt, declaredFunctions, declaredVariables);
                    }

                    break;
                }
                case WhileDeclarationExpression whileExpr:
                {
                    TraverseAST(whileExpr.Condition, declaredFunctions, declaredVariables);
                    foreach (var stmt in whileExpr.Body)
                    {
                        TraverseAST(stmt, declaredFunctions, declaredVariables);
                    }

                    break;
                }
                case DoWhileDeclarationExpression dowhileExpr:
                {
                    TraverseAST(dowhileExpr.Condition, declaredFunctions, declaredVariables);
                    foreach (var stmt in dowhileExpr.Body)
                    {
                        TraverseAST(stmt, declaredFunctions, declaredVariables);
                    }

                    break;
                }
                case CallExpression call:
                {
                    TraverseAST(call.Caller, declaredFunctions, declaredVariables);
                    foreach (var arg in call.Arguments)
                    {
                        TraverseAST(arg, declaredFunctions, declaredVariables);
                    }

                    break;
                }
                case MemberExpression member:
                    TraverseAST(member.Object, declaredFunctions, declaredVariables);
                    TraverseAST(member.Property, declaredFunctions, declaredVariables);
                    break;
                case BinaryExpression binary:
                {
                    TraverseAST(binary.Left, declaredFunctions, declaredVariables);
                    if (binary.Right != null)
                    {
                        TraverseAST(binary.Right, declaredFunctions, declaredVariables);
                    }

                    break;
                }
                case ObjectLiteralExpression objLiteral:
                {
                    foreach (var prop in objLiteral.Properties)
                    {
                        if (prop.Value != null)
                        {
                            TraverseAST(prop.Value, declaredFunctions, declaredVariables);
                        }
                    }

                    break;
                }
                case VectorDeclarationExpression vecDecl:
                {
                    foreach (var expr in vecDecl.Expressions)
                    {
                        TraverseAST(expr, declaredFunctions, declaredVariables);
                    }

                    break;
                }
            }
        }

        private static int GetTokenStyle(
            Token token,
            int tokenIdx,
            Token[] tokens,
            HashSet<string> declaredFunctions,
            HashSet<string> declaredVariables,
            Dictionary<string, IRuntimeValue> externalValues)
        {
            switch (token.Type)
            {
                case TokenType.Let:
                case TokenType.Const:
                case TokenType.Function:
                case TokenType.If:
                case TokenType.While:
                case TokenType.Do:
                case TokenType.Delete:
                case TokenType.Break:
                    return STYLE_KEYWORD;

                case TokenType.Vector:
                    return STYLE_VECTOR;

                case TokenType.Identifier:
                    {
                        string name = token.Value;

                        if (externalValues.TryGetValue(name, out var runtimeValue))
                        {
                            if (runtimeValue.Type is ValueType.NativeFunction or ValueType.Function or ValueType.AnonymousFunction)
                                return STYLE_FUNCTION;
                            else if (runtimeValue.Type == ValueType.Object)
                                return STYLE_OBJECT;
                            else if (runtimeValue.Type is ValueType.Boolean or ValueType.Null)
                                return STYLE_LITERAL_KEYWORD;
                            else
                                return STYLE_IDENTIFIER;
                        }

                        if (name == "true" || name == "false" || name == "null" || name == "version")
                            return STYLE_LITERAL_KEYWORD;

                        // TODO @bogz 
                        if (name == "print" || name == "read" || name == "exit" || name == "clear" || name == "ld" || name == "reset")
                            return STYLE_BUILTIN;

                        if (tokenIdx > 0 && tokens[tokenIdx - 1].Type == TokenType.Function)
                            return STYLE_FUNCTION;

                        if (tokenIdx + 1 < tokens.Length && tokens[tokenIdx + 1].Type == TokenType.OpenParenthesis)
                            return STYLE_FUNCTION;

                        if (tokenIdx > 0 && tokens[tokenIdx - 1].Type == TokenType.Dot)
                            return STYLE_IDENTIFIER;

                        if (tokenIdx + 1 < tokens.Length && tokens[tokenIdx + 1].Type == TokenType.Colon)
                            return STYLE_IDENTIFIER;

                        if (declaredFunctions.Contains(name))
                            return STYLE_FUNCTION;

                        if (declaredVariables.Contains(name))
                            return STYLE_IDENTIFIER;

                        return STYLE_DEFAULT;
                    }

                case TokenType.Number:
                    return STYLE_NUMBER;

                case TokenType.TextLiteral:
                    return STYLE_STRING;

                case TokenType.Comment:
                    return STYLE_COMMENT;

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
                    return STYLE_PUNCTUATION;

                default:
                    return STYLE_DEFAULT;
            }
        }
    }
}