# Engine rendering, graphics backend and shader pipeline

Scope: `Horizon/Rendering/`, `Horizon/Graphics/`, `Horizon/shaders/`, `Horizon.Shaders.Build/`, `scripts/verify-apk-shaders.ps1`, and how the
platform heads feed the backend. Paths are relative to the engine repo `horizon/` (workspace path `<root>/horizon/...`) unless stated.

## 1. Mental model

- One graphics API: **Vulkan 1.3+ via Silk.NET.Vulkan 2.23**. No OpenGL, no WebGPU. Desktop uses the system Vulkan loader; macOS and iOS use
  **MoltenVK** (portability subset, `Silk.NET.MoltenVK.Native`); Android uses the device driver. "OpenGL way" in comments only describes camera
  conventions (`GraphicsDevice.ClipCorrection`).
- `GraphicsDevice` (`Horizon/Graphics/Device/GraphicsDevice*.cs`, a 6-file partial class) is the single abstraction: an OpenGL-flavoured
  *immediate-mode state machine* over Vulkan. Bind shader/textures/buffers/target, set blend/depth, then `Draw*`/`Dispatch`. Nothing touches the
  command buffer until a draw/dispatch needs it (`GraphicsDevice.State.cs:12`).
- Render passes are implicit (dynamic rendering). Rendering into a target begins on first draw and ends when something else needs the command
  buffer (clear of another target, upload, dispatch). Renderers never manage passes.
- Shaders are **Slang** (`.slang`; `.hlsl` accepted) compiled at run time to **SPIR-V 1.6** by the managed `Slangc.NET` package (`ShaderCompiler`),
  cached on disk by content hash. Platforms with no Slang natives (Android, iOS) load precompiled modules from the shader pack
  `Horizon/shaders/packed/horizon.hzshaders`, built by `Horizon.Shaders.Build`.
- One fixed pipeline layout for all shaders (section 4); only the `Params` uniform block is reflected (`UniformBlockLayout`).
- Rendering runs on a **dedicated render thread** beside the simulation thread. Drawables draw from published snapshots (interpolated between the
  last two ticks) when `RenderFrame.Active.IsDecoupled`.
- Scene graph drives draws: `GameEngine.Render` -> scenes -> `Renderer2D`/`DeferredRenderer2D`/`Renderer3D` (each owns a render target + post chain)
  -> children (`SpriteBatch`, `TileMap`, `PrimitiveRenderer`, particles, UI, `Mesh3D`, `VoxelRenderer`).
- Resources derive `GpuResource` and are owned/freed through `ObjectManager` asset managers.

## 2. Frame flow: engine tick to present

Render loop `Horizon/Core/Windowing/WindowManager.cs:689` `DrawFrames()`:

1. Wait for demand/frame/surface, `DoSurfaceRequests()` (surface detach/attach only between frames).
2. `Graphics.BeginFrame(w,h)` (`GraphicsDevice.cs:281`): recover lost surface, (re)make swapchain on size/vsync/stale, acquire image. With no surface
   the frame still runs so render-target work happens (`Frame.HasImage=false`).
3. `Scaling.BeginFrame` (`Rendering/Scaling/FrameScaler.cs`): if render scale != 1 a smaller scene target stands in for the window
   (`BindRenderTarget(null)` is redirected, `GraphicsDevice.State.cs:280`).
4. `DrawFrame(dt)` (`WindowManager.cs:404`): exclusive work while sim parked, then `DrawFrom(snapshots.Acquire(Presentation), dt)`, which sets
   `RenderFrame.Begin` (`Core/Threading/RenderFrame.cs`) and calls `GameEngine.Render`.
5. `GameEngine.Render` (`Horizon/Engine/GameEngine.cs:~384`): viewport, `Clear(All)`, `CameraBlock.BeginFrame`, loading screen if up, `base.Render` (inside
   `AssetScope.EnterGlobal()`), `#if DEBUG` debugger overlay, `CameraBlock.EndFrame`, scheduled screenshots.
6. `Renderer2D.Render` (`Rendering/Common/Renderer2D.cs:~152`): `RenderState.Save()`, `BeginGpuScope`, bind own `FrameBuffer`, blend=Alpha, depth per
   `DepthForChildren`, `Clear()`, set static `current`, render children, `BeforeResolve(dt)` (lighting), then `PostProcessing.Run` or direct `Resolve()`
   (one screen triangle through `Renderer2DTechnique`) into the outer renderer's target or the window; `RenderState.Restore`.
