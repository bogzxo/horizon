using System.Runtime.CompilerServices;
using System.Text;

using Bogz.Logging.Loggers;

using Horizon.Horlang.Lexxing;
using Horizon.Horlang.Parsing;
using Horizon.Horlang.Runtime;


namespace Horizon.Horlang;

using Environment = Horizon.Horlang.Runtime.Environment;
public class HorlangRuntime
{
    public Environment Environment { get; init; }
    public HorlangInterpreter Interpreter { get; init; }

    public HorlangRuntime()
    {
        Environment = new();
        Environment.Declare("true", new BooleanValue(true));
        Environment.Declare("false", new BooleanValue(false));
        Environment.Declare("version", new StringValue("0.0.2"));
        Environment.Declare("null", new NullValue());
        Interpreter = new HorlangInterpreter();
    }
    public string Evaluate(in string input)
    {
        try
        {
            Token[] tokens = Lexer.Tokenize(input);
            var parser = new Parser();
            ProgramStatement ast = parser.ProduceSyntaxTree(tokens);

            return Interpreter.Evaluate(ast, Environment).ToString() ?? string.Empty;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }
        return string.Empty;
    }
}

internal class Program
{
    static void Main(string[] _)
    {
        Console.WriteLine("Horlang REPL");
        HorlangRuntime runtime = new();
        runtime.Environment.Declare("print", new NativeFunctionValue((args, env) =>
        {
            StringBuilder sb = new();
            foreach (var item in args)
                sb.Append(item.ToString() + " ");

            Console.WriteLine("> " + sb.ToString());
            return new StringValue(string.Empty);
        }));

        while (true)
        {
            Console.Write("> ");
            string? input = Console.ReadLine();

            if (input is null) continue;

            Console.WriteLine(runtime.Evaluate(input));
            Console.WriteLine();
        }
        ConcurrentLogger.Instance.Dispose();
    }
}
