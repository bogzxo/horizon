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
    public static readonly NullValue NULL = new();
    private static readonly Parser parser = new();

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

        // to_text(value) gives a value back as the source that declares it, to_text() the whole user scope.
        GlobalScope.Declare("to_text", new NativeFunctionValue((args, _) =>
        {
            return new StringValue(args.Length > 0 ? HIDLWriter.Write(args[0]) : ToText());
        }), true);
    }

    /// <summary>
    /// Writes everything declared in the user scope as source that declares it again when it is evaluated,
    /// see <see cref="HIDLWriter"/> for what can be written.
    /// </summary>
    public string ToText() => HIDLWriter.Write(UserScope);

    /// <summary>
    /// Saves everything declared in the user scope to a file that <see cref="Evaluate"/> can read back.
    /// </summary>
    public void Save(in string path) => File.WriteAllText(path, ToText());

    /// <summary>
    /// Creates a valid runtime value without directly modifying the current environment; This can be used to declare a system object by generating a valid runtime value that can be injected into <see cref="Environment.DeclareSystem(in string identifier, in IRuntimeValue value)"/>.
    /// </summary>
    /// <param name="input">The code to evaluate.</param>
    public (bool success, IRuntimeValue result) GenerateValue(in string input)
    {
        return GenerateValue(input, out _);
    }

    /// <summary>
    /// Creates a valid runtime value without directly modifying the current environment and returns all declared runtime values.
    /// </summary>
    /// <param name="input">The code to evaluate.</param>
    /// <param name="declaredValues">The runtime values declared during evaluation in the scratch environment.</param>
    public (bool success, IRuntimeValue result) GenerateValue(in string input, out Dictionary<string, IRuntimeValue> declaredValues)
    {
        Environment scratchEnv = new Environment(UserScope);
        IRuntimeValue val = NULL;
        bool success = false;
        try
        {
            ProgramStatement ast = new Parser().ProduceSyntaxTree(Lexer.Tokenize(input));
            val = Interpreter.Evaluate(ast, scratchEnv);
            success = true;
        }
        catch
        {
        }

        declaredValues = scratchEnv.GetAllDeclaredValues(true);
        scratchEnv.Reset(true);
        return (success, val);
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
            string? value = Interpreter.Evaluate(parser.ProduceSyntaxTree(Lexer.Tokenize(input)), useGlobalScope ? GlobalScope : UserScope)?.ToString();
            return (value is not null, value ?? string.Empty);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }
}