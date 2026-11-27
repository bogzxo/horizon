# Engine core (horizon/Horizon: Core, Engine, Platform, Logging, Input, Content, Audio, Physics)

Paths below are relative to `horizon/Horizon/` (the engine project) unless prefixed. Rendering, Graphics, shaders and the
UI belong to other docs; only the hooks into them are described here.

## 1. Mental model

Horizon is a Vulkan-based .NET 11 game engine. One `GameEngine` (an `Entity`) owns a tree of **entities** (children) and
**components** (behaviours). There is no ECS and no DI container: the model is a classic Unity-ish scene graph with
components, driven by three cooperating threads and a snapshot hand-off between simulation and rendering.

- `Entity` (`Core/Entity.cs:27`): node in the tree. Has `Components` (`IGameComponent`) and `Children` (`Entity`).
- `GameComponent` (`Core/Components/GameComponent.cs:25`): base class for behaviours. `Parent`, `Enabled`, `Name`, and
  virtual turns `Initialize / UpdateState / UpdatePhysics / Render / Capture`.
- `GameObject : Entity` (`Engine/GameObject.cs`): adds the static `GameObject.Engine` (the singleton engine). Scenes,
  cameras, renderers derive from it. `GameEngine.Instance` is the same thing for non-entities.
- `GameEngine : Entity` (`Engine/GameEngine.cs:23`): root of the tree. Its components/children are the engine services.
- `Scene : GameObject` (`Engine/Scenes/Scene.cs:34`): one screen (menu, level). Owns an `AssetScope` so GPU stuff
  made while it is set up is freed when it is left.
- `SceneManager : Entity` (`Engine/Scenes/SceneManager.cs:43`; namespace is `Horizon.Engine.Components`, folder is
  `Engine/Scenes`): swaps/pushes/pops scenes, handles `Preload`, `Cover`, and `CollectAfterChange`.
- `WindowManager : GameComponent` (`Core/Windowing/WindowManager.cs:52`, partial across 9 files: `.cs`, `.Application.cs`,
  `.Device.cs`, `.FileDrop.cs`, `.Frame.cs`, `.Latency.cs`, `.Platform.cs`, `.Sleep.cs`, `.Surface.cs`): owns the render
  thread and simulation loop; the engine's real "main loop".
- `IPlatformHost` (`Platform/IPlatformHost.cs`): everything native (window, surface, input, audio, platform thread).
  Implemented in sibling projects (`Horizon.Platform.Desktop`, `.Android`, `.iOS`). Core has "no native types".

### Three threads (the central invariant)

1. **Platform thread** = the thread that called `Run()`/`Start()` (Main thread on desktop). Pumps the OS window/input
   events. Only thread allowed to change the window. `host.Post/Invoke` marshal work onto it.
2. **Render thread** ("Render", `WindowManager.DrawFrames`, `Core/Windowing/WindowManager.cs:720`). Only thread that
   may touch the GPU (`EntityLifecycle.ClaimRenderThread()`). Runs `Initialize()` of everything, flushes pending adds, swaps scenes, draws frames.
3. **Simulation thread** ("Simulation", `Core/Threading/SimulationLoop.cs`). Runs `SimulationContext.RunPending()` (async continuations), `UpdateState` (logic), and
   `UpdatePhysics` per tick at independent rates (default 120/120, `WindowManagerConfiguration.UpdatesPerSecond`
   / `PhysicsUpdatesPerSecond`, `Core/Windowing/WindowManagerConfiguration.cs:124-130`). Starts only after the first
   frame has been drawn (`WindowManager.cs:773`: `if (simulation is null) StartSimulation();`).

Rendering is decoupled from simulation: at the end of each tick the sim thread calls `Entity.Capture()` over the whole
tree, publishing structs into `Snapshot<T>` slots (`Core/Threading/Snapshot.cs`) of a 4-slot `SnapshotClock`
(`Core/Threading/SnapshotClock.cs`). The render thread draws interpolated between the last two ticks
(`PresentationMode.Interpolated`, default) or from the newest (`Latest`) (`Core/Threading/RenderFrame.cs:6`).
`RenderFrame.Active` is a thread-static the draw code reads (`Entity.Render`, line ~95, branches on
`frame.IsDecoupled` to `RenderCaptured`).

## 2. Startup path: Main to first frame

Real example: `Fighter2D/App/Program.cs:30-55` (`PackedFiles.Unpack(); DesktopPlatform.Install(); ... new GameEngine(cfg); Startup.Attach(engine); engine.Run(scene, transition)`). Hex does the same (`hex/Hex/Program.cs:30`).

```csharp
Horizon.Platform.Desktop.DesktopPlatform.Install();        // Platform/PlatformInstallation.cs: static IPlatform factory
using var engine = new GameEngine(WindowManagerConfiguration.Default1600x900 with { WindowTitle = "My game" });
engine.Run<MainMenuScene>();                                // blocks until the window closes
```

Sequence (all verified in code):

1. `DesktopPlatform.Install()` -> `PlatformInstallation.Install(...)` (`Platform/PlatformInstallation.cs`). Must precede the
   engine; otherwise `Resolve` throws `InvalidOperationException` with that exact advice. Alternatively hand a platform in
   via `GameEngineConfiguration.Platform` (Android activity, tests).
