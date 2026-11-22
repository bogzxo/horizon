# Engine periphery: platform heads, Terminal, Tooling, Testing, Tests, AutoVoxel, CI

Scope: everything in `horizon/` around the core `Horizon/` project. All paths are relative to the engine repo root
(`C:\Users\bogdan\source\repos\bogzxo\horizon\horizon`) unless they start with `../` (workspace root). Everything below
was read from code; "unclear" marks what was not verifiable.

## 1. Project map at a glance

| Project | TFM | Kind | In `Horizon.sln`? | Depends on | One line |
|---|---|---|---|---|---|
| `Horizon.Platform.Desktop` | net11.0 | lib | yes | Horizon, Silk.NET (Windowing/SDL/Input/MoltenVK.Native) | Windows/Linux/macOS window, input, audio, Vulkan surface |
| `Horizon.Platform.Android` | net11.0-android | lib | **no** | Horizon | Activity + SurfaceView host, AAudio, APK assets, via raw NDK P/Invoke |
| `Horizon.Platform.iOS` | net11.0-ios | lib | **no** | Horizon, Silk.NET.MoltenVK.Native | UIKit host, CAMetalLayer surface via MoltenVK, AVAudioEngine |
| `Horizon.Android.Probe` | net11.0-android | exe | **no** | Silk.NET.Vulkan only (not the engine) | Stage-0 check: can .NET Android load system Vulkan |
| `Horizon.Terminal` | net11.0 | lib | yes | Horizon, Horizon.UI, Horizon.HIDL | VT parser, screen, view, panels, host with plugins/profiles, TUIs |
| `Horizon.Tooling` | net11.0 | lib | yes | Horizon, Horizon.UI | LSP client + DAP (debug adapter) client for editors |
| `Horizon.Testing` | net11.0 | exe | yes | Horizon, Platform.Desktop, UI, Terminal, HIDL | The engine's examples + self-checks ("check scenes"), NOT xUnit |
| `Horizon.Testing.Android` | net11.0-android | exe | **no** | Horizon, UI, HIDL, Terminal, Platform.Android | Same sources linked into an APK, plus device checks |
| `Horizon.Testing.iOS` | net11.0-ios | exe | **no** | Horizon, UI, HIDL, Terminal, Platform.iOS | Same sources linked into an iOS app |
| `Horizon.Tests` | net11.0 | xUnit | yes | Horizon, Platform.Desktop, UI, Tooling, Terminal, HIDL | Headless unit tests, `dotnet test` |
| `Horizon.Shaders.Build` | net11.0 | exe | yes (listed in sln) | Horizon | Shader pack builder for phones (no Slang there) |
| `AutoVoxel` | net11.0 | exe | yes | Horizon, Platform.Desktop, UI | Path-traced voxel game, used as an engine example/stress |

Facts about solution membership: `Horizon.sln` lists Horizon, HIDL, Testing, Tests, AutoVoxel, Terminal, Tooling, UI,
Platform.Desktop, Shaders.Build (plus solution folders Scripting, Tools). Android/iOS projects are deliberately left out
so a desktop build never needs a mobile workload (comments in `Horizon.Platform.Android/Horizon.Platform.Android.csproj`,
`Horizon.Platform.iOS/README.md`). Note `tasks.hor` says "Ctrl+Shift+B builds the solution and then the shader pack
builder, which isn't in the solution" (`tasks.hor:2`), yet `Horizon.sln` does list `Horizon.Shaders.Build`; the comment
looks stale (unclear which is intended, the sln line is authoritative for what `dotnet build Horizon.sln` does).

Shared build config:
- `Directory.Build.props` (engine root) imports any parent `Directory.Build.props` (workspace's `../Directory.Build.props`
  sets `HorizonRoot`/`HidlRoot`) and sets `<Version>1.5.0</Version>` for every project whose name starts with `Horizon`
  (AutoVoxel gets no version).
- `global.json`: SDK `11.0.100-rc.2.26504.105`, `rollForward: latestMajor`, `allowPrerelease: true`.
- Libraries use `LangVersion preview`, `Nullable enable`, `AllowUnsafeBlocks`, `InvariantGlobalization` (iOS test app turns
  it OFF, see gotchas). Desktop/Terminal/Tooling set `IsAotCompatible` + `IsTrimmable`.
- `qodana.yaml`: stock JetBrains Qodana starter (`ide: QDNET`, `profile: qodana.starter`), no custom rules, no failure
  conditions. No Qodana workflow exists in `.github/workflows` (only `ci.yml`, `build.yml`), so unclear whether it runs anywhere.
- `THIRD-PARTY-NOTICES.md`, `scripts/verify-apk-shaders.ps1` (see section 5) live at the root.

## 2. The platform abstraction seam

The core engine never names a platform head. Contracts live in `Horizon/Platform/`:

| File | Contract |
|---|---|
| `Horizon/Platform/PlatformInstallation.cs` | `IPlatform` (`CreateHost(PlatformHostRequest)`, `CreateAudioOutput()`), static `PlatformInstallation.Install/Current/Resolve` |
| `Horizon/Platform/IPlatformHost.cs:19` | `IPlatformHost` (Features, Window?, SurfaceAhead?, Surface, Input, State, CreateAudioOutput, Attach(IPlatformEvents), Initialize, IsPlatformThread, Post/Invoke, Wake, RequestClose); `IPlatformLoop` (Run, PumpUntil, Finish); `IPlatformEvents` (callbacks host -> engine); records `PlatformDisplay`, `PlatformState`, `PlatformHostRequest(WindowManagerConfiguration, ApplicationBehaviour, ReadsGamepads)` |
| `Horizon/Platform/PlatformFeatures.cs` | `[Flags]` capability set: Window, WindowFrame, DragArea, HotKeys, DropDown, CornerRadius, DisplaySettings, PointerCapture, Clipboard, Gamepads, FileSystem, AutomaticRenderScale (720p default scale, phones). UI asks these before drawing buttons |
| `Horizon/Platform/IPlatformSurface.cs` | `IPlatformSurface` (VulkanInstanceExtensions, IsAvailable, PixelSize, `CreateVulkanSurface(instance, instanceFunction)`), `IPlatformSurfaceAhead` |
| `Horizon/Platform/IPlatformInput.cs`, `IPlatformWindow.cs` | input feed and optional desktop-window ops |
| `Horizon/Platform/IPlatformStorage.cs`, `PlatformStorage.cs`, `DiskStorage.cs` | `Config/Data/State/Cache(name)`, `OpenRead`, atomic `BeginWrite` -> `IStorageWrite.Commit()`; `PlatformStorage.Current` is a static assigned by the head |
| `Horizon/Platform/IAudioOutput.cs` | `Open(AudioFormat)`, `Start(AudioRender)`, `Pause`, `Resume`; render callback `bool(Span<float>)` |
| `Horizon/Platform/PlatformDispatcher.cs` | Post/Invoke/Drain queue bound to the platform thread, used by every host |
| `Horizon/Content/ContentFiles.Source` | content source (APK assets, bundle, disk), set by mobile bootstraps |

