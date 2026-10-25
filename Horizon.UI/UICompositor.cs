using System.Diagnostics;
using System.Numerics;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Input;
using Horizon.Rendering;
using Horizon.Rendering.PostProcessing;
using Horizon.Rendering.Spriting;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI;

/// <summary>
/// Runs a UI. It owns the skin and the modules, routes the pointer to the component under it, and draws
/// everything on top of the scene.
/// The work is split the way the engine splits it. Input, layout and painting happen on the simulation thread
/// and produce a list of quads. <see cref="Render"/> only hands the latest list to the sprite renderer, so
/// components never touch the GPU. The pointer and the keys are handed out in <see cref="UpdateState"/>, the
/// laying out and the painting wait for the end of the tick (<see cref="Capture"/>) so that what is painted is
/// the UI as every update of the tick left it. Painted in its own update, a name tag the scene puts on a sprite
/// afterwards was drawn where the sprite had been the tick before, a whole tick behind it.
/// <para>
/// Drawn alongside the simulation (see <see cref="RenderFrame.IsDecoupled"/>) the list that was painted last is
/// published at the end of every tick (<see cref="Capture"/>), and every frame draws the UI at the moment between the
/// last two ticks it shows. What slides, pops or is tweened goes the same distance for the same time from frame to frame
/// at any frame rate, however the ticks and the frames line up. Quads that are the same in both lists but for where they
/// are, how big and what colour are blended, whatever changed in steps (the text, which art) changes at the moment it
/// changed. A UI that is drawn by hand and never published is drawn from its newest list, as it always was.
/// </para>
/// <para>
/// A UI can have effects of its own, see <see cref="PostProcessing"/>.
/// </para>
/// <para>
/// However fast the frames come the UI is only put together and drawn anew so many times a second, see
/// <see cref="FrameRateLimit"/>.
/// </para>
/// </summary>
public partial class UICompositor : GameComponent, IDisposable
{
    /// <summary>Puts text on the system's clipboard, from any thread, the way a text box's Ctrl+C does.</summary>
    public static void Copy(string text) => UIKeyboard.Copy(text);

    /// <summary>Where the skin every UI has unless it asks for another one is kept.</summary>
    public const string DEFAULT_SKIN_DIRECTORY = "Assets/uix/dead_revolver/";

    /// <summary>The definition of that skin.</summary>
    public const string DEFAULT_SKIN_FILE = "skin.hor";


    internal readonly Camera2D viewportCamera;
    private string skinDirectory;
    private string skinFile;
    private string? skinTheme;

    // For the layout debugger. Where the pointer was last, and when the UI was last updated.
    private Vector2 lastPointer;

    /// <summary>Where the pointer was and whether it was down as of the last update, in the camera's world space.</summary>
    public UIPointer Pointer { get; private set; }

    /// <summary>
    /// The size of the screen the UI was laid out for. Set, the whole UI is scaled so that a screen of that
    /// size just fits into whatever the camera sees. A layout made for 1600 by 900 comes out twice as big in a
    /// window of 3200 by 1800, and has the same room to lay itself out in either way. Where the window is
    /// wider or taller than the design the UI gets the extra room, it is never cut off.
    /// Left unset the UI is laid out against the window as it is, a unit per pixel.
    /// </summary>
    public Vector2? DesignSize { get; set; }

    /// <summary>
    /// Makes the whole UI bigger or smaller by a factor, on top of what <see cref="DesignSize"/> does. For a
    /// setting that lets the player decide how big they want things.
    /// </summary>
    public float Scale { get; set; } = 1.0f;

    /// <summary>
    /// How quickly the UI's own movements go, every component's hover and slide, 1 as they're made, 2 twice as quick,
    /// 0 for none at all (a hover is lit at once and a drawer is open or shut). For a setting that lets the player
    /// have things move as they like, or keep still. A component asks it through <see cref="UIComponent.MotionTime"/>.
    /// </summary>
    public float AnimationSpeed { get; set; } = 1.0f;

    /// <summary>
    /// How much bigger than it was laid out the UI is drawn right now, <see cref="Scale"/> times whatever
    /// fitting the <see cref="DesignSize"/> comes to, times the <see cref="DisplayScale"/>. A unit of the layout is
    /// this many units of the camera.
    /// </summary>
    public float UIScale { get; private set; } = 1.0f;

    /// <summary>
    /// How much bigger the screen the window is on wants things drawn, 1.5 on one Windows scales by 150%. Only for a
    /// window that follows the scale (<c>WindowManagerConfiguration.ScaleToDisplay</c>) and a UI with no
    /// <see cref="DesignSize"/>, which is fitted to the screen whatever its scale, 1 otherwise. Part of <see cref="UIScale"/>.
    /// </summary>
    public float DisplayScale { get; private set; } = 1.0f;

    // Scrolling that was asked for with Scroll and hasn't been handed out yet.
    private float pendingScroll;

    /// <summary>
    /// Scrolls whatever is under the pointer as if the mouse wheel had been turned, for input that isn't a
    /// wheel. The stick of a gamepad, or a test. Safe from any thread, it happens on the next update.
    /// </summary>
    /// <param name="notches">How far, a notch of the wheel being 1. Positive is up.</param>
    public void Scroll(float notches)
    {
        float before;
        do
        {
            before = pendingScroll;
        } while (Interlocked.CompareExchange(ref pendingScroll, before + notches, before) != before);
    }

    // A skin asked for with SetSkin, waiting for the render thread to load it.
    private (string Directory, string File, string? Theme)? requestedSkin;
    private readonly Lock skinLock = new();

    private readonly UIRenderer renderer = new();

    /// <summary>The quads that are drawn, as they were last uploaded. For tests that look at what is on screen, render thread.</summary>
    internal ReadOnlySpan<SpriteItem> Drawn => renderer.Uploaded;

    // Where the UI is drawn while it has effects that are on, rather than straight over the scene
    private readonly PostLayer layer = new();

    // Counts the updates, every list is told which one it was painted in, and the time they've been painted for
    private int paintFrame;
    private float paintClock;

    // Whether what is on the layer is what the UI looks like right now, and what it was drawn with. Seen
    // through another camera the same list is another picture
    private bool pictureCurrent;
    private Matrix4x4 pictureView, pictureProjection;

