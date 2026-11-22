# Threads, entities and components

The example for this page is `entities` (`Examples/FirstSteps/EntitiesExample.cs`), three hives that keep hatching little bugs which fly about for a few seconds and get binned, while the panel on the left prints the scene's actual entity tree, read live, so additions, set ups, switch offs and destroys can be watched as they happen.

## Three threads

Horizon runs on three threads, the one that calls `Run()` looks after the window and input, the render thread owns the GPU and draws the frames, and the simulation thread runs `UpdateState` and `UpdatePhysics`, tick after tick, 120 times a second each unless the engine is told otherwise.

Frames never read the game while it is running, at the end of every tick everything publishes a snapshot of itself in `Capture`, and frames are drawn between the last two ticks, so things move smoothly at any frame rate, regardless of how the tick rate and the refresh rate of the screen line up. The `pacing` example puts a number on how evenly that comes out.

The rule that follows from all of this, and the one that will catch you out if you skip it, is that constructors never touch the GPU, that happens in `Initialize`, which runs on the render thread. A constructor can run on any thread, the examples' host happens to make scenes on the render thread and a game might not, so a texture, a shader or a buffer made in a constructor works on one machine and crashes on another.

## Entities and components

Everything in Horizon is an `Entity`, a `GameObject` is an entity with children and components, and a `GameComponent` is a bit of behaviour bolted onto one, every one of them gets the same turns.

| Turn | Thread | When |
|---|---|---|
| constructor | any | when it is made, no GPU |
| `Initialize` | render | once, before its first turn, with `Parent` already set |
| `UpdateState` | simulation | every tick, input has already moved on for this tick |
| `UpdatePhysics` | simulation | every physics step, the same `dt` every time |
| `Capture` | simulation | at the end of a tick, publishing what the frame will draw |
| `Render` | render | every frame |

```csharp
public sealed class Spin : GameComponent
{
    private TransformComponent2D transform = null!;

    // Render thread, once, before its first turn. Parent is set by now
    public override void Initialize() => transform = Parent.GetComponent<TransformComponent2D>()!;

    // Simulation thread, the same dt every step
    public override void UpdatePhysics(float dt) => transform.Rotation += 90.0f * dt;
}

// In your scene's Initialize
var crate = batch.AddEntity(new Sprite(new Vector2(32)));
crate.ConfigureAtlas(atlas, sheet, "star");
crate.AddComponent(new Spin());
batch.Add(crate);
```

`AddEntity` puts a child in the tree so it gets its turns, and `AddComponent` bolts a component on, a scene's own `Initialize` adds what it starts with and then calls `base.Initialize()`, which sets every child up in the order it was added, before anything gets a turn, and which also runs `PostInit`, mind that `PostInit` is called from in there and so runs before anything just added has been set up.

## Switching off and destroying

`Enabled` stops the engine giving an entity its turns, not updated, not captured and so not drawn, and everything under it freezes with it, switched back on it carries on exactly where it left off. It only stops the engine calling it, not you, and it reads false until the entity has been set up, so a brand new entity sits out its first tick.

`Destroy` is fine from any thread, the entity is out of the tree and gets no more turns from that moment, and it is disposed of on the render thread at the start of the next frame, which is when whatever it had on the GPU goes, the example keeps a count of textures in its panel to show it happening.

## Timers

Timers are entities too, so they get their turns and stop when switched off like anything else.

```csharp
AddEntity(new IntervalRunnerSubStep(0.5f, () => Hatch(2)));
AddEntity(new IntervalRunnerFixedStep(0.25f, RefreshReadout));
```

`IntervalRunnerSubStep` keeps whatever time is left over and fires as many times as it is owed, so two waves a second stays two waves a second, `IntervalRunnerFixedStep` fires once an update at most and drops the leftover, which is all a readout needs.
