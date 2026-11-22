# Getting started

## Running the examples

Horizon.Testing builds against the engine projects next to it, so from a checkout of the engine it runs straight out of its own folder, with the .NET 11 SDK and a GPU that does Vulkan being all it asks for.

```
cd Horizon.Testing
dotnet run                  the selector
dotnet run -- tweens        straight into an example by its id
dotnet run -- --checks      every check one after the other, then out
```

An id that does not exist opens the selector anyway, after printing the ids there are, every id is in `Host/TestCatalog.cs` and most examples also answer to the older names they went by, `dotnet run -- primitives` still opens the shapes.

The program moves its working folder to the folder of the exe before anything else, since the shaders, the skins and `Assets/examples` are copied there at build and `dotnet run` would otherwise start it in the folder of the project, where none of them are, a game of your own wants the same line.

## Keys that work in every example

| Key | What it does |
|---|---|
| Esc | back to the selector |
| Page Up, Page Down | the example before or after, round the ends of the list |
| F1 | folds the list of the example's keys away and back |
| F3 | goes round the performance overlay, a pill, the whole card, nothing |
| F4 | steps the render scale, Auto, Native, 75% and 50% |
| F5 | opens the render scale picker, upscaler and all |
| F6, F7 | step the UI skin and its theme |
| F8, F9 | pause, and step a single tick while paused |
| F10 | the Skyline debugger, in a Debug build |
| F11 | VSync on and off |

The skin and the render scale are remembered between runs, in `examples_skin.txt` and `examples_render_scale.txt` next to the exe, except on a run of the checks, which always wants the same skin and scale. With a controller and no keyboard, View (Back on most pads) goes to the menu when let go, and View held with a bumper steps to the example either side.

## Environment variables

| Variable | What it does |
|---|---|
| `HORIZON_LOG_LOOPS=5` | logs how every loop is doing every 5 seconds, its rate and how long its turns take |
| `HORIZON_LOG_ALLOCATIONS=5` | logs what every thread allocates, by type, every 5 seconds |
| `HORIZON_SHADER_CACHE=off` | compiles every shader anew rather than taking it from the cache |
| `HORIZON_INPUT_SCRIPT=file` | plays a scripted gamepad from a file, with lines like `2.0 tap A` or `8 quit` |
| `HORIZON_SCREENSHOT=f.png@3` | saves what was drawn three seconds in |

## The first example

`quickstart` is the place to start, forty lines that put a bouncing ball with a lamp on it into a lit world with a UI over it, and nothing in it makes a renderer, a camera or a UI, since `Scene2D` hands all of that over before a line of the example runs.

```csharp
public class QuickStartExample : Scene2D, ITestControls
{
    private Vector2 ball = new(0.0f, 120.0f), velocity = new(260.0f, 180.0f);
    private Light2D? lamp;
    private Label status = null!;

    public QuickStartExample()
        : base(lit: true, fancy: true)
    {
        status = UI.CreateModule().AddComponent(new Label { Position = new Vector2(0.0f, -40.0f), Anchor = Horizon.Rendering.Origin.Top, TextScale = 0.3f });
    }

    public override void Initialize()
    {
        base.Initialize();

        Lighting!.Ambient = new Vector3(0.15f, 0.16f, 0.2f);
        lamp = Lighting.AddLight(new Light2D { Radius = 360.0f, Intensity = 1.4f, Size = 10.0f, Color = new Vector3(1.0f, 0.9f, 0.7f) });

        Shapes.Describe = shapes =>
        {
            shapes.FillCircle(ball, 14.0f, new Vector3(1.0f, 0.85f, 0.4f));
        };
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        ball += velocity * dt;
        if (MathF.Abs(ball.X) > 300.0f) velocity.X = -velocity.X;
        if (ball.Y < -200.0f || ball.Y > 300.0f) velocity.Y = -velocity.Y;

        if (lamp is not null) lamp.Position = ball;

        if (Engine.Input.Keyboard.WasPressed(Key.F)) Fancy = !Fancy;
    }
}
```

`Scene2D` comes with a `Camera`, a `Renderer`, `Lighting` when it is made `lit`, `Sprites`, `Shapes` and a `UI`, and `Fancy` switches the lighting between the plain deferred lights and the path traced kind, the full example also puts down a couple of walls in an `OcclusionMap2D` for the light to bounce off, which is worth reading next to this.

The UI is made in the constructor since UI components own nothing on the GPU, the light is added in `Initialize` since that runs on the render thread, and the ball moves in `UpdateState` on the simulation thread, which is the split the [next page](threads-and-entities.md) is about.

## A game of your own

A game outside the engine's checkout takes Horizon from nuget.org, the engine, the desktop platform and the UI are three packages, HIDL comes along with them, and the engine's package copies its shaders, fonts and UI skins next to the program at build and publish.

```xml
<ItemGroup>
  <PackageReference Include="bogzxo.Horizon.Engine" Version="1.4.0" />
  <PackageReference Include="bogzxo.Horizon.Platform.Desktop" Version="1.4.0" />
  <PackageReference Include="bogzxo.Horizon.UI" Version="1.4.0" />
</ItemGroup>
```

The program is the same as the one of the examples without the host, the platform is said before the engine is made, and `Run` opens the window and does not come back until it is closed.

```csharp
using Horizon.Core;
using Horizon.Engine;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
Horizon.Platform.Desktop.DesktopPlatform.Install();

using var engine = new GameEngine(WindowManagerConfiguration.Default1600x900 with { WindowTitle = "My game" });
engine.Run<MainScene>();
```

`WindowManagerConfiguration` is a struct of `init` properties, so anything else about the window and the loops is changed with the same `with`, `UpdatesPerSecond`, `PhysicsUpdatesPerSecond`, `Presentation` and `VSync` among them, Horizon.Testing turns VSync off and ticks at 120 a second, or 240 for a run of the checks, which goes by updates with nobody watching and is done in half the time.