    /// <summary>
    /// Whether the UI keeps the picture it last drew and lays that over the frame for as long as nothing in it
    /// changes, rather than drawing every quad of it again every frame. On unless it is switched off. A UI that
    /// is standing still (most of them, most of the time) then costs one quad a frame however much is in it, and
    /// nothing is sent to the GPU for it at all. What it takes is the memory of a picture the size of the screen.
    /// </summary>
    public bool Retained { get; set; } = true;

    /// <summary>
    /// What <see cref="FrameRateLimit"/> is for every UI made from here on, 120 unless a game says otherwise. Set
    /// it before the UIs are made (as the game starts), the ones there are already keep what they have.
    /// </summary>
    public static float DefaultFrameRateLimit { get; set; } = 120.0f;

    /// <summary>
    /// How many times a second the UI is put together and drawn anew at the most, 0 for as often as there are
    /// frames. A game that runs at three thousand frames a second was blending every moving UI between its last
    /// two ticks, sending the lot to the GPU and drawing every quad of it three thousand times a second, for a
    /// screen that shows a hundred and forty four of them on a good day and a player who reads a health bar at
    /// about four. The frames in between get what was drawn last, the picture of it laid over once more (one
    /// quad, see <see cref="Retained"/>) or the same quads again where there is no picture.
    /// <para>
    /// It is rounded to whole frames, the UI is drawn anew on the frame nearest to when it is due. So a screen of
    /// 144 has it every frame rather than five frames out of six, which is a judder anybody can see, one of 240
    /// has it every other frame, and a game that is slower than the limit never notices there is one.
    /// </para>
    /// <para>
    /// Only the drawing is held back. The pointer, the updates, the laying out and the painting are the
    /// simulation's and go at its rate, a click is felt as soon as ever, and the <see cref="Cursor"/> is drawn
    /// where the mouse is every frame whatever this says. A UI that has been standing still is drawn the moment
    /// something in it changes, there is nothing to wait for then.
    /// </para>
    /// </summary>
    [Inspect(0.0f, 480.0f)]
    public float FrameRateLimit { get; set; } = DefaultFrameRateLimit;

    // When the UI was last put together and sent to the GPU and when it was last asked to draw, by the
    // stopwatch, 0 for never. And how long a frame is these days, smoothed
    private long refreshedAt, renderedAt;
    private double frameSeconds;

    /// <summary>
    /// Helper method to say whether a UI that was last drawn anew a while ago is due again, see
    /// <see cref="FrameRateLimit"/>. It is if this frame is nearer to the moment it is due than the next one
    /// will be, which is where the half a frame comes from.
    /// </summary>
    /// <param name="sinceRefresh">How long ago it was drawn anew, in seconds.</param>
    /// <param name="frameSeconds">How long a frame is.</param>
    /// <param name="limit">How many times a second it may be, 0 for no limit.</param>
    internal static bool RefreshDue(double sinceRefresh, double frameSeconds, float limit) =>
        limit <= 0.0f || sinceRefresh + frameSeconds * 0.5 >= 1.0 / limit;

    /// <summary>
    /// Helper method to note that a frame is being drawn and say whether the UI may be drawn anew in it. Render
    /// thread, once a frame.
    /// </summary>
    private bool NoteFrame()
    {
        long now = Stopwatch.GetTimestamp();

        // A UI nobody drew for a while (a menu that was closed) has not had a frame that long, it had none
        if (renderedAt != 0)
        {
            double took = Math.Min(Stopwatch.GetElapsedTime(renderedAt, now).TotalSeconds, 0.1);
            frameSeconds = frameSeconds <= 0.0 ? took : frameSeconds + (took - frameSeconds) * 0.1;
        }

        renderedAt = now;

        return refreshedAt == 0 || RefreshDue(Stopwatch.GetElapsedTime(refreshedAt, now).TotalSeconds, frameSeconds, FrameRateLimit);
    }

    /// <summary>
    /// The effects the UI goes through before it is laid over whatever is under it, none to begin with.
    /// <code>
    /// compositor.PostProcessing.Add(new BlurEffect());
    /// </code>
    /// They only ever see the UI and leave what is behind it alone. While any of them is on the UI is drawn onto
    /// a layer of its own first (see <see cref="PostLayer"/>) and every component keeps track of how fast it is
    /// going across the screen, by where it ends up being drawn. Layout, tweens and the place of its module all
    /// count. With none on there is no layer and no keeping track, the UI costs what it does without any of this,
    /// and an effect that is only for what moves is skipped (layer and all) whenever nothing in the UI does.
    /// What a component paints inside of its own bounds (the handle of a slider) goes as fast as the component.
    /// </summary>
    public PostProcessor PostProcessing => layer.Effects;

    // Replaced wholesale on every change, so the simulation thread can walk the array it has while
    // another thread creates a module.
    private readonly Lock modulesLock = new();
    private UIModule[] modules = [];

    // The simulation thread paints into `back`, then swaps it in as `front` for the render thread to upload.
    private readonly Lock frameLock = new();
    private UIDrawList back = new();
    private UIDrawList front = new();
    private int paintedFrame;
    private int uploadedFrame;

    private bool pointerWasDown;
    private UIComponent? hovered;
    private UIComponent? pressed;

    /// <summary>The topmost component the pointer is over, null for none. Simulation thread.</summary>
    public UIComponent? HoveredComponent => hovered;

    // The part of a highlighted component its tabs are painted as, a number no component gives a part of its own
    private const int GRIPS_PART = -7004;

    // The component whose edge the pointer is on or has hold of, see UIComponent.Resizable
    private UIComponent? gripped;
    private UIComponent? focus;

    // How long (in seconds) the pointer has to rest on a component before its tooltip comes up, and how far it can
    // drift (in units of the camera) without that counting as moving
    private const float TOOLTIP_DELAY = 0.55f;
    private const float TOOLTIP_DRIFT = 3.0f;

    // How long the pointer has been resting, where it came to rest, and which module is showing a tooltip right now
    private float restingFor;
    private Vector2 restingAt;
    private UIModule? tooltipModule;

    // A tooltip put up by a component rather than the pointer resting on it (a hover asked for with the keyboard), where,
    // and where the pointer was as it went up, it comes down once the pointer moves
    private (UIComponent Owner, string Text, Vector2 At, bool Markdown, Vector2 Pointer)? pinned;

    // Scratch for cutting a tooltip into lines, see UITooltip
    internal List<(int Start, int Length)> TooltipLines { get; } = [];

    // A tooltip that is markdown, laid out once for as long as it is the same text
    internal UIRichText TooltipRich { get; } = new();