How a platform is chosen:
1. Desktop: program calls `Horizon.Platform.Desktop.DesktopPlatform.Install()` first thing in `Main` (see
   `Horizon.Testing/Program.cs:~69`, `AutoVoxel/Program.cs`). `PlatformInstallation.Resolve` throws with that exact hint if
   nothing is installed (`Horizon/Platform/PlatformInstallation.cs`).
2. Android/iOS: NOT globally installed. The activity/app delegate builds its own `IPlatform` and passes it via
   `GameEngineConfiguration.Platform` (`Horizon/Engine/GameEngineConfiguration.cs:35`, `init` property; tests also use it).
3. One host per engine; never shared (`DesktopPlatform.CreateHost` returns a new `DesktopHost` each call).

Threading model across heads (matches `Horizon.Testing/Program.cs` header): the thread that calls `Run()` is the platform/
window thread (owns window + input); a render thread owns the GPU; a simulation thread runs updates. Heads implement
`IPlatformHost.Post/Invoke` for marshalling to the platform thread (`DesktopHost.cs:~100`). On macOS, creating the SDL
Metal view must happen on the main thread so `SilkSurface` takes an `Invoke` delegate (`DesktopHost.cs` Initialize).

Boundary tests enforce the seam: `Horizon.Tests/Platform/BoundaryTests.cs` asserts no public `Horizon.Input`/UI signature
exposes `Silk.NET.Input`, Vulkan types stay in `Horizon.Graphics`, `Horizon.Platform` contracts name no Silk types, and the GPU is
created from the platform surface not from Silk windows. Break these and CI fails.

## 3. Horizon.Platform.Desktop

Directory: `Horizon.Platform.Desktop/`.
- `DesktopPlatform.cs` - `IPlatform`; `Install()`; `CreateAudioOutput()` => `SdlAudioOutput`.
- `DesktopHost.cs` - `IPlatformHost`+`IPlatformLoop`. Computes `PlatformFeatures` (Window, DisplaySettings, PointerCapture,
  Clipboard, FileSystem, DropDown always; Gamepads if `request.ReadsGamepads`; WindowFrame+CornerRadius if custom frame;
  DragArea only on SDL; HotKeys only custom-frame on Windows). `Attach` creates `DesktopWindow`; `Initialize` binds the
  dispatcher thread, creates `DesktopInput`, then `SilkSurface`. `Run()` -> `Window.RunUntilClosed()`.
- `SilkSurface.cs` (Vulkan surface from Silk `IVkSurface`), `Win32SurfaceAhead.cs` (Windows present-without-surface probe).
- `Window/DesktopWindow*.cs` - partial class split by concern: `.cs` (main), `.Sdl.cs`, `.Frame.cs` + `.Frame.Windows.cs`
  (custom/borderless frame), `.HitTest.cs`, `.Corners.cs`, `.DisplayScale.cs`, `.Icon.cs`, `.Quake.cs` (drop-down terminal
  window, `PlatformFeatures.DropDown`), `.Waiting.cs` (event-wait loop).
- `Input/` - `DesktopInput`, GLFW wrappers (`Glfw/GlfwKeyboard|Mouse|Cursor`, `GlfwInputWithoutGamepads`), `SilkInputSource`,
  `SilkGamepadSource`, `WindowsClipboard`, `WindowsTouch`.
- `SdlAudioOutput.cs` - SDL audio, `IAudioOutput`.

Windowing backend choice (`Window/DesktopWindow.Sdl.cs:15`): `HORIZON_WINDOWING=glfw|sdl`; default is **GLFW on Windows,
SDL on everything else**. GLFW has no Vulkan on macOS; SDL goes through a Metal surface with `Silk.NET.MoltenVK.Native`
bundled so users need no Vulkan SDK. SDL needs a loadable Vulkan loader on Linux (`"The window has no Vulkan surface, is the Vulkan loader installed?"`, `DesktopHost.cs`).
`InternalsVisibleTo Horizon.Tests`. `IsPublishable=false`.

Env vars read by head/engine (verified in code): `HORIZON_WINDOWING`, `HORIZON_AUDIO=off`.

## 4. Horizon.Platform.Android

Directory: `Horizon.Platform.Android/`. Design rule (csproj comment): talks straight to NDK libs (`libandroid.so`,
`libaaudio.so`) via P/Invoke, **no Java binding packages, no AndroidX**; Vulkan comes from the system loader.

| Area | Files |
|---|---|
| Activity host | `Hosting/HorizonActivity.cs` (abstract base: `Configure()`, `MakeEngine()`, `abstract MakeFirstScene()`), `.Input.cs`, `.Screen.cs`, `AndroidHost.cs`, `AndroidPlatform.cs`, `AndroidSession.cs`, `AndroidSession.Surface.cs`, `AndroidDisplayInfo.cs` |
| Bootstrap | `Bootstrap/AndroidBootstrap.cs` (once per process), `AndroidLogger.cs` (logcat + `files/debug.log`, previous run `debug-previous.log`), `AndroidPaths.cs`, `AndroidSettings.cs` |
| Content/Storage | `Content/AndroidContent.cs`+`AssetIndex.cs`+`AssetStream.cs` (APK assets via `AAssetManager`), `Storage/AndroidStorage.cs` |
| Surface/Native | `Surface/AndroidSurface.cs`, `Native/NativeWindow.cs`, `NativeAudio.cs`, `NativeAssets.cs`, `VulkanAndroidSurface.cs` (extensions `VK_KHR_surface`, `VK_KHR_android_surface`) |
| Input | `Input/AndroidInput.cs`, `AndroidPointers.cs` (touch), `AndroidKeys.cs`, `AndroidGamepads.cs`, `HorizonSurfaceView.cs` |
| Audio | `Audio/AAudioOutput*.cs` (Callbacks, Reopen, Stream partials) |

Key mechanics (verified):
- A game derives `HorizonActivity`, marks it `[Activity(... ConfigurationChanges = HorizonActivity.HANDLED_CHANGES, Theme = HorizonActivity.THEME)]` and
  overrides `MakeFirstScene()` (`HorizonActivity.cs:6-20` doc example; real use `Horizon.Testing.Android/TestingActivity.cs`).
- The engine belongs to the **process's `AndroidSession`** (static `AndroidSession.For`), not to the activity, so activity
  recreation finds the engine running and just rebinds a surface. Engine stops only when the activity finishes for good or the game quits.
- `SurfaceDestroyed` => engine detaches swapchain+Vulkan surface; `SurfaceCreated` re-attaches (generation +1). Keep GPU
  work off a detached surface (`AndroidSession.Surface.cs`).
- `AndroidBootstrap.Start` (`Bootstrap/AndroidBootstrap.cs`): sets logger first, reads settings from `files/horizon.env`
  then intent string extras (`--es HORIZON_*`, extras win), resolves path-like settings into app folders
  (`HORIZON_SCREENSHOT`, `HORIZON_INPUT_SCRIPT`, `HORIZON_SHADER_MANIFEST`), exports them to the process environment, sets
  `PlatformStorage.Current` and `ContentFiles.Source = AndroidContent`. A process takes settings ONCE; later intents' extras
  are ignored with a warning (use `am force-stop`).
