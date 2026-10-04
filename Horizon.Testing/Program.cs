using System;
using System.Collections.Generic;
using System.Text;
using Horizon.Engine;

namespace Horizon.Testing;

internal class Program
{
    public static void Main(string[] args)
    {
        using var engine = new GameEngine(GameEngineConfiguration.Default with
        {

        });

        var host = engine.AddEntity(new TestHost(TestCatalog.Tests));

        // `dotnet run -- particles` starts straight in a test, by the id it has in TestCatalog.
        // Without one, or with one that doesn't exist, the selector comes up.
        if (args.Length > 0 && TestCatalog.Find(args[0]) is { } test)
        {
            host.Start(test);
        }
        else
        {
            if (args.Length > 0)
                Console.WriteLine($"There is no test called '{args[0]}'. The tests are: {string.Join(", ", TestCatalog.Tests.Select(t => t.Id))}.");

            host.ShowSelector();
        }

        engine.Run();
    }
}