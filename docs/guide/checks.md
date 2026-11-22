# Checks and adding your own

## The checks

The last heading in the selector is not lessons, the checks are scenes that drive themselves and say whether the engine still does what it did, a scripted pointer clicks through the UI, bodies are dropped and thrown, maps are loaded and compared with what was put in them, and the window's surface is taken away and given back.

```
dotnet run -- --checks
```

runs every check one after the other, prints how each went and closes the window, leaving with the number of checks that failed as its exit code, so a build machine can run it as it is and anything other than zero is a failure. The selector has a button for the same run which comes back to the selector rather than closing, and a single check opens by its id like any example, `dotnet run -- check-physics`.

A run of the checks ticks at 240 updates a second rather than 120 and draws at the platform's default render scale and skin, never at whatever was picked last, so every run is judged the same way, and the picked scale comes back once the run is done.

| Id | What it checks | Source |
|---|---|---|
| `check-tilemap` | three small maps that between them have one of everything a map can have, and that what comes out is what was put in | `Checks/TileMapChecks.cs` |
| `check-physics` | bodies falling, landing, bouncing and thrown, a character stepping a slab and stopped by a wall, a fluid finding its level | `Checks/PhysicsCheck.cs` |
| `check-ui` | a scripted pointer clicking through buttons, sliders and toggles made in C# and by a script | `Checks/UIBasicsCheck.cs` |
| `check-ui-layout` | grids, scaling a whole UI, scrolling, number boxes, layouts from files with items from templates | `Checks/UILayoutCheck.cs` |
| `check-ui-controls` | the dropdown and its list, sliders with ends and steps, the colour picker, a layout's entrances | `Checks/UIControlsCheck.cs` |
| `check-ui-navigation` | the navigator driven from code, menus, grids, list boxes, tabbing, pasting, tooltips and dialogs | `Checks/UINavigationCheck.cs` |
| `check-ui-skins` | every skin under `Assets/uix` in every theme it has, whether each comes up, has its art and holds a layout made for another | `Checks/UISkinsCheck.cs` |
| `check-ui-parts` | resizing by the edges, readouts, graphs and meters that make no garbage, the art picker and the performance overlay | `Checks/UIPartsCheck.cs` |
| `check-ui-text` | styled text out of tags in square brackets, and markdown in a label, wrapped and clicked | `Checks/UITextCheck.cs` |
| `check-surface` | the window's surface taken away and given back three times with resizes in between, the device and a render target lasting through it | `Checks/SurfaceCheck.cs` |
| `check-render-scale` | the scene drawn at 0.75 and 0.5 and blown up with CAS, bilinear and nearest, the UI at full size or drawn small, the GPU upscalers matched against their references | `Checks/RenderScaleCheck.cs` |
| `check-render-scale-quality` | the upscalers side by side, CAS, GSR 1, FSR 1 and bilinear at 0.75, 0.5 and 720p, how close each comes to native and its GPU time in the log | `Checks/RenderScaleQualityCheck.cs` |
| `check-crt-resolution` | the CRT effect at native, 75% and 45% with zoom and aspect changes, the tube grid staying the size of the artwork | `Checks/RenderScaleQualityCheck.Crt.cs` |

The phone builds add checks of their own, `Horizon.Testing.Android` and `Horizon.Testing.iOS` each have a README that says how to run them there.

## What a run prints

Checks print the moment they are made, so a run from the command line shows what broke as it breaks, and each scene gets a line as it is judged, lines like these, with the numbers of the run.

```
[Tween test] pass: ends where it was told to
[Checks] check-physics, 41 of 41 held.
[Checks] 512 of 512 checks passed.
```

The whole run is kept as a report, a line per check scene with its failures and skips under it and the totals last, and the same report as JSON for a script, on a phone it is also written to a file for `adb pull`. A check that cannot be made on the platform it runs on is skipped with the reason, neither passed nor failed, and a check scene that never finishes within its time limit is judged when the limit runs out, what it made so far counts and never finishing counts as one more failure, so a hung check never holds the whole run up.

## Checks inside an example

Examples check things too, `tweens`, `gamepads` and the tile map checks run a list of checks as the scene starts and put the results on screen, using `TestChecks`, a plain checklist where every check is a name and whether it held.

```csharp
var checks = new TestChecks("Tweens");
checks.Check("ends where it was told to", () => tweened == 10.0f);
checks.Report();
resultsLabel.Text = checks.Describe();
```

A check that throws is a check that failed, it is noted and the rest carry on, `Skip` notes a check that cannot be made here along with why, and `Describe` gives every check a line of its own for a label. The UI checks that click about use `UISelfTest` instead, which drives a scripted pointer over the compositor.

## Adding an example

Adding an example is two steps, a scene in the folder of its level under `Examples/`, and a line for it in `Host/TestCatalog.cs`, nothing else needs to know it exists.

```csharp
new("quickstart", TestLevel.FirstSteps, "Quick start",
    "The least a scene can be. Scene2D hands over a lit world, a camera and a UI, and forty lines put a bouncing ball with a lamp on it in there.",
    ["Scene2D", "Light2D", "OcclusionMap2D", "Shapes"],
    "Examples/FirstSteps/QuickStartExample.cs", () => new QuickStartExample()),
```

The line is the id for the command line, the level it is listed under, its name, a sentence or two shown next to the list, the parts of the engine it is about, where its source is, and a factory that makes a fresh scene every time it is started, a factory rather than a scene on purpose, since leaving an example throws its scene away, GPU resources and all, so coming back to it starts from scratch. `Aliases` keeps old ids working, and `Unsupported` names why an example cannot run on a platform, its row still shows and starting it puts up a screen that says why, so nothing quietly goes missing from the list.

A scene that reacts to keys or the mouse implements `ITestControls`, and the host lists them on screen, so the keys can never go out of date with the code.

```csharp
public IReadOnlyList<TestControl> Controls { get; } = [new("G", "toggle gravity")];
```

Art and maps go in `Assets/examples` as files, made by a script in `Art/` if they are made at all, never painted as the scene starts.

## Adding a check

A check is a scene at the `TestLevel.Checks` level that implements `ISelfCheck`, which is what lets the host run it with the others and judge it.

```csharp
internal interface ISelfCheck
{
    bool Finished { get; }   // whether it has checked everything it was going to
    int Failed { get; }      // how many of its checks did not hold
    int Count { get; }       // how many it made
    int Skipped => 0;        // how many it left out because the platform cannot do what they check
}
```

The host watches `Finished` every update and moves on once it reads true, checks have no number in the selector since they are not a lesson, and a run of the checks picks them up by their level alone.