7. `Scaling.EndFrame()` upscales the scene target (CAS/FSR1/GSR1/nearest), then `Graphics.EndFrame()` (`GraphicsDevice.cs:349`): `EndRendering`, pending
   window clear, swapchain image -> PresentSrc, close GPU scopes, `Submit(final:true)` (timeline semaphore), `swapchain.Present()`, stats, advance
   `frameIndex` (`FRAMES_IN_FLIGHT=2`), `OpenFrame()` waits on the timeline for the frame about to be reused.

`Flush()` (`GraphicsDevice.cs:578`) submits mid-frame and keeps recording; `WaitIdle()` is for shutdown/readback. `Horizon/Engine/LoadingScreen.cs:79` also
begins/ends frames itself.

## 3. Directory map

### `Horizon/Graphics/` (backend)
| Path | What |
|---|---|
| `Device/GraphicsDevice.cs` | frame lifecycle, timeline semaphore, GPU timestamp scopes (`MAX_SCOPES=48`), `Supports(GraphicsFeature)` (`:225`), `ClipCorrection` (`:142`), `Retire(Action)` (`:630`) |
| `Device/GraphicsDevice.State.cs` | bind API, blend/depth/stencil/cull, samplers, `BindlessSlot` (`:257`), `BindRenderTarget` (`:280`), lazy descriptor/pipeline flush |
| `Device/GraphicsDevice.Commands.cs` | `Draw/DrawIndexed/DrawIndexedInstanced/DrawInstanced/DrawIndirect(Count)`, `Dispatch` (`:148`), `Barrier` (`:157`), `Transition`, clears, copies, uploads, mipmaps, `ReadPixels`, `EndRendering` |
| `Device/GraphicsDevice.Resources.cs` | internal buffer/texture/shader-module create+destroy |
| `Device/GraphicsDevice.Surface.cs`, `GraphicsDevice.Scaling.cs`, `SurfaceState.cs`, `Vulkan/SurfaceLifecycle.cs` | surface detach/attach/loss; render-scale target redirection |
| `Device/ObjectManager.cs` | `GameComponent` owning 5 asset managers, factories, `Snapshot/ReleaseSince/Release(scope)` |
| `Device/RenderState.cs` | static `RenderState` Save/Restore; `BlendMode` presets Alpha/Premultiplied/Additive/Replace |
| `Device/GraphicsEnums.cs` | CullMode, Topology, PixelFormat, BufferUsage/Access, ShaderStage, BlendFactor, AttachmentPoint, `GraphicsFeature` |
| `Resources/GpuResource.cs:19` | base: unique uint `Handle`, `Destroy()` -> `DestroyCore()` |
| `Resources/GpuBuffer.cs`, `StreamBuffer.cs` | general buffer (`BufferAccess.Dynamic` updates in-place via `GlobalBarrier`); mapped per-frame ring (3 regions) |
| `Resources/Texture.cs`, `RenderTarget.cs`, `VertexArray.cs`, `IVertex.cs`, `ImageFile.cs` | textures (bindless slot on creation), MRT targets, vertex layouts |
| `Descriptions/*.cs` | `BufferDescription`, `RenderTargetDescription`, `TextureDefinition` presets, `ShaderDescription` |
| `Vulkan/VulkanContext.cs` | instance/device/queues, `Prepare`/`AttachSurface`/`DetachSurface` ahead-of-window lifecycle, `SurfaceTransformPolicy`, Vulkan 1.4 feature detection (`HasPushDescriptors`, `HasHostImageCopy`, `HasIndexUint8`, `HasMaintenance56`), portability, env switches |
| `Vulkan/VulkanLoader.cs` | loader env tweaks (drops implicit layers, remembers GPU vendor driver) |
| `Vulkan/PipelineCache.cs` | `GraphicsKey` -> pipeline map, persistent `pipelines.cache` |
| `Vulkan/DescriptorLayouts.cs`, `BindlessTextures.cs`, `FrameResources.cs`, `VulkanMemory.cs`, `Swapchain.cs` | shared layout, bindless table (clamped `[256, 4096]`), per-frame arenas + `RetiredBuffers`, suballocator, swapchain |
| `Shaders/Shader.cs`, `Technique.cs` | `Shader` (modules + Params layout), `Technique` (shader + CPU Params copy) |
| `Shaders/ShaderCompiler*.cs`, `ShaderPreprocessor.cs`, `UniformBlockLayout.cs`, `ShaderLines.cs` | compile pipeline |
| `Shaders/Packing/*.cs` | `ShaderPack`, `ShaderPackSet`, `ShaderPackWriter/Builder`, `ShaderManifest`, `ShaderManifestRecorder` |