- **`HORIZON_SHADERS` is forced to `packed`** on phones (no Slang). The bootstrap logs an Error if `shaders/packed` is missing from the APK.
- Driver diagnostics: `HORIZON_TURNIP_MODE` (bandwidth|profiled|prefer_sysmem|prefer_gmem -> native `TU_AUTOTUNE_ALGO`),
  `HORIZON_TURNIP_TUNE_SMALL`, `HORIZON_TURNIP_SHADER_DUMP` (redirects fd 2 to `turnip-shaders.txt`). Adreno/Mesa Turnip specific.
- `AndroidPlatform.CreateAudioOutput()` => `AAudioOutput`; `PlatformFeatures` for a phone includes `AutomaticRenderScale`
  and no Window features (see `AndroidHost.cs`).
- `SupportedOSPlatformVersion 26.0`. A plan doc is referenced in a csproj comment (`notes/android-plan.md`, `android-host-results.md`);
  those are not in the engine repo checkout (unclear where; workspace `artifacts/android-platform-baseline/` is referenced by READMEs).

### Android build/signing (from READMEs and csproj; nothing here was run)
- Needs the `android` workload (`dotnet workload install android`; `dotnet workload restore <csproj>`), a JDK 21 and Android SDK.
  CI installs deps with `-t:InstallAndroidDependencies -p:AndroidSdkDirectory=... -p:JavaSdkDirectory=... -p:AcceptAndroidSdkLicenses=True`
  (`.github/workflows/ci.yml` job `android-probe`).
- Machine-specific paths in `Horizon.Testing.Android/README.md`: SDK `C:/Users/bogdan/AppData/Local/Android/Sdk`, JDK
  `D:/Microsoft/VSShared/Android/openjdk/jdk-21.0.8`, test device serial `R52M416M2TH` (the "SM-T725" tablet noted in the csproj; arm64 is the target that counts, x64 is emulator-only and unvalidated).
- Debug build produces an auto-signed (debug keystore) APK: `bin/Debug/net11.0-android/android-arm64/dev.horizon.testing-Signed.apk`.
  `EmbedAssembliesIntoApk=true`, `AndroidPackageFormat=apk` so `adb install` alone works (no fast deployment). Release signing: unclear / not configured in these projects.
- Content packaging (`Horizon.Testing.Android.csproj`): `MonoAndroidAssetsPrefix=ApkAssets` trick so assets keep `Assets/...` and
  `shaders/...` logical paths; engine `Assets/{global,fonts,uix}` + `shaders/**` (including `shaders/packed/*.hzshaders`) + Testing's `Assets/**` and `shaders/**` are linked as `AndroidAsset`.
  Target `HorizonCheckContent` (BeforeTargets PrepareForBuild) **errors** if (a) any asset path has a segment beginning with `_` or `.`
  (aapt2 silently drops them) or (b) `Horizon/shaders/packed/*.hzshaders` is missing.
- Release is JIT CoreCLR (`AndroidLinkMode=None`, arm64 only). NativeAOT path: `dotnet publish -c Release -r android-arm64 -p:PublishAot=true` (comment says Fighter2D.Android does it the same way).
- Verify pack in an APK: `scripts/verify-apk-shaders.ps1 -Apk <path>` (checks canonical pack contains post shaders
  copy/cas/upscale_nearest/gsr1/fsr1_easu/fsr1_rcas/crt/crt_shrink and two required "tablet key" hashes, then compares to the one embedded pack; uses `Horizon.Shaders.Build/bin/Release/.../Horizon.Shaders.Build.exe info`).

### Horizon.Android.Probe
`Horizon.Android.Probe/` = 3 files (`MainActivity.cs`, `VulkanProbe.cs`) + README. Independent of the engine: loads Vulkan via
Silk.NET.Vulkan 2.23.0, creates an instance, enumerates GPUs, reports on screen, logcat tag `HorizonProbe`, and
`files/probe.txt` (must contain `PROBE PASSED`; missing file == failed run). Application id `dev.horizon.androidprobe`. Only
this project is built in CI (job `android-probe`, ubuntu). It proves loader only, not rendering.

## 5. Horizon.Platform.iOS

Directory: `Horizon.Platform.iOS/` (twin of Android; `README.md` is the best source). Not in any solution; builds only on
a Mac with Xcode 27 + the .NET 11 iOS workload (see memory note: iOS builds happen on the MacBook).
- `Hosting/HorizonAppDelegate.cs` (derive, override `MakeFirstScene`), `HorizonSceneDelegate.cs` (Info.plist needs a
  `UIApplicationSceneManifest` pointing at it), `HorizonViewController`, `HorizonMetalView` (CAMetalLayer), `IosHost`, `IosPlatform`,
  `IosSession` (+`.Surface.cs`; process singleton `IosSession.Current`, one engine at a time, engine made first time the view lays out with a size).
- `Bootstrap/IosBootstrap.cs`, `IosLogger.cs` (console + `Documents/debug.log`), `Content/IosContent.cs` (bundle as content),
  `Storage/IosStorage.cs` (Application Support + Caches), `Surface/IosSurface.cs` (`VK_KHR_surface`, `VK_EXT_metal_surface` via MoltenVK).
- `Input/IosPointers` (touch/Pencil/trackpad), `IosKeys` (HID usage), `IosInput` (soft keyboard via UIKeyInput), `IosGamepads` (README says "Controllers aren't read yet" but a 191-line `IosGamepads.cs` exists; status unclear).
- `Audio/IosAudioOutput.cs` (AVAudioEngine one source node, ambient session).
- Lifecycle: background => detach swapchain + surface (Metal kills GPU use in background); resign active => pause audio, release touches/keys.
- Settings: `Documents/horizon.env` then process env (simulator fills from `SIMCTL_CHILD_HORIZON_*`). `HORIZON_SHADERS` always `packed`.
- README's "still to do": UIScene lifecycle, GameController pads, audio interruptions/route changes, AOT release build, device signing.

### Horizon.Testing.iOS build (from csproj + README)
- On Mac: `git submodule update --init` then `dotnet build Horizon.Testing.iOS/Horizon.Testing.iOS.csproj -c Debug`.
  **Never pass `-r`/global RuntimeIdentifier**: it reaches Horizon.HIDL (publishes AOT) and fails NETSDK1203. The project picks
  `iossimulator-arm64` itself; `-p:HorizonDevice=true` switches to `ios-arm64`.
- `PublishAot=true` (iOS has no JIT), `Registrar=managed-static`, `InvariantGlobalization=false` (invariant mode breaks the rc.2 NativeAOT link on `_GlobalizationNative_` exports), `ValidateXcodeVersion=false` (workload wants Xcode 27.0, Mac has 27.3 beta; remove when they agree).
- Content as `BundleResource` at `.app` root; `HorizonCheckContent` target errors if `Horizon/shaders/packed/*.hzshaders` is absent.
- Run: `xcrun simctl boot "iPhone 17 Pro"; xcrun simctl install booted <.app>; SIMCTL_CHILD_HORIZON_TESTING_SCENE=selector xcrun simctl launch --console-pty --terminate-running-process booted dev.horizon.testing`.
  Outputs in app container Documents: `debug.log`, `screenshots/`, `checks/full-report.txt`.
- Compile-only check from Windows: `dotnet msbuild Horizon.Testing.iOS/Horizon.Testing.iOS.csproj -restore -t:Compile` (linking needs the Mac).
- Terminal example reports "not here" on iOS (no process spawn).