    private System.Collections.Frozen.FrozenDictionary<string, string>? strings;

    /// <summary>
    /// Texts of this UI's own, what an <c>@key</c> in it is looked up in before the game's language. For a program whose
    /// UI isn't the game's (an editor that shows a game's layouts in the game's language next to its own UI in its
    /// own), see <see cref="Horizon.Content.Localisation.ReadFile"/>. Null for the game's language alone.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Strings
    {
        get => strings;
        set => strings = value is null ? null : value as System.Collections.Frozen.FrozenDictionary<string, string> ?? System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(value);
    }

    /// <summary>
    /// What a text of this UI is shown as, a key looked up in its <see cref="Strings"/> and then in the game's language,
    /// anything else the text itself. Makes no garbage.
    /// </summary>
    public string Resolve(string text)
    {
        if (strings is not null && text.Length > 1 && text[0] == '@' && text[1] != '@'
            && strings.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(text.AsSpan(1), out string? own))
            return own;

        return Horizon.Content.Localisation.Resolve(text);
    }

    /// <summary>
    /// Called with what was pasted (control with V) while nothing in the UI had the focus. For an editor that pastes
    /// things rather than text. Null for nothing, which is what it is unless somebody sets it.
    /// </summary>
    public Action<string>? Pasted { get; set; }

    public IReadOnlyList<UIModule> Modules => modules;

    /// <summary>The look of the UI, null until <see cref="Initialize"/> has loaded it (or if it failed to).</summary>
    public UISkin? Skin
    {
        get => skin;
        private set => skin = value;
    }

    // Swapped on the render thread, read by the updates, and only ever swapped for one that is all there
    private volatile UISkin? skin;

    /// <summary>
    /// A list as it was at the end of a tick, which frames that are drawn alongside the simulation are drawn from.
    /// </summary>
    private sealed class CapturedList
    {
        public SpriteItem[] Items = new SpriteItem[256];
        public readonly List<UIDrawList.Run> Runs = [];
        public readonly List<TextureAtlas> Atlases = [];
        public readonly List<UIGlassPane> Glass = [];
        public int Count;
        public int Painted = -1;
        public UISkin? Skin;
        public bool Incomplete;

        public ReadOnlySpan<SpriteItem> Span => Items.AsSpan(0, Count);
    }

    private readonly SnapshotBuffer<CapturedList> captured = new(static () => new CapturedList());

    // What of the captured lists was uploaded last, which two lists and how far between them, -1 for something else.
    // And where blended lists are put together
    private int shownBefore = -1, shownAfter = -1;
    private float shownAlpha = float.NaN;
    private SpriteItem[] blended = [];

    /// <summary>
    /// Where the pointer comes from. The mouse unless set to something else, which could be a cursor
    /// moved with a gamepad, or a recording.
    /// </summary>
    public Func<UIPointer>? PointerSource { get; set; }

    /// <summary>
    /// Whether the UI has the pointer. It is over a component that stops it, or it is still held
    /// after being pressed on one. The game can use this to keep clicks on the UI to the UI.
    /// </summary>
    public bool IsPointerOverUI { get; private set; }

    /// <summary>
    /// The component that gets what is typed on the keyboard, null when nothing does. Pressing the
    /// pointer on a component that takes the focus sets it, pressing anywhere else clears it.
    /// </summary>
    public UIComponent? Focus
    {
        get => focus;
        set
        {
            if (focus == value)
                return;

            // Whatever was typed before now was not meant for this component.
            UIKeyboard.Clear();
            focus = value;

            // So the paste of an editor knows whether a text box somewhere has the keyboard
            if (value is not null)
                focusOwner = this;
            else if (focusOwner == this)
                focusOwner = null;
        }
    }

    // The compositor whose component has the keyboard, null while none does. There is one keyboard, so one of these
    private static UICompositor? focusOwner;

    // Whether the skin is whichever one the game is set to (UISkins) and not one this UI asked for, the theme it
    // would like of that one, and which change of the setting it has caught up with
    private bool followsSkins;
    private string? wantedTheme;
    private int skinsVersion;

    /// <summary>
    /// A UI in the skin the game is set to, see <see cref="UISkins"/>, which is the Dead Revolver one until
    /// somebody picks another. It changes its look along with that setting.
    /// </summary>
    /// <param name="viewportCamera">The camera the UI is drawn with. What it sees is the screen the modules are laid out against.</param>
    /// <param name="theme">
    /// Which theme of the skin to draw the UI in ("red", "gold"), null for the one the game is set to. A skin
    /// that has no theme of that name is drawn in the one the game is set to as well.
    /// </param>
    public UICompositor(in Camera2D viewportCamera, string? theme = null)
    {
        this.viewportCamera = viewportCamera;
        followsSkins = true;
        wantedTheme = theme;
        skinsVersion = UISkins.Version;
        (skinDirectory, skinFile, skinTheme) = UISkins.Resolve(theme);
    }

    /// <param name="viewportCamera">The camera the UI is drawn with. What it sees is the screen the modules are laid out against.</param>
    /// <param name="skinDirectory">The directory holding the skin definition and its art.</param>
    /// <param name="skinFile">The name of the skin definition in that directory.</param>
    /// <param name="theme">Which of the skin's themes to use, null for the one it says is its usual one.</param>
    public UICompositor(in Camera2D viewportCamera, string skinDirectory, string skinFile, string? theme = null)
    {
        this.viewportCamera = viewportCamera;
        this.skinDirectory = skinDirectory;
        this.skinFile = skinFile;
        this.skinTheme = theme;
    }

    // Whether the camera is the compositor's own (see ForScreen), which it keeps the size of the window
    private bool ownsCamera;

    /// <summary>
    /// A UI laid out against the whole window, with a camera of its own that it keeps the size of the window. For a
    /// UI that isn't drawn through any camera of the scene (a HUD, a menu laid over everything), which saves making
    /// one and remembering to resize it. The window can be resized under it, it follows on the next update.
    /// </summary>
    /// <param name="theme">Which theme of the usual skin to draw the UI in, null for the one the skin says is its usual one.</param>
    public static UICompositor ForScreen(string? theme = null) =>
        new(new Camera2D(GameEngine.Instance.WindowManager.ViewportSize), theme) { ownsCamera = true };

