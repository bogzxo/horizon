using System.Text;

using Bogz.Logging.Loggers;

using Horizon.Horlang.Runtime;

namespace Horizon.Horlang;

internal class Program
{
    private static bool shouldHalt = false;
    private static void Main(string[] _)
    {
        Console.WriteLine($"Horlang REPL v{HorlangRuntime.VERSION}");
        HorlangRuntime runtime = new();

        StringValue valTest = new("teehee");
        runtime.Environment.Declare("val", new NativeValue(() =>
        {
            return valTest;
        }, (val) =>
        {
            valTest = (StringValue)val;
        }), false);
        runtime.Environment.Declare("exit", new NativeFunctionValue((args, env) =>
        {
            shouldHalt = true;
            return new StringValue("Halting...");
        }), true);

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

            return new StringValue(runtime.Evaluate(File.ReadAllText(fileName)).result);
        }), true);

        while (!shouldHalt)
        {
            Console.Write("> ");
            string? input = Console.ReadLine();

            if (input is null) continue;

            Console.WriteLine(runtime.Evaluate(input).result);
            Console.WriteLine();
        }
        ConcurrentLogger.Instance.Dispose();
    }
}