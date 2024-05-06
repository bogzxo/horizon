using Horizon.HIDL.Lexxing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;

namespace Horizon.HIDL;

using Environment = Horizon.HIDL.Runtime.Environment;

/// <summary>
/// Provides a REPL for using Horizon's custom expressive language. The lexer, parser and interpreter are not intended to be modified or extended by the end user, however extensions can be implemented through native callbacks and native variables. Variables and functions that should be protected may be defined in the Global scope and are protected, alternately scope specific system variables can be declared and are immutable by default as well as being protected from erasing via UserScope.Reset(). Please see the UserScope property for further usage.
/// </summary>
public class HIDLRuntime
{
    public const string VERSION = "0.0.4";
    private static readonly NullValue NULL = new();
    private static readonly Parser parser = new();
    private static readonly Environment scratchEnv = new();

    /// <summary>
    /// The user scope for code execution, variables (and by extension function and objects) may be declared here.
    /// </summary>
    public Environment UserScope { get; init; }

    /// <summary>
    /// The global scope for code execution, system readonly variables (and by extension function and objects) may be declared here that are safe from a userspace reset.
    /// </summary>
    public Environment GlobalScope { get; init; }

    /// <summary>
    /// The interpreter is the backend for handling execution of the abstract syntax tree generated after tokenization.
    /// </summary>
    public HIDLInterpreter Interpreter { get; init; }

    public HIDLRuntime()
    {
        Interpreter = new();
        GlobalScope = new();
        UserScope = new(GlobalScope);

        GlobalScope.Declare("true", new BooleanValue(true), true);
        GlobalScope.Declare("false", new BooleanValue(false), true);
        GlobalScope.Declare("version", new StringValue(VERSION), true);
        GlobalScope.Declare("null", NULL, true);
        GlobalScope.Declare("reset", new NativeFunctionValue((_, _) =>
        {
            UserScope.Reset();
            return new StringValue("UserScope Reset!");
        }), true);
    }

    /// <summary>
    /// Creates a valid runtime value without directly modifying the current environment; This can be used to declare a system object by generating a valid runtime value that can be injected into <see cref="Environment.DeclareSystem(in string identifier, in IRuntimeValue value)"/>.
    /// </summary>
    /// <param name="identifier">The identifier/name.</param>
    /// <param name="code">The code to evaluate.</param>
    public IRuntimeValue GenerateValue(in string input)
    {
        // TODO: i dont even need to explain

        scratchEnv.Copy(UserScope);

        ProgramStatement ast = new Parser().ProduceSyntaxTree(Lexer.Tokenize(input));
        IRuntimeValue val = Interpreter.Evaluate(ast, scratchEnv);
        scratchEnv.Reset(true);
        return val;
    }


    /// <summary>
    /// Evaluates an input, then returns a tuple containing a success flag and the result as a string.
    /// </summary>
    /// <param name="input">The code to be interpreted.</param>
    /// <returns>A tuple containing a success flag and the result as a string.</returns>
    public (bool success, string result) Evaluate(in string input, in bool useGlobalScope = false)
    {
        try
        {
            string? value = Interpreter.Evaluate(parser.ProduceSyntaxTree(Lexer.Tokenize(input)), useGlobalScope ? GlobalScope : UserScope).ToString();
            return (value is not null, value ?? string.Empty);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }
}