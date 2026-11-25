# The Horizon guide

Horizon is a 2D game engine for .NET 11, with a Vulkan renderer, lit and path traced lighting, tile maps, physics, particles, a UI framework called UIX and a scripting language called HIDL, and this guide walks through all of it by way of Horizon.Testing, the project of examples that ships with the engine, one scene per thing the engine can do, with a small host that lets you pick one and flick between them.

Every example is a plain Scene, the same kind you would write for your own game, so the examples double as the tutorial, they come in an order, simplest first, and each one only leans on the ones before it, which means reading them top to bottom never has you looking at something that has not been explained yet.

## Reading order

1. [Getting started](getting-started.md), running the examples, the keys that work in all of them, and a first scene of your own.
1. [A first game: the three body problem](a-first-game.md), `samples/ThreeBody`, a whole small game on the NuGet packages, read through.
2. [Threads, entities and components](threads-and-entities.md), how Horizon runs, the one thing that will catch you out if you skip it.
3. [Drawing, input and the world](drawing-and-input.md), shapes, sprites, cameras, the keyboard, the mouse and gamepads.
4. [Making a game](making-a-game.md), tweens, scenes and transitions, tile maps, particles, physics and a bit of 3D.
5. [Light and effects](light-and-effects.md), deferred and path traced lighting, post processing and render scale.
6. [UI and HIDL](ui-and-hidl.md), a HUD, a panel in C# and as a script, screens out of layout files, lists, dialogs, styled text and components of your own.
7. [Desktop applications](desktop-applications.md), Horizon as the engine of high performance desktop GUI programs, the way [Hex](https://github.com/bogzxo/hex) is built on it.
8. [The examples, one by one](examples.md), every example in the selector, what it shows and where its source is.
9. [Checks and adding your own](checks.md), the scenes that test the engine by themselves, and how to add an example or a check.

## Where things are

| Folder | What is in it |
|---|---|
| `Horizon.Testing/Program.cs` | The entry point, the engine is made, the host and the performance overlay go on it, and the window opens |
| `Horizon.Testing/Host/` | The host, the selector, the list of examples (`TestCatalog.cs`) and the check runner |
| `Horizon.Testing/Examples/` | The examples, a folder per level, `FirstSteps`, `Game`, `Lighting`, `Showcase` and `Internals` |
| `Horizon.Testing/Checks/` | Scenes that drive themselves and say whether the engine still does what it did |
| `Horizon.Testing/Assets/examples/` | The art, maps and UI layouts of the examples, as files, the way a game keeps them |
| `Horizon.Testing/Art/` | The Python scripts that make the art that is generated rather than drawn |
| `Horizon.Testing/shaders/testing/` | The few shaders the examples bring with them |
| `Horizon.Testing.Android/`, `Horizon.Testing.iOS/` | The same examples on a phone, each with a README of its own |

The maps open in Tiled and the layouts open in Hex, nothing an example shows is painted by code as the scene starts, what is generated is generated once by a script in `Art/` and saved as a file like anything else.