## 6. Shader packs (needed by every mobile build)

Phones have no Slang compiler, so shaders ship as a prebuilt pack `Horizon/shaders/packed/horizon.hzshaders`.
- Builder: `Horizon.Shaders.Build/Program.cs` (223 lines). Commands: `build|verify|check|refresh|info` with `--manifest --content --out/--pack [--capacity n]`. Exit 0 ok, 1 bad manifest/pack, 2 bad usage. Uses the engine's own `ShaderCompiler`.
- Manifest: `Horizon/shaders.manifest`, recorded by running everything with `HORIZON_SHADER_MANIFEST=<path>` set.
- Runtime selection: `HORIZON_SHADERS=packed` (never falls back to cache/compiler), `HORIZON_SHADER_CACHE=off` to recompile.
- Workflow lives in `tasks.hor` automations (see section 9). Tests: `Horizon.Tests/Shaders/*` (`ShaderManifestTests`,
  `ShaderPackBuilderTests`, `ShaderPackFormatTests`, `ShaderPackUpscalerTests`, `ShaderPackedModeTests`, `ShaderSourceIdentityTests`, helper `ShaderTestFiles.cs`). `ShaderPackBuilderTests` compile "for real with Slang" (`tasks.hor` `test_shaders` comment).

## 7. Horizon.Terminal

A terminal **emulator** library (UI component), split out of the engine so games don't carry it. Namespace is still
`Horizon.UI.Components` (RootNamespace override; "only the assembly is new"). The pseudo-terminal itself stays in the core:
`Horizon/Core/Processes/PseudoTerminal.cs` (abstract; `.Windows.cs` = ConPTY, Win10 1809+; `.Unix.cs` = posix_openpt; `.HandedOver.cs`).
On Android a pty works from API 34 (bionic `posix_spawn_file_actions_addchdir_np`), running `/system/bin/sh`.
Wiring: bytes out of `PseudoTerminal.Output` (reader thread) -> queued to UI thread -> `VtParser` -> `TerminalScreen` -> `TerminalView` draws.

Core types (all `Horizon.Terminal/`):
- `VtParser.cs` (`IVtHandler`, `VtParams`) - escape-sequence state machine incl. colon sub-params.
- `TerminalScreen.cs` (1177 lines) - grid of `TerminalCell` (`TerminalCell.cs`), scrollback (default 10000), modes
  (alt screen, bracketed paste, mouse, origin, autowrap), OSC title/hyperlinks (OSC 8), reflowing resize; `Responses` = bytes to send back to the process.
- `TerminalView.cs` + partials (`.Blocks`, `.Completion`, `.Editor`, `.Line`, `.Minimap`, `.Output`, `.Pane`, `.ScrollBar`) - UI component (kind name `terminal`). `Start(commandLine, folder, env)` spawns a pty via `PseudoTerminal.Start(... PseudoTerminal.DefaultShell() ...)` (`TerminalView.cs:206`); `Attach(PseudoTerminal)`; `Start(ITerminalProgram)` runs a process-less program.
- `TerminalPanel.cs` (tabs: `TerminalTab` holds a `SplitTree<TerminalView>` from `SplitTree.cs`), `.Find.cs`, `.Overflow.cs`. Kind `terminal_panel`.
- `TerminalBlocks.cs` (command blocks from shell integration), `TerminalLine/LineCompletion/CommandLookup`, `TerminalKeys.cs`, `TerminalProfile.cs`, `TerminalOutputHighlighter.cs`.
- `TerminalKinds.cs` - `[ModuleInitializer]` registers `terminal`/`terminal_panel` UI kinds and settings help; `TerminalKinds.Register()` explicit.
- `Hosting/TerminalHost.cs` (+`.Api/.Blocks/.Tui/.Watching`) - the application-facing host: `new TerminalHost(panel, TerminalHostOptions)`; options carry `Name` ("dusk"/"hex"), plugin folders, keys files, settings/history files, `Say/Notify`, `ITerminalHostUi`, `Api` extra script functions, `Problem` callback (matchers parse compiler output), `ProgramFor` (embedded programs). Static `EnginePlugins`/`EngineKeys` = `AppContext.BaseDirectory/Assets/terminal/...`.
  - `ITerminalHostUi` (`Hosting/ITerminalHostUi.cs`): `Palette, Choose, AskText, OpenFile, ShowSettings, OpenInDawn` - the program (Hex or Dusk) implements these.
  - `ITerminalProgram` (`Hosting/ITerminalProgram.cs`): `Attach, Input(string bytes), Resize, Update(dt), IsRunning, Name` - used for the HIDL REPL (`TerminalHidlRepl.cs`) and HIDL-written TUIs (`TerminalTui.cs`, `Assets/terminal/tui/*.hor`: files, git, processes, repl, settings, widgets).
  - `TerminalPlugin.cs` + `Assets/terminal/plugins/{core,profiles}/plugin.hor`, `Assets/terminal/keys.hor` - plugins/keys are **HIDL scripts**.
  - Shell integration scripts `Assets/terminal/shell/dusk.{bash,fish,ps1,zsh}`, `dawn.cmd`, `tui.cmd`, `zsh/.z*` emit marks for blocks/cwd. Copied to output via `None Update ... CopyToOutputDirectory` in the csproj (a new asset type needs a glob there).
  - `TerminalProblems*.cs` (problem matchers), `TerminalLinks.cs`, `TerminalFiles*.cs`, `TerminalSettings*.cs`, `TerminalSessions.cs`, `TerminalHistory.cs`, `TerminalShellHistory.cs`, `TerminalIntegration.cs`.
- `Completion/` - `TerminalToolCompletion`, `TerminalDotnetCompletion`, `TerminalHelpParser` (parses `--help` output), `TerminalToolRunner`, `TerminalGitValues`.
- `InternalsVisibleTo`: Horizon.Tests, Horizon.Testing.
- Consumers named in comments: Dusk (stray `dusk/` checkout), Hex's terminal page, and `Horizon.Testing`'s `TerminalExample`.

Tests: `Horizon.Tests/UI/Terminal*Tests.cs` (~25 files: Screen, Host, Tui, LineEditor, Find, Links, Profiles, ...) plus `Horizon.Tests/Core/PseudoTerminalTests.cs`,
`UnixPseudoTerminalTests.cs`, `ShellIntegrationTests.cs`, `RealProgramsTests.cs`.

## 8. Horizon.Tooling

"What's for an editor and not for a game, out of the engine" (csproj comment). Namespace `Horizon.UI.Scripting`. Two clients, no UI:

