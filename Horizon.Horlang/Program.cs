using System;
using System.Text;

using Bogz.Logging.Loggers;

using Horizon.HIDL.Runtime;

namespace Horizon.HIDL;

internal class Program
{
    static readonly string[] INTRO_FRAMES = [@"
      ___                       ___           ___ 
     /\__\          ___        /\  \         /\__\
    /:/  /         /\  \      /::\  \       /:/  /
   /:/__/          \:\  \    /:/\:\  \     /:/  / 
  /::\  \ ___      /::\__\  /:/  \:\__\   /:/  /  
 /:/\:\  /\__\  __/:/\/__/ /:/__/ \:|__| /:/__/   
 \/__\:\/:/  / /\/:/  /    \:\  \ /:/  / \:\  \   
      \::/  /  \::/__/      \:\  /:/  /   \:\  \  
      /:/  /    \:\__\       \:\/:/  /     \:\  \ 
     /:/  /      \/__/        \::/__/       \:\__\
     \/__/                     ~~            \/__/", @"
      ___                                             
     /\  \                     _____                  
     \:\  \       ___         /::\  \                 
      \:\  \     /\__\       /:/\:\  \                
  ___ /::\  \   /:/__/      /:/  \:\__\   ___     ___ 
 /\  /:/\:\__\ /::\  \     /:/__/ \:|__| /\  \   /\__\
 \:\/:/  \/__/ \/\:\  \__  \:\  \ /:/  / \:\  \ /:/  /
  \::/__/       ~~\:\/\__\  \:\  /:/  /   \:\  /:/  / 
   \:\  \          \::/  /   \:\/:/  /     \:\/:/  /  
    \:\__\         /:/  /     \::/  /       \::/  /   
     \/__/         \/__/       \/__/         \/__/    ", @"
      ___                      _____                  
     /__/\        ___         /  /::\                 
     \  \:\      /  /\       /  /:/\:\                
      \__\:\    /  /:/      /  /:/  \:\   ___     ___ 
  ___ /  /::\  /__/::\     /__/:/ \__\:| /__/\   /  /\
 /__/\  /:/\:\ \__\/\:\__  \  \:\ /  /:/ \  \:\ /  /:/
 \  \:\/:/__\/    \  \:\/\  \  \:\  /:/   \  \:\  /:/ 
  \  \::/          \__\::/   \  \:\/:/     \  \:\/:/  
   \  \:\          /__/:/     \  \::/       \  \::/   
    \  \:\         \__\/       \__\/         \__\/    
     \__\/                                            ", @"
      ___                        ___           ___ 
     /  /\           ___        /  /\         /  /\
    /  /:/          /__/\      /  /::\       /  /:/
   /  /:/           \__\:\    /  /:/\:\     /  /:/ 
  /  /::\ ___       /  /::\  /  /:/  \:\   /  /:/  
 /__/:/\:\  /\   __/  /:/\/ /__/:/ \__\:| /__/:/   
 \__\/  \:\/:/  /__/\/:/~~  \  \:\ /  /:/ \  \:\   
      \__\::/   \  \::/      \  \:\  /:/   \  \:\  
      /  /:/     \  \:\       \  \:\/:/     \  \:\ 
     /__/:/       \__\/        \__\::/       \  \:\
     \__\/                         ~~         \__\/"];

    private static bool shouldHalt = false;
    private static void Main(string[] args)
    {
        Console.Title = "Horizon Integrated Dynamic Language Runtime";
        RunIntro();

        HIDLRuntime runtime = new();

        StringValue promptVal = new(">");
        runtime.UserScope.Declare("prompt", new NativeValue(() =>
        {
            return promptVal;
        }, (val) =>
        {
            promptVal = (StringValue)val;
        }), false);

        runtime.UserScope.Declare("exit", new NativeFunctionValue((args, env) =>
        {
            shouldHalt = true;
            return new StringValue("Halting...");
        }), true);
        runtime.UserScope.Declare("clear", new NativeFunctionValue((args, env) =>
        {
            ClearConsole();
            return new StringValue("Cleared!");
        }), true);

        runtime.UserScope.Declare("print", new NativeFunctionValue((args, env) =>
        {
            StringBuilder sb = new();
            foreach (var item in args)
                sb.Append(item.ToString());

            Console.WriteLine($"{promptVal.Value} " + sb.ToString());
            return new StringValue(sb.ToString().Trim());
        }), true);
        runtime.UserScope.Declare("read", new NativeFunctionValue((args, env) =>
        {
            return new StringValue(Console.ReadLine() ?? string.Empty);
        }), true);
        runtime.UserScope.Declare("ld", new NativeFunctionValue((args, env) =>
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

        bool startupFile = args.Length > 0 && File.Exists(args[0]);

        while (!shouldHalt)
        {
            if (startupFile)
            {
                startupFile = false;
                Console.WriteLine(runtime.Evaluate(File.ReadAllText(args[0])).result);
                continue;
            }
            Console.Write($"{promptVal.Value} ");
            string? input = Console.ReadLine();

            if (input is null) continue;

            Console.WriteLine(runtime.Evaluate(input).result);
            Console.WriteLine();
        }
        ConcurrentLogger.Instance.Dispose();
    }

    private static void ClearConsole()
    {
        Console.CursorVisible = false;
        Console.Clear();
        Console.SetCursorPosition(0, 0);
        Console.WriteLine(INTRO_FRAMES[INTRO_FRAMES.Length - 1]);
        Console.SetCursorPosition(0, 13);
        Console.WriteLine($"Horizon Integrated Dynamic Language REPL v{HIDLRuntime.VERSION}");
        Console.CursorVisible = true;
    }

    private static void RunIntro()
    {
        Console.CursorVisible = false;
        for (int index = 0; index < INTRO_FRAMES.Length; index++)
        {
            Console.SetCursorPosition(0, 0);
            Console.Clear();
            Console.WriteLine(INTRO_FRAMES[index]);
            Thread.Sleep(500);
        }
        Console.SetCursorPosition(0, 13);
        Console.WriteLine($"Horizon Integrated Dynamic Language REPL v{HIDLRuntime.VERSION}");
        Console.CursorVisible = true;
    }
}