### `Horizon/Rendering/` (namespace `Horizon.Rendering`)
| Path | What |
|---|---|
| `Common/Renderer2D.cs:25` | base renderer: `FrameBuffer`, `Technique`, `PostProcessing`, `FollowWindow`, `Resize`, hooks `BeforeResolve`/`Clear`/`Resized`, `FullScreenPass` |
| `Common/Renderer2DTechnique.cs` | `ScreenTechnique`, `Renderer2DTechnique` (`shaders/renderer2d/standard.slang`) |
| `Common/CameraBlock.cs` | static camera UBO writer; `CameraBlock.Use(camera)`; mirrors `shaders/common/camera.slang` |
| `Common/PostProcessing/` | `PostProcessor` (ping-pong), `PostEffect`, `PostLayer` (retained UI overlay), `PostTarget`, `BlurredBackdrop`, `ShrunkCache`, `BloomEffect`/`BlurEffect`/`CrtEffect`/`ExposureEffect`, `GaussianBlur` |
| `Common/Transitions/` | Fade/Blur/Rot transitions |
| `Scaling/` | `FrameScaler`, plan/settings/environment, CAS, FSR1 EASU+RCAS, GSR1, nearest, `DensityRenderScale` |
| `2D/DeferredRenderer2D.cs:25` | G-buffer (albedo, surface, material) + lights (`MaxLights=64`) + optional path-traced GI |
| `2D/Lighting/` | `Light2D`, `LightCells/LightTiles`, `OcclusionMap2D`, `PathTracedLighting2D`, `SpriteShadows`, `AmbientOcclusion2D` |
| `2D/Spriting/` | `SpriteBatch` (`:24`), `SpriteBatchMeshComponent.cs`, `SpriteItem` (GPU struct), `TextureAtlas`, Aseprite import |
| `2D/Tiling/` | `TileMap`, `TileMapBatch` (compute culling), `TileMapGeometryOcclusion`, `TileMapSilhouettes`, pathfinder, rooms |
| `2D/Primitives/` | `PrimitiveRenderer` (SDF shapes, one draw), `ShapeInstance` |
| `2D/Particles/` | `ParticleRenderer`, CPU and compute simulators |
| `2D/Text/` | `DistanceFieldFont` (SDF R8 atlas from TTF/BMFont), `GlyphAtlas`, `GlyphOutlines`, `GlyphSmoothing` |
| `3D/` | `Renderer3D` (depth-on `Renderer2D`), `Mesh3D`, `Vertex3D`, `Voxels/` (`VoxelRenderer` compute path tracer) |

UI rendering is in `Horizon.UI` (separate project; engine shader `Horizon/shaders/ui/ui.slang`); not covered here.