Language server client (`Horizon.Tooling/LanguageServers/`):
- `JsonRpcConnection.cs` - JSON-RPC over streams (`Request`, `Notify`, `OnNotification`, `OnRequest`, `OnRequestLater`, `Closed`); throws `RemoteException(code,msg)`.
- `LanguageServers.cs` - static registry. `Register(name, command, args, languageId, extensions, settings?)` (e.g. csharp-ls for .cs, pyright for .py per the doc comment); `RegistrationFor(path)`; `For(path, rootDirectory, selectedSolution?, ...)` lazily starts a server per (registration, root, solution) and returns null if the command is not on the machine (callers must tolerate null); `Running`, `Stop`, `StopUnder(folder)`, `StopAll()`, `IsAvailable(cmd)`, `SolutionIn(folder)`.
- `LanguageServer.cs` (+ ~15 partials: Navigation, Edits, Semantic, Hierarchy, Synchronization, Questions, CodeLens, Folding, Highlights, Selection, TypeHierarchy, OnTypeFormatting, DiagnosticLocations, EditRequest, Sources, HeaderSource). `LanguageServer.Start(name, command, args, languageId, root, settings)`, `Over(name, languageId, root, input, output)` (stream pair, for tests); `Open/Change/Close(path,text)`, `FilesChanged`, `Complete(path,line,char,start,trigger,again)`, `TryTake(out path, out diagnostics)` (polled by the UI), `IsRunning/IsReady/Failure/ErrorOutput`, `Woken` callback. Output types come from `Horizon.UI` (`UICompletion`, `UICodeDiagnostic`).
- `LanguageServerProgram.cs` (`Locate` finds command on PATH incl. PATHEXT), `LanguageServerSemantics.cs`, `LanguageServerOutput.cs`.

