using System;
using System.Collections.Generic;
using System.Drawing;
using AutocompleteMenuNS;
using Horizon.HIDL.Lexxing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;
using ScintillaNET;
using ValueType = Horizon.HIDL.Runtime.ValueType;

namespace Horizon.HIDL.Editor
{
    public class HidlAutocomplete
    {
        private readonly AutocompleteMenu _menu;
        private readonly Scintilla _scintilla;

        public HidlAutocomplete(Scintilla scintilla)
        {
            _scintilla = scintilla;
            _menu = new AutocompleteMenu
            {
                TargetControlWrapper = new Scintilla5Wrapper(scintilla),
                MinFragmentLength = 1,
                SearchPattern = @"[\w\.]",
                ImageList = CreateDefaultImageList(),
                Colors = new Colors
                {
                    BackColor = Color.FromArgb(37, 37, 38),
                    ForeColor = Color.FromArgb(220, 220, 220),
                    SelectedBackColor = Color.FromArgb(0, 122, 204),
                    SelectedForeColor = Color.White
                }
            };

            if (_scintilla.Font != null)
            {
                _menu.Font = new Font(_scintilla.Font.FontFamily, 11f);
            }

            _scintilla.CharAdded += (s, e) =>
            {
                if (e.Char == '.' || char.IsLetterOrDigit((char)e.Char))
                {
                    _menu.Show(_scintilla, false);
                }

                else if (e.Char == '\n')
                {
                    var scintilla = (ScintillaNET.Scintilla)s;

                    // Get the current line number
                    int currentLine = scintilla.LineFromPosition(scintilla.CurrentPosition);

                    // Ensure there is a line above it
                    if (currentLine > 0)
                    {
                        // Get the indentation level of the previous line
                        int previousLineIndent = scintilla.Lines[currentLine - 1].Indentation;

                        // Apply the same indentation level to the current line
                        if (previousLineIndent > 0)
                        {
                            scintilla.Lines[currentLine].Indentation = previousLineIndent;

                            // Move the cursor to the end of the new indentation to allow the user to type
                            scintilla.GotoPosition(scintilla.Lines[currentLine].Position + (previousLineIndent));
                        }
                    }
                }
            };
        }

        private static ImageList CreateDefaultImageList()
        {
            ImageList il = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };

            // cheeky ik
            Bitmap DrawIcon(Color bg, string label)
            {
                Bitmap bmp = new Bitmap(16, 16);
                using Graphics g = Graphics.FromImage(bmp);
                g.Clear(Color.Transparent);
                using SolidBrush b = new SolidBrush(bg);
                g.FillEllipse(b, 1, 1, 14, 14);
                using Font font = new Font("Consolas", 7.5f, FontStyle.Bold);
                using SolidBrush textBrush = new SolidBrush(Color.White);
                StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(label, font, textBrush, new RectangleF(0, 0, 16, 16), sf);
                return bmp;
            }

            il.Images.Add(DrawIcon(Color.FromArgb(100, 156, 215), "K")); // 0: Keyword
            il.Images.Add(DrawIcon(Color.FromArgb(120, 120, 220), "f")); // 1: Function
            il.Images.Add(DrawIcon(Color.FromArgb(100, 200, 225), "v")); // 2: Variable
            il.Images.Add(DrawIcon(Color.FromArgb(215, 157, 120), "p")); // 3: Property
            il.Images.Add(DrawIcon(Color.FromArgb(80, 140, 180), "o"));  // 4: Object / Vector

            return il;
        }

