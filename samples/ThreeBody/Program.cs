using Horizon.Core;
using Horizon.Engine;

using ThreeBody;

// The engine's skins and shaders are next to the exe, and dotnet run starts it in the project's folder
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// A desktop window, said before the engine is made
Horizon.Platform.Desktop.DesktopPlatform.Install();

using var engine = new GameEngine(WindowManagerConfiguration.Default1600x900 with { WindowTitle = "Three Body" });
engine.Run<MainScene>();
