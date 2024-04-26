using Horizon.Horlang.Lexxing;
using Horizon.Horlang.Parsing;
using Horizon.Horlang.Runtime;

namespace Horizon.Horlang;

using Environment = Horizon.Horlang.Runtime.Environment;

/// <summary>
/// Provides a REPL for using Horizon's custom expressive language. The lexer, parser and interpreter are not intended to be modified or extended by the end user, however extensions can be implemented through native callbacks and native variables. Please see the Environment property for further usage.
/// </summary>
public class HorlangRuntime
{
    public const string VERSION = "0.0.3";

    /// <summary>
    /// The global scope for code execution, variables (and by extension function and objects) may be declared here.
    /// </summary>
    public Environment Environment { get; init; }

    /// <summary>
    /// The interpreter is the backend for handling execution of the abstract syntax tree generated after tokenization.
    /// </summary>
    public HorlangInterpreter Interpreter { get; init; }

    public HorlangRuntime()
    {
        Interpreter = new();
        Environment = new();

        Environment.Declare("true", new BooleanValue(true));
        Environment.Declare("false", new BooleanValue(false));
        Environment.Declare("version", new StringValue(VERSION));
        Environment.Declare("null", new NullValue());

        Evaluate("func not(input) { !input }");
    }
    /// <summary>
    /// Shorthand function for declaring a native callback, the environment is provided to optionally lookup scoped variables in the case of the function being defined as part of an object.
    /// </summary>
    /// <param name="identifier">The identifier/name of the function</param>
    /// <param name="nativeFunction">The function definition.</param>
    /// <param name="isConst">A flag determining whether or not this may be overwritten.</param>
    /// <returns>The final computed runtime value.</returns>
    public IRuntimeValue DeclareNativeFunction(in string identifier, in NativeFunctionValue nativeFunction, in bool isConst = true)
        => Environment.Declare(identifier, nativeFunction, isConst);

    /// <summary>
    /// Shorthand function for declaring a native variable, the <see cref="NativeValue"/> structure provides accessor and mutator callbacks.
    /// </summary>
    /// <param name="identifier">The identifier/name of the value</param>
    /// <param name="nativeFunction">The value definition.</param>
    /// <param name="isConst">A flag determining whether or not this may be overwritten.</param>
    /// <returns>The final computed runtime value.</returns>
    public IRuntimeValue DeclareNativeValue(in string identifier, in NativeValue nativeValue, in bool isConst = true)
        => Environment.Declare(identifier, nativeValue, isConst);

    /// <summary>
    /// Evaluates an input, then returns a tuple containing a success flag and the result as a string.
    /// </summary>
    /// <param name="input">The code to be interpreted.</param>
    /// <returns>A tuple containing a success flag and the result as a string.</returns>
    public (bool success, string result) Evaluate(in string input)
    {
        try
        {
            Token[] tokens = Lexer.Tokenize(input);
            var parser = new Parser();

            ProgramStatement ast = parser.ProduceSyntaxTree(tokens);
            string? value = Interpreter.Evaluate(ast, Environment).ToString();
            return (value is not null, value ?? string.Empty);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }
}