        public void UpdateAutocompleteItems(HIDLRuntime? runtime)
        {
            List<AutocompleteItem> items = new();
            HashSet<string> addedKeys = new(StringComparer.OrdinalIgnoreCase);

            void AddItem(string name, string typeName, string? parentObject = null, string? toolTip = null)
            {
                string key = string.IsNullOrEmpty(parentObject) ? name : $"{parentObject}.{name}";
                if (addedKeys.Add(key))
                {
                    items.Add(new HidlAutocompleteItem(name, typeName, parentObject, toolTip));
                }
            }

            // Add some builtin keywords
            AddItem("let", "Keyword", toolTip: "Declare a variable");
            AddItem("const", "Keyword", toolTip: "Declare a constant variable");
            AddItem("func", "Keyword", toolTip: "Declare a function");
            AddItem("if", "Keyword", toolTip: "If condition branch");
            AddItem("while", "Keyword", toolTip: "While loop");
            AddItem("do", "Keyword", toolTip: "Do-while loop");
            AddItem("delete", "Keyword", toolTip: "Delete variable");
            AddItem("break", "Keyword", toolTip: "Break loop execution");
            AddItem("vec", "Keyword", toolTip: "Vector creation helper vec(...)");

            // Add some builtin literals temporarily @bogz make this dynamic
            AddItem("true", "Boolean", toolTip: "Boolean literal true");
            AddItem("false", "Boolean", toolTip: "Boolean literal false");
            AddItem("null", "Null", toolTip: "Null literal");
            AddItem("version", "String", toolTip: "HIDL Runtime version");
            AddItem("print", "Function", toolTip: "print(value) - Output text or value to console");
            AddItem("read", "Function", toolTip: "read() - Read input line");
            AddItem("exit", "Function", toolTip: "exit() - Exit process");
            AddItem("clear", "Function", toolTip: "clear() - Clear console");
            AddItem("ld", "Function", toolTip: "ld(filePath) - Load external script file");
            AddItem("reset", "Function", toolTip: "reset() - Reset user scope");

            // Add runtime symbols
            if (runtime != null)
            {
                try
                {
                    var declaredValues = runtime.UserScope.GetAllDeclaredValues(true);
                    foreach (var kvp in declaredValues)
                    {
                        string varName = kvp.Key;
                        IRuntimeValue val = kvp.Value;
                        string typeStr = GetRuntimeTypeName(val);

                        AddItem(varName, typeStr, toolTip: $"Runtime {typeStr} {varName}");

                        // Inspect properties of runtime objects and vectors
                        AddRuntimeChildProperties(varName, val, AddItem);
                    }
                }
                catch
                {
                }
            }

            // add static AST Symbols
            string code = _scintilla.Text;
            if (!string.IsNullOrWhiteSpace(code))
            {
                try
                {
                    Token[] tokens = Horizon.HIDL.Lexxing.Lexer.Tokenize(code);
                    if (tokens.Length > 0)
                    {
                        var ast = new Parser().ProduceSyntaxTree(tokens);
                        ExtractAstSymbols(ast, AddItem);
                    }
                }
                catch
                {
                }
            }

            _menu.SetAutocompleteItems(items);
        }

        private static void AddRuntimeChildProperties(string parentPath, IRuntimeValue val, Action<string, string, string?, string?> addItem)
        {
            if (val is ObjectValue objVal && objVal.Properties != null)
            {
                foreach (var prop in objVal.Properties)
                {
                    string propType = GetRuntimeTypeName(prop.Value);
                    addItem(prop.Key, propType, parentPath, $"Runtime property {parentPath}.{prop.Key} : {propType}");
                    AddRuntimeChildProperties($"{parentPath}.{prop.Key}", prop.Value, addItem);
                }
            }
            else if (val is Vector2Value)
            {
                addItem("x", "Number", parentPath, $"Vector component {parentPath}.x");
                addItem("y", "Number", parentPath, $"Vector component {parentPath}.y");
            }
            else if (val is Vector3Value)
            {
                addItem("x", "Number", parentPath, $"Vector component {parentPath}.x");
                addItem("y", "Number", parentPath, $"Vector component {parentPath}.y");
                addItem("z", "Number", parentPath, $"Vector component {parentPath}.z");
            }
            else if (val is Vector4Value)
            {
                addItem("x", "Number", parentPath, $"Vector component {parentPath}.x");
                addItem("y", "Number", parentPath, $"Vector component {parentPath}.y");
                addItem("z", "Number", parentPath, $"Vector component {parentPath}.z");
                addItem("w", "Number", parentPath, $"Vector component {parentPath}.w");
            }
        }

        private static string GetRuntimeTypeName(IRuntimeValue val)
        {
            if (val == null) return "Unknown";
            return val.Type switch
            {
                ValueType.Number => "Number",
                ValueType.String => "String",
                ValueType.Boolean => "Boolean",
                ValueType.Object => "Object",
                ValueType.Function => "Function",
                ValueType.NativeFunction => "Function",
                ValueType.Vector2 => "Vector2",
                ValueType.Vector3 => "Vector3",
                ValueType.Vector4 => "Vector4",
                ValueType.Null => "Null",
                _ => val.Type.ToString()
            };
        }