    /// <inheritdoc cref="ForScreen(string?)"/>
    /// <param name="skinDirectory">The directory holding the skin definition and its art.</param>
    /// <param name="skinFile">The name of the skin definition in that directory.</param>
    public static UICompositor ForScreen(string skinDirectory, string skinFile, string? theme = null) =>
        new(new Camera2D(GameEngine.Instance.WindowManager.ViewportSize), skinDirectory, skinFile, theme) { ownsCamera = true };

    public UIModule CreateModule()
    {
        var module = new UIModule(this);

        lock (modulesLock)
            modules = [.. modules, module];

        return module;
    }

    public bool RemoveModule(UIModule module)
    {
        lock (modulesLock)
        {
            int index = Array.IndexOf(modules, module);
            if (index < 0)
                return false;

            modules = [.. modules[..index], .. modules[(index + 1)..]];
            return true;
        }
    }

    public override void Initialize()
    {
        // The one every UI with this skin draws with, see UISkin.Shared
        Skin = UISkin.Shared(skinDirectory, skinFile, skinTheme);

        renderer.Initialize();

        UIKeyboard.Hook();
    }

    /// <summary>
    /// Swaps the look of the whole UI for another skin, such as another theme of the same art. Safe
    /// from any thread, the skin is loaded the next time the UI is drawn, and if it can't be the
    /// current one stays.
    /// </summary>
    /// <param name="directory">The directory holding the skin definition and its art.</param>
    /// <param name="file">The name of the skin definition in that directory.</param>
    /// <param name="theme">Which of the skin's themes to use, null for the one it says is its usual one.</param>
    public void SetSkin(string directory, string file, string? theme = null)
    {
        lock (skinLock)
        {
            // A skin of its own from here on, whatever the game is set to
            followsSkins = false;
            requestedSkin = (directory, file, theme);
        }
    }

    /// <summary>
    /// Goes (back) to the skin the game is set to, see <see cref="UISkins"/>, and follows it from here on. Safe
    /// from any thread, like <see cref="SetSkin"/>.
    /// </summary>
    /// <param name="theme">Which theme of it this UI would like, null for the one the game is set to.</param>
    public void FollowSkins(string? theme = null)
    {
        lock (skinLock)
        {
            followsSkins = true;
            wantedTheme = theme;
            skinsVersion = UISkins.Version;
            requestedSkin = UISkins.Resolve(theme);
        }
    }

    /// <summary>Whether the UI is in the skin the game is set to and not in one of its own.</summary>
    public bool FollowsSkins => followsSkins;

    /// <summary>
    /// Swaps the UI over to another theme of the skin it has, see <see cref="UISkin.Themes"/> for the ones
    /// there are. Safe from any thread, like <see cref="SetSkin"/>.
    /// </summary>
    public void SetTheme(string? theme)
    {
        lock (skinLock)
        {
            // Following the game's skin it is the theme this UI would like of whichever skin that is
            if (followsSkins)
            {
                wantedTheme = theme;
                requestedSkin = UISkins.Resolve(theme);
            }
            else
            {
                requestedSkin = (skinDirectory, skinFile, theme);
            }
        }
    }

    private void LoadRequestedSkin()
    {
        (string Directory, string File, string? Theme)? request;
        lock (skinLock)
        {
            // The game was set to another skin since this UI last looked
            if (followsSkins && skinsVersion != UISkins.Version)
            {
                skinsVersion = UISkins.Version;
                requestedSkin = UISkins.Resolve(wantedTheme);
            }

            request = requestedSkin;
            requestedSkin = null;
        }

        if (request is not { } wanted || UISkin.Shared(wanted.Directory, wanted.File, wanted.Theme) is not { } skin)
            return;

        (skinDirectory, skinFile, skinTheme) = wanted;

        // What was painted so far shows art of the old skin's atlas, not of this one's
        Skin = skin;
        renderer.Clear();
        pictureCurrent = false;

        // And the first list painted with the new one goes up as soon as it is there, limit or no limit
        refreshedAt = 0;
    }

    public override void UpdateState(float dt)
    {
        // A camera of its own is kept the size of the window, here on the simulation thread where it is read
        if (ownsCamera && GameEngine.Instance.WindowManager.ViewportSize is { X: > 0.0f, Y: > 0.0f } window && viewportCamera.ViewSize != window)
            viewportCamera.ViewSize = window;

        if (Skin is not { } skin)
            return;

        paintFrame++;

        var snapshot = modules;

        // What the camera sees. Its bounds start at the bottom left corner, the world being Y-up.
        var bounds = viewportCamera.Bounds;
        var screen = new UIRect(
            new Vector2(bounds.X, bounds.Y),
            new Vector2(bounds.X + bounds.Width, bounds.Y + bounds.Height));

        // Worked out before anything else uses it. The pointer has to be scaled the same way the layout is.
        float fit = DesignSize is { X: > 0.0f, Y: > 0.0f } design && !screen.IsEmpty
            ? MathF.Min(screen.Width / design.X, screen.Height / design.Y)
            : 1.0f;
        DisplayScale = DesignSize is null && GameEngine.Instance?.WindowManager is { ScalesToDisplay: true } scaled ? scaled.DisplayScale : 1.0f;
        UIScale = MathF.Max(0.01f, fit * Scale * DisplayScale);

        // Every module that was designed for a screen of its own works out its own scale the same way
        foreach (var module in snapshot)
            module.FitTo(screen);

        // The pointer is tested against the layout of the previous update, which is the one on screen.
        RoutePointer(snapshot);
        RouteKeyboard(snapshot);
        UpdateTooltip(snapshot, dt);

        foreach (var module in snapshot)
        {
            if (module.Enabled)
                module.Update(dt);
        }

        // Laid out and painted once everything has had its update, at the end of the tick, see Capture. A UI
        // nobody captures (set up with the simulation standing still, or updated and drawn by hand) is painted
        // here and now, as it always was
        pending = new PendingPaint(snapshot, screen, skin, dt);
        if (!capturedBefore)
            PaintPending();
    }

    private readonly record struct PendingPaint(UIModule[] Modules, UIRect Screen, UISkin Skin, float Delta);

    // What the last update left to be laid out and painted, and whether this UI is captured at the end of its ticks
    private PendingPaint? pending;
    private bool capturedBefore;

