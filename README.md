# Horizon

Horizon is a 2D game engine for .NET 11, built around a Vulkan renderer with deferred and path traced lighting, tile maps straight out of Tiled, physics with fluids, particles on the CPU or in a compute shader, a UI framework called UIX, and HIDL, a scripting language of its own that the UI layouts, skins and bindings are written in. It runs on Windows, macOS and Linux, with Android and iOS heads alongside, and it is the engine under the Hex editor and the games built next to it.

A game runs on three threads, the window and input on one, the GPU on another and the simulation on a third, ticking at a fixed rate while frames are drawn between the last two ticks, so things move smoothly at any frame rate, and the engine stays out of the way of the game's own code, a scene is a tree of entities and components with a turn for each thread, and nothing more is asked of it.

## Getting started

Horizon is on nuget.org, a game needs three packages, HIDL comes along with them, and the engine's package copies its shaders, fonts and UI skins next to the program at build and publish.

```xml
<ItemGroup>
  <PackageReference Include="bogzxo.Horizon.Engine" Version="1.4.0" />
  <PackageReference Include="bogzxo.Horizon.Platform.Desktop" Version="1.4.0" />
  <PackageReference Include="bogzxo.Horizon.UI" Version="1.4.0" />
</ItemGroup>
```

```csharp
using Horizon.Core;
using Horizon.Engine;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
Horizon.Platform.Desktop.DesktopPlatform.Install();

using var engine = new GameEngine(WindowManagerConfiguration.Default1600x900 with { WindowTitle = "My game" });
engine.Run<MainScene>();
```

`MainScene` is a `Scene`, or a `Scene2D` for a lit world with a camera, sprites, shapes and a UI already in it, the [guide](docs/guide/getting-started.md) goes from there.

## The examples

Horizon.Testing is the engine's examples, one scene per thing the engine can do, in an order, simplest first, with a selector to pick one and keys to flick between them, every example is a plain scene, the same kind a game is made of, so they double as the tutorial.

```
cd Horizon.Testing
dotnet run                  the selector
dotnet run -- quickstart    straight into an example by its id
dotnet run -- --checks      every self check, leaving with how many failed
```

They go from a bouncing ball with a lamp on it, through sprites, cameras, input, tile maps, particles, physics and menus, to three showcase scenes, a night in town, a dungeon lit by whatever in it glows and a village with a sun that goes round, and the [list of examples](docs/guide/examples.md) says what each one shows and where its source is.

## Documentation

| | |
|---|---|
| [The guide](docs/guide/README.md) | Horizon by way of its examples, from a first scene to the checks |
| [HIDL reference](Horizon.HIDL/docs/reference.md) | The scripting language |
| [Publishing the packages](docs/nuget-publishing.md) | How the NuGet packages are made and published |
| [`docs/context/`](docs/context/) | Maps of the code, for finding your way round the engine itself |

## What is in here

| Folder | What it is |
|---|---|
| `Horizon` | The engine, rendering, content, input, audio, entities, physics, packaged as `bogzxo.Horizon.Engine` |
| `Horizon.UI` | UIX, the compositor, controls, skins, layouts and the code editor |
| `Horizon.HIDL` | HIDL, the scripting language, a submodule of its own (bogzxo/hidl) |
| `Horizon.Platform.Desktop`, `.Android`, `.iOS` | The platforms, the window, input and audio of each |
| `Horizon.Terminal` | A terminal, a VT screen over ConPTY |
| `Horizon.Tooling` | Language server and debug adapter clients for editors |
| `Horizon.Testing`, `.Android`, `.iOS` | The examples, on the desktop and on phones |
| `Horizon.Tests` | The unit tests, `dotnet test` |
| `Horizon.Shaders.Build` | Builds the shader packs phones use, since there is no shader compiler on them |
| `AutoVoxel` | A path traced voxel game, an example and a stress test of the engine |

## Building

```
git clone --recurse-submodules https://github.com/bogzxo/horizon
cd horizon
dotnet build Horizon.sln
dotnet test Horizon.Tests
```

The .NET 11 SDK and a GPU with Vulkan are all it asks for, the SDK version is pinned in `global.json`. Third party code and assets are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
