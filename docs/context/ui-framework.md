# Horizon.UI: the UI framework

Scope: `horizon/Horizon.UI/` (assembly `Horizon.UI`, namespaces `Horizon.UI`, `.Components`, `.Drawing`, `.Skinning`,
`.Scripting`, `.Diagnostics`, `.Input`). Paths below are relative to `horizon/Horizon.UI/` unless they start with
`hex/`, `Fighter2D/` or `horizon/Horizon.Tests/`. Hex is built on this; the engine core does not reference it
(`Horizon.UI.csproj:4-9` comment). Target `net11.0`, AOT/trim compatible (`IsAotCompatible`), `InvariantGlobalization`.

## 1. Mental model in one page

- **Retained component tree, painted by a per-tick display list.** You build a tree of `UIComponent` objects
  (`Components/UIComponent.cs:51`). They persist; you mutate properties. There is no "draw call" API you invoke
  per frame as a user. But internally each simulation tick the whole visible tree is re-measured, re-arranged and
  re-painted into a flat list of sprite quads (`UIDrawList`), and that list is what the renderer uploads
  (`UICompositor.cs:20-30`, `UICompositor.PaintPending` at ~762). So: retained API, immediate-mode painting
  underneath. A component's `Paint(UIDrawList)` runs every tick unless it opts in to reuse (see section 9).
- **Nothing owns GPU resources.** Components only emit quads. Input, layout and painting run on the **simulation
  thread**; `UICompositor.Render` (render thread) only uploads the latest list and draws it
  (`UICompositor.cs:20-24`, `Render` at 1022). Children arrays are replaced wholesale on mutation under a lock
  (`UIComponent.cs:57-60,284-286`) so another thread may add to the tree while the sim thread walks a snapshot.
- **Y points up.** `Position` is offset from the anchor with +Y up (`UIComponent.cs:107`); the world is Y-up; `UIRect`
  `Min` is bottom-left. Window pixels (screenshots, MCP) are Y-down, converted in Hex's `McpBox`.
- **Layout is anchor/pivot/size/fill, with container-specific arrange.** Not CSS/flexbox. Two passes per tick:
  `MeasureTree` leaves-up, `ArrangeTree` root-down (`UIComponent.cs:327-390`).
- **Skin = art + palette, themes = palette overlays** (`Skinning/`). Components ask the skin for regions by name and
  fall back to flat colours when the skin lacks art.
- **Layouts are HIDL scripts** (`.hor` files starting `#define layout`) that call `compositor.<kind>({...})`;
  C# loads them with `UILayout.Load` and fetches parts by variable name with `layout.Get<T>("name")`.
- **Hierarchy:** `UICompositor` (a `GameComponent`, one per UI; owns skin, input routing, tooltips, focus, draw
  lists) -> `UIModule`s (a self-contained tree + HIDL runtime, e.g. HUD, menu, dialog, editor document) -> `Root`
  `Panel` -> `UIComponent`s.

## 2. Directory map (with key types)

