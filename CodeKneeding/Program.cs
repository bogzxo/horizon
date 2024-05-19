using Horizon.Engine;

namespace CodeKneading;

static class Program
{
    public static void Main(string[] args)
    {
        using (var engine = new GameEngine(GameEngineConfiguration.Default with { InitialScene = typeof(MainScene) }))
            engine.Run();
    }
}