    /// <summary>
    /// Helper method to lay the UI out and paint it into the list the next frame is drawn from, with whatever the
    /// last update handed out. Simulation thread.
    /// </summary>
    private void PaintPending()
    {
        if (pending is not { } paint)
            return;

        pending = null;
        var (snapshot, screen, skin, dt) = paint;

        // One clock for both lists, see UIDrawList.Begin
        paintClock = (paintClock + dt) % 3600.0f;
        back.Begin(skin, paintFrame, dt, paintClock);
        foreach (var module in snapshot)
        {
            if (!module.Enabled)
                continue;

            module.Layout(screen, skin);
            module.Paint(back);

            // What is drawn about a module is cut off where the module is. A layout in an editor's canvas is, and
            // the boxes of the layout debugger and the mark round what is picked were not, they ran on out of the
            // canvas over the lists next to it and whatever was behind
            if (module.Clip is { } cut)
                back.PushClip(cut);

            // On top of the module and with its transform, so the layout lines up with what it is the layout of.
            if (module.ShowInLayoutDebugger && LayoutOverlay is { } overlay)
                UILayoutOverlay.Paint(back, module, overlay, module.ToLocal(lastPointer));

            // Whatever an editor has picked out is marked whether the debugger is open or not.
            if (Highlighted is { } highlighted && highlighted.Module == module)
            {
                UILayoutOverlay.PaintHighlight(back, highlighted);

                // With the tabs of the edges an editor lets it be dragged by
                // As a part of it that is nobody else's, like everything that is drawn about a component
                if (HighlightGrips != UIResize.None)
                {
                    back.PushOwner(highlighted);
                    back.BeginPart(GRIPS_PART);
                    highlighted.PaintGrips(back, HighlightGrips, HighlightLitGrips);
                    back.EndParts();
                    back.PopOwner();
                }
            }

            // Through the interface an enumerator is an object, every tick, so only when there is something in it
            if (Highlights.Count > 0) foreach (var marked in Highlights)
            {
                if (marked.Module == module && marked != Highlighted)
                    UILayoutOverlay.PaintHighlight(back, marked);
            }

            if (module.Clip is not null)
                back.PopClip();
        }
        back.End();

        // Nothing happened. What was painted is what is on screen already, and nobody needs to hear about it
        if (paintedFrame > 0 && back.SameAs(front))
            return;

        // Something did, a window that only draws when something changed has frames to draw (see RunMode.Application)
        GameEngine.Instance?.WindowManager?.NoteChange();

        lock (frameLock)
        {
            (front, back) = (back, front);
            paintedFrame++;
        }
    }