| Path | What is in it |
| --- | --- |
| `UICompositor.cs` (1383 lines) | The runner. `UpdateState` 693, `Capture` 860, `Render` 1022, `PaintPending` 762, `FindAtWindow` 534, `CreateModule` 547, `SetSkin` 613, `SetTheme` 646, `Retained` / `FrameRateLimit` / `PostProcessing` properties |
| `UICompositor.Input.cs` | `RouteKeyboard` 51, `RoutePointer` 447, scroll/tooltip/hover/context routing |
| `UICompositor.Focus.cs`, `.Touch.cs`, `.MiddleScroll.cs` | Focus hand-back for windows, touch/fling/pan gestures, middle-click autoscroll |
| `UIModule.cs`, `.Runtime.cs`, `.Helpers.cs` | Module: `Root`, layers, design size/fit, popup slot, `Layout` ~330, `Paint` ~357, `HitTest`, `ToLocal/ToWorld`. `.Runtime.cs` registers every component kind for HIDL (`SetupRuntime` ~88) |
| `UITweens.cs` | Extension tween helpers (`PopIn`, `SlideIn`, `TweenOpacity`, `TweenOffset`, ...); paired with `UIDecorative` for capped decorative idle ticks |
| `Components/UIComponent*.cs` | Base class split by concern: `.cs` (tree/layout/paint), `.Interaction.cs` (pointer/keys/focus virtuals), `.Hover.cs`, `.Resizing.cs` (drag-edge grips), `.Sliding.cs` (slide in/out layout), `.Scripting.cs` (`DefineScript`/`Expose`/`InvokeScript` + `ScriptCaller` sandboxing) |
| `Components/Containers/` | `Panel`, `StackPanel`, `VirtualStack`, `GridPanel`, `ScrollPanel` (+`.Bar`), `Group`, `Collapsible`, `TabPanel`, `WindowPanel` (+`.Modal/.Motion/.TitleActions`), `TitleBar` (+`.Caption/.TitleActions`), `Divider`, `Spacer`, `Include` |
| `Components/Controls/` | Interactive: `Button`, `ToggleButton`, `TextBox`, `NumberBox`, `Slider`, `Dropdown`, `Selector`, `ListBox` (+`.Choices`), `TreeView` (+8 partials), `Table`, `TabStrip`, `Breadcrumbs`, `ColorPicker`, `CodeEditor` (+~33 partials), `FormPanel` (`UIFormField`), `Settings/*`, `OnScreenKeyboard`, pickers, `ArrangeOverlay`, `DrawingPanel` |
| `Components/Display/` | Non-interactive: `Label`, `Image`, `Symbol`, `Badge`, `ProgressBar`, `Meter`, `Graph`, `Readout`, `SegmentBar`, `Sparks`, `Lights`, `GlowField`, `StatusBar`, `Notifications`, `CodeView`, `UIWorkCards` |
| `Components/Popups/` | `MenuBar`, `MenuPanel` (+`.Keys/.Placing`), `ContextMenu`, `UIDialog`, `UIChooser` (+`.Search/.Spans`), `UICommands`, `UIFileBrowser`/`UIFileDialogs`, `UIScrim`, `UIRecentList`, `UIShortcutText` |
| `Components/Terminal/` | Empty here; the terminal component lives in `horizon/Horizon.Terminal` and registers via `UIModule.AddKind<T>` |
| `Layout/` | `UILayout` (HIDL layout loader/binder), `UIRect`, `UIEdges`, `UIFit`, `OriginExtensions`, `UILayoutOverlay` (debug boxes) |
| `Drawing/` | `UIDrawList` (+ `.Text/.Icons/.Frames/.Fades/.Squiggles/.Reuse`), `UIRenderer` (draw list -> `SpriteBatch`), `UIFont`, `UIRichText` (+ `.Code/.Document`), `UIGlass` (`.Frosted/.Clear/.Optics`)/`UIGlassPass`, `UIInBetween` (tick interpolation), `UIScrollBar`, `UITooltip`, `UIMinimap`, `UIAnimatedIcons`, `UIDecorative`, `UIKeycaps`, `UISymbols` (Codicons) |
| `Skinning/` | `UISkin` (616 lines) + `.Loading`, `UISkins`, `UIPalette` (+`.Derive`), `UITheme`, `UIRegion`, `UISyntaxColors*`, `UITextRole` |
| `Input/` | `UIKeyboard` (char-queue of editing keys), `UIKeys`, `UIShortcuts` (chords), `UINavigator` (gamepad/keyboard nav), `UIPointer`, `UICursor` |
| `Scripting/` | `UIScript` (HIDL value <-> C# conversions), `UIScriptedLayout`/`UIScriptedScreens` (script-owned views, Hex extensions), `UIPropertyHelp`, `ICodeHighlighter`, `TextMateHighlighter` (`CodeLineStart`/`LongLineSpans`), `HidlHighlighter`, `CodeLanguages` (+`.Files`), `Grammars/`, `SemanticOverlay` |
| `Diagnostics/` | `UIStatistics`, `UIPaintProfile`, `PerformanceOverlay`, `PerformanceFace`, `PerformanceSample` |
| `Engine/Scene2D.cs` | A `Scene` that comes with a camera, renderers and `UI` (`UICompositor.ForScreen()`) at line 84 |
| `Engine/Debugging/` | `SkylineDebugger` (menu bar, scene tree, inspector). **Debug builds only**; `csproj` removes `Engine\Debugging\**` from non-Debug configs |
| Tests | `horizon/Horizon.Tests/UI/` (~94 files; e.g. `ChooserInteractionTests.cs`, `InBetweenTests.cs`, `ModuleReplacementTests.cs`) |

## 3. The component tree and layout

### UIComponent (`Components/UIComponent.cs`)
Abstract base. Key members:
- Tree: `Parent`, `Children` (`IReadOnlyList`), `ChildSpan` (alloc-free, protected), `Add<T>` (re-parents, rejects
  cycles), `Insert`, `MoveChild`, `Remove`, `Module` (walks up to the owning `UIModule`; `UIComponent.cs:77-81`),
  `OnParentChanged`.
- Placement: `Anchor` (`Origin`, point of parent), `Pivot` (point of self, defaults to `Anchor`), `Position`
  (offset, +Y up), `Size` (0 on an axis = automatic), `Scale` (multiplies auto size, affects layout), `Fill`
  (`UIFill.Horizontal|Vertical|Both`, stretch to parent content area), `Padding` (`UIEdges`).
- Visual-only transforms (layout ignores them; hit-testing honours them): `VisualOffset`, `VisualScale`, `Opacity`.
  Tweens animate these (section 8).
- State: `Visible`, `Enabled`, `Layer` (string group name; hidden per module via `UIModule.SetLayerVisible`), `ZOffset`
  (>0 painted after everything in the module and hit first), `Glass`, `Lift` (shadow), `Tooltip`/`TooltipMarkdown`,
  read-only `Bounds` (module space, last layout), `DesiredSize`, `IsHovered`, `IsPressed`, `IsFocused`,
  `EnabledInHierarchy`, `IsHiddenByLayer`.
- Fixed per-component identity: `PaintId` (unique float, used by draw-list reuse; `UIComponent.cs:68`).

### Layout algorithm
Per tick, `UIModule.Layout(screen, skin)` (`UIModule.cs` ~330) does:
1. `Root.MeasureTree(skin)`: children first, then `DesiredSize = Size>0 ? Size : Measure(skin) * Scale`, then
   `Folding()` (collapsed tabs) and slide adjustments.
2. `Frame` = the screen in module units, or the centred design rect when `DesignSize` + `UIFit.Contain`.
3. `Root.ArrangeTree(Frame)` -> `Arrange(content)`; default `Arrange` calls `child.Place(content)` for each child
   (`UIComponent.cs:385-415`: `center = area.Center + anchor*area.Size + Position - pivot*size`, with `Fill` forcing
   size to area and zeroing anchor/pivot on that axis).
4. A label that wrapped to a different width than it measured calls `module.NoteLayoutStale()`; layout is repeated up
   to `LAYOUT_AGAIN = 2` times that frame.

Override points for a container: `Measure(UISkin)` (return needed size; children already measured, read
`child.DesiredSize`), `Arrange(UIRect content)` (call `child.ArrangeTree(rect)` yourself). See `StackPanel`
(`Containers/StackPanel.cs:20`): vertical/horizontal run with `Spacing` (-1 = skin default) and `Stretch`.
There is **no dirty-flag layout**: measure+arrange run every tick for every enabled module.

### Module-level scale/fit
`UIModule.DesignSize` + `Fit` (`UIFit.Contain|Stretch`, `Layout/UIFit.cs`) fit a layout designed for e.g. 1600x900 to
the real screen (`UIModule.FitTo`, `UnitScale`). Layouts declare it with `compositor.design({ size: vec(1600, 900),
fit: "stretch" })` (`UIModule.Runtime.cs:92`). Without it the module uses `Compositor.UIScale`
(= fit x `Scale` x `DisplayScale`; `UICompositor.UpdateState` ~715-735; phones clamp via `MinimumShortEdge`).
`Viewport`/`Clip`/`FrameColor` let an editor embed a module in a canvas (Hex's designer does this).
`ToLocal`/`ToWorld` convert between camera world space and module space.

### Built-in containers
`Panel` (background region/colour/radius/outline/`Inset`/`Hole`/`Template`; `Panel.cs:14`), `StackPanel`, `GridPanel`,
`ScrollPanel` (+`.Bar`), `Group`, `Collapsible`, `TabPanel`, `WindowPanel` (draggable/resizable/modal; partials
`.Modal/.Motion/.TitleActions`), `TitleBar`, `Divider`, `Spacer`, `Include` (builds another `.hor` in place).

## 4. Control catalogue (HIDL kind name -> class)

From `UIModule.Runtime.cs:136-183` (`Register<T>(name)`; this is the authoritative list of what layouts can make):

| HIDL kind | Class | Notes |
| --- | --- | --- |
| `button` `toggle` | `Button` (`Controls/Button.cs:16`), `ToggleButton` | `OnPressed` Action or script handler; `Icon` = Codicon name; `Primary`/`Danger`/`Indicator`; skin region `Style` + `_pressed`/`_hover` |
| `textbox` `number_box` | `TextBox` (`Controls/TextBox.cs:24`), `NumberBox` | single-line; `Text` |
| `slider` `progress_bar` `meter` `graph` `readout` `segments` `sparks` `symbol` | matching classes | display/gauges |
| `label` `image` | `Label` (wrap, `WrapAround`, rich), `Image` | |
| `panel` `group` `stack` `grid` `scroll` `collapsible` `tabs` `window` `divider` `spacer` `include` | containers above | |
| `selector` `dropdown` `list` | `Selector`, `Dropdown`, `ListBox` | |
| `tree_view` `table` | `TreeView` (`UITreeItem` tree data, actions/columns partials), `Table` | |
| `color_picker` | `ColorPicker` | |
| `menu_bar` | `MenuBar` | menus from `Menu`/`MenuItem` records |
| `code` `code_editor` | `CodeView` (read-only), `CodeEditor` (`Controls/CodeEditor.cs:105`, 7k lines across partials) | see section 7 |
| `status_bar` `tab_strip` `breadcrumbs` `title_bar` `notifications` | respective classes | |
| `drawing` | `DrawingPanel` | custom-paint surface |
| `form` | `FormPanel` | |

Not registered as kinds (C# only): `UIDialog`, `UIChooser`, `ContextMenu`, `UIFileBrowser`, `SettingsWindow`,
`ArrangeOverlay`, `OnScreenKeyboard`, pickers (`SkinPicker`, `ArtPicker`, `RenderScalePicker`), `Lights`, `GlowField`.
Other assemblies add kinds via `UIModule.AddKind<T>("name")` (`UIModule.Runtime.cs` ~190; the terminal does this,
`Horizon.Terminal/TerminalKinds.cs`).

## 5. Rendering path

1. `UICompositor.UpdateState(dt)` (sim thread): compute `UIScale`, `module.FitTo`, `RoutePointer`, `RouteKeyboard`,
   `UpdateTextInput`, `UpdateTooltip`, then `module.Update(dt)` per enabled module (tweens tick, hover step, slide
   step, `Update`) and stores a `PendingPaint`.
2. `Capture()` (end of tick; `UICompositor.cs:860`) lays out and paints (`PaintPending`) so the list reflects every
   update of the tick, then publishes it. If nobody captures, `UpdateState` paints immediately (`capturedBefore`).
3. `PaintPending` (762): `back.Begin(...)`, per module `Layout` then `Paint(back)`, then layout-debugger overlay,
   highlight, tooltip, popups, cursor; swaps `back`/`front` under `frameLock`.
4. `Render(dt)` (render thread, 1022): `skin.Update()` stitches newly requested art into the atlas; uploads the
   `front` list through `UIRenderer` (`Drawing/UIRenderer.cs`: `SpriteBatch` of `SpriteItem` quads, no own shader);
   draws. A list painted with missing art (`front.Incomplete`) is skipped and the previous stays up.
5. **Decoupled rendering** (`RenderFrame.IsDecoupled`): frames between two ticks blend the last two lists
   (`Drawing/UIInBetween.cs`, caching `framesBefore` by `UIFrameList.Stamp`; "quads that are the same in both lists
   but for position/size/colour are blended", things that change in steps flip at their moment). Tests:
   `Horizon.Tests/UI/InBetweenTests.cs`.
6. `Retained` (default true, `UICompositor.cs` ~200): while nothing changes the compositor keeps a picture of the UI
   and overlays that as one quad instead of redrawing every quad. `pictureGlassGeneration` (`ForgetGlass`) invalidates
   `Retained` layers whenever a glass backdrop changes. `FrameRateLimit` (default 120, `DefaultFrameRateLimit`,
   `[Inspect(0..480)]`) limits how often the UI is re-put-together and uploaded; input and updates still run at sim
   rate. The mouse `Cursor` is drawn every frame regardless.
7. `PostProcessing` (`compositor.PostProcessing.Add(new BlurEffect())`): UI is drawn into its own `PostLayer`
   while any effect is on; components then track motion speed.
8. **Rimless Apple Liquid Glass** (`Drawing/UIGlass.cs`, `Drawing/UIGlassPass.cs`, `horizon/Horizon/shaders/post/glass.slang`):
   - `UIGlass(Blur, Clarity, Bend)` uses 3 parameters only (`SilverLining`, `Sheen`, and `Rim` were removed completely).
   - `glass.slang` implements a smooth $C^1$ polynomial smooth-max meniscus dome (`pow(depth, 0.25) * pi/2`), grazing
     RGB dispersion, `uSharpRect` cropped sharp backdrop sampling, and a 1-tap flat-interior fast path
     (`-d >= rimWidth + k*0.25 + 1.0`).
   - `UIGlassPane.Under` (recorded via `UIDrawList.GlassMark()`) captures the backdrop *before* a popup/component's
     `Lifted` shadow so glass never blurs its own drop shadow (`UIGlassPass.Capture` -> draw `[Under..Start)` ->
     `UIGlassPass.Composite`).
   - GPU/VRAM efficiency: region-scissored blur via `GraphicsDevice.SetScissor`/`ResetScissor` + `GaussianBlur.Margin`,
     128px-grid spatial `madeOf` cache keys with run-texture invalidation, cropped `keptSharp` targets, 120-frame idle
     VRAM eviction, and `pictureGlassGeneration` (`ForgetGlass` on `Retained` layers).

`UIDrawList` primitives (`Drawing/UIDrawList.cs`): `Rect`, `Box` (skin corner radius), `RoundRect`, `Outline`,
`Frame`, `NineSlice(region, rect, tint)`, `Region`, `Image`, `Symbol`, `Text(...)` (in `.Text.cs`), `PushClip/PopClip`,
`Shadow`, `Lifted`, `GlassMark`, `Glass`, `Popup`, `Squiggles` (diagnostic underlines). `BeginPart(n)` / `EndParts()` number
sub-parts of a component so quad counts stay stable for tick blending (see the comment in `Button.Paint`,
`Controls/Button.cs:128-132`). Painting rule: always use `list.Skin` for colours/regions and fall back to flat
colours if `skin.TryGetRegion` fails.

## 6. Input, focus, events

- **Pointer** (`UICompositor.RoutePointer`, `Input.cs:447`): `PointerSource?.Invoke() ?? ReadPointer()` -> hit test
  modules last-to-first (last module is on top; `UIModule.HitTest` -> `UIComponent.HitTest`,
  `UIComponent.Interaction.cs:43`). Hit testing happens against the **previous** tick's layout and accounts for
  `VisualOffset/VisualScale` and scrolling drawn-moves (`Compositor.DrawnMove`). Components opt in to being hit with
  `HitTestVisible` (or by having a `Tooltip`). `ClipsChildren` stops hits outside bounds. `Module.Interactive=false`
  makes a module look-only.
- **Component virtuals** (all `protected internal virtual`, `UIComponent.Interaction.cs`): `OnPointerDown/Drag/Up`,
  `OnClick`, `OnScroll`, `OnPan` (touch), `OnPointerCancel`, `OnActivate` (nav), `OnAdjust`, `OnNavigate`,
  `Navigable`, `Focusable`, `TabStop`, `KeepsTab`, `TakesRawKeys`, `WantsTextInput`, `OnKeyDown(UIKeyPress)`,
  `OnTextInput(char)`, `OnPaste(string)`, `OnEscape`. Popups: `module.Popup` + `PaintPopup`, `PopupContains`,
  `TakesPopupKeys`, `OnPopupKeys`, `PopupClosed`, `PaintClosing`.
- **Keyboard & IME**: `UIKeyboard` (static, `Input/UIKeyboard.cs`) queues the *character stream* from the window thread; `UICompositor.Input.cs` calls `WindowManager.NoteInput()` on keyboard/pointer activity to lift calm throttling immediately. Editing keys are encoded as private control chars (`BACKSPACE`, `ENTER`, `TAB`, `LEFT`, `HOME`, `COPY`, `PASTE` ...). A component reads them in `OnTextInput(char)`. Paste text is fetched separately with `TryTakePaste`. On SDL hosts (macOS/Linux), `SdlTextEditing` (`Horizon.Platform.Desktop/Input/SdlTextEditing.cs`) delivers intact UTF-8 emoji/IME commits and `InputEvent.Composition` preedit events (routed through `UICompositor.Input.cs` to `CodeEditor` and `TextBox`, with caret bounds sent to `IPlatformInput.SetTextInputRect`). A focused component with `TakesRawKeys` (terminal) instead receives `UIKeyPress` through `OnKeyDown` and Tab/Escape too. Key repeat is synthesised (`UIKeyboard.Repeat`).
- **Routing order each tick** (`RouteKeyboard`, `Input.cs:51`): keep focus sane -> `RouteToPopup` (open menu owns all
  keys) -> Escape (`Escape()`: dialog, then popup, then focus) -> Tab/Shift+Tab `FocusNext` (over
  `UIModule.CollectFocusable`, components with `Focusable`/`TabStop`) -> typed chars to `focus`.
  Focus: `compositor.Focus` (property, `UICompositor.cs` field `focus`), `FocusVisible` (ring only after a key,
  web `:focus-visible` semantic), window focus hand-back (`WindowPanel.KeyboardBefore`, `Focus.cs`).
- **Shortcuts**: `UIShortcuts` (`Input/UIShortcuts.cs`): key + ctrl/shift/alt, `whileTyping`, takes/passes-on, VS
  Code style two-key chords (`Ctrl+K Z`, `ChordWait`). `UICommands` (`Components/Popups/UICommands.cs`): declare
  menus + groups, then components add commands ("Edit/find") with key and `UIKeyUse` (`Shown|Bound|BoundWhileTyping`);
  `Build` yields the `MenuBar` and key bindings. Hex's user key remapping rides on this.
- **Gamepad/keyboard navigation**: `module.Navigation` (`UINavigator`, lazily created); components mark `Navigable`.
- **Touch**: `UICompositor.Touch.cs` (taps deferred, fling, pan, long-press context); `OnScreenKeyboard` control;
  `UICompositor.UpdateTextInput` shows platform text input for `WantsTextInput` focus.
- **Tooltips**: `UpdateTooltip` (rest 0.55 s, `TOOLTIP_DELAY`), markdown tooltips (`UIRichText`), drawn by the module.
- **Handlers**: C# `Action` properties (`Button.OnPressed`) or script handlers (`InvokeScript(handler)`; errors are
  logged, never thrown into the game; `UIComponent.Scripting.cs`).

## 7. Text rendering and editing

- Font: `UIFont` (`Drawing/UIFont.cs`, bitmap `.fnt`, default `Assets/fonts/vcr_mono/vcr_mono.fnt`, `UISkin.cs:28`);
  `skin.Font.Measure(text, scale)`. TrueType outline rasterisation (`horizon/Horizon/Rendering/2D/Text/GlyphOutlines.cs`)
  supports `.ttf`, `.otf`, and `.ttc` TrueType collections (`stbtt_GetFontOffsetForIndex`, e.g. macOS `Menlo.ttc`).
  `GlyphAtlas` (`horizon/Horizon/Rendering/2D/Text/GlyphAtlas.cs`) grows `512 -> 1024 -> 2048` with sub-rect GPU
  uploads (no permanent CPU pixel/staging mirrors), and `horizon/Horizon/shaders/ui/ui.slang` takes a zero-derivative
  `SampleLevel(uv, 0)` early exit for rasterized atlas glyphs. Text drawn via `UIDrawList.Text`. Inline icons: `$(codicon-name)` inside any
  label (`Button.Written`, `Controls/Button.cs:~100`); Hex's MCP strips these via regex. `@key` strings are
  localised: `UIComponent.Resolve(text)` -> `UICompositor.Resolve` (own `Strings` first, then
  `Horizon.Content.Localisation`).
- `Label` handles wrap and `WrapAround` (text flowing around a `UIWrapShape`), `UIRichText` renders markdown/rich
  (`.Code.cs`, `.Document.cs`).
- `UITextDocument` (`Controls/UITextDocument.cs:47` + `.Edits/.Lines/.Comments/.External`) is the headless
  multi-line model: lines as immutable strings, snapshot undo steps share unchanged lines, multi-caret
  (`ExtraCarets`, capped at `MAX_OCCURRENCE_CARETS = 10000`), auto-indent/bracket pairing, find options (`UIFindOptions`), comments (`.Comments.cs`),
  external rebase (`ExternalPositionMap`, `RebaseHistory`), and `Version` counter. Testable without a window.
- `CodeEditor` (`Controls/CodeEditor.cs:105` + partials by feature: `.Gutter .Minimap .Squiggles .Lenses .LensesAbove .Inlays
  .SignaturePopup .NativeFolding .Semantic .Todos .Rulers .Whitespace .Ranges .Rows .Fit .Flow .Backdrop .Bar .Blocks
  .DiagnosticCard .Jumps .Lightbulb .MiddleScroll .CompletionDocumentation .AgentHints .Prediction .RemoteCarets .Reuse .Spelling`). Language-server data enters through hooks
  (`Diagnostics`, `CompletionSource`, `HoverSource`) as `UICodeDiagnostic`, completion items etc.; **colour** comes
  from `ICodeHighlighter` (`Scripting/ICodeHighlighter.cs`): `TextMateHighlighter` (VS Code grammars in
  `Scripting/Grammars/`, Oniguruma via Onigwrap; `CutNear` guards UTF-16 surrogate pairs so emoji/non-BMP text never
  splits across highlight chunks, `LongLineSpans` pieces `2048`-char chunks, `CodeLineStart` matches `Depth` & `RuleStack`) or `HidlHighlighter` (`Scripting/HidlHighlighter.cs`, native C# scanner + `Overlay` full-buffer tokenization);
  `CodeLanguages.cs` picks by extension and
  `CodeLanguages.Register(ext, scope)` adds one. Highlighting runs on a thread-pool thread after a pause; until it
  returns the typed line is coloured with `ReadLine`. Semantic tokens overlay via `SemanticOverlay`.
  `CodeEditor` uses `PaintVersion` reuse (section 9). Also data/navigation controls: `TreeView` (+8 partials incl. `.Columns/.Actions/.Edit/.Wrap/.Reuse` using `UITreeItem.wrapped` proxy rows), `ListBox` (+`.Choices`), `Table`, `TabStrip`, `FormPanel` (`UIFormField`).

## 8. Skinning, theming, animation

- `UISkin` (`Skinning/UISkin.cs`): sprite-sheet or image regions stitched lazily into its own `TextureAtlas`; shared
  per `(path, theme)` (`UISkin.Shared`, static dictionary line ~55); `Palette` (`UIPalette`) holds every colour;
  properties like `ControlColor`, `HoverColor`, `AccentColor`, `TextColor`, `BorderColor`, `CornerRadius`, `Spacing`,
  `ButtonPadding`, `TextScale`, `DisabledTint`. `TryGetRegion(name, out UIRegion)` (nine-slice borders, content
  padding, text colour). Missing names are cached as null (components ask every frame). Skin files are `.hor`
  (HIDL) with `regions`, `icons`, `themes`.
- `UITheme` (`Skinning/UITheme.cs`): a palette + art-layer overlay; allowed keys only (`title base art syntax terminal
  regions icons corner_radius popup_radius row_height` + palette colours); anything else is rejected. Lives in
  skin file `themes:` or `themes/<name>.hor`. Switch at runtime: `compositor.SetSkin(dir, file, theme)`,
  `SetTheme(theme)`, `FollowSkins()`. `UISkin` loading is deferred to the render thread (`requestedSkin` +
  `LoadRequestedSkin`).
- Syntax colours per theme: `UISyntaxColors*.cs`; terminal 16 colours: `UIPalette.Terminal.cs`.
- Animation: (a) `UITweens` extension methods on `UIComponent.Tweens` (a `TweenContext`, ticked in `UpdateTree`),
  one tween per channel (`Offset/Scale/Opacity/Position/Size`) replaces the previous; (b) `UIComponent.Intro`
  (`UIIntro.Pop|Fade|Slide`, `IntroTime/Delay/Offset/Easing`; `PlayIntro`; layout keys `intro:`); (c) hover animation
  (`UIComponent.Hover.cs`, `hoverness`), button `Punch`; (d) `Sliding` (animated layout size); (e) overscroll/ease in
  `UIOverscroll.cs`, `UIScrollEase.cs`; (f) animated icons (`UIAnimatedIcons.cs`). Global speed
  `compositor.AnimationSpeed`; `MotionTime(t)` in components respects it (0 = reduced motion; see Button.Update).
  Animations that change only `Visual*`/`Opacity` do not relayout neighbours. User-followed animations (scroll
  glides, overscroll springs, panel easing) call `WindowManager.RequestSmooth()` so they stay smooth instead of
  dropping to the calm rate (24 Hz focused / 10 Hz unfocused); decorative motion (`UIDecorative.cs`, spinners/pulses)
  uses `WindowManager.RequestTickIn(...)` capped at 12 Hz.

## 9. Invalidation and performance

- No invalidation API: everything repainted every tick; savings come from (1) `Retained` picture overlay when nothing
  changed (invalidated by `pictureGlassGeneration` / `ForgetGlass`), (2) `FrameRateLimit` + `WindowManager.PaceCalmFrames()` in `RunMode.Application`, (3) **per-component paint reuse** (`Drawing/UIDrawList.Reuse.cs`): override
  `protected virtual long PaintVersion` (return a number that changes whenever what you paint changes; -1 = never
  reuse; leaf components only — `UIComponent.cs:593`). If version and `PaintContext` (bounds, clip, offsets) match last tick, the component's quads are copied from
  the previous list; glass panes participate in `PaintRecord` reuse by rebasing `Under` and `Start`. Worth it for heavy painters (`CodeEditor`, `Label` via `UIRichText` layout version, `TerminalView.PaintVersion` in `horizon/Horizon.Terminal/TerminalView.cs`); verify with `HORIZON_UI_REUSE_CHECK=1`
  (paints anyway and logs differences). Call `list.Uncacheable()` for things that can't be replayed (raised
  components, animated icons).
- **Terminal memory & paint efficiency**: `TerminalCell` (`horizon/Horizon.Terminal/TerminalCell.cs`) is packed to
  32 bytes with scrollback blank-tail compaction, paired with `TerminalView.PaintVersion` reuse.
- **Settings virtualization**: `SettingsPage.RowsStack` (`Components/Controls/Settings/SettingsPage.Rows.cs`) keeps only viewport-visible rows (+ buffer and focused/flashing/popup rows) attached as live children when a page exceeds 24 items, avoiding ~1,500-component measure/arrange/update overhead per tick. `SettingsPage.RowItems` exposes the full list (attached and detached) for tests/automation (e.g. `DawnScene.SelfTest.cs:Descendants`).
- **Native dialogs**: `UIFileDialogs` (`Components/Popups/UIFileDialogs.cs`) delegates to `IPlatformDialogs` (`MacDialogs` on macOS when `PlatformFeatures.NativeDialogs` is available) and falls back to the in-app `UIFileBrowser`.
- Zero-alloc conventions: use `ChildSpan`, cache composed strings (`Button.Written` reference-equality cache),
  reuse scratch `List`s (`TooltipLines`), `FrozenDictionary` strings, `GetAlternateLookup<ReadOnlySpan<char>>`.
  `Resolve` "makes no garbage" by design. `Horizon.Tests/Core/HotPathTests.cs` references the compositor (hot path
  allocation guard).
- Diagnostics: `UIStatistics`, `UIPaintProfile.On` (per-component paint/measure timing), `PerformanceOverlay`,
  `compositor.LayoutOverlay` (`UILayoutOverlay`, boxes for the layout debugger), `Highlighted` component marker.
- Layout files are cached in `UILayout.programs` by path+write-time, rechecked at most every `RECHECK_MS=1000`
  (`Layout/UILayout.cs:30-36`), `UILayout.Forget(path)` to evict (hot reload).

## 10. HIDL relationship (layouts, scripts, binding)

- **Declarative layouts**: a `.hor` file with `#define layout` runs in a per-layout HIDL scope where `compositor`
  is a system object (`UIModule.Runtime.cs:90`, `Runtime.UserScope.DeclareSystem("compositor", ...)`). Each
  `compositor.button({ label: "Play", pos: vec(0,32), parent: other })` creates the component, applies properties
  via `ApplyScript` (properties = whatever `DefineScript` `Expose`d; unknown names throw
  `"<Type> has no property called 'x'"`), reparents to `parent` or to the module root / capture parent, and returns
  the component's script object (`component.Object`). Variables (`let play = ...`) become `layout.Parts`.
  Real example: `Fighter2D/Assets/ui/layouts/fight_overlay.hor` (`compositor.design`, `layer: "hud"`,
  `intro: "slide"`, `background: "panel"`, `anchor`/`pivot` strings via `Origin`).
- **Loading from C#** (`Layout/UILayout.cs`): `UILayout.Load(module, path, parent?, introDelay)` (76) or
  `module.LoadLayout(path)` (`UIModule.cs` ~269); `LoadCode(module, code)` for strings; `layout.Get<T>(name)` (113,
  throws with a clear message if missing/wrong type), `TryGet`. Example: `Fighter2D/Menus/Shared/MenuConfirm.cs:47`.
- **Data binding is imperative**, not reactive: a `panel` with `template: "row.hor"` is a container;
  `layout.Populate(container, count|data, fill)` (139/177) builds items, `Bind(container, data, fill)` (196) diffs
  (keeps existing items, only adds/removes, only new items run intros) and calls `fill(itemLayout, datum, index)`.
  Call `Bind` every time the list changes. Each layout/template has its own scope so names do not collide.
- **Includes/templates**: `compositor.include({ parent, file })` (`Containers/Include.cs`, `Absorb` merges parts).
- **Layers/screens**: `layer: "name"` on components + `compositor.hidden_layers([...])` +
  `module.SetLayerVisible` to switch screens in one file.
- **Script-driven UIs (Hex extensions)**: `UIScriptedLayout` (`Scripting/UIScriptedLayout.cs`) wraps a layout made by
  a script owner (`IUIScriptOwner`): `ui.get(name)`, `ui.on_tick`, `ui.on_key`, `ui.on_resize`, `ui.close()`,
  `ui.size`. `UIScriptedScreens<TFile>` manages per-file views, tab pages, windows.
  `UIComponent.ScriptCaller` lets an extension supply its own handler invocation (time-limited). See
  `hex/docs/layouts-and-hidl.md` (Hex's designer edits these same files, "drawn by the same code a game draws them
  with") and `hex/docs/extensions.md`.
- Hex's own UI is also a layout plus C# (`layout.Get<Button>("act_agents")`, then `Add(new StackPanel{...})` for
  dynamic parts, `hex/Hex.Dawn/Agents/DawnScene.AgentsPanel.Chats.cs:40-46`).
- Script conversions: `Scripting/UIScript.cs` (`ToNumber/Text/Vector2/Color/Edges`, `FromEnum`, enums given as
  snake_case strings like `"top_left"`). Vectors in layouts: `vec(x,y)`, colours `vec(r,g,b[,a])` in 0..1.

## 11. How to build a new control

Minimal sketch (patterned on `Button`, `Controls/Button.cs`):

```csharp
public class Chip : UIComponent
{
    public string Label { get; set; } = "";
    public Action? OnPressed { get; set; }

    protected override bool HitTestVisible => true;          // receive the pointer
    protected internal override bool Navigable => true;      // gamepad/keyboard nav
    protected internal override void OnActivate() => OnClick();
    protected internal override bool TabStop => OnPressed is not null;

    protected override Vector2 Measure(UISkin skin) =>        // automatic size
        skin.Font.Measure(Resolve(Label), skin.TextScale) + skin.ButtonPadding.Total;

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        bool lit = EnabledInHierarchy && IsHovered;
        list.Box(Bounds, skin.ControlColor);
        if (lit) list.Box(Bounds, skin.HoverColor);
        list.Text(Resolve(Label), Bounds.Shrink(skin.ButtonPadding), Origin.Center, skin.TextScale, skin.ControlTextColor);
    }

    protected internal override void OnClick() => OnPressed?.Invoke();

    protected override void DefineScript()                    // make it scriptable / HIDL-visible
    {
        base.DefineScript();
        Expose("label", () => Label, v => Label = v);
    }
}
// Register for layouts: in-engine add a Register<Chip>("chip") line in UIModule.Runtime.cs SetupRuntime;
// from another assembly call UIModule.AddKind<Chip>("chip") before modules are created.
```

Checklist and rules:
- Must have a public parameterless ctor (`Register<T> where T : new()`).
- Override `Measure` (not `Size`) for automatic size; children are already measured when it runs. Containers also
  override `Arrange`. Use `DesiredSize` of children, never `Bounds` (stale) in `Measure`.
- Paint only into `list`; never touch GPU, never allocate per frame. Use `Bounds` (module space). Use
  `list.BeginPart(n)`/`EndParts()` when you paint a variable number of conditional quads (hover box, marker) so the
  in-between tick blending does not mismatch quads.
- Respect `EnabledInHierarchy`, `skin.DisabledTint`, and `Opacity` is applied for you by the base.
- Animate with `Tweens`/`Visual*`, not by mutating `Position`, unless the layout should move.
- Update logic in `Update(float dt)` (sim thread, once per tick). Do not rely on `Paint` for state changes.
- Properties you want in layouts/inspectors: `Expose(...)` in `DefineScript` (`DefineScript` runs lazily the first time
  `.Object` is touched, so ctor-time defaults matter).
- Popups/dropdowns: set `Module.Popup = this` via `OpenPopup` (`UIComponent.Interaction.cs` ~200-260), implement
  `PaintPopup/PopupContains/PopupClosed`, so it paints on top and is not clipped (see `Dropdown`, `MenuBar`).
- Heavy painters: add a `PaintVersion` (section 9) and run `HORIZON_UI_REUSE_CHECK=1`.

## 12. How to compose a screen

From code (template project, `hex/Hex.Core/Projects/ProjectTemplates.cs:545-548`):

```csharp
UICompositor ui = AddComponent(UICompositor.ForScreen());   // in a Scene.Initialize (Scene2D already has `UI`)
UIModule screen = ui.CreateModule();
screen.AddComponent(new Label("Hello") { Anchor = Origin.Center, Pivot = Origin.Center, TextScale = 0.45f });
```
Container nesting (Hex): `var rows = scroll.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 1 });`
(`Add<T>` returns the child, so `x = parent.Add(new ...)` is the idiom).

From a layout (preferred for anything designable):
```csharp
var layout = module.LoadLayout("Assets/ui/menu.hor");          // or UILayout.Load(module, path, parent)
layout.Get<Button>("play").OnPressed = Play;
layout.Bind("saves", saves, (item, save, i) => item.Get<Label>("name").Text = save.Name);
```
Skin: `UICompositor.ForScreen(skinDir, skinFile, theme)` / `ui.SetSkin(...)`. Without a skin nothing paints
(`UpdateState` returns early `if (Skin is not {} skin)`).
Multiple modules = independent screens/layers; later modules draw over and get the pointer first
(`UICompositor.RoutePointer`). Replace one with `ReplaceModule(current, replacement)`; remove with `RemoveModule`.
Dialogs: `UIDialog` sits on the module root; `module.Dialog` finds the open one; Escape cancels it.

## 13. Accessibility / automation hooks (Hex MCP)

The UI framework has **no accessibility tree or automation API of its own**. `hex_ui_tree` / `hex_click` are
implemented in Hex (`hex/Hex.Dawn/Agents/DawnScene.McpUiTree.cs`, tool defs in `DawnScene.McpScreen.cs`) by walking
the public UI surface. What it relies on (so changing these breaks it):
- `compositor.Modules`, `UIModule.Enabled`, `module.Root`, `component.Children`, `Visible`, `Enabled`, `Bounds`
  (module space), `module.ToWorld`, `IsFocused`, `Tooltip`, `module.TooltipText`/`Popup`, `compositor.Resolve`.
- Text of a node is extracted per type in Hex (`McpTextOf`: `Label.Text`, `Button.Label`, `ToggleButton.Label`,
  `TextBox.Text`, `CodeEditor.Document.LineCount`); adding a new text control means adding a case there.
- Ids (`c12`, `r40`, `s3`, `m1`, `i4`) come from a `ConditionalWeakTable` in Hex; names come from `layout.Parts`.
  Rows of `TreeView`, `StatusBar` items, `MenuBar` titles and open-menu items are not components, so Hex uses
  per-control geometry accessors, e.g. `TreeView.Actions` partial `ActionBounds`/`CellBounds`
  (`TreeView.Actions.cs:49`, `TreeView.Columns.cs:103`), `CodeEditor.AgentHintDismissBounds` ("for accessibility
  clients and pointer tests", `CodeEditor.AgentHints.cs:39`).
- Input injection: `UICompositor.PointerSource` (a `Func<UIPointer>`), `FindAtWindow(pixel)` (534), `FindAt(point,
  hidden)` on modules, `Compositor.Focus`, `UIKeyboard` char queue.
- Other read-only helpers: `compositor.HoveredComponent`, `TooltipOf`, `Highlighted`, `UIModule.KindOf(component)`
  (HIDL kind name for a type), `UIModule.Kinds`.
Unclear: whether any control exposes semantic roles; none found in this assembly.

## 14. Conventions

- Doc comments are long prose explaining the *why* (and past bugs). Read them before changing behaviour; many encode
  a regression ("Clicking the explorer's icon drew a ring...").
- Files: one class per file; `partial` split by feature as `Class.Feature.cs` (e.g. `CodeEditor.*.cs`,
  `UICompositor.*.cs`, `WindowPanel.*.cs`). File-scoped namespaces in newer files, block namespaces in a few
  (`UIModule.cs`). `UI` prefix on non-component helpers (`UIRect`, `UIEdges`, `UIDialog`, `UITweens`).
- Colours are `Vector4` RGBA 0..1; positions `Vector2`; rects `UIRect` (Min/Max, `Shrink(UIEdges)`, `Contains`,
  `FromCenter`). `Origin` enum (`TopLeft`, `Center`, ...) from engine core; layouts use snake_case strings.
- Skin region naming: style stem + `_hover`, `_pressed`, `_flat` suffix = body-less label button, `selection`.
- Features are discoverable through string names in HIDL; adding a property = `Expose("snake_case", ...)`.
- Builds: Debug includes `Engine/Debugging/`; Release does not compile it. `LangVersion preview`, nullable on,
  unsafe allowed.

## 15. Threading and gotchas

- Sim thread: `UpdateState`, `Capture`, layout, paint, all component code, handlers. Render thread: `Render`,
  skin art stitching, `UIRenderer.Upload`. Window thread: pushes chars into `UIKeyboard`'s `ConcurrentQueue`.
  Highlighters run on the thread pool and must not touch components.
- Component trees may be mutated from any thread (copy-on-write children), but `Parent`, `Bounds`, property values
  are plain fields; do not read half-built state from other threads.
- Hit-testing uses last tick's layout; a component created and clicked in the same tick may not be hittable yet.
- `Opacity <= 0` or `!Visible` or hidden layer: not painted, not hit, but still laid out (except `Visible=false`
  skipped by `StackPanel.Measure`).
- `Tooltip` on any component makes it hittable and steals the press from what's under it.
- A layout script assigned a property the component doesn't `Expose` throws at load; the whole `Load` throws with a
  readable message (`UILayout.Get` also throws, not returns null: use `TryGet` for optional parts).
- Pasted text arrives as a `PASTE` mark + side channel, not as chars; handle in `OnPaste`.
- Raw-key components must set `TakesRawKeys`; the compositor flips `UIKeyboard.RawKeys` per tick only for the UI that
  owns focus (`focusOwner`), so two compositors can fight otherwise (`Input.cs:62-70` comment).
- `ZOffset > 0` components are lifted out and painted after the module (`UIModule.Raise`); they make enclosing reuse
  records uncacheable.
- Skins are shared statics for the process lifetime; mutating one affects every UI using it.
- `FrameRateLimit` rounding: UI may display up to a frame stale; input latency is unaffected.
- Mobile: `DisplayScale`/`MinimumShortEdge` only apply on hosts without a desktop window.
- TODO-style notes: none grep-visible as `TODO`; unfinished areas are unclear. (Not exhaustively searched.)

## 16. Where to look for X

| Want | Look at |
| --- | --- |
| Add a built-in control | `Components/Controls|Display|Containers`, then `UIModule.Runtime.cs` `SetupRuntime` |
| Make a control available from another assembly | `UIModule.AddKind<T>` (`Horizon.Terminal/TerminalKinds.cs` as example) |
| Layout math (anchor/pivot/fill) | `UIComponent.Place` (`UIComponent.cs` ~395), `Measure`/`Arrange` overrides in `StackPanel`, `GridPanel`, `ScrollPanel` |
| Window scaling / design resolution | `UIModule.FitTo`, `UICompositor.UpdateState`, `Layout/UIFit.cs` |
| Hover/press/click dispatch | `UICompositor.RoutePointer`, `UIComponent.HitTest`, `UIComponent.Interaction.cs` |
| Keyboard focus / Tab / Escape | `UICompositor.Input.cs` `RouteKeyboard`, `UICompositor.Focus.cs` |
| Typed characters & key encoding | `Input/UIKeyboard.cs`, `Input/UIKeys.cs` |
| Menu/shortcut registration | `Components/Popups/UICommands.cs`, `Input/UIShortcuts.cs`, `MenuBar*.cs` |
| Painting primitives | `Drawing/UIDrawList*.cs` |
| Drawing text / fonts / rich text | `UIDrawList.Text.cs`, `Drawing/UIFont.cs`, `UIRichText*.cs`, `horizon/Horizon/Rendering/2D/Text/GlyphAtlas.cs`, `horizon/Horizon/shaders/ui/ui.slang` |
| Glass/blur/shadows | `Drawing/UIGlass*.cs`, `horizon/Horizon/shaders/post/glass.slang`, `horizon/Horizon/Rendering/Common/PostProcessing/GaussianBlur.cs` (`Margin`), `UIDrawList.cs` (`GlassMark/Glass/Lifted/Shadow`) |
| Skin art, palette, themes | `Skinning/UISkin*.cs`, `UIPalette.cs`, `UITheme.cs` |
| Tweens/intros | `UITweens.cs`, `UIComponent.cs` (Intro), `UIComponent.Sliding.cs` |
| Code editor features | `Components/Controls/CodeEditor.<Feature>.cs`, `UITextDocument*.cs`, `Scripting/*Highlighter*.cs` |
| Layout files / bindings | `Layout/UILayout.cs`, `UIModule.Runtime.cs`, `Scripting/UIScript*.cs` |
| Properties exposed to HIDL | each component's `DefineScript` override; base in `UIComponent.Scripting.cs` |
| Script-owned UI (extensions) | `Scripting/UIScriptedLayout.cs`, `UIScriptedScreens.cs`, `hex/docs/extensions.md` |
| MCP UI tree / click | `hex/Hex.Dawn/Agents/DawnScene.McpUiTree.cs`, `DawnScene.McpScreen.cs` |
| Perf / frame pacing / retained picture | `UICompositor.cs` (`Retained`, `FrameRateLimit`, `NoteFrame`), `Drawing/UIDrawList.Reuse.cs`, `Diagnostics/` |
| Debug overlays | `Layout/UILayoutOverlay.cs`, `Engine/Debugging/SkylineDebugger.cs` (Debug only) |
| UI tests | `horizon/Horizon.Tests/UI/` |
| Scene with a UI ready | `Engine/Scene2D.cs` (`UI` property) |