### Shaders and tools
- Sources `Horizon/shaders/{common,lighting,models,particle,post,primitives,renderer2d,spritebatch,tilemap,ui,voxels}/*.slang`, copied to output by `Horizon/Horizon.csproj:54`.
- Pack `Horizon/shaders/packed/horizon.hzshaders` (committed), manifest `Horizon/shaders.manifest` (beside, not inside, `shaders/` so games don't copy it).
- Builder `Horizon.Shaders.Build/Program.cs`; verifier `scripts/verify-apk-shaders.ps1`.
- `<workspace root>/tools/slang/` is a standalone Slang SDK drop (`slangc.exe`, `slangd.exe`, dlls). I found no engine code referencing it (engine uses the
  `Slangc.NET` NuGet, `Horizon.csproj:35`). Purpose: unclear (likely manual compilation / editor tooling).

## 4. Backend details

### Device creation (`Vulkan/VulkanContext.cs`)
- Instance requests API 1.4 (`Version14`, `:29`); device must be >= 1.3 (`:473`) with dynamic rendering, synchronization2, timeline semaphores,
  descriptor indexing (partially bound, update-after-bind sampled images, non-uniform indexing, runtime arrays) (`:645-660`); otherwise it throws.
- Push descriptors: core on 1.4, `VK_KHR_push_descriptor` on 1.3.
- MoltenVK: `VK_KHR_portability_enumeration` (instance) and `VK_KHR_portability_subset` (device), `IsPortability`; bindless capacity may drop below 4096
  (`BindlessTextures.Capacity`, `MAX_CAPACITY=4096`); MoltenVK lacks multi-draw indirect count (`:88` comment).
- Queues: graphics plus optional async compute and transfer; submissions serialized by `QueueLock`.
- Env: `HORIZON_VULKAN_VALIDATION`, `HORIZON_VULKAN_LABELS` (default on in Debug), `HORIZON_VULKAN_LAYERS=all`, `HORIZON_VULKAN_DRIVER=all`, `HORIZON_VULKAN14=off`, `HORIZON_GPU_SCOPES=off`, `HORIZON_LIGHT_CELLS=off`, `HORIZON_VOXEL_TRACE=1`.
- Surfaces implement `IPlatformSurface`: `Horizon.Platform.Desktop/SilkSurface.cs` (SDL window), `Horizon.Platform.Android/Surface/AndroidSurface.cs`
  (native window), `Horizon.Platform.iOS/Surface/IosSurface.cs` (CAMetalLayer, VK_EXT_metal_surface). The window makes the `GraphicsDevice` with its surface;
  `GraphicsDevice.Current` throws before then (`IsAvailable` probes).

### Binding model: one layout for every shader (`Vulkan/DescriptorLayouts.cs:5`)
- Set 0: binding 0 camera UBO, 1 `Params` UBO (both dynamic offsets into the per-frame uniform arena), 2..9 storage buffers (`STORAGE_BINDINGS=8`).
- Set 1: textures 0..7, storage images 8..11, separate samplers 12..19 (for HLSL). Partially bound; pushed when push descriptors exist.
- Set 2: bindless table `Sampler2D name[capacity]`; a texture gets a slot at creation; sprite items carry the slot in their flags (`NO_TEXTURE=0xFFFF`).
- Prelude macros (`ShaderPreprocessor.PreludeFor`, `ShaderPreprocessor.cs:~58`): `BIND_UNIFORM(n)`, `BIND_BUFFER(n)`, `BIND_TEXTURE(n)`, `BIND_IMAGE(n)`, `BIND_SAMPLER(n)`,
  `BINDLESS_TEXTURES(name)`, `NO_TEXTURE`, `HORIZON_SLANG`. Bindless capacity is in the prelude, so it is in every key.

### Pipelines and clip space
- `PipelineCache.GraphicsKey(Shader, Blend, BlendMode, ColorWrite, Topology, Cull, VertexLayout, Attachments)` (`PipelineCache.cs:17`). Viewport, depth/stencil
  test and masks are dynamic. Pipelines are created lazily at first use; the driver cache is saved ~3 s after the last new pipeline.
- Vulkan clip space is Y-down, depth 0..1; engine cameras are Y-up. `CameraBlock` multiplies by `GraphicsDevice.ClipCorrection`. Slang matrices are
  row-major, used as `mul(rowVector, matrix)` like System.Numerics (`shaders/common/camera.slang`).

### Memory
`FrameResources` holds per-frame `LinearArena`s (uniforms, staging), a descriptor pool reset each frame, and a `Retired` list. `VulkanMemory` suballocates
(stats in `GraphicsStatistics`). `StreamBuffer<T>`: mapped once, 3 regions in rotation guarded by fences, 1.5x growth headroom (growth waits on GPU, 1 s cap).

## 5. Shader pipeline end to end

### Source language
- `.slang`: one file, all stages marked `[shader("vertex"|"fragment"|"compute")]`, entries `vsMain/fsMain/csMain`; one SPIR-V module, several stages.
- `.hlsl`: per entry point `vsMain, fsMain/psMain, csMain, gsMain` (`ShaderCompiler.Slang.cs:23`). Engine shaders are all Slang.
- Args: `-target spirv -profile spirv_1_6 -matrix-layout-row-major -fvk-use-entrypoint-name` (`ShaderCompiler.Slang.cs:~27`); `TARGET_PROFILE="spirv_1_6"`.
- Includes: `#include "rel.slang"` (relative, then root) and `#include <common/camera.slang>` (root `ShaderPreprocessor.Root="shaders"`). The engine resolves them
  (not Slang), emits `#line`, once per program, `\n` endings so keys match across Windows and APK.

### Run-time flow
`Shader.Load(dir,name)` (`Shader.cs:~75`) -> `ShaderDescription.FromPath` -> `ObjectManager.Instance.Shaders.CreateOrGet` (cached by name; `Scoped=false`, never
released) -> `Shader.TryCreate` -> `ShaderCompiler.Compile` (`ShaderCompiler.cs:~74`), per definition in `Parallel.For`:
1. Preprocess; record to manifest if `HORIZON_SHADER_MANIFEST` set (before cache lookup, so hits are recorded too).
2. `UnitsOf` -> `ShaderUnit` with `Key = SHA256(fingerprint|language|entry|source)`, each part length-prefixed (`ShaderCompiler.Keys.cs`).
   `CompilerFingerprint = CACHE_FORMAT | Slangc.NET version | compiler args`.
3. `Resolve` (`ShaderCompiler.cs:193-201`): Packed -> pack only; Automatic checks disk cache (`.spv` + `.notes`) -> **shipped `ShaderPackSet` (`*.hzshaders`) BEFORE `CompileFresh`** (avoiding loading Slang unless needed), falling back to pack on missing Slang natives (`DllNotFound`/`EntryPointNotFound`/`BadImageFormat` sets `slangMissing`).
4. `CompiledShader` (stages, SPIR-V, thread group sizes, merged `Params` layout from reflection) -> `VkShaderModule`s (one per distinct SPIR-V array).
Failure returns `Shader.Invalid` and logs `[ShaderCompiler] <name> didn't compile:`; drawing with an invalid technique draws nothing.

### Env vars
| Var | Effect |
|---|---|
| `HORIZON_SHADERS=packed` | pack only: no cache, no compiler, Slang natives never load |
| `HORIZON_SHADERS=compile` | never use a pack |
| unset | cache -> compiler -> pack if Slang missing |
| `HORIZON_SHADER_CACHE=off` or dir | disable or relocate cache (default `PlatformStorage.Cache("Horizon/<game>")/Shaders`; 30-day prune) |
| `HORIZON_SHADER_MANIFEST=<file>` | record every program asked for |

### Per platform
- **Desktop**: Slang natives from NuGet, disk cache; `shaders/**` (incl. `packed/`) copied beside the exe.
- **Android**: no Slang -> pack. `Horizon.Testing.Android/Horizon.Testing.Android.csproj:~109-141` puts `shaders/**` (sources AND pack) in APK assets because the runtime
  hashes sources to compute keys, then looks them up in the pack. Build errors if no `.hzshaders`; guards aapt2 dropping `_`/`.`-prefixed paths.
- **iOS**: always packed (`Horizon.Platform.iOS/README.md`); MoltenVK translates SPIR-V to MSL.
- Code refers to `notes/android-plan.md` for SPIR-V 1.2; not verified to exist.

### Pack, manifest, builder
- `ShaderPackEntry(Key, Origin, Spirv, Stages, Params)` (`Packing/ShaderPack.cs`). Pack has fingerprint, target profile, bindless capacities. `ShaderPackSet.For(content)`
  reads every `*.hzshaders` in `shaders/packed`, drops mismatching fingerprint/profile with a warning. A miss throws a message naming key, packs and fix (`ShaderPackSet.Missing`).
- Manifest tab-separated: `file|inline <lang> <capacity> <key> <path>` then `input <path> <sha256>` per file and include.
- `Horizon.Shaders.Build`: `build --manifest --content <bin dir> --out <pack> [--capacity n]...`, `verify`, `check` (manifest stale vs sources?), `refresh`, `info`. Exit 0/1/2.
- Automated in `tasks.hor`: RECORD runs `Horizon.Testing --checks`, every example in `EXAMPLES`, and a Fighter2D match with `HORIZON_SHADER_MANIFEST` set; then build the
  pack twice (determinism check) and verify against a fresh compile. `PACKED_CHECKS` re-runs checks with `HORIZON_SHADERS=packed HORIZON_SHADER_CACHE=off`.
  Tests: `dotnet test Horizon.Tests --filter FullyQualifiedName~Horizon.Tests.Shaders` (task `test_shaders`).
- `scripts/verify-apk-shaders.ps1 -Apk <apk>`: canonical pack parses and has copy/cas/upscale_nearest/gsr1/fsr1_easu/fsr1_rcas/crt/crt_shrink and two hard-coded tablet keys;
  APK's embedded pack SHA-256 equals repo pack; every embedded source/include hash matches manifest `input` lines. Prints `VERIFIED` or throws.

### Add a new shader
1. Create `Horizon/shaders/<area>/<name>.slang`. Full-screen pass: `#include <common/screen.slang>`, `struct Params {...}; BIND_UNIFORM(1) ConstantBuffer<Params> params;`
   `BIND_TEXTURE(0) Sampler2D uSource;` and `[shader("fragment")] float4 fsMain(ScreenVaryings input) : SV_Target0` (see `shaders/post/blur.slang`).
   Geometry: `#include <common/camera.slang>` and `toClip(...)`.
2. Load: `Technique.Load("shaders/<area>","<name>")`, `new ScreenTechnique("<unique name>", "shaders/<area>/<name>.slang")`, or `new PostTechnique("<name>")` for post.
3. Uniforms by `Params` member name: `technique.SetUniform("uX", v)`. Textures: `texture.Bind(slot)` matching `BIND_TEXTURE(n)`. Storage: `technique.BindBuffer(n>=2, buf)`.
4. Mobile: re-record the manifest and rebuild the pack via the tasks.hor automations; commit `shaders.manifest` and `packed/horizon.hzshaders`. Otherwise desktop works and
   Android/iOS fail with "isn't in the shader pack (key ...)". Editing a shared include (e.g. `common/camera.slang`) changes every dependent key.
5. If you change a GPU-side struct (camera block, `SpriteItem`, `LightData`, 80 bytes std430), update the C# mirror by hand; comments in both say "must match".

## 6. Resources and lifetime

| Type | Created by | Notes |
|---|---|---|
| `GpuBuffer` | `GpuBuffer.Create(BufferDescription...)` | `Upload`, `BindStorage`; `BufferAccess.Dynamic` = device-local via commands; `LastUse` submission guards overwrite |
| `Texture` | `Texture.Create` / `TextureDefinition` presets | bindless slot at creation; `Bind(unit)`, `UploadTexels`, `GenerateMipmaps` |
| `RenderTarget` | `RenderTarget.Create(RenderTargetDescription)` | MRT via `Attachments` (Color0.., DepthStencil); `Bind()`, `.Color`, `BindAttachment` |
| `VertexArray` | `VertexArrayDescription` | layout hashed into pipeline key; `SetLayout<T>` for `IVertex` |
| `Shader`/`Technique` | `Shader.Load`, `Technique.Load` | `Technique` keeps a CPU byte[] of Params, sent via `SetParams` before draw if dirty |

- `GpuResource.Destroy()` runs once on the render thread. `AssetScope` (per scene) releases what a scene made except named/shared items; `ObjectManager.Dispose` order:
  RenderTargets, Textures, Shaders, VertexArrays, Buffers.
- The device never frees what the GPU may read: `Retire(Action)` defers to frame completion. `GraphicsDevice.Unbind(resource)` clears stale bindings.
- `Renderer2D.Resize` disposes and recreates `FrameBuffer` and `Technique`; cached references go stale (use `Resized()`).

## 7. Threading

- Render thread only (named "Render", `WindowManager.cs:693`): everything touching `GraphicsDevice`, `RenderState`, `CameraBlock`, GPU resource create/destroy.
- Simulation thread publishes snapshots each tick (`SnapshotBuffer<T>`, `Core/Threading/SnapshotBuffer.cs`); render reads last two and interpolates by `RenderFrame.Alpha`
  (`PresentationMode.Interpolated|Latest`). Examples: `SpriteBatch.Capture`/`RenderCaptured`, `PrimitiveRenderer.Describe/Shapes`, lights in `DeferredRenderer2D` (`lightLock`).
  When sim is parked (scene setup) `IsDecoupled` is false and drawables read live state (`SpriteBatch.Render`, `:382`).
- Any-thread-safe: `PostProcessor.Add/Remove/Enabled`, `FrameScaler.Settings` (applied next frame), `GraphicsDevice.SurfaceChanged()`, `ShaderCompiler.Compile`.
- Queue submits take `Context.QueueLock`. `CreateFence/WaitFence` are timeline-value based (used by `StreamBuffer`).

## 8. Recipes

### Draw a new streamed-instance thing (from `PrimitiveRenderer` / `SpriteBatchMesh`)
```csharp
tech = Technique.Load("shaders/myarea", "mything");
stream = new StreamBuffer<MyInstance>(/* see Resources/StreamBuffer.cs */);
// Render(dt), render thread:
using var scope = GraphicsDevice.Current.BeginGpuScope("mything");
RenderFrame frame = RenderFrame.Active;            // draw from snapshot if frame.IsDecoupled
Span<MyInstance> items = stream.Begin(count);      // write-only mapped memory
CameraBlock.Use(camera);
tech.Bind();
tech.SetUniform("uModel", in model);
stream.BindRange(3);                               // shader: BIND_BUFFER(3)
GraphicsDevice.Current.DrawInstanced(Topology.Triangles, 6, (uint)count, (uint)first);
stream.End();
```
Save/restore any state you change (`RenderState.Save/Restore`; stencil/colour-write like `DrawPasses` in `SpriteBatchMeshComponent.cs`). Inside a `DeferredRenderer2D`
write all three G-buffer attachments (`shaders/common/gbuffer.slang`) or the object lights like a hole. Add with `renderer.AddEntity(new MyDrawable())`.

### Post effect
Subclass `PostEffect` (`Common/PostProcessing/PostEffect.cs:16`): `Initialize()` makes `PostTechnique`/`PostTarget`; `Render(PostContext)` calls `ctx.Draw()` once.
`renderer.PostProcessing.Add(new MyEffect())`. Blend and depth are off during effects. See `BlurEffect`.

### Compute pass
`ShaderDescription.Compute(path)`; `device.BindShader`, `BindStorageBuffer/BindStorageImage`, `device.Dispatch(shader.GroupsFor(n))`, `device.Barrier(BarrierTargets...)` between dependent
passes. Examples: `ComputeParticleSimulator2D`, `shaders/lighting/*`, `VoxelRenderer`. Gate optional features with `device.Supports(GraphicsFeature.X)` and `WarnUnsupported`.

## 9. Renderer notes

- `Renderer2D` target defaults to RGBA8 nearest + DepthStencil (stencil used for sprite masks). Renderers nest (child shown inside its `outer`; static `Renderer2D.Current`).
  `FollowWindow` tracks `Scaling.TargetSize` (scaled scene size).
- `DeferredRenderer2D`: children write MRT G-buffer; `BeforeResolve` collects <=64 lights into a storage buffer; the resolve technique lights. `HoldsPicture=false`.
  Path-traced GI in `PathTracedLighting2D` + `shaders/lighting/gi_*.slang` (needs rgba16f and rgba32f storage images).
- Sprites: `SpriteItem` per-instance in storage buffer, quad = 6 verts instanced, bindless texture index in flags -> one draw for all textures; masked sprites add a stencil mask pass.
- Text: SDF atlas cached on disk beside shader cache.
- Tiles: `tilemap_cull.slang` compute culling + `tilemap.slang`.
- 3D: `Renderer3D` = depth-on `Renderer2D`. Voxels: separate compute path tracer (tree traversal, GI cache, a-trous, TAA, tonemap, bloom, sky).
- Render scale: `RenderComposition.NativeOverlay` (UI drawn at native size after scene is upscaled at first UI draw via `BeginOverlay`) vs `WholeFrame`; default upscaler CAS.

## 10. Performance patterns

- Instancing + bindless textures = one draw per batch. State is lazily flushed with dirty flags; setting the same state twice is free.
- `Params` is one dynamic-offset UBO slice per draw; `SetUniform` only marks dirty.
- `CameraBlock.Use` skips the upload if camera/frame/alpha unchanged.
- Streaming through `StreamBuffer`; never read mapped memory back.
- Driver pipeline cache + shader cache/pack avoid compile hitches.
- GPU timing: `using var s = device.BeginGpuScope(name)`; results in `GpuScopes`/`GpuFrameMilliseconds` once the frame completes; `Statistics` for counts/memory.
- Allocation-free render path is a goal (render stats track bytes allocated per frame).

## 11. Gotchas and invariants

- Vulkan only; don't write GL-isms.
- Unknown `Params` names are ignored with one warning per (shader, name): typos give wrong output, not errors. Watch the log for `[Technique]`.
- `Technique.SetShader` only takes the first valid shader; a technique can't be re-pointed.
- `Shader.Load` returns `Shader.Invalid` on failure rather than throwing; draws nothing.
- Keys depend on whole preprocessed text, Slangc.NET version (2026.19.0), args, `CACHE_FORMAT`, bindless capacity. Any change to those or an included file invalidates the pack.
- Never `AppendLine` in shader text handling (CRLF vs LF changes keys); `ShaderLines` normalises.
- A manifest only contains shaders the recording run loaded; code paths not covered by `EXAMPLES`/Fighter2D recording are missing on mobile.
- Limits: 8 textures, 4 storage images, 8 storage buffers per draw (`ShaderPreprocessor` consts); use bindless beyond that.
- Hand-mirrored structs (`CameraBlock.Block` Pack=4, `SpriteItem`, `LightData`) must match the Slang side.
- Everything that draws must restore `RenderState`; renderers assume blend=Alpha, depth off, cull none.
- Surface can be lost/recreated between frames (Android); size-dependent resources must react to `SwapchainGeneration`.
- Both `IsDecoupled` and live-state paths must work in every drawable.
- Debugger overlay draws only in `#if DEBUG`.

## 12. Rough edges

- `tools/slang/` role unclear. The pack is a large committed artifact; every shader edit needs record + rebuild.
- `verify-apk-shaders.ps1` hard-codes two tablet keys and a shader name list.
- No runtime check that a game's every shader is in the pack until first use on device.
- Adreno 6xx note near `VulkanContext.cs:470`: SPIR-V 1.6 stopped running there; 1.6 is currently the only profile.

## 13. Where to look for X

| Need | Look at |
|---|---|
| Draw/bind/blend API | `Graphics/Device/GraphicsDevice.State.cs`, `.Commands.cs` |
| Frame begin/end, swapchain | `GraphicsDevice.cs:281,349`, `Vulkan/Swapchain.cs`, `GraphicsDevice.Surface.cs` |
| Device requirements, MoltenVK | `Vulkan/VulkanContext.cs:470-760` |
| Pipeline state key | `Vulkan/PipelineCache.cs` |
| Binding numbers, macros | `Vulkan/DescriptorLayouts.cs`, `Shaders/ShaderPreprocessor.cs` |
| Bindless | `Vulkan/BindlessTextures.cs`, `GraphicsDevice.State.cs:257` |
| Compile/cache/pack fallback | `Shaders/ShaderCompiler*.cs` |
| Pack format, builder | `Shaders/Packing/*`, `Horizon.Shaders.Build/Program.cs` |
| Pack regeneration workflow | `tasks.hor` |
| APK shader sanity | `scripts/verify-apk-shaders.ps1`, `Horizon.Testing.Android/*.csproj` |
| Camera data in shaders | `Rendering/Common/CameraBlock.cs`, `shaders/common/camera.slang` |
| Screen pass boilerplate | `shaders/common/screen.slang`, `Rendering/Common/Renderer2DTechnique.cs` |
| Post effects | `Rendering/Common/PostProcessing/` |
| Sprites | `Rendering/2D/Spriting/SpriteBatch.cs`, `SpriteBatchMeshComponent.cs`, `StreamBuffer.cs` (`TouchRegion`, revision-aware `BeginItems`, shared quad `ushort[6]` IB `DrawQuadInstanced`/`DrawQuadIndirect`/`DrawQuadIndirectCount`), `TileMapGpu` (`Tile`), `TileMapBatch` (`BarrierTargets.Indirect` WAR guard), `TileMapGeometryOcclusion` (`SmearVertical` row-major running sums), `shaders/spritebatch/sprites.slang` |
| Lighting | `Rendering/2D/DeferredRenderer2D.cs`, `2D/Lighting/` (`OcclusionMap2D` `EnvelopeFloat` for grids `<= 2048`, `gi_cascade.slang` screen-AABB clipping + `uTexRadiance.a` `leaveWall`, `sprite_flood_final.slang` LDS JFA fusion, `direct.slang`/`light_cells.slang` `ambientOcclusionPair` + UV hoisting, outward-spiral `sdf_rows`/`sdf_columns`), `shaders/lighting/`, `shaders/renderer2d/deferred.slang` |
| Debug shapes | `Rendering/2D/Primitives/PrimitiveRenderer.cs`, `shaders/primitives/shapes.slang` |
| Particles | `Rendering/2D/Particles/`, `shaders/particle/` |
| Voxels / 3D | `Rendering/3D/`, `shaders/voxels/`, `shaders/models/mesh.slang` |
| Render scale | `Rendering/Scaling/`, `shaders/post/{cas,fsr1_*,gsr1,upscale_nearest}.slang` |
| Dual-source UI & Vulkan prep | `Graphics/Device/{GraphicsEnums,RenderState}.cs` (`BlendFactor.Src1Alpha`/`OneMinusSrc1Alpha`, `BlendMode.DualSource`), `shaders/ui/ui_dual.slang`, `Graphics/Vulkan` (`PreparedDevice`, `SupportsDualSourceBlend`), `Core/Windowing/WindowManager.Device.cs` |
| ShaderPack & warming | `ShaderPack` (`ReadOnlyMemory<byte>` + lazy shared `.Spirv`), `CompiledStage`, `WarmComputePipelines`, `Technique.SetParamsBlock<T>` |
| Render loop, snapshots | `Core/Windowing/WindowManager.cs:404-720`, `Core/Threading/RenderFrame.cs` |
| Ownership by scene | `Graphics/Device/ObjectManager.cs`, `Horizon/Content` |
| GPU timing/stats | `GraphicsDevice.BeginGpuScope`, `GraphicsStatistics.cs`, `GpuScope.cs` |
| Env switches | `HORIZON_SHADERS`, `HORIZON_SHADER_CACHE`, `HORIZON_SHADER_MANIFEST`, `HORIZON_VULKAN_{VALIDATION,LABELS,LAYERS,DRIVER}`, `HORIZON_GPU_SCOPES` |
