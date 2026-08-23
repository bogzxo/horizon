using System;
using System.Collections.Generic;
using System.Text;
using Horizon.Engine;

namespace Horizon.Testing;

internal class Program
{
    public static void Main(string[] _)
    {
        using var engine = new GameEngine(GameEngineConfiguration.Default with
        {
            
        });
        engine.SetScene(new Scenes.UITestScene());
        engine.Run();
    }
}