    /// <summary>
    /// Publishes the list that was painted last, for the frames that are drawn alongside the simulation. Simulation
    /// thread, at the end of every tick. A UI that is updated and drawn by hand has to call this by hand as well (at the
    /// end of the tick, from the <c>Capture</c> of whatever owns it), or it is drawn from its newest list as it always was.
    /// </summary>
    public override void Capture()
    {
        // From here on the painting waits for this, the end of the tick
        capturedBefore = true;
        PaintPending();

        if (captured.BeginPublish() is not { } into)
            return;

        lock (frameLock)
        {
            // Every slot is written to every tick, but a UI that stands still has nothing new to copy
            if (into.Painted == paintedFrame && into.Skin == front.Skin)
                return;

            ReadOnlySpan<SpriteItem> items = front.Items;
            if (into.Items.Length < items.Length)
                into.Items = new SpriteItem[(int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)items.Length)];

            items.CopyTo(into.Items);
            into.Count = items.Length;

            into.Runs.Clear();
            foreach (var run in front.Runs)
                into.Runs.Add(run);

            into.Atlases.Clear();
            foreach (var atlas in front.Atlases)
                into.Atlases.Add(atlas);

            into.Glass.Clear();
            foreach (var pane in front.GlassPanes)
                into.Glass.Add(pane);

            into.Painted = paintedFrame;
            into.Skin = front.Skin;
            into.Incomplete = front.Incomplete;
        }
    }

    /// <summary>
    /// Helper method to upload what a frame that is drawn alongside the simulation shows, the last two captured lists
    /// blended to the moment between them. Only when that is something else than what was uploaded last.
    /// </summary>
    /// <param name="due">Whether the UI may be drawn anew this frame, see <see cref="FrameRateLimit"/>.</param>
    /// <returns>False if nothing was captured yet, the UI is drawn from its newest list then.</returns>
    private bool UploadCaptured(in RenderFrame frame, UISkin skin, bool due)
    {
        if (!captured.TryGet(frame, out CapturedList before, out CapturedList after, out bool continuous))
            return false;

        // Not to be shown until it is the UI as it is meant to look
        if (after.Painted == 0 || after.Incomplete || after.Skin != skin)
        {
            GameEngine.Instance.SceneManager.ReportUnfinished();
            return true;
        }

        // Two lists of the same quads are blended one with the other as they come. Two that aren't (a number in a
        // corner changed, something lit up under the pointer) are blended quad by quad wherever the newer one has
        // a quad the older one had, by who painted it, and the rest of it is shown as it is now. Not blending such
        // lists at all was how one clock in a HUD had the whole UI drawn in steps a tick apart
        bool sameList = before.Painted == after.Painted;
        bool canBlend = continuous && !sameList && before.Skin == skin && !before.Incomplete;

        // A list that changed in steps is shown as it was until the moment is all the way at the newer one
        CapturedList shown = sameList || canBlend || frame.Alpha >= 1.0f || before.Skin != skin || before.Incomplete ? after : before;
        float alpha = canBlend ? frame.Alpha : 1.0f;

        int from = canBlend ? before.Painted : shown.Painted;
        if (from == shownBefore && shown.Painted == shownAfter && alpha == shownAlpha)
            return true;

        // Not yet, see FrameRateLimit. What is up there already is shown once more and this is asked again the
        // next frame, so whatever the UI comes to rest as gets there a hundred and twentieth of a second late
        // at the worst
        if (!due)
            return true;

        if (canBlend && alpha < 1.0f)
        {
            if (blended.Length < after.Count)
                blended = new SpriteItem[(int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)after.Count)];

            ReadOnlySpan<SpriteItem> a = before.Span, b = after.Span;
            if (!Blendable(before, after))
            {
                BlendByKey(a, b, alpha);
            }
            else for (int i = 0; i < b.Length; i++)
            {
                // The same quad if it shows the same thing the same way, then only where it is, how big and what colour
                // moved. One that is suddenly somewhere else entirely (a menu that was folded away off screen and
                // popped up) is not shown flying over, it was put there, see SpriteItem.CanBlend
                blended[i] = a[i].Flags == b[i].Flags && a[i].TexMin == b[i].TexMin && a[i].TexMax == b[i].TexMax && SpriteItem.CanBlend(a[i], b[i])
                    ? SpriteItem.Blend(a[i], b[i], alpha)
                    : b[i];
            }

            renderer.Upload(blended.AsSpan(0, b.Length), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(after.Runs), skin, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(after.Atlases), BlendGlass(before, after, alpha));
        }
        else
        {
            renderer.Upload(shown.Span, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(shown.Runs), skin, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(shown.Atlases), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(shown.Glass));
        }

        (shownBefore, shownAfter, shownAlpha) = (from, shown.Painted, alpha);
        pictureCurrent = false;
        refreshedAt = renderedAt;
        return true;
    }

    // Where the panes of glass of two lists blended are put together
    private readonly List<UIGlassPane> blendedGlass = [];

    /// <summary>
    /// Helper method for the panes of glass of a frame between two ticks. Blended one with the other when the two lists
    /// have as many, so a dialog that slides in has its glass slide with it, and the newer list's as they are otherwise.
    /// </summary>
    private ReadOnlySpan<UIGlassPane> BlendGlass(CapturedList before, CapturedList after, float alpha)
    {
        if (before.Glass.Count != after.Glass.Count)
            return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(after.Glass);

        blendedGlass.Clear();
        for (int i = 0; i < after.Glass.Count; i++)
            blendedGlass.Add(UIGlassPane.Blend(before.Glass[i], after.Glass[i], alpha));

        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(blendedGlass);
    }

    // Where every quad of the older list is, by who painted it as which of its quads, for lists that aren't the same quads
    private readonly Dictionary<Vector2, int> byKey = [];

    /// <summary>
    /// Helper method to blend the quads of the newer list with the ones of the older list the same component painted
    /// as the same one of its quads, wherever the older list has it. What is new is shown as it is.
    /// </summary>
    private void BlendByKey(ReadOnlySpan<SpriteItem> a, ReadOnlySpan<SpriteItem> b, float alpha)
    {
        byKey.Clear();
        for (int i = 0; i < a.Length; i++)
            byKey.TryAdd(a[i].Key, i);

        for (int i = 0; i < b.Length; i++)
        {
            blended[i] = byKey.TryGetValue(b[i].Key, out int was) &&
                a[was].Flags == b[i].Flags && a[was].TexMin == b[i].TexMin && a[was].TexMax == b[i].TexMax && SpriteItem.CanBlend(a[was], b[i])
                    ? SpriteItem.Blend(a[was], b[i], alpha)
                    : b[i];
        }
    }

    /// <summary>
    /// Helper method to say whether two lists are the same quads (the same many, in the same runs, every one painted
    /// by the same component as the same one of its quads) so that they can be blended one quad with the other.
    /// Blending by place in the list alone was how a hover that put one quad in or took one out had every quad
    /// after it blended with its neighbour, a button's background halfway to the next one's, a bar across the
    /// row for a frame. A list that isn't the same quads is shown as it is, the step is a tick long and nobody sees it.
    /// </summary>
    private static bool Blendable(CapturedList before, CapturedList after)
    {
        if (before.Count != after.Count ||
            !System.Runtime.InteropServices.CollectionsMarshal.AsSpan(before.Runs).SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(after.Runs)))
            return false;

        ReadOnlySpan<SpriteItem> a = before.Span, b = after.Span;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Key != b[i].Key) return false;
        }

        return true;
    }

    /* For whoever is working on a layout rather than using it, an editor say */

    /// <summary>
    /// What of its layout the UI draws over itself (the edges of its components, their padding, the gaps between
    /// them), null for none of it, which is what it is unless somebody sets it. Only the modules that have
    /// <see cref="UIModule.ShowInLayoutDebugger"/> on are drawn over. Works the same in every build.
    /// </summary>
    public UILayoutOverlayOptions? LayoutOverlay { get; set; }

    /// <summary>
    /// A component of this UI that is marked out from the rest, null for none. What an editor has selected.
    /// </summary>
    public UIComponent? Highlighted { get; set; }

    /// <summary>
    /// The edges of <see cref="Highlighted"/> that get a tab drawn on them, the way a component that is
    /// <see cref="UIComponent.Resizable"/> has them, for an editor that lets what it marks out be dragged bigger.
    /// The editor does the dragging (<see cref="UIComponent.EdgesAt"/>, <see cref="UIComponent.BeginResize"/>).
    /// </summary>
    public UIResize HighlightGrips { get; set; }

    /// <summary>Which of <see cref="HighlightGrips"/> are lit, the pointer being on them or having hold of them.</summary>
    public UIResize HighlightLitGrips { get; set; }

    /// <summary>
    /// More components marked out the way <see cref="Highlighted"/> is, for an editor with several selected.
    /// Replace the collection rather than changing it, it's read while the UI paints.
    /// </summary>
    public IReadOnlyCollection<UIComponent> Highlights { get; set; } = [];

    /// <summary>
    /// Called when the other button of the pointer (right click) goes down and nothing under it handled it with
    /// <see cref="UIComponent.OnContextMenu"/>. With the innermost component under the pointer (null if none) and
    /// where the pointer is, in the camera's world. Show a <see cref="ContextMenu"/> from here.
    /// </summary>
    public Action<UIComponent?, Vector2>? ContextRequested { get; set; }

    // Whether the other button was down as of the last update
    private bool secondaryWasDown;

    public override void Render(float dt)
    {
        LoadRequestedSkin();

        if (Skin is not { } skin)
            return;

        RenderFrame frame = RenderFrame.Active;
        bool due = NoteFrame();

        if (frame.IsDecoupled)
        {
            // Art that was asked for while painting is stitched into the atlas now, whichever list is shown
            skin.Update();

            if (UploadCaptured(frame, skin, due))
            {
                DrawUploaded(dt);
                return;
            }
        }

        lock (frameLock)
        {
            // Art that was asked for while painting is stitched into the atlas now. A frame that was painted
            // while any was missing is skipped, what was shown before it stays up until the next one has it.
            skin.Update();

            // A new list that isn't due yet (see FrameRateLimit) waits here until it is, it is not forgotten
            if ((uploadedFrame != paintedFrame || shownAfter >= 0) && due)
            {
                // A frame painted with a skin that has been swapped out since is skipped too.
                if (front.Skin == skin && !front.Incomplete)
                {
                    renderer.Upload(front);
                    pictureCurrent = false;
                    refreshedAt = renderedAt;
                }

                uploadedFrame = paintedFrame;

                // What was uploaded is the newest list, not one that was captured
                shownBefore = shownAfter = -1;
                shownAlpha = float.NaN;
            }

            // Nothing painted yet, or what was painted last is still waiting for its art. What is about to be
            // drawn is not the UI as it is meant to look. A scene that was just set isn't shown like that
            if (paintedFrame == 0 || front.Incomplete)
                GameEngine.Instance.SceneManager.ReportUnfinished();
        }

        DrawUploaded(dt);
    }

    /// <summary>
    /// Helper method to draw what was uploaded last, or lay the picture of it over the frame once more if it is still
    /// what the UI looks like.
    /// </summary>
    private void DrawUploaded(float dt)
    {
        using var scope = GameEngine.Instance.Graphics.BeginGpuScope("ui");

        DrawList(dt);
        DrawCursor();
    }

    /// <summary>
    /// A pointer drawn over this UI where the mouse is as each frame is drawn, null for none, which is what it is
    /// unless somebody sets one. See <see cref="UICursor"/> for why it isn't a component like everything else.
    /// </summary>
    public UICursor? Cursor { get; set; }

    // What the cursor is drawn with, made the first time there is one to draw
    private Horizon.Rendering.Spriting.SpriteBatch? cursorBatch;

    /// <summary>
    /// Helper method to draw the cursor, straight over whatever the UI put on screen, from where the mouse is now.
    /// </summary>
    private void DrawCursor()
    {
        if (Cursor is not { Visible: true } cursor)
            return;

        Vector2 window = GameEngine.Instance.WindowManager.WindowSize;
        var seen = viewportCamera.Bounds;
        if (window.X <= 0.0f || window.Y <= 0.0f || seen.Width <= 0.0f)
            return;

        if (cursorBatch is null)
        {
            cursorBatch = new Horizon.Rendering.Spriting.SpriteBatch();
            cursorBatch.Initialize();
            cursorBatch.InitializeAll();
        }

        // Out of the window and into what the camera sees, which has its Y going up
        Vector2 at = cursor.Position?.Invoke() ?? GameEngine.Instance.Input.Mouse.LivePosition;
        Vector2 point = new(seen.X + at.X / window.X * seen.Width, seen.Y + (1.0f - at.Y / window.Y) * seen.Height);

        Horizon.Graphics.Texture? texture = cursor.Texture is { IsValid: true } picture ? picture : null;
        Vector2 size = (cursor.Size != Vector2.Zero ? cursor.Size : texture?.Size ?? new Vector2(12.0f)) * UIScale;
        Vector2 hotspot = cursor.Hotspot * UIScale;

        // The hotspot is from the top left, the quad from its bottom left
        Vector2 min = new(point.X - hotspot.X, point.Y + hotspot.Y - size.Y);
        SpriteItem item = SpriteItem.Rectangle(
            min,
            min + size,
            Vector2.Zero,
            texture?.Size ?? Vector2.Zero,
            SpriteItem.PackColor(cursor.Tint),
            texture is null ? SpriteItem.NoTexture : 0u);

        var before = Horizon.Graphics.RenderState.Save();
        Horizon.Graphics.RenderState.Blend = true;
        Horizon.Graphics.RenderState.BlendMode = Horizon.Graphics.BlendMode.Alpha;
        Horizon.Graphics.RenderState.DepthTest = false;

        ReadOnlySpan<SpriteItem> items = [item];
        ReadOnlySpan<SpriteTexture> textures = texture is null ? [] : [new SpriteTexture(texture)];
        cursorBatch.Draw(items, textures, viewportCamera);

        Horizon.Graphics.RenderState.Restore(before);
    }

    /// <summary>
    /// Helper method to draw the list that was uploaded last, or lay the picture of it over the frame once more.
    /// </summary>
    private void DrawList(float dt)
    {
        // A UI with nothing on it (an overlay that's switched off, say) draws nothing, and in particular doesn't lay an
        // empty picture over the whole screen every frame, which is a full screen pass for fuck all. It's painted
        // afresh the moment it has something again
        if (renderer.IsEmpty)
        {
            pictureCurrent = false;
            return;
        }

        // Seen through a camera that has changed, what was drawn before is not what the UI looks like any more
        if (viewportCamera.View != pictureView || viewportCamera.Projection != pictureProjection)
            pictureCurrent = false;

        // Nothing has changed since the picture on the layer was drawn. That is laid over the frame as it is,
        // and none of the UI is drawn again
        if (Retained && pictureCurrent && layer.Replay(dt))
            return;

        // Onto its own layer, through its effects if it has any that are on, and kept there to be laid over
        // again. Straight over the scene where there is no layer to be had (inside of a renderer that lights
        // its picture, or a UI that asked not to be kept)
        Renderer2D? output = Renderer2D.Current;
        bool layered = layer.Begin(Retained);

        if (renderer.Glass.IsEmpty || (!layered && output is { HoldsPicture: false }))
        {
            renderer.Draw(viewportCamera);
            GlassDrawn = default;
        }
        else
            DrawGlassy(layered ? layer.Under : output, layered ? layer.Picture : null);

        if (layered)
            layer.End(dt);

        // Only a picture that made it onto the layer is there to be shown again. One with glass in it shows what was
        // behind the glass, which a scene that moves under it doesn't keep still for, see GlassKeepsPicture
        pictureCurrent = layered && (renderer.Glass.IsEmpty || GlassKeepsPicture);
        (pictureView, pictureProjection) = (viewportCamera.View, viewportCamera.Projection);
    }

    /// <summary>
    /// Whether a UI with glass in it (see <see cref="UIGlass"/>) keeps the picture it drew and lays that over for as
    /// long as nothing in the UI changes, the way it does without (see <see cref="Retained"/>). Off unless asked for,
    /// the glass then shows the scene as it was when the UI last changed, which is right for an application whose
    /// UI is all there is (Hex), and wrong for a menu over a game that goes on moving under it.
    /// </summary>
    public bool GlassKeepsPicture { get; set; }

    /// <summary>
    /// What a dialog, a menu that drops down and anything else that pops up over the rest of this UI sits on, null
    /// for nothing (a solid panel, as they always were). See <see cref="UIGlass"/>.
    /// </summary>
    public UIGlass? PopupGlass { get; set; }

    // What puts the glass down, made the first time there is any
    private UIGlassPass? glassPass;

    /// <summary>
    /// How many panes of glass the UI put down the last time it was drawn anew, and how many blurs that took. For a test
    /// that wants to know the glass is there, read from anywhere.
    /// </summary>
    public (int Panes, int Blurs) GlassDrawn { get; private set; }

    /// <summary>
    /// Helper method to draw a list with glass in it, in goes. A stretch of quads, a pane, the next stretch, and so on,
    /// every pane blurring what was drawn before it.
    /// </summary>
    /// <param name="output">What the frame under the UI is drawn into, a renderer or (null) the window.</param>
    /// <param name="picture">The layer the UI is drawn onto, null for straight over the frame.</param>
    private void DrawGlassy(Renderer2D? output, RenderTarget? picture)
    {
        glassPass ??= new UIGlassPass();
        glassPass.Reset();

        ReadOnlySpan<UIGlassPane> panes = renderer.Glass;
        ReadOnlySpan<SpriteItem> items = renderer.Uploaded;

        bool sent = renderer.Begin();
        int from = 0, blurredBy = -1, blurs = 0;

        // What is under every pane, for keeping its blur from one frame to the next. Only when what is behind the UI
        // holds still (GlassKeepsPicture), the UI can't see a scene that moves under it
        ulong under = GlassKeepsPicture ? Seed(output) : 0;
        int hashed = 0;

        for (int i = 0; i < panes.Length; i++)
        {
            int start = Math.Clamp(panes[i].Start, from, items.Length);
            if (sent)
                renderer.DrawRange(from, start, viewportCamera);

            if (under != 0)
            {
                under = Mix(under, items[hashed..start]);
                under = Mix(under, panes[i]);
                hashed = start;
            }

            bool fresh = blurredBy < 0 || !SameBlur(panes, items, blurredBy, i);
            glassPass.Draw(panes[i], i, under, viewportCamera, output, picture, fresh);
            if (fresh)
            {
                blurredBy = i;
                blurs++;
            }

            from = start;
        }

        if (sent)
            renderer.DrawRange(from, items.Length, viewportCamera);

        renderer.End();
        GlassDrawn = (panes.Length, blurs);
    }

    /// <summary>
    /// Helper method to say whether a pane can show the blur an earlier one made, which it can when it smears as far and
    /// nothing drawn since that blur (quads or panes) is anywhere near it. Panes side by side (a side bar and the panel
    /// under the editor) are one copy and one blur then, not one each.
    /// </summary>
    private static bool SameBlur(ReadOnlySpan<UIGlassPane> panes, ReadOnlySpan<SpriteItem> items, int blurredBy, int pane)
    {
        UIGlassPane at = panes[pane];
        if (at.Blur != panes[blurredBy].Blur)
            return false;

        // As far round it as the blur reaches, what is there is smeared into it
        Vector2 reach = new(at.Blur * 2.0f);
        Vector2 min = Vector2.Min(at.Min, at.Max) - reach, max = Vector2.Max(at.Min, at.Max) + reach;

        for (int i = blurredBy; i < pane; i++)
        {
            if (Overlaps(Vector2.Min(panes[i].Min, panes[i].Max), Vector2.Max(panes[i].Min, panes[i].Max), min, max))
                return false;
        }

        int end = Math.Min(at.Start, items.Length);
        for (int i = Math.Max(0, panes[blurredBy].Start); i < end; i++)
        {
            ref readonly SpriteItem item = ref items[i];
            Vector2 a = item.Origin, b = item.Origin + item.AxisX, c = item.Origin + item.AxisY, d = b + item.AxisY;
            if (Overlaps(Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)), Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d)), min, max))
                return false;
        }

        return true;
    }

    // Bumped by ForgetGlass, so nothing that was kept matches any more
    private ulong glassGeneration = 1;

    /// <summary>
    /// Has every pane of glass blur what is behind it again on the next frame, for an application whose UI keeps its
    /// glass (<see cref="GlassKeepsPicture"/>) and that knows something behind the UI changed that the UI can't see.
    /// </summary>
    public void ForgetGlass() => glassGeneration++;

    // Helper method for what every hash of what is under the glass starts from, how big the picture is and which
    // generation of it, see ForgetGlass
    private ulong Seed(Renderer2D? output)
    {
        Vector2 size = Renderer2D.OutputSize(output);
        return Mix(Mix(0x9E3779B97F4A7C15UL, glassGeneration), ((ulong)(uint)size.X << 32) | (uint)size.Y) | 1;
    }

    private static ulong Mix(ulong hash, ulong value) => (hash ^ value) * 0x100000001B3UL + (hash >> 29);

    // Helper method to fold quads into a hash, as the numbers they are, eight bytes at a time
    private static ulong Mix(ulong hash, ReadOnlySpan<SpriteItem> items)
    {
        ReadOnlySpan<byte> bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(items);
        ReadOnlySpan<ulong> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bytes);
        foreach (ulong word in words)
            hash = Mix(hash, word);
        for (int i = words.Length * 8; i < bytes.Length; i++)
            hash = Mix(hash, bytes[i]);
        return hash | 1;
    }

    private static ulong Mix(ulong hash, in UIGlassPane pane)
    {
        hash = Mix(hash, (ulong)BitConverter.SingleToUInt32Bits(pane.Min.X) << 32 | BitConverter.SingleToUInt32Bits(pane.Min.Y));
        hash = Mix(hash, (ulong)BitConverter.SingleToUInt32Bits(pane.Max.X) << 32 | BitConverter.SingleToUInt32Bits(pane.Max.Y));
        hash = Mix(hash, (ulong)BitConverter.SingleToUInt32Bits(pane.Blur) << 32 | BitConverter.SingleToUInt32Bits(pane.Opacity));
        return hash | 1;
    }

    private static bool Overlaps(Vector2 aMin, Vector2 aMax, Vector2 bMin, Vector2 bMax) =>
        aMin.X < bMax.X && aMax.X > bMin.X && aMin.Y < bMax.Y && aMax.Y > bMin.Y;

    /// <summary>
    /// Frees what the UI has on the GPU, the layer it is drawn onto for its effects, along with the effects.
    /// Render thread. Its skin is not its own (see <see cref="UISkin.Shared"/>) and stays.
    /// </summary>
    public void Dispose()
    {
        layer.Dispose();
        glassPass?.Dispose();
        glassPass = null;

        renderer.Clear();
        Skin = null;

        GC.SuppressFinalize(this);
    }
}
