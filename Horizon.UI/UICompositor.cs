using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

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
using Horizon.UI.Diagnostics;
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
    /// Makes the whole UI bigger or smaller by a factor, on top of what <see cref="DesignSize"/> and the screen's own
    /// <see cref="DisplayScale"/> do: the interface size the player or the person using a program picks, 1 for 100%, the
    /// size the system would have things on that screen.
    /// <para>
    /// It is the one scale of the UI's. Everything a module lays out, its boxes, its lines and every word, is in units of
    /// the layout and comes out <see cref="UIScale"/> times as big, so a component that wants to stay the size the system's
    /// own parts are whatever the setting (<see cref="TitleBar.PixelSized"/>) is the exception and says so. Text
    /// stays sharp at any of it, letters are made at the size they come to on the screen. A zoom of a component's own (an
    /// editor's code, a terminal's font) is a size of its text in those units and goes on top of this, not instead of it.
    /// </para>
    /// </summary>
    public float Scale { get; set; } = 1.0f;

    /// <summary>
    /// How quickly the UI's own movements go, every component's hover and slide, 1 as they're made, 2 twice as quick,
    /// 0 for none at all (a hover is lit at once and a drawer is open or shut). For a setting that lets the player
    /// have things move as they like, or keep still. A component asks it through <see cref="UIComponent.MotionTime"/>.
    /// </summary>
    public float AnimationSpeed { get; set; } = 1.0f;

    /// <summary>
    /// How much of a button's give is kept, the bump as the pointer comes over it and the squeeze as it's pressed: 1 as
    /// they're made, less for a quieter UI, 0 for none. What suits a game's few big buttons is a lot on a tool with
    /// hundreds of small ones, where every pass of the pointer sets a row of them going.
    /// </summary>
    public float ButtonGive { get; set; } = 1.0f;

    /// <summary>Mouse-wheel distance for content scrolling. Zoom and value-editing gestures keep their own units.</summary>
    public float ScrollSpeed { get; set; } = 1.0f;

    /// <summary>Middle-mouse autoscroll speed, relative to its default distance-driven motion.</summary>
    public float MiddleScrollSpeed { get; set; } = 1.0f;

    /// <summary>
    /// How much bigger than it was laid out the UI is drawn right now, <see cref="Scale"/> times whatever
    /// fitting the <see cref="DesignSize"/> comes to, times the <see cref="DisplayScale"/>. A unit of the layout is
    /// this many units of the camera, and of the window's pixels for a UI drawn straight into the window. Applied as the
    /// transform of every module (<see cref="UIModule.UnitScale"/>), never by tweening a component's own size, which would
    /// draw its letters out of a picture made at another size.
    /// </summary>
    public float UIScale { get; private set; } = 1.0f;

    /// <summary>
    /// How much bigger the screen the window is on wants things drawn, 1.5 on one Windows scales by 150%. Only for a
    /// window that follows the scale (<c>WindowManagerConfiguration.ScaleToDisplay</c>) and a UI with no
    /// <see cref="DesignSize"/>, which is fitted to the screen whatever its scale, 1 otherwise. Part of <see cref="UIScale"/>.
    /// </summary>
    public float DisplayScale { get; private set; } = 1.0f;

    /// <summary>
    /// The fewest units of layout the short side of the screen is allowed to have, which caps <see cref="DisplayScale"/>
    /// on a small dense screen. A phone at density 3 would otherwise lay out in 440 units, 720 keeps a desktop's UI
    /// fitting. 0 for no cap, the screen's scale as it is.
    /// </summary>
    public float MinimumShortEdge { get; set; } = 720.0f;

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

    // What ScrollPrecisely was given and hasn't been handed out yet, in notches, and the tick the horizontal part of the
    // window's wheel was last used on (the vertical part has UIKeyboard.UseScroll)
    private float pendingPreciseX, pendingPreciseY;
    private static long scrollXUsedOn = -1;

    // The tick a pinch or smart zoom of the window was last used on, so one that went to a UI is not given to the next
    private static long gestureUsedOn = -1;

    /// <summary>
    /// Scrolls whatever is under the pointer as if a trackpad had been dragged: fractional notches (a notch is
    /// <see cref="PreciseScroll.NotchPoints"/> layout units) that scrolling components follow to the pixel with no stepping or
    /// easing. Safe from any thread, it happens on the next update.
    /// </summary>
    /// <param name="notches">X positive to the right, Y positive up.</param>
    public void ScrollPrecisely(Vector2 notches)
    {
        lock (skinLock)
        {
            pendingPreciseX += notches.X;
            pendingPreciseY += notches.Y;
        }
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
    private ulong pictureGlassGeneration;
    private Matrix4x4 pictureView, pictureProjection;
    private bool retained = true;

    /// <summary>
    /// Whether the UI keeps the picture it last drew and lays that over the frame for as long as nothing in it
    /// changes, rather than drawing every quad of it again every frame. On unless it is switched off. A UI that
    /// is standing still (most of them, most of the time) then costs one quad a frame however much is in it, and
    /// nothing is sent to the GPU for it at all. What it takes is the memory of a picture the size of the screen.
    /// </summary>
    public bool Retained
    {
        get => retained;
        set
        {
            if (retained == value)
                return;
            retained = value;
            pictureCurrent = false;
        }
    }

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

        // In low latency what is new goes up on the frame that finds it, frames show the newest tick as it is there
        // (WindowManager.LowLatency), so nothing is blended anew between two of them and there is nothing to hold back
        if (GameEngine.Instance?.WindowManager is { LowLatency: true })
            return true;

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

    // What each list was painted as (see TraceTag), swapped with them, and what the list on the GPU was, for FrameTrace
    private object? backTag, frontTag, uploadedTag;
    private long uploadedPainted = -1;
    private UISkin? uploadedSkin;

    /// <summary>
    /// What this UI is painting as right now, a face of a program say, kept with every list painted from here on and
    /// said of every frame drawn from one while <see cref="Horizon.Core.Diagnostics.FrameTrace"/> is on. Null (the usual)
    /// leaves this UI out of the trace.
    /// </summary>
    public object? TraceTag { get; set; }
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

    // The tooltip up from the pointer resting, what said it, what it says and where it hangs from. It stays put while the
    // pointer moves about what it's about or goes over to read it, and for a moment after it wanders off either, so the
    // way across to it doesn't take it down
    private (UIComponent Of, string Text, bool Markdown, Vector2 At)? held;
    private float heldFor;
    private const float TOOLTIP_GRACE = 0.35f;

    // What was last clicked and where, its tooltip kept down until the pointer leaves it
    private UIComponent? hushed;
    private Vector2 hushedAt;

    // A tooltip put up by a component rather than the pointer resting on it (a hover asked for with the keyboard), where,
    // and where the pointer was as it went up, it comes down once the pointer moves
    private (UIComponent Owner, string Text, Vector2 At, bool Markdown, Vector2 Pointer)? pinned;

    // Scratch for cutting a tooltip into lines, see UITooltip
    internal List<(int Start, int Length)> TooltipLines { get; } = [];

    // A tooltip that is markdown, laid out once for as long as it is the same text
    internal UIRichText TooltipRich { get; } = new() { Snug = true };

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
        public readonly UIFrameList Frames = new();
        public int Count;
        public int Painted = -1;
        public UISkin? Skin;
        public bool Incomplete;
        public object? Tag;

        public ReadOnlySpan<SpriteItem> Span => Items.AsSpan(0, Count);
    }

    private readonly SnapshotBuffer<CapturedList> captured = new(static () => new CapturedList());

    // What of the captured lists was uploaded last, which two lists and how far between them, -1 for something else.
    // And what puts a frame between two of them together
    private int shownBefore = -1, shownAfter = -1;
    private float shownAlpha = float.NaN;
    private Vector2? shownPointer;
    private bool followingPointer;

    internal bool FollowingPointerForTest => followingPointer;

    // How far between two ticks the last frame was drawn, and the newer of the two, for hit testing where things are
    // drawn (see DrawnMove). Written by the render thread, read by the simulation's
    private volatile float drawnAlpha = 1.0f;
    private volatile int drawnAfter = -1;

    // The frames of the list painted before the newest, by key, worked out once that list is painted when a hit test
    // asks. By how many times it's been painted into and not by the newest's number, a tick that changed nothing paints
    // into it again and keeps the number, and the frames found were of a list that wasn't there any more
    private readonly Dictionary<Vector2, int> priorFrames = [];
    private int priorFor = -1, backPainted;

    /// <summary>
    /// How far what a component is inside of is drawn, in the last frame drawn, from where the newest tick laid it out,
    /// in the units of the layout. Only the moves of scrolls and of the layout moving things about, nothing for a
    /// component painted in no frame that moved. Simulation thread.
    /// </summary>
    internal Vector2 DrawnMove(UIComponent component)
    {
        float alpha = drawnAlpha;
        if (alpha >= 1.0f || drawnAfter != paintedFrame || !front.HomeOf(component.PaintId, out int at) || (uint)at >= (uint)front.FrameList.Frames.Count)
            return Vector2.Zero;

        UIFrame latest = front.FrameList.Frames[at];
        if (priorFor != backPainted)
        {
            priorFor = backPainted;
            priorFrames.Clear();
            for (int i = 0; i < back.FrameList.Frames.Count; i++)
                priorFrames.TryAdd(back.FrameList.Frames[i].Key, i);
        }

        if (!priorFrames.TryGetValue(latest.Key, out int was) || was >= back.FrameList.Frames.Count)
            return Vector2.Zero;

        UIFrame shown = UIInBetween.Between(back.FrameList.Frames[was], latest, alpha);
        return shown.Shift - latest.Shift + (shown.Laid - latest.Laid);
    }
    private readonly UIInBetween inBetween = new();

    /// <summary>
    /// Whether every tick painted is held up against what its frames resolve to and a difference is logged,
    /// <c>HORIZON_UI_FRAMES_CHECK=1</c>. Costs a second list's worth of work a tick, for a test run.
    /// </summary>
    public static bool CheckFrames { get; set; } = Environment.GetEnvironmentVariable("HORIZON_UI_FRAMES_CHECK") == "1";

    // The last difference logged, so one that stays isn't logged every tick
    private string? framesDiffered;

    /// <summary>What this UI has cost since it was last reset, see <see cref="UIStatistics"/>.</summary>
    public UIStatistics Statistics { get; } = new();

    // Helper method to count quads that went up to the GPU
    private void CountUpload(int quads)
    {
        Statistics.FramesUploaded++;
        Statistics.QuadsUploaded += quads;
        Statistics.BytesUploaded += (long)quads * System.Runtime.CompilerServices.Unsafe.SizeOf<SpriteItem>();
    }

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
            NoteWindowsEntered(focus, value);
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
    private bool followsSkinsResolved;
    private bool initialized;
    private bool resourcesInitialized;

    /// <summary>Whether the default skin source has been resolved yet (deferred while disabled).</summary>
    internal bool IsSkinSourceResolved => followsSkinsResolved;

    public UICompositor(in Camera2D viewportCamera, string? theme = null)
    {
        this.viewportCamera = viewportCamera;
        followsSkins = true;
        wantedTheme = theme;
        skinDirectory = string.Empty;
        skinFile = string.Empty;
        skinTheme = theme;
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

    /// <summary>
    /// The innermost component at a pixel of the window (from its top left, the way the pointer and a finger say where
    /// they are), in the topmost module that has one there. What the pointer would land on, labels and panels too,
    /// whatever size the scene is drawn at. For checks and tools.
    /// </summary>
    public UIComponent? FindAtWindow(Vector2 pixel)
    {
        Vector2 world = ToWorld(pixel);
        UIModule[] now = modules;
        for (int i = now.Length - 1; i >= 0; i--)
        {
            if (now[i].FindAt(world) is { } found)
                return found;
        }

        return null;
    }

    public UIModule CreateModule()
    {
        var module = new UIModule(this);

        lock (modulesLock)
            modules = [.. modules, module];

        return module;
    }

    /// <summary>Replaces a registered module in place, preserving its order and disabling the retired module.</summary>
    public bool ReplaceModule(UIModule current, UIModule replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (current.Compositor != this || replacement.Compositor != this)
            throw new ArgumentException("Both modules must belong to this compositor.");
        lock (modulesLock)
        {
            int index = Array.IndexOf(modules, current);
            if (index < 0)
                return false;
            if (ReferenceEquals(current, replacement))
                return true;
            if (Array.IndexOf(modules, replacement) >= 0)
                throw new ArgumentException("The replacement module is already registered.");
            var next = (UIModule[])modules.Clone();
            next[index] = replacement;
            current.Enabled = false;
            modules = next;
        }

        ClearModuleReferences(current);
        return true;
    }

    public bool RemoveModule(UIModule module)
    {
        bool removed;
        lock (modulesLock)
        {
            int index = Array.IndexOf(modules, module);
            if (index < 0)
                return false;

            modules = [.. modules[..index], .. modules[(index + 1)..]];
            removed = true;
        }

        if (removed)
            ClearModuleReferences(module);

        return removed;
    }

    /// <summary>Helper method to drop any focus, hover, press, resize, tooltip or autoscroll state owned by a removed or retired module.</summary>
    private void ClearModuleReferences(UIModule module)
    {
        if (Focus?.Module == module)
            Focus = null;

        if (lastRoutedFocus?.Module == module)
            lastRoutedFocus = null;

        if (hovered?.Module == module)
            SetHovered(null);

        if (pressed?.Module == module)
        {
            var cancelled = pressed;
            pressed = null;
            touchDeferred = false;
            cancelled.IsPressed = false;
            if (cancelled.IsResizing)
                cancelled.EndResize();
        }

        if (gripped?.Module == module)
        {
            var grip = gripped;
            gripped = null;
            if (grip.IsResizing)
                grip.EndResize();
            else
                grip.ActiveGrip = UIResize.None;
        }

        if (middleScrollTarget?.Module == module)
        {
            middleScroll.Cancel();
            middleScrollTarget = null;
        }

        if (tooltipModule == module)
        {
            tooltipModule.ShowTooltip(null, default);
            tooltipModule = null;
            TooltipOf = null;
            held = null;
        }

        if (pinned?.Owner.Module == module)
            pinned = null;

        if (hushed?.Module == module)
            hushed = null;

        if (touchFocus?.Module == module)
            touchFocus = null;

        if (touchTooltip?.Module == module)
            touchTooltip = null;

        if (touchFocused?.Module == module)
            touchFocused = null;

        if (panFrom?.Module == module)
            panFrom = null;

        if (FocusShownOn?.Module == module)
            FocusShownOn = null;

        if (Highlighted?.Module == module)
            Highlighted = null;
    }

    private void EnsureSkinSourceResolved()
    {
        lock (skinLock)
        {
            if (followsSkins && (!followsSkinsResolved || skinsVersion != UISkins.Version))
            {
                skinsVersion = UISkins.Version;
                (skinDirectory, skinFile, skinTheme) = UISkins.Resolve(wantedTheme);
                followsSkinsResolved = true;
            }
        }
    }

    private void EnsureSkinLoaded()
    {
        if (Skin is not null)
            return;
        EnsureSkinSourceResolved();
        Skin = UISkin.Shared(skinDirectory, skinFile, skinTheme);
    }

    private void EnsureResourcesInitialized()
    {
        if (resourcesInitialized)
            return;

        resourcesInitialized = true;
        EnsureSkinLoaded();

        renderer.Initialize();

        UIKeyboard.Hook();
        if (InputOf is { } input)
            HookComposition(input);
    }

    public override void Initialize()
    {
        initialized = true;
        if (Enabled)
            EnsureResourcesInitialized();
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

    /// <summary>
    /// Has a skin loaded ahead of being worn, on the render thread at the next frame, nothing on screen changing. For a
    /// UI that's about to change its theme (a pointer over the button that does it), so the change finds it there and is
    /// painted in it from the same tick. Art painted aside in it meanwhile (see <see cref="PaintAside"/>) goes into its
    /// atlas as it would for the skin worn. From any thread, and nothing once it's loaded.
    /// </summary>
    public void WarmSkin(string directory, string file, string? theme = null)
    {
        if (UISkin.Known(directory, file, theme) is { } known)
        {
            if (!ReferenceEquals(known, Skin))
                warmSkin = known;
            return;
        }

        lock (skinLock)
            warmRequest = (directory, file, theme);
    }

    /// <summary>A skin loaded ahead (see <see cref="WarmSkin"/>) and not worn yet, null while it isn't there. From any thread.</summary>
    public UISkin? Warmed => warmSkin;

    // A skin to be loaded ahead (WarmSkin), and one loaded and not worn yet, whose atlas takes the art painted aside
    private (string Directory, string File, string? Theme)? warmRequest;
    private volatile UISkin? warmSkin;

    // The list the UI is painted into aside, never shown, and the screen the last update had
    private readonly UIDrawList aside = new();
    private UIRect lastScreen;

    /// <summary>
    /// Lays the UI out and paints it as it is right now into a list that's never shown, in a skin (the worn one when
    /// null), so what that asks for (art put in the atlas, letters drawn, text measured) is there by the time it's shown.
    /// For a UI about to change: the change made, painted aside and undone. What's on screen is untouched, the paint at the
    /// end of the tick lays it out as it is again. Simulation thread, in an update.
    /// </summary>
    public void PaintAside(UISkin? skin = null)
    {
        if ((skin ?? Skin) is not { } painted || lastScreen.IsEmpty)
            return;

        aside.Previous = front;
        aside.MotionSpeed = AnimationSpeed;
        aside.Begin(painted, paintFrame, 0.0f, paintClock);
        foreach (var module in modules)
        {
            if (!module.Enabled)
                continue;
            module.Layout(lastScreen, painted);
            module.Paint(aside);
        }
        aside.End();
    }

    // Helper method for the skin that was asked for when it's been loaded before, leaving the request where it is
    private UISkin? RequestedAndKnown()
    {
        lock (skinLock)
            return requestedSkin is { } wanted ? UISkin.Known(wanted.Directory, wanted.File, wanted.Theme) : null;
    }

    // Helper method to load the skin asked to be warmed, render thread
    private void LoadWarmSkin()
    {
        (string Directory, string File, string? Theme)? request;
        lock (skinLock)
        {
            request = warmRequest;
            warmRequest = null;
        }

        if (request is { } wanted && UISkin.Shared(wanted.Directory, wanted.File, wanted.Theme) is { } loaded && !ReferenceEquals(loaded, Skin))
            warmSkin = loaded;
    }

    private void LoadRequestedSkin()
    {
        (string Directory, string File, string? Theme)? request;
        lock (skinLock)
        {
            // The game was set to another skin since this UI last looked
            if (followsSkins && (!followsSkinsResolved || skinsVersion != UISkins.Version))
            {
                skinsVersion = UISkins.Version;
                requestedSkin = UISkins.Resolve(wantedTheme);
                followsSkinsResolved = true;
            }

            request = requestedSkin;
            requestedSkin = null;
        }

        if (request is not { } wanted || UISkin.Shared(wanted.Directory, wanted.File, wanted.Theme) is not { } skin)
            return;

        (skinDirectory, skinFile, skinTheme) = wanted;

        // What was uploaded is drawn on in the skin it was painted in (the renderer keeps that one's atlas, a shared skin
        // is never let go of) until a list painted in this one is there. Cleared here, every change of theme was a frame
        // or more of no UI at all, the ground flashing through, and the first change to a skin not drawn with before was
        // a tenth of a second of it while its art was put together
        Skin = skin;
        pictureCurrent = false;
        if (ReferenceEquals(warmSkin, skin))
            warmSkin = null;

        // And the first list painted with the new one goes up as soon as it is there, limit or no limit
        refreshedAt = 0;
    }

    public override void UpdateState(float dt)
    {
        if (initialized && Skin is null && Enabled)
            EnsureSkinLoaded();

        // A camera of its own is kept the size of the window, here on the simulation thread where it is read
        if (ownsCamera && GameEngine.Instance.WindowManager.ViewportSize is { X: > 0.0f, Y: > 0.0f } window && viewportCamera.ViewSize != window)
            viewportCamera.ViewSize = window;

        // A skin asked for that's been loaded before is worn from this tick on, painted with and seen by the updates. Left
        // for the frame that loads it, one more list went out in the old skin, which that frame threw away, and the change
        // was a frame late at best
        if (RequestedAndKnown() is { } asked && !ReferenceEquals(asked, Skin))
            Skin = asked;

        if (Skin is not { } skin)
            return;

        paintFrame++;

        var snapshot = modules;

        // What the camera sees. Its bounds start at the bottom left corner, the world being Y-up.
        var bounds = viewportCamera.Bounds;
        var screen = new UIRect(
            new Vector2(bounds.X, bounds.Y),
            new Vector2(bounds.X + bounds.Width, bounds.Y + bounds.Height));
        lastScreen = screen;

        // Worked out before anything else uses it. The pointer has to be scaled the same way the layout is.
        float fit = DesignSize is { X: > 0.0f, Y: > 0.0f } design && !screen.IsEmpty
            ? MathF.Min(screen.Width / design.X, screen.Height / design.Y)
            : 1.0f;
        DisplayScale = DesignSize is null && GameEngine.Instance?.WindowManager is { ScalesToDisplay: true } scaled ? scaled.DisplayScale : 1.0f;

        // A phone's screen is small in its own units, 440 dp across at density 3, and a UI laid out for a desktop
        // doesn't fit in that. The scale gives as much as it takes to keep MinimumShortEdge units on the short side,
        // never less than 1. Only on a phone or a tablet (a host with no desktop window), a desktop at 150% is left
        // exactly as the system scales it, whatever its size
        if (DisplayScale > 1.0f && MinimumShortEdge > 0.0f && !screen.IsEmpty
            && GameEngine.Instance?.WindowManager.Host.Features.HasFlag(Horizon.Platform.PlatformFeatures.Window) == false)
            DisplayScale = MathF.Max(1.0f, MathF.Min(DisplayScale, MathF.Min(screen.Width, screen.Height) / MinimumShortEdge));

        UIScale = MathF.Max(0.01f, fit * Scale * DisplayScale);

        // Every module that was designed for a screen of its own works out its own scale the same way
        foreach (var module in snapshot)
            module.FitTo(screen);

        // The pointer is tested against the layout of the previous update, which is the one on screen.
        RoutePointer(snapshot, dt);
        RouteKeyboard(snapshot);
        UpdateTextInput();
        UpdateTooltip(snapshot, dt);

        foreach (var module in snapshot)
        {
            if (!module.Enabled)
                continue;
            // The first update of every module weighed, with HORIZON_LOG_STARTUP, for what a program's start goes on
            long allocated = Horizon.Core.Diagnostics.StartupTrace.On && !Horizon.Core.Diagnostics.StartupTrace.SceneReady ? GC.GetAllocatedBytesForCurrentThread() : -1;
            long started = allocated >= 0 ? Stopwatch.GetTimestamp() : 0;
            module.Update(dt);
            if (allocated >= 0)
                Horizon.Logging.Log.Info($"[Startup] {StartupName(module)} updated in {UIStatistics.Since(started):0.0} ms, {(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1024} KB");
        }

        // Laid out and painted once everything has had its update, at the end of the tick, see Capture. A UI
        // nobody captures (set up with the simulation standing still, or updated and drawn by hand) is painted
        // here and now, as it always was
        pending = new PendingPaint(snapshot, screen, skin, dt);
        if (!capturedBefore)
            PaintPending();
    }

    // A module as the startup's weighing says it, by what's first in it and how many things it has
    private static string StartupName(UIModule module) => $"module of {(module.Root.Children.Count > 0 ? module.Root.Children[0].GetType().Name : "nothing")} and {module.Root.Children.Count - 1} more";

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

        // Asked for in this tick after the update (a change of face, say), worn from this list on when it's there
        if (RequestedAndKnown() is { } asked)
        {
            if (!ReferenceEquals(asked, Skin))
                Skin = asked;
            skin = asked;
        }

        // One clock for both lists, see UIDrawList.Begin
        paintClock = (paintClock + dt) % 3600.0f;
        long started = Stopwatch.GetTimestamp();
        back.Previous = front;
        back.MotionSpeed = AnimationSpeed;
        back.Begin(skin, paintFrame, dt, paintClock);
        backTag = TraceTag;
        backPainted++;

        // Letters ClearType's way or grey, hinted or not, for the screen the window is on now (see GlyphSmoothing)
        float density = GameEngine.Instance?.WindowManager?.DisplayScale ?? 1.0f;
        bool subpixels = Horizon.Rendering.Text.GlyphSmoothing.Subpixels(density, drawnNative, out bool bgr);
        bool dualSource = drawnNative && (GameEngine.Instance?.Graphics?.SupportsDualSourceBlend ?? false);
        back.UseSmoothing(subpixels, bgr, Horizon.Rendering.Text.GlyphSmoothing.Hints(density), dualSource);
        foreach (var module in snapshot)
        {
            if (!module.Enabled)
                continue;

            long laying = Stopwatch.GetTimestamp();
            long weighed = Horizon.Core.Diagnostics.StartupTrace.On && !Horizon.Core.Diagnostics.StartupTrace.SceneReady ? GC.GetAllocatedBytesForCurrentThread() : -1;
            module.Layout(screen, skin);
            Statistics.LayoutMs += UIStatistics.Since(laying);
            long painting = Stopwatch.GetTimestamp(), paintFrom = weighed >= 0 ? GC.GetAllocatedBytesForCurrentThread() : -1;
            module.Paint(back);
            if (weighed >= 0)
                Horizon.Logging.Log.Info($"[Startup] {StartupName(module)} laid out in {UIStatistics.Since(laying) - UIStatistics.Since(painting):0.0} ms, {(paintFrom - weighed) / 1024} KB, painted in {UIStatistics.Since(painting):0.0} ms, {(GC.GetAllocatedBytesForCurrentThread() - paintFrom) / 1024} KB");
            PaintMiddleScroll(back, module);

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
            if (Highlights.Count > 0)
            {
                if (Highlights is IReadOnlyList<UIComponent> highlightList)
                {
                    for (int h = 0; h < highlightList.Count; h++)
                    {
                        var marked = highlightList[h];
                        if (marked.Module == module && marked != Highlighted)
                            UILayoutOverlay.PaintHighlight(back, marked);
                    }
                }
                else
                {
                    foreach (var marked in Highlights)
                    {
                        if (marked.Module == module && marked != Highlighted)
                            UILayoutOverlay.PaintHighlight(back, marked);
                    }
                }
            }

            if (module.Clip is not null)
                back.PopClip();
        }
        back.End();
        Moved = back.Moving;
        Statistics.PaintMs += UIStatistics.Since(started);
        if (CheckFrames && back.FramesDiffer() is { } differs && differs != framesDiffered)
        {
            framesDiffered = differs;
            Horizon.Logging.Log.Warning($"[UI frames] what the frames resolve to isn't what was painted, {differs}");
        }
        Statistics.Ticks++;
        Statistics.QuadsPainted += back.Items.Length;
        Statistics.MostQuads = Math.Max(Statistics.MostQuads, back.Items.Length);

        // Nothing happened. What was painted is what is on screen already, and nobody needs to hear about it
        started = Stopwatch.GetTimestamp();
        bool same = paintedFrame > 0 && back.SameAs(front);
        Statistics.CompareMs += UIStatistics.Since(started);
        if (same)
            return;

        Statistics.TicksChanged++;

        // Something did, a window that only draws when something changed has frames to draw (see RunMode.Application)
        GameEngine.Instance?.WindowManager?.NoteChange();

        lock (frameLock)
        {
            (front, back) = (back, front);
            (frontTag, backTag) = (backTag, frontTag);
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
        PublishCaptured(SnapshotClock.Active);
    }

    internal void CaptureForTest(UIRect screen, UISkin skin, float dt, SnapshotClock clock)
    {
        Skin ??= skin;
        pending = new PendingPaint(modules, screen, skin, dt);
        capturedBefore = true;
        PaintPending();
        PublishCaptured(clock);
    }

    private void PublishCaptured(SnapshotClock? clock)
    {
        if (captured.BeginPublish(clock) is not { } into)
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
            into.Runs.AddRange(front.Runs);

            into.Glass.Clear();
            into.Glass.AddRange(front.GlassPanes);

            // And as frames, which the frames between this tick and the next are put together from
            into.Frames.CopyFrom(front.FrameList);

            into.Atlases.Clear();
            into.Atlases.AddRange(front.Atlases);

            into.Painted = paintedFrame;
            into.Skin = front.Skin;
            into.Tag = frontTag;
            into.Incomplete = front.Incomplete;
        }
    }

    /// <summary>
    /// Helper method to upload what a frame that is drawn alongside the simulation shows, the last two captured lists
    /// blended to the moment between them. Only when that is something else than what was uploaded last.
    /// </summary>
    /// <param name="due">Whether the UI may be drawn anew this frame, see <see cref="FrameRateLimit"/>.</param>
    /// <returns>False if nothing was captured yet, the UI is drawn from its newest list then.</returns>
    // A captured frame may still say "dragging" after the window has heard release.
    // Read position first, then the live button, so post-release motion cannot move it.
    internal static Vector2? LiveDragPointer(Mouse mouse)
    {
        Vector2 position = mouse.LivePosition;
        return mouse.IsLiveDown(Horizon.Input.MouseButton.Left) ? position : null;
    }

    internal bool UploadCapturedForTest(in RenderFrame frame, UISkin skin) => UploadCaptured(frame, skin, NoteFrame());

    private bool UploadCaptured(in RenderFrame frame, UISkin skin, bool due)
    {
        if (!captured.TryGet(frame, out CapturedList before, out CapturedList after, out bool continuous))
            return false;

        // Not to be shown until it is the UI as it is meant to look
        if (after.Painted == 0 || after.Incomplete || after.Skin != skin)
        {
            GameEngine.Instance?.SceneManager?.ReportUnfinished();
            return true;
        }

        // Two ticks that painted something different are put together between them from their frames, see
        // UIInBetween.Frames. Not doing it whenever anything changed was how one clock in a HUD had the whole UI drawn
        // in steps a tick apart
        bool sameList = before.Painted == after.Painted;
        bool canBlend = continuous && !sameList && before.Skin == skin && !before.Incomplete;

        // A list that changed in steps is shown as it was until the moment is all the way at the newer one
        CapturedList shown = sameList || canBlend || frame.Alpha >= 1.0f || before.Skin != skin || before.Incomplete ? after : before;
        float alpha = canBlend ? frame.Alpha : 1.0f;
        if (!sameList && !continuous)
            Statistics.FramesBroken++;
        else if (canBlend && frame.Alpha >= 1.0f)
            Statistics.FramesAtNewer++;

        // Where the pointer is right now, for a window that's being dragged (see UIComponent.FollowsPointer). Only the
        // mouse, a pointer of somebody else's (a recording, a test) is theirs to read on their own thread
        // Only while something of this UI is still being dragged. The list a drag left behind kept its frame that follows
        // the pointer while nothing here painted again, and a press in another UI of the window (the editor's, under a
        // window of the chrome's) took the window with it until the button came up
        Vector2? pointer = after.Frames.Follows && pressed is { FollowsPointer: true } && PointerSource is null
            && InputOf is { } dragInput && LiveDragPointer(dragInput.Mouse) is { } dragPointer
            ? ToWorld(dragPointer) : null;
        followingPointer = pointer is not null;

        // What goes where the pointer goes is drawn as often as low latency allows for as long as it's held
        if (followingPointer)
        {
            GameEngine.Instance?.WindowManager?.RequestUrgentFrames();
            GameEngine.Instance?.WindowManager?.RequestUncappedFrames();
        }

        int from = canBlend ? before.Painted : shown.Painted;
        if (from == shownBefore && shown.Painted == shownAfter && alpha == shownAlpha && pointer == shownPointer)
            return true;

        // Not yet, see FrameRateLimit. What is up there already is shown once more and this is asked again the
        // next frame, so whatever the UI comes to rest as gets there a hundred and twentieth of a second late
        // at the worst. Live pointer-following drags bypass the rate limit so window motion stays locked to the cursor.
        bool pointerMoved = pointer is not null && pointer != shownPointer;
        if (!due && !pointerMoved)
            return true;

        if (canBlend && alpha < 1.0f || pointer is not null)
        {
            long started = Stopwatch.GetTimestamp();
            bool between = canBlend && alpha < 1.0f;
            CapturedList target = between ? after : shown;
            inBetween.Frames(
                between ? before.Frames : shown.Frames,
                target.Frames,
                between ? alpha : 1.0f,
                pointer,
                target.Span,
                CollectionsMarshal.AsSpan(target.Runs),
                CollectionsMarshal.AsSpan(target.Glass),
                hasPaintedGlass: true);
            Statistics.BlendMs += UIStatistics.Since(started);
            Statistics.FramesBlended++;
            CountUpload(inBetween.FramedQuads.Length);
            renderer.Upload(inBetween.FramedQuads, inBetween.FramedRuns, skin, CollectionsMarshal.AsSpan(target.Atlases), inBetween.FramedGlass);
        }
        else
        {
            renderer.Upload(shown.Span, CollectionsMarshal.AsSpan(shown.Runs), skin, CollectionsMarshal.AsSpan(shown.Atlases), CollectionsMarshal.AsSpan(shown.Glass));
            CountUpload(shown.Count);
        }

        (shownBefore, shownAfter, shownAlpha, shownPointer) = (from, shown.Painted, alpha, pointer);
        (uploadedTag, uploadedSkin, uploadedPainted) = ((canBlend && alpha < 1.0f ? after : shown).Tag, skin, shown.Painted);
        drawnAfter = after.Painted;
        drawnAlpha = canBlend ? alpha : 1.0f;
        pictureCurrent = false;
        if (due)
            refreshedAt = renderedAt;
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
        if (!resourcesInitialized)
            EnsureResourcesInitialized();

        LoadRequestedSkin();
        if (warmRequest is not null)
            LoadWarmSkin();

        if (Skin is not { } skin)
            return;

        // Art painted aside in a skin loaded ahead goes into its atlas as the worn one's does
        warmSkin?.Update();

        RenderFrame frame = RenderFrame.Active;
        bool due = NoteFrame();
        Statistics.Frames++;
        Statistics.CountGpu();

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
                // A frame painted with a skin that has been swapped out since is skipped too, the one before it staying
                // up, and the change of skin isn't all there till one is
                if (front.Skin != skin)
                    GameEngine.Instance.SceneManager.ReportUnfinished();
                if (front.Skin == skin && !front.Incomplete)
                {
                    renderer.Upload(front);
                    CountUpload(front.Items.Length);
                    (uploadedTag, uploadedSkin, uploadedPainted) = (frontTag, skin, paintedFrame);
                    pictureCurrent = false;
                    refreshedAt = renderedAt;
                }

                uploadedFrame = paintedFrame;

                // What was uploaded is the newest list, not one that was captured
                shownBefore = shownAfter = -1;
                shownAlpha = float.NaN;
                followingPointer = false;
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
    // See DrawUploaded, written on the render thread and read by the paint. Taken to be so until a draw says otherwise
    private volatile bool drawnNative = true;

    private void DrawUploaded(float dt)
    {
        // A UI drawn straight into the window is drawn at its own pixels over the scene blown up, unless the whole frame
        // is drawn small (see FrameScaler). One inside of a renderer is part of that renderer's picture
        if (Renderer2D.Current is null && (!renderer.IsEmpty || Cursor is { Visible: true }))
            GameEngine.Instance.WindowManager.Scaling.BeginOverlay();

        // Whether it's drawn at the window's own pixels and nothing else's, which a subpixel letter has to be: blown up
        // with a frame drawn small its stripes are no longer the screen's, and inside a renderer its blend would land
        // on the renderer's other pictures too
        drawnNative = Renderer2D.Current is null
            && GameEngine.Instance.WindowManager.Scaling.Settings.Composition != Horizon.Rendering.Scaling.RenderComposition.WholeFrame;

        using var scope = GameEngine.Instance.Graphics.BeginGpuScope(UIStatistics.GPU_SCOPE);

        long started = Stopwatch.GetTimestamp();
        DrawList(dt);
        DrawCursor();
        if (Horizon.Core.Diagnostics.FrameTrace.On && TraceTag is not null)
            Horizon.Core.Diagnostics.FrameTrace.Ui(uploadedTag, uploadedSkin is { } traced ? traced.Theme ?? string.Empty : null, renderer.IsEmpty, uploadedPainted);
        Statistics.DrawMs += UIStatistics.Since(started);
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

        ulong generation = Volatile.Read(ref glassGeneration);
        if (generation != pictureGlassGeneration)
            pictureCurrent = false;

        // Nothing has changed since the picture on the layer was drawn. That is laid over the frame as it is,
        // and none of the UI is drawn again
        if (Retained && pictureCurrent)
        {
            using (GameEngine.Instance.Graphics.BeginGpuScope(UIStatistics.REPLAY_SCOPE))
            {
                if (layer.Replay(dt))
                    return;
            }
        }

        // Onto its own layer, through its effects if it has any that are on, and kept there to be laid over
        // again. Straight over the scene where there is no layer to be had (inside of a renderer that lights
        // its picture, or a UI that asked not to be kept)
        Renderer2D? output = Renderer2D.Current;
        bool layered = layer.Begin(Retained);

        using (GameEngine.Instance.Graphics.BeginGpuScope(UIStatistics.QUADS_SCOPE))
        {
            if (renderer.Glass.IsEmpty || (!layered && output is { HoldsPicture: false }))
            {
                glassPass?.Reset();
                renderer.Draw(viewportCamera);
                GlassDrawn = default;
            }
            else
                DrawGlassy(layered ? layer.Under : output, layered ? layer.Picture : null);
        }

        if (layered)
        {
            using var laid = GameEngine.Instance.Graphics.BeginGpuScope(UIStatistics.LAYER_SCOPE);
            layer.End(dt);
        }

        // Only a picture that made it onto the layer is there to be shown again. One with glass in it shows what was
        // behind the glass, which a scene that moves under it doesn't keep still for, see GlassKeepsPicture
        pictureCurrent = layered && (renderer.Glass.IsEmpty || GlassKeepsPicture);
        pictureGlassGeneration = generation;
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

    // Cached backdrop hashes for static glass panes, keyed on (uploadedRevision, seed, cameraRevision, paneIndex, under, region, blur)
    private struct GlassHashEntry
    {
        public ulong Revision;
        public ulong Seed;
        public ulong CameraRevision;
        public int PaneIndex;
        public int Under;
        public int RegionX, RegionY, RegionW, RegionH;
        public uint BlurBits;
        public ulong Hash;
    }

    private readonly GlassHashEntry[] glassHashCache = new GlassHashEntry[16];
    private int glassHashCursor;
    private ulong glassCameraRevision = 1;
    private Matrix4x4 glassHashView, glassHashProjection;

    private bool TryGetCachedGlassHash(ulong revision, UIRenderer renderer, ulong seed, ulong cameraRev, int paneIndex, int under, (int X, int Y, int W, int H) region, uint blurBits, out ulong hash)
    {
        for (int c = 0; c < glassHashCache.Length; c++)
        {
            ref GlassHashEntry entry = ref glassHashCache[c];
            if (entry.Hash == 0
                || entry.Seed != seed
                || entry.CameraRevision != cameraRev
                || entry.PaneIndex != paneIndex
                || entry.Under != under
                || entry.RegionX != region.X
                || entry.RegionY != region.Y
                || entry.RegionW != region.W
                || entry.RegionH != region.H
                || entry.BlurBits != blurBits)
            {
                continue;
            }

            if (entry.Revision == revision)
            {
                hash = entry.Hash;
                return true;
            }

            if (under <= renderer.FirstChangedItemSince(entry.Revision))
            {
                entry.Revision = revision;
                hash = entry.Hash;
                return true;
            }
        }

        hash = 0;
        return false;
    }

    private void StoreCachedGlassHash(ulong revision, ulong seed, ulong cameraRev, int paneIndex, int under, (int X, int Y, int W, int H) region, uint blurBits, ulong hash)
    {
        glassHashCache[glassHashCursor++ & 15] = new GlassHashEntry
        {
            Revision = revision,
            Seed = seed,
            CameraRevision = cameraRev,
            PaneIndex = paneIndex,
            Under = under,
            RegionX = region.X,
            RegionY = region.Y,
            RegionW = region.W,
            RegionH = region.H,
            BlurBits = blurBits,
            Hash = hash
        };
    }

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
        ReadOnlySpan<UIDrawList.Run> runs = renderer.Runs;

        bool sent = renderer.Begin();
        int from = 0, blurredBy = -1, blurs = 0;
        (int X, int Y, int W, int H) blurredRegion = default;

        Vector2 size = Renderer2D.OutputSize(output);
        bool validSize = size.X >= 1.0f && size.Y >= 1.0f;
        float pixelsPerUnit = validSize ? UIGlassPass.PixelsPerUnit(viewportCamera, size) : 1.0f;
        Matrix4x4 toClip = viewportCamera.View * viewportCamera.Projection;
        bool hasInverse = Matrix4x4.Invert(toClip, out Matrix4x4 fromClip);

        if (viewportCamera.View != glassHashView || viewportCamera.Projection != glassHashProjection)
        {
            glassHashView = viewportCamera.View;
            glassHashProjection = viewportCamera.Projection;
            glassCameraRevision++;
        }

        // What is under every pane, for keeping its blur from one frame to the next. Only when what is behind the UI
        // holds still (GlassKeepsPicture), the UI can't see a scene that moves under it
        ulong seed = GlassKeepsPicture ? Seed(output) : 0;
        ulong uploadRev = renderer.Revision;
        ulong camRev = glassCameraRevision;

        for (int i = 0; i < panes.Length; i++)
        {
            ref readonly UIGlassPane pane = ref panes[i];
            if (!validSize || pane.Opacity <= 0.0f)
                continue;

            var (rect, clip) = UIGlassPass.Pixels(pane, viewportCamera, size);
            float vx0 = MathF.Max(MathF.Max(rect.X, clip.X), 0.0f);
            float vy0 = MathF.Max(MathF.Max(rect.Y, clip.Y), 0.0f);
            float vx1 = MathF.Min(MathF.Min(rect.Z, clip.Z), size.X);
            float vy1 = MathF.Min(MathF.Min(rect.W, clip.W), size.Y);
            if (vx1 <= vx0 || vy1 <= vy0)
                continue;

            float minBox = MathF.Min(rect.Z - rect.X, rect.W - rect.Y);
            UIGlassOptics optics = UIGlassOptics.Compute(pane.Bend * pixelsPerUnit, pane.Lens, minBox);
            float pad = optics.PaddingPixels;
            Vector4 pVis = new(
                MathF.Max(vx0 - pad, 0.0f),
                MathF.Max(vy0 - pad, 0.0f),
                MathF.Min(vx1 + pad, size.X),
                MathF.Min(vy1 + pad, size.Y));

            float radius = MathF.Max(0.0f, pane.Blur) * pixelsPerUnit;
            float share = GaussianBlur.Share(radius);
            int margin = share > 0.001f ? GaussianBlur.Margin(radius, size) : 2;
            int maxW = (int)size.X, maxH = (int)size.Y;
            bool draggingGlass = (followingPointer || pressed is not null || gripped is not null) && GlassKeepsPicture;
            var region = GlassCaptureRegion(pVis, margin, maxW, maxH, draggingGlass);

            var (rWorldMin, rWorldMax) = hasInverse
                ? RegionWorldBounds(region, fromClip, size)
                : (new Vector2(float.NegativeInfinity), new Vector2(float.PositiveInfinity));

            int under = Math.Clamp(pane.Under, from, items.Length);
            int start = Math.Clamp(pane.Start, under, items.Length);

            if (sent && under > from)
                renderer.DrawRange(from, under, viewportCamera);

            ulong madeOf = 0;
            if (seed != 0)
            {
                uint blurBits = BitConverter.SingleToUInt32Bits(pane.Blur);
                if (!TryGetCachedGlassHash(uploadRev, renderer, seed, camRev, i, under, region, blurBits, out madeOf))
                {
                    madeOf = Mix(seed, ((ulong)(uint)region.X << 32) | (uint)region.Y);
                    madeOf = Mix(madeOf, ((ulong)(uint)region.W << 32) | (uint)region.H);
                    madeOf = Mix(madeOf, blurBits);

                    for (int j = 0; j < i; j++)
                    {
                        ref readonly UIGlassPane earlier = ref panes[j];
                        if (Overlaps(Vector2.Min(earlier.Min, earlier.Max), Vector2.Max(earlier.Min, earlier.Max), rWorldMin, rWorldMax))
                            madeOf = MixPaneFull(madeOf, earlier);
                    }

                    int runIdx = 0;
                    int lastMixedRun = -1;
                    float rMinX = rWorldMin.X, rMinY = rWorldMin.Y, rMaxX = rWorldMax.X, rMaxY = rWorldMax.Y;
                    for (int k = 0; k < under; k++)
                    {
                        ref readonly SpriteItem item = ref items[k];
                        float ox = item.Origin.X, ax = item.AxisX.X, bx = item.AxisY.X;
                        float minX = ox + (ax < 0.0f ? ax : 0.0f) + (bx < 0.0f ? bx : 0.0f);
                        float maxX = ox + (ax > 0.0f ? ax : 0.0f) + (bx > 0.0f ? bx : 0.0f);
                        if (maxX <= rMinX || minX >= rMaxX)
                            continue;

                        float oy = item.Origin.Y, ay = item.AxisX.Y, by = item.AxisY.Y;
                        float minY = oy + (ay < 0.0f ? ay : 0.0f) + (by < 0.0f ? by : 0.0f);
                        float maxY = oy + (ay > 0.0f ? ay : 0.0f) + (by > 0.0f ? by : 0.0f);
                        if (maxY <= rMinY || minY >= rMaxY)
                            continue;

                        while (runIdx < runs.Length && runs[runIdx].First + runs[runIdx].Count <= k)
                            runIdx++;
                        if (runIdx < runs.Length && runIdx != lastMixedRun && k >= runs[runIdx].First)
                        {
                            lastMixedRun = runIdx;
                            UIDrawList.Run run = runs[runIdx];
                            if (run.Image0 is { } img0)
                                madeOf = Mix(madeOf, img0.Native.Handle);
                            if (run.Image1 is { } img1)
                                madeOf = Mix(madeOf, img1.Native.Handle);
                        }

                        madeOf = Mix(madeOf, in item);
                    }

                    madeOf |= 1;
                    StoreCachedGlassHash(uploadRev, seed, camRev, i, under, region, blurBits, madeOf);
                }
            }

            bool fresh = blurredBy < 0 || !SameBlur(panes, items, blurredBy, blurredRegion, i, pVis, margin, rWorldMin, rWorldMax, size);
            bool captured;
            using (GameEngine.Instance.Graphics.BeginGpuScope(UIStatistics.GLASS_SCOPE))
                captured = glassPass.Capture(pane, madeOf, region, viewportCamera, output, picture, fresh);
            if (fresh || captured)
            {
                blurredBy = i;
                blurredRegion = region;
                blurs++;
            }

            if (sent && start > under)
                renderer.DrawRange(under, start, viewportCamera);

            using (GameEngine.Instance.Graphics.BeginGpuScope(UIStatistics.GLASS_SCOPE))
                glassPass.Composite(pane, viewportCamera, output, picture);

            from = start;
        }

        if (sent && from < items.Length)
            renderer.DrawRange(from, items.Length, viewportCamera);

        renderer.End();
        if (seed != 0)
            renderer.AcknowledgeGlassBaseline(uploadRev);

        GlassDrawn = (panes.Length, blurs);
    }

    /// <summary>
    /// Computes the pixel capture region for a glass pane. Normally quantizes the padded visible bounds to a 128 px
    /// grid so small movements reuse the cached blur; during a pointer-following drag over a stationary backdrop,
    /// captures the full viewport once so dragging across the entire screen hits the cached blur on every frame.
    /// </summary>
    internal static (int X, int Y, int W, int H) GlassCaptureRegion(Vector4 pVis, int margin, int maxW, int maxH, bool fullViewport)
    {
        if (fullViewport)
            return (0, 0, Math.Max(1, maxW), Math.Max(1, maxH));

        int rx0 = Math.Max(0, (int)MathF.Floor((pVis.X - margin) / 128.0f) * 128);
        int ry0 = Math.Max(0, (int)MathF.Floor((pVis.Y - margin) / 128.0f) * 128);
        int rx1 = Math.Min(maxW, (int)MathF.Ceiling((pVis.Z + margin) / 128.0f) * 128);
        int ry1 = Math.Min(maxH, (int)MathF.Ceiling((pVis.W + margin) / 128.0f) * 128);
        if (rx1 <= rx0) rx1 = Math.Min(maxW, rx0 + 1);
        if (ry1 <= ry0) ry1 = Math.Min(maxH, ry0 + 1);
        return (rx0, ry0, rx1 - rx0, ry1 - ry0);
    }

    private static (Vector2 Min, Vector2 Max) RegionWorldBounds((int X, int Y, int W, int H) region, in Matrix4x4 fromClip, Vector2 size)
    {
        Vector2 a = UnprojectPixel(new Vector2(region.X, region.Y), fromClip, size);
        Vector2 b = UnprojectPixel(new Vector2(region.X + region.W, region.Y), fromClip, size);
        Vector2 c = UnprojectPixel(new Vector2(region.X, region.Y + region.H), fromClip, size);
        Vector2 d = UnprojectPixel(new Vector2(region.X + region.W, region.Y + region.H), fromClip, size);
        return (Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)), Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d)));
    }

    private static Vector2 UnprojectPixel(Vector2 pixel, in Matrix4x4 fromClip, Vector2 size)
    {
        float ndcX = pixel.X / size.X * 2.0f - 1.0f;
        float ndcY = 1.0f - pixel.Y / size.Y * 2.0f;
        Vector4 world = Vector4.Transform(new Vector4(ndcX, ndcY, 0.0f, 1.0f), fromClip);
        float w = world.W == 0.0f ? 1.0f : world.W;
        return new Vector2(world.X / w, world.Y / w);
    }

    /// <summary>
    /// Helper method to say whether a pane can show the blur an earlier one made, which it can when it smears as far,
    /// its visible bounds lie inside the earlier blur's safe interior, and nothing drawn since that blur (quads or panes)
    /// overlaps this pane's region.
    /// </summary>
    private static bool SameBlur(
        ReadOnlySpan<UIGlassPane> panes,
        ReadOnlySpan<SpriteItem> items,
        int blurredBy,
        (int X, int Y, int W, int H) blurredRegion,
        int pane,
        Vector4 pVis,
        int margin,
        Vector2 rWorldMin,
        Vector2 rWorldMax,
        Vector2 size)
    {
        UIGlassPane at = panes[pane];
        if (at.Blur != panes[blurredBy].Blur)
            return false;

        int maxW = (int)size.X, maxH = (int)size.Y;
        float safeX0 = blurredRegion.X <= 0 ? 0.0f : blurredRegion.X + margin;
        float safeY0 = blurredRegion.Y <= 0 ? 0.0f : blurredRegion.Y + margin;
        float safeX1 = blurredRegion.X + blurredRegion.W >= maxW ? size.X : blurredRegion.X + blurredRegion.W - margin;
        float safeY1 = blurredRegion.Y + blurredRegion.H >= maxH ? size.Y : blurredRegion.Y + blurredRegion.H - margin;

        if (MathF.Max(0.0f, pVis.X - 2.0f) < safeX0
            || MathF.Max(0.0f, pVis.Y - 2.0f) < safeY0
            || MathF.Min(size.X, pVis.Z + 2.0f) > safeX1
            || MathF.Min(size.Y, pVis.W + 2.0f) > safeY1)
            return false;

        for (int i = blurredBy; i < pane; i++)
        {
            if (Overlaps(Vector2.Min(panes[i].Min, panes[i].Max), Vector2.Max(panes[i].Min, panes[i].Max), rWorldMin, rWorldMax))
                return false;
        }

        int end = Math.Min(at.Under, items.Length);
        float rMinX = rWorldMin.X, rMinY = rWorldMin.Y, rMaxX = rWorldMax.X, rMaxY = rWorldMax.Y;
        for (int i = Math.Max(0, panes[blurredBy].Under); i < end; i++)
        {
            ref readonly SpriteItem item = ref items[i];
            float ox = item.Origin.X, ax = item.AxisX.X, bx = item.AxisY.X;
            float minX = ox + (ax < 0.0f ? ax : 0.0f) + (bx < 0.0f ? bx : 0.0f);
            float maxX = ox + (ax > 0.0f ? ax : 0.0f) + (bx > 0.0f ? bx : 0.0f);
            if (maxX <= rMinX || minX >= rMaxX)
                continue;

            float oy = item.Origin.Y, ay = item.AxisX.Y, by = item.AxisY.Y;
            float minY = oy + (ay < 0.0f ? ay : 0.0f) + (by < 0.0f ? by : 0.0f);
            float maxY = oy + (ay > 0.0f ? ay : 0.0f) + (by > 0.0f ? by : 0.0f);
            if (maxY > rMinY && minY < rMaxY)
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
    public void ForgetGlass() => Interlocked.Increment(ref glassGeneration);

    // Helper method for what every hash of what is under the glass starts from, how big the picture is and which
    // generation of it, see ForgetGlass
    private ulong Seed(Renderer2D? output)
    {
        Vector2 size = Renderer2D.OutputSize(output);
        return Mix(Mix(0x9E3779B97F4A7C15UL, Volatile.Read(ref glassGeneration)), ((ulong)(uint)size.X << 32) | (uint)size.Y) | 1;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong hash, ulong value) => (hash ^ value) * 0x100000001B3UL + (hash >> 29);

    // Helper method to fold one quad into a hash, as the numbers it is, eight bytes at a time without span/cast overhead in Debug
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong hash, in SpriteItem item)
    {
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<SpriteItem>();
        int words = size >> 3;
        ref byte raw = ref System.Runtime.CompilerServices.Unsafe.As<SpriteItem, byte>(ref System.Runtime.CompilerServices.Unsafe.AsRef(in item));
        ref ulong w0 = ref System.Runtime.CompilerServices.Unsafe.As<byte, ulong>(ref raw);
        for (int i = 0; i < words; i++)
        {
            ulong word = System.Runtime.CompilerServices.Unsafe.Add(ref w0, i);
            hash = (hash ^ word) * 0x100000001B3UL + (hash >> 29);
        }
        for (int i = words << 3; i < size; i++)
        {
            ulong b = System.Runtime.CompilerServices.Unsafe.Add(ref raw, i);
            hash = (hash ^ b) * 0x100000001B3UL + (hash >> 29);
        }
        return hash | 1;
    }

    private static ulong MixPaneFull(ulong hash, in UIGlassPane pane)
    {
        hash = Mix(hash, ((ulong)BitConverter.SingleToUInt32Bits(pane.Min.X) << 32) | BitConverter.SingleToUInt32Bits(pane.Min.Y));
        hash = Mix(hash, ((ulong)BitConverter.SingleToUInt32Bits(pane.Max.X) << 32) | BitConverter.SingleToUInt32Bits(pane.Max.Y));
        hash = Mix(hash, ((ulong)BitConverter.SingleToUInt32Bits(pane.ClipMin.X) << 32) | BitConverter.SingleToUInt32Bits(pane.ClipMin.Y));
        hash = Mix(hash, ((ulong)BitConverter.SingleToUInt32Bits(pane.ClipMax.X) << 32) | BitConverter.SingleToUInt32Bits(pane.ClipMax.Y));
        hash = Mix(hash, ((ulong)BitConverter.SingleToUInt32Bits(pane.Radius) << 32) | BitConverter.SingleToUInt32Bits(pane.Blur));
        hash = Mix(hash, ((ulong)BitConverter.SingleToUInt32Bits(pane.Opacity) << 32) | BitConverter.SingleToUInt32Bits(pane.Bend));
        hash = Mix(hash, ((ulong)BitConverter.SingleToUInt32Bits(pane.Lens) << 32) | pane.Tint);
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
        // The keyboard's owner is everybody's, a compositor thrown away while it had it was kept by it with its scene
        if (focusOwner == this)
            focusOwner = null;

        if (compositionInput is not null)
        {
            compositionInput.EventReceived -= OnCompositionEvent;
            compositionInput = null;
        }
        compositions.Clear();

        middleScroll.Cancel();
        focus = null;
        lastRoutedFocus = null;
        hovered = null;
        pressed = null;
        gripped = null;
        middleScrollTarget = null;
        tooltipModule = null;
        TooltipOf = null;
        held = null;
        pinned = null;
        hushed = null;
        touchFocus = null;
        touchTooltip = null;
        touchFocused = null;
        panFrom = null;
        FocusShownOn = null;
        Highlighted = null;

        cursorBatch?.Dispose();
        cursorBatch = null;
        layer.Dispose();
        glassPass?.Dispose();
        glassPass = null;
        pictureCurrent = false;

        renderer.Dispose();
        initialized = false;
        resourcesInitialized = false;
        Skin = null;

        GC.SuppressFinalize(this);
    }
}
