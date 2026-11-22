# Making a game

The second level of the examples, what a game is put together from, tweens, scenes and the transitions between them, tile maps, particles and physics. Each section names its example, and the snippets come from the "In your own game" block at the top of that example's file.

## Tweens

Every entity and every UI component has its own `Tweens`, made the first time it is asked for, which move along with its updates and stop when it is switched off, so a tween never outlives what it animates. A tween is told how to read a value, how to write it, where to take it and how long to take.

```csharp
marker.Tweens.Play(
    Tween.To(() => marker.Position.X, x => marker.Position = new Vector2(x, 0), 400, 1.5f)
        .SetEasing(Easing.OutQuad)
        .SetLoops(-1, LoopMode.PingPong));
```

`SetDelay` holds one back, `SetLoops` repeats it, a count of -1 for ever, `SetTimeScale` speeds it up or slows it down, `Pause` and `Play` stop and start it, and `OnComplete`, `OnLoop` and `OnKill` say when each happens. A sequence plays its steps one after the other, with `Join` running a step alongside the one before it.

```csharp
entity.Tweens.Play(Tween.Sequence()
    .Append(Tween.To(() => a, x => a = x, 1, 0.5f))
    .AppendCallback(() => Log("halfway"))
    .Append(Tween.To(() => b, x => b = x, 1, 0.5f))
    .Join(Tween.To(() => c, x => c = x, 1, 0.5f))
    .Build());
```

The `tweens` example puts a marker per easing on a track side by side, the quickest way to see what each easing does, and checks everything a tween promises as it starts, each on a `TweenContext` that is ticked by hand so nothing depends on how fast the machine happens to run.

## Scenes and transitions

A game is a run of scenes, and `transitions` tours three of them with a different way of getting to each. A transition is made once and kept, since it hangs on to its GPU resources between uses, and a scene is changed from the simulation thread.

```csharp
var fade = new FadeTransition { OutTime = 0.3f, InTime = 0.45f };

// UpdateState, IsLeaving stops a mashed button setting it twice
if (!IsLeaving && Engine.Input.Keyboard.WasPressed(Key.Enter))
    Engine.SetScene(new LevelScene(), fade);        // null for a hard cut

// Know what's coming? Set it up ahead, then setting it later swaps straight away
_next = new LevelScene();
Engine.SceneManager.Preload(_next);

// A scene kept between visits (a hub, a world map) rather than thrown away when it is left
public override bool Persistent => true;
```

`FadeTransition`, `BlurTransition` and `RotTransition` come with the engine, `ScreenTransition` is where one of your own starts, and `SceneManager.Transition` sets one for every change. Whatever a scene loads is tracked by its `Assets` and freed with it, which is why nothing in the example frees anything by hand, and a scene's `Initialize` runs on the render thread with the simulation standing still, the one place its GPU resources get made.

## Tile maps

Maps are drawn in [Tiled](https://www.mapeditor.org) and loaded off their files, the `tilemap` example's street in `Assets/examples/world/town.tmx` has one of everything a map can have, layers that scroll slower than the camera, a layer in front of everything, tiles turned every way and animated, and lamps and a spawn point put down as objects. Open it in Tiled next to the example and change something.

```csharp
// Constructor, a map is read off its file without touching the GPU
map = renderer.AddEntity(TileMap.Load("Assets/maps/town.tmx"));
renderer.AddEntity(players);                      // whatever stands between the map and its front layer
renderer.AddEntity(map.Foreground);

map.DispatchObjects(objects => objects
    .OfClass("spawn", spawn => player.Position = spawn.Position)
    .OfClass("light", lamp => lighting.AddLight(new Light2D { Position = lamp.Position })));

foreach (TileMapBox box in map.BuildColliders())
    body.CreateRectangularFixture(box.Min, box.Size);
```

The map is an entity like any other, its `Foreground` is added separately after whatever stands in front of the map and behind its front layer, and `DispatchObjects` hands the objects of a map to whoever knows what to do with their class. `FindLayer` with `Visible` and `SetTile` changes a map as it runs, `TilesAt` says what is under a spot of the world, and `ParallaxOrigin` is where the layers that scroll at their own speed line up. The village showcase takes maps further, every building's inside a map of its own laid over the street with `TileMapRooms` and `TileMapDoor`, and villagers that find their way with `TileMapPathfinder`.

## Particles

Two particle simulators share one renderer, `CpuParticleSimulator2D` and `ComputeParticleSimulator2D`, the second running in a compute shader, and the `particles` example runs them side by side, the CPU one on the left and the compute one on the right, so they can be compared by eye. A renderer compiles its shaders as it is made, so it is made in `Initialize`.

```csharp
public override void Initialize()
{
    sparks = AddEntity(new ParticleRenderer2D(65536, new ComputeParticleSimulator2D())
    {
        StartColor = new Vector3(1.0f, 0.8f, 0.3f),
        EndColor = new Vector3(0.5f, 0.1f, 0.0f),
        ParticleSize = 1.5f,
        Gravity = new Vector2(0, -600)
    });

    base.Initialize();
}

// UpdateState
if (mouse.WasPressed(MouseButton.Left))
    sparks.AddBurst(aim, 8000, 500);                                    // where, how many, how fast
if (mouse.IsDown(MouseButton.Right))
    sparks.AddCone(aim, Vector2.UnitY, MathF.PI / 6.0f, 64, 700);       // a fountain, upwards in a 30 degree cone
```

The first number is how many particles the renderer holds at most, 65,536 here on either simulator.

## Physics and fluid

`PhysicsWorld` is the engine's rigid body physics, stepped on the simulation thread in `UpdatePhysics` at its own fixed rate, every step told the same length of time whatever the machine is busy with. The `fluid` example pours the particles of a `PhysicsFluidParticleSimulator2D` into three basins, water that levels itself out and sand that heaps up where it lands, with a crate to drop in, while the rain of a `PhysicsParticleSimulator2D`, simulated on the GPU, lands on the map and bounces off the crate but passes through the water.

For a character, `CharacterController2D` walks, jumps and stands on what the map's colliders say is solid, the `town`, `dungeon` and `village` showcases all move their player with one, and `check-physics` checks that bodies fall, land, bounce and are thrown, that a character steps a slab and is stopped by a wall, and that the fluid finds its level.

## A bit of 3D

The renderers have depth too, `meshes` builds a floor, a crate, a ball, a can and hills out of vertices by hand with `Mesh3D` and `Vertex3D`, lights them with one sun and shows them through a `Camera3D` that goes round them, with `Renderer3D` drawing them on the same screen the 2D renderers draw on.
