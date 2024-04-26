using System.Runtime.CompilerServices;
using System.Text;

using Bogz.Logging.Loggers;

using Horizon.Horlang.Runtime;

namespace Horizon.Horlang;

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
            return new StringValue(sb.ToString().Trim());
        }), true);
        runtime.Environment.Declare("read", new NativeFunctionValue((args, env) =>
        {
            return new StringValue(Console.ReadLine() ?? string.Empty);
        }), true);
        runtime.Environment.Declare("ld", new NativeFunctionValue((args, env) =>
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Please specify a file to load.");
                return new NullValue();
            }
            else if (args[0].Type != Runtime.ValueType.String)
            {
                Console.WriteLine("Please specify a valid string to load.");
                return new NullValue();
            }

            string fileName = ((StringValue)args[0]).Value;

            if (!File.Exists(fileName))
            {
                Console.WriteLine("Please specify a file to load.");
                return new NullValue();
            }

            return new StringValue(runtime.Evaluate(File.ReadAllText(fileName)));
        }), true);

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