Debug adapter client (`Horizon.Tooling/Debugging/`):
- `DebugAdapterConnection.cs` - DAP framing (`Content-Length` headers), `Request(command, args)`, `OnEvent`.
- `DebugSession.cs` (+ partials Breakpoints, SourceBreakpoints, DataBreakpoints, InstructionBreakpoints, Functions, Exceptions, Goto, RunTo, Memory, Disassembly, Modules, Threads, Sources, Completions). `DebugSession.Start(command, args)` / `Over(input, output)`; `Launch(program, args, workdir, breakpointsByFile, kind = CORECLR|PYTHON "debugpy", ...)`, `Attach(pid, ...)`, `Continue/StepOver/StepIn/StepOut/Pause/Stop`, `Scopes/Variables/Evaluate/TryEvaluate/SetVariable`, event queue via `TryTake(out DebugEvent)`, `State` (`DebugState`), `JustMyCode`. Records: `DebugFrame`, `DebugScope`, `DebugVariable`, `DebugEvaluation`, `DebugEvent`/`DebugEventKind`, `DebugException`.
- Real adapter expected: netcoredbg (tests look next to Hex's exe at `hex/Hex/bin/Debug/net11.0/netcoredbg/netcoredbg.exe`, on PATH, or `HEX_NETCOREDBG`).

Tests: `Horizon.Tests/Scripting/DebugSession*Tests.cs` (hand-played adapter over pipes, always runs; netcoredbg cases only when it's installed),
`CallHierarchyErrorTests`, `CodeActionResolutionTests`, `CompletionDocumentationTests`, `DiagnosticLocationTests`, `DocumentHighlightTests`.

Hex consumes this (not in scope) - treat public API here as Hex-facing.

## 9. Horizon.Testing: the examples and self-check harness (custom, not xUnit)

`Horizon.Testing/` is an **Exe** (WinExe-less console) that opens a real window with the real engine. Two things in one
(`Program.cs` header is a good tutorial):
- **Examples** (`Examples/{FirstSteps,Game,Lighting,Showcase,Internals}`): ordinary `Scene`s used as tutorials, listed in `Host/TestCatalog.cs`.
- **Checks** (`Checks/`): scenes that drive themselves and report pass/fail; catalog `Level == TestLevel.Checks`.

It needs a GPU and a display. It is NOT run in CI (CI comment: "Nothing here needs a GPU", it only runs `Horizon.Tests`).

Key types (`Horizon.Testing/Host/`):
- `TestHost : GameObject` (`TestHost.cs`) - added on the **engine** (`engine.AddEntity`) so it outlives scene swaps. `Start(test)`, `ShowSelector()`, `RunChecks(exit?)`, `ExitAfterChecks`, `CheckTimeout`, `ChecksDone` event, `RememberRenderScale(path)`. `JudgeCheck()` polls `ISelfCheck.Finished` each frame, aggregates into a `CheckReport`, sets `Environment.ExitCode = report.Failed` and `Engine.Exit()` (`TestHost.cs:~440-466`).
- `TestCatalog.cs` - `Tests` array; `Numbered(...)`; `Find(idOrAlias)`; each entry `new TestDefinition(Id, Level, Name, Summary, Shows[], Source, Func<Scene> Create) { Aliases, Unsupported }`. Factory not instance on purpose (leaving a scene disposes its GPU stuff).
- `TestDefinition.cs` - `TestLevel` enum (FirstSteps, MakingAGame, LightAndEffects, AllTogether, UnderTheHood, Checks); `ISelfCheck { Finished; Failed; Count; Skipped }`.
- `CheckReport.cs` - `CheckOutcome` records -> `ToText()` and `WriteJson(Stream)`. `CheckLog.cs` collects failure/skip messages per scene.
- `TestPlatform.Desktop.cs` - static `TestPlatform { NAME, Extra, Terminal, TerminalUnsupported, HowToStart }`. **This file is the platform seam of the harness**: `Horizon.Testing.Android` and `.iOS` exclude it and supply their own `TestPlatform` (so no `#if` in the catalog). A new platform-varying thing goes in all three.
- `TestSelectorScene`, `TestControl`/`ITestControls` (keys help on screen), `TouchKey`, `UnsupportedScene` (row shown, explains why), `HostStyle`.
- `Checks/TestChecks.cs` - tiny checklist: `Check(name, Func<bool>)` (exceptions count as failed), `Skip(name, why)`, `Report()`, `Describe()`. `Checks/UISelfTest.cs` - scripted fake pointer for UI checks (moves/clicks/drags/`Check`/`WaitFor`/`Settle`; goes by updates, not clock; `HORIZON_CHECK_PACE=8` slows it for watching).
- Checks present (catalog ids): `check-tilemap`, `check-physics`, `check-ui`, `check-ui-layout`, `check-ui-controls`, `check-ui-navigation`, `check-ui-skins`, `check-ui-parts`, `check-ui-text`, `check-surface`, `check-render-scale`, `check-render-scale-quality`, `check-crt-resolution` (+ more; see `TestCatalog.cs:164-230`, particles check in `Checks/ParticlesCheck.cs`).
- Screenshots: not a pixel-diff test. `HORIZON_SCREENSHOT=file.png@3` (optionally `@3+8x0.5`) makes `GameEngine` (`Horizon/Engine/GameEngine.cs:94`) save a frame at 3 s; for eyeballing and the AI testers, no golden-image comparison found. `RenderScaleCheck`/`RenderScaleQualityCheck`/`CasReferenceTests` assert numerically.
- Scripted input: `HORIZON_INPUT_SCRIPT=file` (`Horizon/Input/InputScript.cs`), lines like `2.0 tap A`, `3.0 hold DPadRight 1.5`, `2.0 tap key:J`, `2.0 chord Ctrl+Shift+G`, `8 quit`. Used to drive examples headless-ish and by the shader recording.

### Commands (desktop; run from repo root)
```
dotnet run --project Horizon.Testing                      # selector
dotnet run --project Horizon.Testing -- tweens            # one example by id/alias
dotnet run --project Horizon.Testing -- --checks          # all checks, exit code = number failed
dotnet run --project Horizon.Testing --no-build -- --checks   # tasks.hor "checks"
```
`Program.cs` does `Directory.SetCurrentDirectory(AppContext.BaseDirectory)` because shaders/skins/Assets are copied next to the exe; `--checks` doubles `UpdatesPerSecond` to 240 and turns VSync off, and skips remembering skin/scale. Must run from a real bin folder (`Horizon.Testing/bin/Debug/net11.0`); copies miss assets (see memory note on running suites from the real bin).
Env vars: `HORIZON_AUDIO=off`, `HORIZON_LOG_LOOPS=5`, `HORIZON_LOG_ALLOCATIONS=5`, `HORIZON_SHADER_CACHE=off`, `HORIZON_INPUT_SCRIPT`, `HORIZON_SCREENSHOT`, `HORIZON_SHADERS=packed`, `HORIZON_SHADER_MANIFEST`.
In-app keys: Esc menu, PgUp/PgDn prev/next, F1 key list, F3 perf overlay, F4 render scale step, F5 render scale picker, F6/F7 skin, F8 pause, F9 step tick, F10 Skyline debugger (Debug builds only).

Assets: `Horizon.Testing/Assets/examples/**` (maps `.tmx/.tsx`, art, `.hor` UI layouts), `Horizon.Testing/shaders/**`; generated by Python scripts in `Horizon.Testing/Art/make_*.py` (never at scene start). Csproj copies `shaders\**` and `Assets\**` with `PreserveNewest`.

### Horizon.Testing.Android / .iOS
Same sources compiled via `<Compile Include="$(_HorizonTestingSources)**\*.cs" ... Link="Linked\..."/>` minus `Program.cs` and `Host/TestPlatform.Desktop.cs`. `AssemblyName=Horizon.Testing` on purpose so the engine/UI `InternalsVisibleTo Horizon.Testing` still applies. Android also has `TestingActivity.cs` (`Activity` with `HORIZON_TESTING_SCENE` extra), `TestingScenes.cs`, `Checks/DeviceChecks*.cs` (extra catalog row `check-device`: APK content hashes, storage commit/abandon, AAudio callback, SurfaceView surface lifecycle, GPU drawing; runs on a pool thread). Android settings: `HORIZON_TESTING_SCENE` (`selector` | `all-checks` | `checks` | any id), `HORIZON_TESTING_QUIT=on`, `HORIZON_TESTING_CHECK_TIMEOUT` (default 300 s). Results pulled by `adb pull .../files/checks/{report,full-report}.{json,txt}` (`Horizon.Testing.Android/README.md`). `all-checks` = full catalog report; `checks` = device checks only. Use a real device, not an emulator, for results. `Horizon.Testing.Android` README also has the Vulkan validation-layer procedure (`HORIZON_VULKAN_VALIDATION=on`).

## 10. Horizon.Tests (xUnit)

`Horizon.Tests/Horizon.Tests.csproj`: xunit 2.9.3, Microsoft.NET.Test.Sdk 18.10.1, xunit.runner.visualstudio 3.1.5, global `<Using Include="Xunit" />`,
`IsTestProject`, `AllowUnsafeBlocks`. References Horizon, Platform.Desktop, UI, Tooling, Terminal, HIDL. **No GPU, no window** (CI states so).
Test classes are public classes with `[Fact]`; names are snake_case sentences (`A_host_without_a_window_is_a_valid_window_manager`).

Layout (`Horizon.Tests/`): `Audio/` (mixer, output), `Content/` (decode, retention, sources, packaged assets; `MemoryContent.cs` = in-memory `IContentSource`), `Core/` (loop, clocks, input, pty, shell integration, work scheduler, allocation, HIDL-adjacent, ~30 files), `Physics/` (character controller, one-way platforms, gravity), `Platform/` (`BoundaryTests`, `DispatcherTests`, `HostTests`, `StorageTests`, `SurfaceTests`), `Rendering/` (render scale, upscaler, CAS reference, mesh, voxel tree, tilemap rooms, occlusion, path tracing), `Scripting/` (HIDL language + debug/LSP tests), `Shaders/` (pack format/builder/manifest), `UI/` (~100 files: code editor, settings, terminal, theme, layout, text, scripted layouts). `Horizon.Tests/TestResults/` is a generated folder.

Patterns worth copying:
- **Fake platform host** for engine tests without a window: `Horizon.Tests/Platform/HostTests.cs:248` `FakeHost : IPlatformHost` (+ `FakeInput`, `FakeOutput`); build `new WindowManager(config, host)`, no GPU. Create engine with `GameEngineConfiguration { Platform = ... }`.
- **Fake debug adapter over pipes**: `Horizon.Tests/Scripting/DebugSessionTests.cs` (`Write(stream, JsonObject)` / `ReadRequest` with a 15 s timeout so a missing request fails fast instead of hanging CI).
- **Real-process tests serialized**: `Core/RealProcessesCollection.cs` `[CollectionDefinition(NAME, DisableParallelization = true)]`; mark classes `[Collection(RealProcessesCollection.NAME)]` when they spawn shells/ptys.
- **Debug-only tests**: files wrapped in `#if DEBUG` (`Core/DebuggerMakerTests.cs`, `Core/InspectableTests.cs`) because the Skyline debugger exists only in Debug builds; hence CI runs Debug and Release.
- **Reflection boundary tests**: `Platform/BoundaryTests.cs`.
- Internals access: Desktop/Terminal/Tooling declare `InternalsVisibleTo Horizon.Tests`; Horizon and UI do too (inferred; Horizon.Testing relies on it).

### Commands
```
dotnet test Horizon.Tests/Horizon.Tests.csproj
dotnet test Horizon.Tests/Horizon.Tests.csproj --filter "FullyQualifiedName~Horizon.Tests.Shaders"     # tasks.hor test_shaders
dotnet test Horizon.Tests --no-build --configuration Release --verbosity normal --blame-hang-timeout 3m --blame-hang-dump-type none   # CI
```
Self-test suite note: run from the real build output, not a copy (copies lose `hex-extensions`; memory note), and avoid heredocs that mangle escapes.

## 11. CI (`.github/workflows/`)

`ci.yml` ("CI"): triggers on push to `master|development|platform`, PR to `master|development`, manual.
- Job `android-probe` (ubuntu): setup-dotnet 11.0.x preview, `dotnet workload install android`, JDK 21 (microsoft), `InstallAndroidDependencies` target, then build `Horizon.Android.Probe` Debug. This is the only automated mobile build; Platform.Android, Testing.Android and everything iOS are NOT built in CI.
- Job `test`: matrix `{windows,ubuntu,macos}-latest x {Debug,Release}`, `fail-fast: false`, checkout with `submodules: recursive` (HIDL), NuGet cache keyed on csproj hashes, `dotnet build Horizon.sln --configuration X`, then `dotnet test Horizon.Tests --no-build --configuration X --blame-hang-timeout 3m --blame-hang-dump-type none`.
`build.yml` ("Build"): on push to `master` and tags `v*`: publishes `AutoVoxel` and `Horizon.Testing` self-contained Release for `win-x64`, `linux-x64`, `osx-arm64`, zips them, uploads artifacts; on a `v*` tag, a `release` job runs `gh release create ... --generate-notes`. Has `contents: write`.
`qodana.yaml` - config only (see section 1).
Note: CI never runs `Horizon.Testing --checks` (needs GPU); those are run by hand / via Hex tasks.

## 12. `tasks.hor` (Hex task file for the engine repo)

HIDL script (`let tasks = {...}`, `let automations = {...}`) read by Hex (command line, `folder`, `group`, `default`, `depends_on`, `terminal`).
- Tasks: `build` (`dotnet build Horizon.Shaders.Build/...`, default build; depends on `build_solution`), `build_solution` (`dotnet build Horizon.sln`), `test` (`dotnet test Horizon.Tests/Horizon.Tests.csproj`), `test_shaders` (filter `FullyQualifiedName~Horizon.Tests.Shaders`), `checks` (`dotnet run --project Horizon.Testing/Horizon.Testing.csproj --no-build -- --checks`, depends on `build`), `run` (examples selector). Note: `checks` and `run` list `depends_on: "build"`.
- Automations (palette, "Automation: <title>"): `record_shader_manifest` (bash; builds Testing, runs `--checks`, then each example id in the `EXAMPLES` list with `HORIZON_INPUT_SCRIPT` of `6 quit` and `HORIZON_AUDIO=off`, optionally a Fighter2D `--map japan --windowed` match if `../Fighter2D` exists, then builder `check`), `build_shader_pack` (HIDL function `build_pack(hex)`: build builder + Testing, manifest `check`, `build` into `Horizon/shaders/packed/horizon.hzshaders`, build again to `Horizon.Shaders.Build/obj/again.hzshaders`, compare `git hash-object --no-filters` of both, then `verify`), `check_shader_manifest`, `refresh_shader_manifest`, `packed_checks` (checks with `HORIZON_SHADERS=packed HORIZON_SHADER_CACHE=off`, the way a phone runs).
- Manifest is intentionally outside `shaders/` so it isn't copied into games; the pack is inside so every game ships it.
- Adding an example id to `TestCatalog` means also adding it to the `EXAMPLES` array at `tasks.hor:~17` or the shader manifest will not record its shaders.

## 13. AutoVoxel

A game/engine stress example, in the solution and published by `build.yml`. Per its `Program.cs` doc: "Back from the dead (it was a Minecraft clone on the OpenGL engine in 2024, meshed chunks and all) as a path traced one, the whole world in the card as a sparse 64-tree and drawn at once."
- Entry: `AutoVoxel/Program.cs` - `DesktopPlatform.Install()`, `GameSettings.Load()`, `new GameEngine(WindowManagerConfiguration.Default1600x900 with {...})`, `PerformanceOverlay`, `engine.Run(firstScene)`.
- CLI: `AutoVoxel` (menu); `--play <size> [seed]` (new world, default seed 1337); `--continue` (last save via `WorldFiles.Latest()`); `--tour [folder]` (screenshot tour, `GameScene.TourFolder`); `--size 1920x1080`.
- Layout: `Blocks/Blocks.cs` (block table), `Generation/{Noise,WorldGenerator}.cs` (696 lines), `Gameplay/{Player,Hud,Tour,GameSettings,WorldFiles}.cs`, `Scenes/{MainMenu,Loading,Game}Scene.cs` + `OptionsPanel.cs`, `Art/make_atlas.py` -> `content/atlas.png` (copied to output by csproj).
- The voxel tree itself is in the core engine (`Horizon.Tests/Rendering/VoxelTreeTests.cs`, `PathTracingTests.cs`), not here. `ServerGarbageCollection=false`, `ConcurrentGarbageCollection=true`, `TieredPGO=true`.
- No tests of its own.

## 14. Recipes

### Add an xUnit test (headless)
1. Create `Horizon.Tests/<Area>/FooTests.cs`: `namespace Horizon.Tests.<Area>; public class FooTests { [Fact] public void Does_the_thing() { ... } }` (Xunit using is global).
2. Need internals? Check that the target project has `<InternalsVisibleTo Include="Horizon.Tests" />`.
3. Engine without a window: use the `FakeHost` pattern (`Horizon.Tests/Platform/HostTests.cs:248`) or `GameEngineConfiguration.Platform`.
4. Real processes/ptys: add `[Collection(RealProcessesCollection.NAME)]`.
5. Debug-only code (Skyline/Inspectable): wrap file in `#if DEBUG`.
6. Run: `dotnet test Horizon.Tests --filter "FullyQualifiedName~Horizon.Tests.Foo"`.

### Add a Horizon.Testing example or check
1. Write the scene in `Horizon.Testing/Examples/<Level>/XExample.cs` (a plain `Scene`) or `Checks/XCheck.cs`. For a check, implement `ISelfCheck` (`Finished/Failed/Count/Skipped`), use `TestChecks` (non-pointer) or `UISelfTest` (UI), call `CheckLog` via `TestChecks.Check` so failures reach the report.
2. Add a `new("id", TestLevel.X, "Name", "Summary", [shows], "Examples/.../XExample.cs", () => new XExample())` line in `Horizon.Testing/Host/TestCatalog.cs` in the right position (order = lesson order = PgUp/PgDn order; checks are unnumbered).
3. If it reacts to keys/mouse, implement `ITestControls`. Art/maps go in `Horizon.Testing/Assets/examples/...` (made by a script in `Art/`, never at scene start). Avoid file/folder names starting with `_` or `.` (aapt2 drops them from the APK; build errors).
4. If it can't run on a platform, set `Unsupported` or use `TestPlatform` (not `#if`) so it appears in the selector with the reason.
5. Add its id to `EXAMPLES` in `tasks.hor` so shader recording sees it, then re-record/refresh the shader manifest and rebuild the pack, or `packed_checks`/phones fail with missing shaders.
6. Run: `dotnet run --project Horizon.Testing -- <id>`.

### Add a platform service (pattern from real code)
Example: audio. Contract `Horizon/Platform/IAudioOutput.cs`; desktop impl `Horizon.Platform.Desktop/SdlAudioOutput.cs`, returned by both `DesktopPlatform.CreateAudioOutput()` and `DesktopHost.CreateAudioOutput()`; Android `Audio/AAudioOutput.cs` returned via `AndroidPlatform.CreateAudioOutput()` -> `session.TrackAudio(...)`; iOS `Audio/IosAudioOutput.cs`. Steps for a new service:
1. Define the interface in `Horizon/Platform/` (no Silk types in its public signature, `BoundaryTests` enforces it).
2. Add a `PlatformFeatures` flag if UI must know whether it exists.
3. Implement in each head, expose through `IPlatformHost` (or via a static like `PlatformStorage.Current`/`ContentFiles.Source` set by the head's bootstrap when it must exist before the engine).
4. Add a fake in `Horizon.Tests/Platform/HostTests.cs` (`FakeHost`) and a test; add a device check in `Horizon.Testing.Android/Checks/DeviceChecks.cs` if hardware-specific.
5. Mobile heads must not reference desktop types; desktop must not reference mobile ones.

### Add a new platform head
Mirror Android/iOS: `IPlatform` + `IPlatformHost` + `IPlatformSurface` + `IPlatformInput`, session that owns the engine, bootstrap that sets `PlatformStorage.Current` + `ContentFiles.Source` + logger + env, a `Horizon.Testing.<X>` app linking Testing sources and providing `Host/TestPlatform.<X>.cs`. Keep out of `Horizon.sln`.

## 15. Gotchas and invariants

- Mobile projects are out of `Horizon.sln`; a plain `dotnet build Horizon.sln` never validates them. CI builds only the Android probe. iOS code can be syntax-checked on Windows with `dotnet msbuild ... -t:Compile` but really builds on the Mac.
- Never pass `-r` when building iOS (NETSDK1203 from Horizon.HIDL AOT); `InvariantGlobalization` must stay false in the iOS test app.
- `HORIZON_SHADERS` is forced to `packed` on Android and iOS. Missing `Horizon/shaders/packed/*.hzshaders` is a build error for both test apps. Stale manifest => `Horizon.Shaders.Build check` fails; run `refresh` for intentional shader edits, `record` when new shaders/programs appear.
- Android settings are read once per process; `adb shell am force-stop dev.horizon.testing` before changing extras.
- aapt2 drops files/dirs starting with `_` or `.` silently; the csproj turns this into an error.
- Android Testing assembly name is `Horizon.Testing` on purpose (InternalsVisibleTo). Namespace `Horizon.Testing.Android` collides with `Android.*` implicit usings, so csproj does `<Using Remove="Android.App" .../>`; in linked files qualify with `global::Android...`. Same for iOS (UIKit, Foundation, CoreGraphics removed).
- Namespace != assembly: `Horizon.Terminal` types live in `Horizon.UI.Components`, `Horizon.Tooling` in `Horizon.UI.Scripting` (moved out of the engine, namespaces kept). Search by type, not by assembly name.
- macOS: surface creation must run on the main thread; Silk GLFW has no Vulkan on mac; default windowing = SDL off Windows. `HORIZON_WINDOWING` overrides.
- Desktop gamepad: `GlfwInputWithoutGamepads` used when `ReadsGamepads` is false and not SDL, because Silk's GLFW context enumerates controllers at creation and a broken driver can stall it for a minute (comment in `DesktopWindow.cs:~147`).
- Check scenes run by *update count* not clock (`UISelfTest`); `--checks` runs at 240 updates/s with VSync off; going faster than frames breaks skin stitching (comment in `Program.cs`).
- Examples are factories (`Func<Scene>`) so leaving an example frees GPU state; constructors must not touch the GPU (Initialize runs on the render thread).
- Real-process pty tests are flaky under parallelism: keep them in the serialized collection. DAP tests have a 15 s read timeout because Linux/macOS runners once hung silently.
- `Horizon.Tests` depends on the Debug/Release split: Skyline-related tests only exist in Debug.
- iOS README says controllers are unread but `IosGamepads.cs` exists: verify before relying on either.
- Version 1.5.0 applies to projects whose *name* starts with `Horizon`.
- `Horizon.Testing.csproj.user` and `.DS_Store` files sit in the Testing folder (not meaningful).
- `docs` mentioned in comments but not present in checkout: `notes/android-plan.md`, `android-host-results.md`, workspace `artifacts/android-platform-baseline/*` (unclear where they live).

## 16. Where to look for X

| Need | Look at |
|---|---|
| Platform contracts | `Horizon/Platform/*.cs`, esp. `IPlatformHost.cs`, `PlatformFeatures.cs`, `PlatformInstallation.cs` |
| Desktop window / SDL vs GLFW | `Horizon.Platform.Desktop/Window/DesktopWindow.cs`, `DesktopWindow.Sdl.cs:15` |
| Desktop Vulkan surface | `Horizon.Platform.Desktop/SilkSurface.cs`, `Win32SurfaceAhead.cs` |
| Android lifecycle / surface loss | `Horizon.Platform.Android/Hosting/AndroidSession*.cs`, `HorizonActivity.cs` |
| Android env settings and driver flags | `Horizon.Platform.Android/Bootstrap/AndroidBootstrap.cs`, `AndroidSettings.cs` |
| APK asset layout | `Horizon.Testing.Android/Horizon.Testing.Android.csproj` (MonoAndroidAssetsPrefix, `HorizonCheckContent`) |
| iOS lifecycle | `Horizon.Platform.iOS/Hosting/IosSession.cs`, `README.md` |
| Audio backends | `SdlAudioOutput.cs`, `Audio/AAudioOutput*.cs`, `Audio/IosAudioOutput.cs`; core `Horizon/Audio/AudioDevice.cs` (`HORIZON_AUDIO`) |
| Shader packs | `Horizon.Shaders.Build/Program.cs`, `tasks.hor`, `Horizon/shaders.manifest`, `scripts/verify-apk-shaders.ps1`, `Horizon.Tests/Shaders/` |
| Pty | `Horizon/Core/Processes/PseudoTerminal*.cs` |
| VT parsing / screen | `Horizon.Terminal/VtParser.cs`, `TerminalScreen.cs` |
| Terminal embedding in an app | `Horizon.Terminal/Hosting/TerminalHost.cs`, `ITerminalHostUi.cs`, `ITerminalProgram.cs` |
| Terminal plugins/keys/shell scripts | `Horizon.Terminal/Assets/terminal/**` |
| LSP client | `Horizon.Tooling/LanguageServers/LanguageServers.cs`, `LanguageServer.cs` |
| Debugger client | `Horizon.Tooling/Debugging/DebugSession.cs`, `DebugAdapterConnection.cs` |
| Check harness | `Horizon.Testing/Host/TestHost.cs`, `TestCatalog.cs`, `CheckReport.cs`, `Checks/TestChecks.cs`, `Checks/UISelfTest.cs` |
| Per-platform harness differences | `Horizon.Testing/Host/TestPlatform.Desktop.cs`, `Horizon.Testing.Android/Host/TestPlatform.Android.cs`, `Horizon.Testing.iOS/Host/TestPlatform.iOS.cs` |
| Scripted input / screenshots | `Horizon/Input/InputScript.cs`, `Horizon/Engine/GameEngine.cs:94` |
| Fake host for tests | `Horizon.Tests/Platform/HostTests.cs:248` |
| Layering rules enforced by tests | `Horizon.Tests/Platform/BoundaryTests.cs` |
| CI | `.github/workflows/ci.yml`, `build.yml` |
| Task/automation definitions | `tasks.hor` |
| Workspace-wide build props | `../Directory.Build.props`, `Directory.Build.props`, `global.json` |
| AutoVoxel | `AutoVoxel/Program.cs`, `Scenes/GameScene.cs`, `Generation/WorldGenerator.cs` |

## 17. Open questions / unclear

- Whether `Horizon.Shaders.Build` is meant to be in `Horizon.sln` (it is listed; `tasks.hor` comment says it isn't).
- Whether Qodana is actually run anywhere (config exists, no workflow).
- iOS gamepad support status (README vs `IosGamepads.cs`).
- Release signing for Android/iOS (only debug auto-signing documented).
- Location of the referenced `notes/android-plan.md`, `android-host-results.md`.
- Whether Platform.Desktop actually has a dedicated Linux/Wayland path beyond SDL defaults (only `DragArea` is gated; no further verification done).
