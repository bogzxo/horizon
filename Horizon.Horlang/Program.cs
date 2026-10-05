using System;
using System.ComponentModel;
using System.Text;


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

        runtime.UserScope.Declare("env", new ObjectValue(
            new Dictionary<string, IRuntimeValue>
            {
                {
                    "print",
                    new NativeFunctionValue((values, _) =>
                    {
                        StringBuilder sb = new();
                        foreach (var item in values)
                            sb.Append(item.ToString());

                        Console.WriteLine($"{promptVal.Value} " + sb.ToString());
                        return new StringValue(sb.ToString().Trim());
                    })
                },
                {
                    "clear",
                    new NativeFunctionValue((args, env) =>
                    {
                        ClearConsole();
                        return new StringValue("Cleared!");
                    })
                }
            }
        ), true );

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

        // save("file.hor") writes everything declared since the REPL started, save("file.hor", "name", value) one value.
        HashSet<string> hostNames = [];
        runtime.UserScope.Declare("save", new NativeFunctionValue((args, env) =>
        {
            if (args.Length < 1 || args[0].Type != Runtime.ValueType.String)
            {
                Console.WriteLine("Please specify a file to save to.");
                return new NullValue();
            }

            string fileName = ((StringValue)args[0]).Value;

            try
            {
                string text = args.Length >= 3 && args[1] is StringValue name
                    ? HIDLWriter.WriteDeclaration(name.Value, args[2])
                    : HIDLWriter.Write(runtime.UserScope, (variable, _) => !hostNames.Contains(variable));

                File.WriteAllText(fileName, text);
                return new StringValue($"Saved {fileName}");
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                return new NullValue();
            }
        }), true);

        // What the REPL declared itself isn't the user's to save, and is there already when the file is loaded again.
        hostNames.UnionWith(runtime.UserScope.Variables.Keys);

        bool startupFile = args.Length > 0 && File.Exists(args[0]);
        Console.WriteLine(runtime.Evaluate(File.ReadAllText("test.hor")).result);

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