        private static void ExtractAstSymbols(IStatement statement, Action<string, string, string?, string?> addItem)
        {
            switch (statement)
            {
                case ProgramStatement program:
                    foreach (var stmt in program.Body)
                        ExtractAstSymbols(stmt, addItem);
                    break;

                case FunctionDeclarationExpression funcDecl:
                    string paramsList = string.Join(", ", funcDecl.Parameters);
                    addItem(funcDecl.Name, "Function", null, $"func {funcDecl.Name}({paramsList})");
                    foreach (var param in funcDecl.Parameters)
                    {
                        addItem(param, "Parameter", null, $"Function parameter {param}");
                    }
                    foreach (var stmt in funcDecl.Body)
                    {
                        ExtractAstSymbols(stmt, addItem);
                    }
                    break;

                case AnonymousFunctionDeclarationExpression anonDecl:
                    foreach (var param in anonDecl.Parameters)
                    {
                        addItem(param, "Parameter", null, $"Function parameter {param}");
                    }
                    foreach (var stmt in anonDecl.Body)
                    {
                        ExtractAstSymbols(stmt, addItem);
                    }
                    break;

                case VariableDeclarationExpression varDecl:
                    string typeName = StringExpressionType(varDecl.Value);
                    string kind = varDecl.ReadOnly ? "Constant" : "Variable";
                    addItem(varDecl.Identifier, typeName, null, $"{kind} {varDecl.Identifier} : {typeName}");

                    if (varDecl.Value != null)
                    {
                        ExtractObjectPropertySymbols(varDecl.Identifier, varDecl.Value, addItem);
                    }
                    break;

                case IfDeclarationExpression ifExpr:
                    foreach (var stmt in ifExpr.Body)
                        ExtractAstSymbols(stmt, addItem);
                    break;

                case WhileDeclarationExpression whileExpr:
                    foreach (var stmt in whileExpr.Body)
                        ExtractAstSymbols(stmt, addItem);
                    break;

                case DoWhileDeclarationExpression dowhileExpr:
                    foreach (var stmt in dowhileExpr.Body)
                        ExtractAstSymbols(stmt, addItem);
                    break;
            }
        }

        private static void ExtractObjectPropertySymbols(string parentPath, IExpression expr, Action<string, string, string?, string?> addItem)
        {
            if (expr is ObjectLiteralExpression objLiteral)
            {
                foreach (var prop in objLiteral.Properties)
                {
                    string propType = StringExpressionType(prop.Value);
                    addItem(prop.Key, propType, parentPath, $"Property {parentPath}.{prop.Key} : {propType}");

                    if (prop.Value != null)
                    {
                        ExtractObjectPropertySymbols($"{parentPath}.{prop.Key}", prop.Value, addItem);
                    }
                }
            }
            else if (expr is VectorDeclarationExpression vecExpr)
            {
                int count = vecExpr.Expressions?.Length ?? 0;
                addItem("x", "Number", parentPath, $"Vector component {parentPath}.x");
                addItem("y", "Number", parentPath, $"Vector component {parentPath}.y");
                if (count >= 3)
                    addItem("z", "Number", parentPath, $"Vector component {parentPath}.z");
                if (count >= 4)
                    addItem("w", "Number", parentPath, $"Vector component {parentPath}.w");
            }
        }

        private static string StringExpressionType(IExpression? expr)
        {
            if (expr == null) return "Unknown";

            return expr.Type switch
            {
                NodeType.NumericLiteral => "Number",
                NodeType.StringLiteral => "String",
                NodeType.BooleanLiteral => "Boolean",
                NodeType.ObjectLiteral => "Object",
                NodeType.VectorDeclaration => "Vector",
                NodeType.FunctionDeclaration => "Function",
                NodeType.AnonymousFunctionDeclaration => "Function",
                NodeType.NullLiteral => "Null",
                NodeType.BinaryExpression => "Number",
                _ => "Unknown"
            };
        }
    }
}