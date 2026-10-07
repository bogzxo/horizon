using Horizon.HIDL.Lexing;
using Horizon.HIDL.Library;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;

using Environment = Horizon.HIDL.Runtime.Environment;

namespace Horizon.HIDL;

/// <summary>
/// A HIDL program's home. The scopes it runs in, the interpreter that runs it, and the library every script gets
/// (math, text, lists, print, include). A host adds its own by declaring system variables in the user scope, see
/// <see cref="Natives"/> for the quick way, and evaluates source with <see cref="Evaluate"/>.
/// <code>
/// var runtime = new HIDLRuntime { BaseDirectory = "Assets/data" };
/// runtime.UserScope.DeclareSystem("fight", Natives.Object(("round", new NumberValue(1))));
/// var (ok, result) = runtime.Evaluate(File.ReadAllText("Assets/data/moves.hor"));
/// </code>
/// </summary>
public class HIDLRuntime
{
    public const string VERSION = "0.1.0";
    public static readonly NullValue NULL = Values.Null;

    // The runtime whose program is running right now on this thread, for library functions that call back into
    // scripts (sort, map). Nested evaluations put the one before back when they are done
    [ThreadStatic]
    private static HIDLRuntime? current;

    /// <summary>
    /// The runtime running a program on this thread right now, null outside of one.
    /// </summary>
    public static HIDLRuntime? Current => current;

    /// <summary>
    /// The user scope for code execution, variables (and by extension function and objects) may be declared here.
    /// </summary>
    public Environment UserScope { get; init; }

    /// <summary>
    /// The global scope. The library lives here, safe from a reset of the user scope.
    /// </summary>
    public Environment GlobalScope { get; init; }

    /// <summary>
    /// The interpreter is the backend for handling execution of the abstract syntax tree generated after tokenization.
    /// </summary>
    public HIDLInterpreter Interpreter { get; init; }

    /// <summary>
    /// Where print writes to. The console unless the host says otherwise, null for nowhere.
    /// </summary>
    public Action<string>? Output { get; set; } = Console.WriteLine;

    /// <summary>
    /// The folder include looks for files in when the program isn't from a file itself. The working directory unless set.
    /// </summary>
    public string? BaseDirectory { get; set; }

    /// <summary>
    /// How include reads a file, given its path as the script wrote it and the folder of the file that asked. Reads
    /// from disk unless the host has files somewhere else (a pack, a download).
    /// </summary>
    public Func<string, string?, string>? IncludeReader { get; set; }

    // Files that are being included right now, to catch a file that includes itself
    private readonly Stack<string> including = new();

    public HIDLRuntime()
    {
        Interpreter = new();
        GlobalScope = new();
        UserScope = new(GlobalScope);

        GlobalScope.Declare("true", Values.True, true);
        GlobalScope.Declare("false", Values.False, true);
        GlobalScope.Declare("null", NULL, true);
        GlobalScope.Declare("version", new StringValue(VERSION), true);

        GlobalScope.Declare("reset", Natives.Function("reset", _ =>
        {
            UserScope.Reset();
            return new StringValue("UserScope Reset!");
        }), true);

        // to_text(value) gives a value back as the source that declares it, to_text() the whole user scope
        GlobalScope.Declare("to_text", Natives.Function("to_text", args => new StringValue(args.Count > 0 ? HIDLWriter.Write(args[0]) : ToText())), true);

        CoreLibrary.Declare(this, GlobalScope);
        TextLibrary.Declare(GlobalScope);
        MathLibrary.Declare(GlobalScope);
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
    /// Creates a valid runtime value without directly modifying the current environment. This can be used to declare a
    /// system object by generating a valid runtime value that can be injected into <see cref="Environment.DeclareSystem"/>.
    /// </summary>
    public (bool success, IRuntimeValue result) GenerateValue(in string input) => GenerateValue(input, out _);

    /// <summary>
    /// Creates a valid runtime value without directly modifying the current environment and returns all declared runtime values.
    /// </summary>
    /// <param name="declaredValues">The runtime values declared during evaluation in the scratch environment.</param>
    public (bool success, IRuntimeValue result) GenerateValue(in string input, out Dictionary<string, IRuntimeValue> declaredValues)
    {
        Environment scratch = new(UserScope);
        IRuntimeValue value = NULL;
        bool success = false;

        try
        {
            value = Run(Parse(input), scratch);
            success = true;
        }
        catch
        {
            // The caller only asked whether it worked
        }

        declaredValues = scratch.GetAllDeclaredValues(true);
        scratch.Reset(true);
        return (success, value);
    }

    /// <summary>
    /// Runs a program and hands back whether it worked and its last value as text. What went wrong, if it didn't.
    /// </summary>
    public (bool success, string result) Evaluate(in string input, in bool useGlobalScope = false)
    {
        try
        {
            IRuntimeValue value = Run(Parse(input), useGlobalScope ? GlobalScope : UserScope);
            return (true, Values.Text(value));
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    /// <summary>
    /// Runs a program and hands back its last value, throwing with what went wrong if it didn't work.
    /// </summary>
    public IRuntimeValue Run(in string input) => Run(Parse(input), UserScope);

    /// <summary>
    /// Runs a program from a file, with include looking next to it. Throws with the file and what went wrong.
    /// </summary>
    public IRuntimeValue RunFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"There is no '{path}'.");

        string? before = BaseDirectory;
        BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        try
        {
            return Run(Parse(File.ReadAllText(path)), UserScope);
        }
        catch (Exception e) when (e is not FileNotFoundException)
        {
            throw new HidlRuntimeException($"'{path}': {e.Message}");
        }
        finally
        {
            BaseDirectory = before;
        }
    }

    /// <summary>
    /// Runs a syntax tree in a scope, as the program of this runtime.
    /// </summary>
    public IRuntimeValue Run(ProgramStatement program, Environment scope)
    {
        HIDLRuntime? before = current;
        current = this;
        try
        {
            return Interpreter.Evaluate(program, scope);
        }
        finally
        {
            current = before;
        }
    }

    /// <summary>
    /// Turns source into a syntax tree, which can be run as often as wanted.
    /// </summary>
    public static ProgramStatement Parse(in string input) => new Parser().ProduceSyntaxTree(Lexer.Tokenize(input));

    /// <summary>
    /// What include("file.hor") does. The file is run in a scope of its own over the global one and everything it
    /// declared comes back as an object, so <c>include("shared.hor").moves</c> is the moves of that file.
    /// </summary>
    public IRuntimeValue Include(string file)
    {
        string? folder = BaseDirectory;
        string path = Path.IsPathRooted(file) || folder is null ? file : Path.Combine(folder, file);
        string full = Path.GetFullPath(path);

        if (including.Contains(full))
            throw new HidlRuntimeException($"'{file}' includes itself, round and round.");

        string source = IncludeReader is { } reader
            ? reader(file, folder)
            : File.Exists(full) ? File.ReadAllText(full) : throw new HidlRuntimeException($"There is no '{path}' to include.");

        Environment scope = new(GlobalScope);
        string? before = BaseDirectory;

        including.Push(full);
        BaseDirectory = Path.GetDirectoryName(full);
        try
        {
            Interpreter.Evaluate(Parse(source), scope);
        }
        catch (HidlRuntimeException e)
        {
            throw new HidlRuntimeException($"'{file}': {e.Message}");
        }
        catch (ParseException e)
        {
            throw new HidlRuntimeException($"'{file}': {e.Message}");
        }
        finally
        {
            BaseDirectory = before;
            including.Pop();
        }

        return new ObjectValue(new Dictionary<string, IRuntimeValue>(scope.Variables));
    }
}