2. `new GameEngine(...)` (`Engine/GameEngine.cs:~147`):
   - sets `GameEngine.Instance` and `GameObject.Engine` (statics; one engine per process in practice, though
     hosts are per-engine);
   - optional `PlatformPaths.BesideProgram = true` (config `FilesBesideProgram`);
   - sets `EntityLifecycle.Scope` so entity setup is wrapped in the owning `Scene.Assets.Enter()` or global scope;
   - adds components in a deliberate order (comment: "in the order they get their turns, input before anything that
     reads it"): `EngineEventHandler`, `ObjectManager` (Graphics folder, GPU asset managers), then creates the platform
     host, `InputManager`, `AudioDevice.For(...)`, (DEBUG only: Skyline debugger via reflection-loaded Horizon.UI),
     `SceneManager` (entity), and finally `WindowManager`.
   - reads env `HORIZON_SCREENSHOT` ("file.png@3" etc.).
3. `engine.Run(scene)` -> `SceneManager.SetScene(scene, transition)` (queued) -> `WindowManager.Run()`
   (`Core/Windowing/WindowManager.cs:593`) -> `Start()` (:630): `host.Initialize()` (native window on platform thread),
   spawn the "Render" thread (`DrawFrames`), then the platform thread enters `IPlatformLoop.Run()` until close.
4. Render thread: `SetUpDrawing()` (in `WindowManager.Device.cs`; makes the GPU device, calls `DeviceReady` =
   `GameEngine.ShowLoadingScreen`), then loop: wait for demand -> `Graphics.BeginFrame` -> `DrawFrame` -> `EndFrame`.
   First frames run `DoExclusiveWork` -> `EntityLifecycle.Flush()` (initialises the engine tree) -> `Exclusive` event ->
   `SceneManager` sets up the first scene entirely (`Change`), then draws. After the first frame, `StartSimulation()`.
5. Simulation thread ticks: `BeginTick` -> `Parent.UpdateState(dt)` / `Parent.UpdatePhysics(dt)` (Parent = the engine) ->
   `EndTick`: `Capture()` over the tree into a snapshot slot; if `exclusiveRequested` or `EntityLifecycle.HasWork`, parks
   on `Rendezvous()` while the render thread does exclusive work, then resumes.
6. Close: `host.RequestClose()` -> `loop.Run` returns -> `StopAsync()` stops frames then simulation -> engine disposed
   (`GameEngine.DisposeOther`: `Audio.Dispose(); Logger.Dispose()`).

Android/iOS use `engine.Start(scene)` (returns immediately; platform calls back) and `StopAsync()` instead of `Run`
(`Engine/GameEngine.cs` ~L420-445). `Run()` throws if the host has no `IPlatformLoop`.

## 3. Directory map

| Folder | Contents / key entrypoints |
| --- | --- |
| `Core/Entity.cs` (611 l) | `Entity`: tree, `AddEntity/AddComponent`, `InitializeAll` (:~245), `UpdateState` (:~341), `UpdatePhysics`, `Render`, `Capture`, `Destroy`, `Dispose`, `FindEntity/FindParent/GetComponent`. |
| `Core/EntityLifecycle.cs` | Static queue of "dirty" entities needing `Initialize` + "retired" entities needing `Dispose`; `Flush()` is called on the render thread with the sim parked. `Scope` func for asset ownership. |
| `Core/Components/` | `IGameComponent`, `IInstantiable`, `ISnapshotSource`, `GameComponent`, `EngineEventHandler` (Pre/PostState, Pre/PostPhysics, Pre/PostRender `Action<float>` hooks), `TransformComponent2D/3D` (lazy dirty-flag model matrix; `Epoch`/`Snap` for non-interpolated teleports). |
| `Core/Windowing/` | `WindowManager*.cs` (loops, frame, display, surface, platform events), `WindowManagerConfiguration` (struct with `Default1600x900`, `Mode`, `Behaviour`, `Presentation`, rates, `RenderScale`, `CustomFrame`), `DisplaySettings`. |
| `Core/Threading/` | `SimulationLoop` (+`ISimulationHost`), `SnapshotClock`/`Snapshot<T>`/`SnapshotBuffer`/`RenderFrame`/`Interpolate`, `LoopTiming` (sleep+spin), `LoopStatistics`, `WorkScheduler`/`WorkItem` (keyed thread-pool jobs with UI-thread `Pump`), `IntervalRunner*`. |
| `Core/Tweening/` | `Tween`, `TweenContext` (lazy `Entity.Tweens`, ticked in `UpdateState`), `TweenSequence`, `Easing`. |
| `Core/Processes/` | `PseudoTerminal` (Windows ConPTY / Unix pty / handed-over), `ProcessEnvironment`. Used by terminal/Hex, not by the game loop. |
| `Core/Platform/` | `PlatformPaths` (config/data/state/cache folders per OS, `BesideProgram` mode, path comparison), `ChildProcesses`. Note: this is `Horizon.Core.Platform`, distinct from top-level `Platform/` (`Horizon.Platform`). |
| `Core/Diagnostics/` | `StartupTrace` (`HORIZON_LOG_STARTUP`), `SlowRender` (`HORIZON_LOG_SLOW=<ms>`), `AllocationLog` (`HORIZON_LOG_ALLOCATIONS=<sec>`). |
| `Core/InspectAttribute.cs`, `MarchingSquares.cs`, `Util.cs`, `Origin.cs` | `[Inspect]` exposes fields to the Skyline inspector; `Origin` enum lives in `Horizon.Rendering` namespace though in Core folder. |
| `Engine/` | `GameEngine`, `GameEngineConfiguration`, `GameObject`, `LoadingScreen`, `IGameDebugger`, `Cameras/` (`Camera`, `Camera2D`, `Camera3D`), `Scenes/` (`Scene`, `SceneManager`, `SceneTransition`, `SceneBelow`). |
| `Platform/` | Abstractions only: `IPlatform`/`PlatformInstallation`, `IPlatformHost` (+`IPlatformLoop`, `IPlatformEvents`, `PlatformDisplay`), `IPlatformWindow`, `IPlatformSurface(+Ahead)`, `IPlatformInput`/`IPlatformGamepads`, `IPlatformDialogs`, `PlatformPower`, `IAudioOutput`, `PlatformFeatures` flags (`NativeDialogs`, `NativeMenuBar`, etc.), `IPlatformStorage`/`PlatformStorage`/`DiskStorage`, `PlatformDispatcher` (helper queue for hosts). |
| `Logging/` | `Log` (static facade), `Logger` (holder), `ILogger`, `Loggers/ConcurrentLogger`. |
| `Input/` | `InputManager(.Events)`, `Keyboard`, `Mouse`, `Touch`, `Key`, `InputEvent`, `InputScript`, `Gamepads/` (`GamepadInputManager`, `Gamepad`, `GamepadBindings`, `KeyboardGamepad`, virtual pads). |
| `Content/` | `ContentFiles` (static read-only content VFS), `IContentFiles` + `DiskContent`/`AssemblyContent`/`IndexedContent`/`OverlayContent`, `Localisation`, `ContentScripts` (HIDL file reader), and the GPU `AssetManager<...>` + `AssetScope` + factory/finalizer interfaces. |
| `Audio/` | `AudioDevice`, `AudioMixer`, `Sound`, `WaveFile`, `OggStream` (NVorbis). |
| `Physics/` | `2D/` (`PhysicsWorld`, `PhysicsBodyComponent2D`, `CharacterController2D`, fixtures, particle/fluid sims in `Simulation/`), `3D/` (`CollisionWorld3D`, `CharacterController3D`, `Box3`, `BoxCollider3D`). Home-grown, no Box2D/Bepu. |
| `Graphics/`, `Rendering/`, `shaders/`, `Assets/`, `Art/` | Out of scope. Core hooks: `Graphics.GraphicsDevice.Current`, `ObjectManager` (Graphics/Device/ObjectManager.cs:64), `CameraBlock.BeginFrame/EndFrame`, `WindowManager.Scaling`. |

Sibling projects in `horizon/` (not core but in the dependency graph): `Horizon.Platform.Desktop` (Silk.NET/SDL/GLFW host,
`DesktopHost.cs`, `DesktopWindow.*`), `Horizon.Platform.Android`, `Horizon.Platform.iOS`, `Horizon.UI` (UI + Skyline
debugger, has `InternalsVisibleTo` access), `Horizon.HIDL` (scripting language submodule), `Horizon.Terminal`,
`Horizon.Tooling`, `Horizon.Testing(.Android/.iOS)`, `Horizon.Tests`, `Horizon.Shaders.Build`, `AutoVoxel`.

## 4. Entity lifecycle in detail

States: constructed (any thread, no GPU) -> **queued** -> `Initialize()` on render thread -> `PostInit()` -> live ->
`Destroy()` -> retired -> `Dispose()` on render thread.

- `AddEntity/AddComponent` (`Entity.cs:~302-363`): the new thing is `Enabled = false` until set up, pushed on the parent's
  `_uninitializedQueue`. If the parent is already live, parent is added to `EntityLifecycle.dirty` so the next
  frame's `Flush()` initialises it. If not live, it is initialised along with the parent by `InitializeAll()`.
- `InitializeAll()` drains queue in insertion order, recursing, and then enables items. While `_waiting > 0`, update/render skip
  not-yet-initialised children/components (checked via `_uninitializedSet`).
- **Guarantee** (`EntityLifecycle` doc): everything added during a tick is set up in one go at the start of the next frame,
  and neither update nor physics runs again until that has happened (sim parks in `EndTick` when `EntityLifecycle.HasWork`).
- `Initialize()` must call `base.Initialize()` (it triggers `PostInit` once, guarded by `Interlocked.Exchange`).
  Source comment shouts about it. Components do not need to (`GameComponent.Initialize` is empty).
- `Destroy()` is callable from any thread: disables, removes from parent, enqueues to `retired`; GPU freeing happens at the
  next frame start. `RemoveEntity/RemoveComponent` detach without disposing.
- Structure changes take `_structuralLock` and swap whole arrays (`_childrenCache`, `_componentsCache`), so update/render
  loops iterate lock-free over immutable arrays. Safe to add/remove during iteration.
- Update order per entity: **components first, then children** (`Entity.UpdateState`), but **Render: children first,
  then components** (`Entity.Render`). Easy to trip on.
- `Enabled` false on an entity skips the whole subtree (update, physics, render, capture).

### Update/Capture/Render per frame

| Call | Thread | Notes |
| --- | --- | --- |
| `UpdateState(dt)` | sim | `dt` per logic tick. `GameEngine.UpdateState` fires `EventManager.PreState/PostState` around `base`. `Scene.UpdateState` accumulates `Time`. |
| `UpdatePhysics(dt)` | sim | Fixed `1/PhysicsUpdatesPerSecond`. Order: logic then physics within one tick (`SimulationLoop` doc). |
| `Capture()` | sim, end of tick | Publish render state to `Snapshot<T>`. If you override, call `base.Capture()` or children are not drawn. |
| `Render(dt)` | render | Draw from snapshots when `RenderFrame.Active.IsDecoupled`; do NOT read live simulation state. |
| `Initialize()/Dispose()/Flush` | render | Only place GPU objects may be created/freed. |

## 5. Key abstractions and extension points

### Add a component

```csharp
public sealed class Spinner : GameComponent            // Core/Components/GameComponent.cs doc sample
{
    public override void UpdateState(float dt) => Parent.GetComponent<TransformComponent2D>()!.Rotation += 90.0f * dt;
}
// elsewhere, e.g. in a Scene.Initialize(): entity.AddComponent<Spinner>();   // or AddComponent(new Spinner())
```

Implement `IGameComponent` directly only when you must inherit something else (e.g. `InputManager`, `WindowManager`
extend `GameComponent`; `ObjectManager` too). Implement `IDisposable` and the entity disposes it. Implement
`ISnapshotSource` (or override `GameComponent.Capture`) if it draws.

### Add an entity / scene

```csharp
internal sealed class TitleScene : Scene                // Engine/Scenes/Scene.cs doc sample
{
    public override void Initialize()
    {
        ActiveCamera = AddEntity(new Camera2D(Engine.ViewportSize));
        base.Initialize();
    }
    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        if (!IsLeaving && Engine.Input.Keyboard.AnyPressed) GoTo(new GameScene());
    }
}
```

`Scene` has `[DynamicallyAccessedMembers(PublicParameterlessConstructor)]` (AOT/trim friendliness): scenes used
generically via `Run<T>()`/`SetScene<T>()` need a public parameterless ctor. `Persistent => true` keeps a scene alive when left.
Stack scenes with `Open(scene, transition, SceneBelow)` / `Return()`; `GoTo` swaps; `SceneManager.ResetTo` clears the stack.
`SceneManager.Paused`, `TimeScale`, `Step()` exist (:109-132) for debugger pause/step.

### Custom scene transition
Derive `SceneTransition` (`Engine/Scenes/SceneTransition.cs`): set `OutTime/InTime/Easing`, implement
`Render(cover, arriving, dt)`; Rendering ships fade/blur/rot (`ScreenTransition` base lives in Rendering).

### Add a platform
Implement `IPlatform` (+ `IPlatformHost`, optionally `IPlatformLoop`) - see `Horizon.Platform.Desktop/DesktopPlatform.cs`,
`DesktopHost.cs`. Host contract (`Platform/IPlatformHost.cs`): `Attach(events)` as the WindowManager is made, `Initialize()` on
the platform thread, `Surface`/`Input`/`State` valid after that, `Post/Invoke/Wake/RequestClose`, `CreateAudioOutput()`.
Advertise capabilities via `PlatformFeatures` flags; UI asks before drawing window chrome. Use `PlatformDispatcher`
(`Platform/PlatformDispatcher.cs`) for the post/invoke queue. Hosts call back through `IPlatformEvents` (implemented
privately by `WindowManager.HostEvents`).

### Replace content source / storage
`ContentFiles.Source = new OverlayContent(new DiskContent(root), new AssemblyContent(asm, prefix))`
(`Content/ContentFiles.cs:12`; install before engine threads start; swapping bumps a version so cache keys change, use
`ContentFiles.Find(path)` to keep a source+key consistent). `PlatformStorage.Current = ...` for writable data
(`Platform/PlatformStorage.cs`; default `DiskStorage`; `WriteAllText` goes through `BeginWrite(...).Commit()` so failed
saves keep the old file).

### Add a GPU asset kind (Content managers)
`AssetManager<TAsset, TFactory, TDescription, TDisposer>` (`Content/AssetManager.cs:16`) with
`IAssetFactory<,>.TryCreate` (static abstract), `IAssetDescription` marker, and an `IGameAssetFinalizer<T>`.
`ObjectManager` (Graphics/Device/ObjectManager.cs) holds one per kind (Textures, Shaders, Buffers, RenderTargets,
VertexArrays). `CreateOrGet(name, desc)` = shared named asset (ref-counted by `AssetScope` users);
`Create(desc)` = unnamed, owned by the current scope. Shaders have `Scoped = false` (kept for good).

### Subscribe to engine events
`engine.EventManager.PreState/PostState/PrePhysics/PostPhysics/PreRender/PostRender` (plain `Action<float>` fields, not
events: assign with `+=`). `WindowManager.Exclusive` event = hook to run code on the render thread with the sim
parked (`RequestExclusive()` to trigger), used by `SceneManager`. `WindowManager.Resized`. `Log.Written` for log taps.

### Debugger/UI hook (DEBUG only)
`GameEngine.DebuggerMaker` is a static `Func<GameObject>` set by Horizon.UI's module initializer; the engine loads the
assembly by name with `Assembly.Load("Horizon.UI")` + `RunModuleConstructor` (Engine/GameEngine.cs, `MakeDebugger`). The core
never references UI. `#if DEBUG` everywhere `Debugger` appears: Release builds have no debugger member at all.

## 6. Core scenarios

### 6a. One frame (game mode)

1. Render thread top of loop (`WindowManager.DrawFrames`): wait for demand (only matters for `DrawOnChange`) and frame period
   (`FramesPerSecond`, 0 = uncapped; VSync default true), do surface requests.
2. `Graphics.BeginFrame`, `Scaling.BeginFrame`, then `DrawFrame(dt)`:
   - `dt -= stalled` (time spent in exclusive work does not count);
   - fires `Resized` if flagged; if sim is parked (`simulationParked.Wait(0)`), do exclusive work then `simulationResumed.Release()`;
   - exclusive work: `ApplyPendingDisplay()`; if `always`: `EntityLifecycle.Flush()` (init new, dispose retired) then `Exclusive?.Invoke(dt)` (SceneManager swaps scenes here);
   - `snapshots.Acquire(Presentation)` returns a `RenderFrame` (two slots + alpha); `RenderFrame.Begin(frame)`; `Parent.Render(dt)` i.e. `GameEngine.Render` (:`TotalTime += dt`, PreRender, clear, `CameraBlock.BeginFrame`, loading screen, `base.Render` through all children/components in `AssetScope.EnterGlobal()`, debugger draw, screenshot handling, PostRender); `RenderFrame.End(); snapshots.Release()`.
3. `Scaling.EndFrame(); Graphics.EndFrame()`.
4. Meanwhile on sim thread at its own rate: `SimulationLoop` computes due logic and physics turns (MAX_CATCH_UP = 4
   ticks, MAX_STRETCH 0.25 s), `UpdateState` then `UpdatePhysics`, `EndTick` -> `snapshots.BeginCapture(); engine.Capture(); EndCapture(stamp, time)`.

Time semantics: `GameEngine.TotalTime` = render clock (double, advances per frame), `GameEngine.Runtime` = sim clock (advances per
logic tick), `WindowManager.SimulatedTime`, `Scene.Time`.

### 6b. Input reaching game code

1. Platform thread: desktop host (`Horizon.Platform.Desktop/Input/*`) turns SDL/GLFW/Silk events into `InputEvent`s via
   `IInputSource.EventReceived`. `InputManager.Initialize()` (`Input/InputManager.cs:~110`) gets `window.Host.Input` and subscribes
   (`AttachSource` / `Post`). Mouse position and live button state update immediately in `Post` (any thread); everything is also
   enqueued to `ConcurrentQueue<InputEvent>`.
2. Sim thread: `InputManager.UpdateState` (first in engine's component order) -> `UpdateScript` (HORIZON_INPUT script playback) ->
   `DrainEvents()` -> `Keyboard.Accept`, `Mouse.Hear*`, `Touch.Apply`, `Gamepads.Hear` -> then `Keyboard.Update/Mouse.Update` roll
   pressed/released edges -> `Gamepads.UpdateState`. `EventReceived` is raised per event for UI.
3. Game code reads `Engine.Input.Keyboard.IsDown/WasPressed/Axis(...)`, `Mouse.Position`, `Gamepads.WasPressed("jump")` (bindings
   by action name, `Input/Gamepads/GamepadBindings.cs`). `WasPressed` is true for exactly one update.
4. `Keyboard.Withheld`/`Mouse.Withheld` (internal): Skyline debugger (`Horizon.UI/Engine/Debugging/SkylineDebugger.cs:328`) sets them
   so a game sees nothing while the UI has focus. This is why `Horizon.Input` internals are visible to Horizon.UI.
5. `InputManager.Enabled = false` freezes input state in place (explicit comment).
6. `Configuration.Gamepads = false` avoids even asking the OS about controllers (a broken driver can stall startup for a minute).
7. Env: `HORIZON_INPUT_LOG=keys` logs keys; `InputScript` (see `Input/InputScript.cs`, env var `InputScript.ENVIRONMENT_VARIABLE`) plays
   scripted keys/pad for unattended runs.

### 6c. Loading content

- Files: all engine file reads go through `ContentFiles` (static) -> `IContentFiles`. Default `new DiskContent()` (cwd-relative).
  Fighter2D `PackedFiles.Unpack()` installs overlay of loose + packed sources. `IndexedContent` normalises to `/`, case-sensitive.
- Sound: `Sound.Load(path)` (`Audio/Sound.cs:75`) -> `ContentFiles.ResolvePath/CacheKey` -> `.ogg` via `OggStream`/NVorbis else `WaveFile.Read`;
  cached by `ContentFiles` cache key; failures log a Warning and cache null. Any thread.
- Text: `Localisation.Load(folder, "en-gb")` reads `.hor` (HIDL) language files declaring `strings`; swapped wholesale; missing key returns the key.
  Layouts use `@key`.
- HIDL scripts: `runtime.ReadingContent()` (`Content/ContentScripts.cs`) points a `HIDLRuntime` at `ContentFiles`.
- GPU assets (textures, shaders, buffers): through `ObjectManager.X.Create/CreateOrGet` on render thread while an `AssetScope` is entered;
  anything made during a scene's setup is the scene's and freed when the scene is left (`Scene.Assets`, `AssetScope.cs`). Outside any
  scope = belongs to nobody (lives until shutdown). `AssetScope.EnterGlobal()` = shared on purpose (never freed with a scene).
  Off-render-thread creation logs a warning once (`warnedThread`).

### 6d. Scene change

`SceneManager.SetScene(scene, transition)` (any thread) marks `_halt` and calls `WindowManager.RequestExclusive()`. Next frame start
(sim parked), `Exclusive` handler runs `Change(dt)`: sets up the incoming scene fully (`InitializeAll`, warm-up turns until
`ReportUnfinished` is clean; logs "Set up 'X' in N ms"), frees the old scene's `Assets` (unless `Persistent`/pushed), then draws. With a
`Transition`, cover-out (tween) -> swap while hidden -> uncover. `Scene.IsLeaving` should gate input in leaving scenes.

## 7. Platform, storage, logging

- `PlatformPaths` (`Core/Platform/PlatformPaths.cs`): `Config/Data/State/Cache(name)` pick Windows `%AppData%`/`%LocalAppData%`, macOS `~/Library/...`,
  Linux XDG. `BesideProgram` (Hex uses it) redirects to `settings/`, `data/`, `cache/` next to the exe. Set it before any folder lookup
  (shader cache asks first). `PlatformPaths.Comparer/Same/IsUnder` for path comparison (case-insensitive except Linux). Use these rather than `Path` equality.
- `Log` (`Logging/Log.cs`): static `Info/Success/Warning/Error/Fatal/Write`, forwards to `Logger.Instance` (default `ConcurrentLogger`: console + `debug.log`,
  background writer thread) and raises `Log.Written` (UI log drawer). Swap logger via `Logger.InitializeLogger(ILogger)`. Convention: prefix messages with
  `[Subsystem]`, e.g. `Log.Info("[SceneManager] ...")`. Non-blocking.
- `PlatformFeatures`: ask before drawing platform-specific chrome.

## 8. Audio

`AudioDevice` (`Audio/AudioDevice.cs`) = 48 kHz stereo float; wraps `IAudioOutput` from the platform host (desktop: `SdlAudioOutput`). No device is
**not** an error: the `Mixer` exists but outputs nothing (`HORIZON_AUDIO=off` forces this; `RunMode.Application` passes `open: false`).
Output thread calls the render delegate -> `AudioMixer.Mix` (`Audio/AudioMixer.cs:195`). Mixer: 48 voices (`MAX_VOICES`), `AudioBus` volumes, `Play(sound, bus, volume,
pitch, pan, loop)` returns `SoundHandle(slot,id)`, `PlayMusic(IAudioStream, fade...)`, `Stop/StopAll/SetVolume(handle)`. Access: `Engine.Audio.Mixer`.
`Sound` is decoded 16-bit stereo PCM `short[]`.

## 9. Physics

Self-contained, deterministic-step, runs in `UpdatePhysics` (sim thread). Not a general engine; built for the 2D fighter plus voxel 3D.
- 2D: `PhysicsWorld : GameComponent` (`Physics/2D/PhysicsWorld.cs:24`), `Gravity`, static vs dynamic body lists (`PhysicsBodyComponent2D`,
  `Static/Dynamic/Kinematic` via `PhysicsBodySimulationType`), fixtures (`RectanglePhysicsFixture`, `CirclePhysicsFixture`, `OutlinePhysicsFixture`),
  `PhysicsStaticGrid` spatial grid (`AllRectangles` + stamp-deduped queries) for static tiles, particles/fluids (CPU `PhysicsParticleGroup` / `PhysicsFluidParticleGroup` occupied-cell spatial table, 4-neighbor `FindPairsDense`, close/spread partitioning, `_nearMask`, pre-sized `_denseCellStart`; GPU feed `PhysicsParticleFeed`), radial forces,
  `RenderDebug` overlay drawn via `PrimitiveRenderer` through `Capture/Render`. `CharacterController2D` (coyote time, jump buffer, short hop) pushes with forces.
- 3D: `CollisionWorld3D` (boxes + `ICollisionField` e.g. voxel tree from Rendering, raycast), `CharacterController3D(world, feet)`; `Box3`.
- Rough edge (explicit TODO, `PhysicsBodyComponent2D.cs:22`): `Position` is publicly settable; teleporting through it is everywhere.
  Many options are off by default (`BodiesPush`, `StopsFlush`); `CharacterController2D` turns `StopsFlush` on.
- Add physics to a scene: `var world = AddComponent<PhysicsWorld>()` (or add to scene entity), `world.CreateBody(type, pos)`, add fixtures, controllers added on the same scene.

## 10. Conventions

- Namespaces mirror folders except oddities: `SceneManager`/`ObjectManager` live in `Horizon.Engine.Components` / `Horizon.Graphics` but
  in `Engine/Scenes` / `Graphics/Device`; `Origin` (Core folder) is `Horizon.Rendering`; `AssetManager` (Content folder) is `Horizon.Content.Managers`,
  descriptions in `Horizon.Content.Descriptions`, disposers in `Horizon.Content.Disposers`.
- csproj (`Horizon.csproj`): `net11.0`, `LangVersion preview`, nullable + implicit usings, `AllowUnsafeBlocks`, `IsAotCompatible`, `IsTrimmable`,
  `InvariantGlobalization`. Code avoids reflection (a comment in `SceneManager` says an `InstanceManager` was removed for AOT).
  Culture: use `CultureInfo.InvariantCulture` when parsing numbers (done throughout).
- Style: doc comments are long, conversational prose with `<see cref>`; XML docs state **which thread** a method runs on ("Render thread", "From any thread").
  Read them; thread contracts are not enforced by types.
  Constants SCREAMING_CASE (`MAX_VOICES`, `ENVIRONMENT_VARIABLE`); private fields camelCase or `_camel` (mixed; no strict rule).
  Types often `sealed`; `partial` classes split by concern (`WindowManager.*.cs`, `InputManager.Events.cs`).
  Helper methods documented "Helper method to ..." (house style for private methods).
- Statics are normal here: `GameEngine.Instance`, `GameObject.Engine`, `ContentFiles.Source`, `PlatformStorage.Current`, `PlatformInstallation`,
  `Logger.Instance`, `EntityLifecycle` (static queues; assumes one engine), `Localisation`, `Sound` cache, `AssetScope.Current` (`[ThreadStatic]`).
  `PlatformInstallation` is a factory so two engines (tests) do not share hosts. Dependencies are reached via `Engine.*` or `Parent.GetComponent<T>()`;
  no container, no source generators in core.
- Components needing a sibling find it in `Initialize()` (`InputManager` throws if its parent has no `WindowManager`).
- Error handling: asset/file failures log a Warning/Error and return null (`Sound.Load`, `AssetManager.Create`), never crash; state-misuse throws
  `InvalidOperationException` with prose explanations. Render-thread exceptions are captured (`ExceptionDispatchInfo`) and rethrown on the platform thread (`WindowManager.renderFailure`).
- Threading: shared mutable state uses `lock`/`Lock`, `Volatile`, `Interlocked`, `ConcurrentQueue`; immutable-array swap for tree; records replaced whole (`DisplayState`, `PlatformDisplay`).
- Environment variables (diagnostics/overrides): `HORIZON_LOG_LOOPS=<sec>`, `HORIZON_LOG_ALLOCATIONS`, `HORIZON_LOG_STARTUP`, `HORIZON_LOG_SLOW=<ms>`,
  `HORIZON_LOG_TRANSITIONS`, `HORIZON_SCREENSHOT=file.png@3[+8x0.5]`, `HORIZON_AUDIO=off`, `HORIZON_INPUT_LOG=keys`, `HORIZON_INPUT_SCRIPT`,
  `HORIZON_WINDOWING` (GLFW vs SDL), `HORIZON_VSYNC=off|on`, `HORIZON_SCALE_TO_DISPLAY`, `HORIZON_SHELL_ENVIRONMENT`, plus rendering ones (`HORIZON_RENDER_SCALE`, `HORIZON_SHADER_CACHE`, `HORIZON_VULKAN_*`, `HORIZON_UPSCALER`).
- `F12` saves a screenshot to `screenshots/` (`GameEngine.ScreenshotKey`, null disables). `GameEngine.CapturePixels()` returns the frame in memory (Hex's MCP uses it).

## 11. Dependencies and build

- `horizon/global.json`: SDK `11.0.100-rc.2.26504.105`, `rollForward: latestMajor`, prerelease allowed.
- `horizon/Directory.Build.props`: imports the nearest parent `Directory.Build.props` (workspace root) and sets `Version 1.5.0` for projects named `Horizon*`.
- Horizon.csproj NuGet: `DotTiled 1.0.0` (Tiled maps), `NVorbis 0.10.5` (ogg), `Slangc.NET 2026.19.0` (Slang shader compiler), `Silk.NET.Vulkan(+EXT,+KHR) 2.23.0`,
  `StbImageSharp`, `StbImageWriteSharp`, `StbTrueTypeSharp`. ProjectReference: `Horizon.HIDL`.
  Windowing/input/SDL (`Silk.NET.Windowing/Input/SDL`, `MoltenVK.Native`) are in `Horizon.Platform.Desktop`, not core.
- Copies to output: `shaders/**`, `Assets/global/**`, `Assets/fonts/**`, `Assets/uix/*.aseprite|.hor|README.txt`.
- `InternalsVisibleTo`: `Horizon.UI`, `Horizon.Tests`, `Horizon.Testing`. Internals like `Keyboard.Withheld`, `GameObject.Engine` setter, `InputManager.Platform` are UI/test-facing.
- Tests: `horizon/Horizon.Tests` (xUnit-style, folders Core/Audio/Physics/Platform/...). Core tests worth reading as examples: `Core/SimulationLoopTests.cs`,
  `SnapshotClockTests.cs`, `InputManagerGamepadsTests.cs`.
- Release config: no debug symbols; Debugger/Skyline compiled out.

## 12. Gotchas and non-obvious things

1. **Thread rules are by convention.** GPU creation off the render thread logs a warning (`AssetManager.warnedThread`), `EntityLifecycle.IsOffRenderThread` exists to check. Constructors run on arbitrary threads with no GPU; put GPU work in `Initialize()`.
2. **`AddEntity` does not initialise immediately.** The child is disabled and not usable until the next frame flush (or parent's InitializeAll). Don't read GPU-backed members of something you just added in an update.
3. **`base.Initialize()`**: the `Entity` base only runs `PostInit()` (once). Scene doc sample calls it last, `GameEngine.Initialize` calls it first; either works, but forgetting it means `PostInit` never fires. The input script env var is `HORIZON_INPUT_SCRIPT`.
4. **Overriding `Capture()` without `base.Capture()`** silently stops the subtree from being drawn in decoupled mode.
5. **Render reads snapshots**: in `Render`, use `Snapshot<T>.TryBlend(RenderFrame.Active, out v)`; reading live fields races with the sim. Teleports: call `Snapshot.Break()` / `TransformComponent2D.Snap` to avoid lerping across the jump.
6. **Update vs render traversal order differ** (components-then-children vs children-then-components).
7. `Entity.Enabled` is volatile, off until set up; setting `Enabled = true` manually on something pre-init does not bypass the queue.
8. **Simulation starts after first frame.** Nothing updates while loading; the loading screen draws. Scene setup also runs "warm-up turns" so first visible frame is complete.
9. `SceneManager` namespace is `Horizon.Engine.Components` despite sitting in `Engine/Scenes`; `Scene.cs` references it fully qualified for that reason.
10. `GameEngine` statics (`Instance`, `GameObject.Engine`) are overwritten by each new engine; multi-engine (tests) shares `EntityLifecycle` statics. Treat as single-engine.
11. `RunMode.Application` (Hex): `DrawOnChange` + `SlowWhenIdle` + `WaitForEvents` flags make frames/ticks sleep when idle (`WindowManager.Application.cs`). Use `WindowManager.NoteInput()` when user input arrives to exit idle/calm throttling immediately, `WindowManager.RequestTickIn(seconds)` for timed simulation wakeups without GPU frames, `NoteChange()` for simulation-thread UI changes (uses `CALM_FRAMES` when calm), `RequestSmooth()` for user-followed animations (glides, springs, easing) that must bypass calm pacing (`CALM_PERIOD` = 24 Hz focused / 10 Hz unfocused; `PaceCalmFrames()` paces render frames to half a calm tick), and `Wake()` when background work finishes. `PlatformPower` / `DesktopPower` observes OS Low Power Mode (e.g. macOS `NSProcessInfoPowerStateDidChangeNotification`) to further lower calm cadence on battery saver. On Windows 10 1803+, `LoopTiming` (`Core/Threading/LoopTiming.cs`) uses per-thread `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` waitable timers and skips global `timeBeginPeriod(1)` for desktop apps (`NeedsSharpTimer`), preserving laptop battery. On macOS, `MacWindowChrome.Maintain` (`Horizon.Platform.Desktop/Mac/MacWindowChrome.cs`) is zero-traffic when window state is unchanged, display pacing queries `NSScreen.maximumFramesPerSecond` for ProMotion 120 Hz, `SdlTextEditing` (`Horizon.Platform.Desktop/Input/SdlTextEditing.cs`) bridges SDL IME composition and full UTF-8 commits (`SDL_TEXTEDITING`/`SDL_TEXTINPUT`/`SDL_SetTextInputRect`), `MacDialogs` implements `IPlatformDialogs` (`NSOpenPanel`/`NSSavePanel`), and `GlyphOutlines` (`Rendering/2D/Text/GlyphOutlines.cs`) supports `.ttc` TrueType collections via `stbtt_GetFontOffsetForIndex`. Games use `RunMode.Game` (default, behaviour None).
12. `EngineEventHandler` members are public delegate fields (not events): `engine.EventManager.PostState += ...`; assigning with `=` clobbers others.
13. Debug builds try to load `Horizon.UI` by assembly name; if present, a Skyline debugger overlay is added to the engine. Release does not. Don't depend on `Debugger` outside `#if DEBUG`.
14. `ContentFiles.Source` should be installed before engine threads start; replaced sources invalidate cache keys (`Version|path`).
15. Audio mixes on the device's own thread; `AudioMixer` calls must be thread-safe against that (it is designed for it). Missing audio device is silent success.
16. `FilesBesideProgram`/`PlatformPaths.BesideProgram` must be set before anything asks for a folder, or caches land in the user dir.
17. Stray TODO count is very low in this scope (one TODO found: `PhysicsBodyComponent2D.Position` mutability). Comments contain some profanity and casual tone; keep to house voice.
18. `Horizon.Tests` may contain a `~syncthing~*.tmp` leftover file (sync artifact, ignore).
19. Pass 7–12 startup and runtime paths: `PreparedDevice` (`Horizon/Graphics/Vulkan`) prepares Vulkan/instance initialization ahead of device creation (`WindowManager.Device.cs`); `AudioDevice` opens asynchronously (`openAsync: true`) with a 48 kHz `AudioMixer` rescaled via `AudioMixer.SetSampleRate(int)` if the backend negotiates another rate, lock-free `IsPaused` via `volatile bool paused`, `AudioMixer.MixFloatStereo1To1Simd` for 1:1 stereo float SIMD mixing, and little-endian bulk `MemoryMarshal.Cast` decoding in `WaveFile` for 16-bit PCM and 32-bit float WAVs; `PhysicsWorld` tracks touched static fixtures sparsely (`touchedStaticFixtures`) so static contact state resets only fixtures hit in the previous step; `SceneManager.SetUp` runs update, physics, and `EntityLifecycle.Flush()` before `Render(dt)` on warm-up turns and exits as soon as `!_unfinished`; and hot simulation/windowing types (`WindowManager`, `SceneManager`, `PhysicsWorld`, `PhysicsBodyComponent2D`, `TransformComponent2D`, `TransformComponent3D`, `IntervalRunnerFixedStep`, `IntervalRunnerSubStep`) are `sealed`.

## 13. Where to look for X

| Need | Look at |
| --- | --- |
| Engine startup / what gets created | `Engine/GameEngine.cs:~147` (ctor), `Core/Windowing/WindowManager.cs:593-690` |
| Frame loop, exclusive work, thread parking | `Core/Windowing/WindowManager.cs` `DrawFrames`, `DrawFrame`, `DoExclusiveWork`, `SimulationHost.EndTick` |
| Fixed/variable tick scheduling | `Core/Threading/SimulationLoop.cs`, `LoopTiming.cs`, `LoopStatistics.cs` |
| Render/sim snapshot hand-off | `Core/Threading/SnapshotClock.cs`, `Snapshot.cs`, `RenderFrame.cs`, `Interpolate.cs` |
| Entity tree behaviour | `Core/Entity.cs`, `Core/EntityLifecycle.cs` |
| Scene switching, stack, transitions | `Engine/Scenes/SceneManager.cs`, `Scene.cs`, `SceneTransition.cs` |
| Cameras | `Engine/Cameras/Camera.cs`, `Camera2D.cs`, `Camera3D.cs` |
| Platform abstraction / add a new OS | `Platform/*.cs`, `Horizon.Platform.Desktop/*`, `.Android`, `.iOS` |
| Window config / run modes / idle behaviour | `Core/Windowing/WindowManagerConfiguration.cs`, `WindowManager.Application.cs` |
| Frameless window / custom title bar | `Core/Windowing/WindowManager.Frame.cs`, `Platform/IPlatformWindow.cs` |
| Keyboard/mouse/touch/gamepad | `Input/InputManager*.cs`, `Keyboard.cs`, `Mouse.cs`, `Touch.cs`, `Gamepads/*` |
| Input scripting for unattended runs | `Input/InputScript.cs` |
| Reading game files / packs | `Content/ContentFiles.cs`, `IContentFiles.cs`, `OverlayContent.cs`, `IndexedContent.cs` |
| Per-scene GPU asset lifetime | `Content/AssetScope.cs`, `Content/AssetManager.cs`, `Graphics/Device/ObjectManager.cs` |
| Localisation | `Content/Localisation.cs` (+ `Horizon.Tests/Core/LocalisationTests.cs`) |
| Where settings/caches are stored | `Core/Platform/PlatformPaths.cs`, `Platform/PlatformStorage.cs`, `DiskStorage.cs` |
| Logging | `Logging/Log.cs`, `Loggers/ConcurrentLogger.cs` |
| Sound playback | `Audio/AudioMixer.cs`, `AudioDevice.cs`, `Sound.cs` |
| 2D physics / character | `Physics/2D/PhysicsWorld.cs`, `CharacterController2D.cs` |
| 3D collision / character | `Physics/3D/CollisionWorld3D.cs`, `CharacterController3D.cs` |
| Tweens | `Core/Tweening/*`, `Entity.Tweens` |
| Background jobs for UI | `Core/Threading/WorkScheduler.cs` |
| Child processes / terminals | `Core/Processes/*`, `Core/Platform/ChildProcesses.cs` |
| Perf/hitch diagnostics | `Core/Diagnostics/*`, env vars in section 10 |
| Screenshots / frame capture | `GameEngine.CaptureScreenshot/CapturePixels` |
| Example game bootstrap | `Fighter2D/App/Program.cs`, `Fighter2D/App/Startup.cs` |
