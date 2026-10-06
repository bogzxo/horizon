using System.Numerics;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Input;
using Horizon.Rendering.PostProcessing;
using Horizon.Rendering.UIX.Components;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX;

/// <summary>
/// Runs a UI: owns the skin and the modules, routes the pointer to the component under it, and draws
/// everything on top of the scene.
/// The work is split the way the engine splits it. Input, layout and painting happen in
/// <see cref="UpdateState"/> on the logic thread and produce a list of quads; <see cref="Render"/>
/// only hands the latest list to the sprite renderer, so components never touch the GPU.
/// <para>
/// A UI can have effects of its own, see <see cref="PostProcessing"/>: with a
/// <see cref="MotionBlurEffect"/> what slides, pops or is tweened about is smeared along the way it goes.
/// </para>
/// </summary>
public class UICompositor : IGameComponent, IDisposable
{
    /// <summary>Where the skin every UI has unless it asks for another one is kept.</summary>
    public const string DEFAULT_SKIN_DIRECTORY = "Assets/uix/dead_revolver/";

    /// <summary>The definition of that skin.</summary>
    public const string DEFAULT_SKIN_FILE = "skin.hor";

    // How long (in milliseconds) the UI can go without an update before what moved in the meantime is not motion
    private const long MOTION_BREAK = 100;

    internal readonly Camera2D viewportCamera;
    private string skinDirectory;
    private string skinFile;
    private string? skinTheme;

    // For the layout debugger: where the pointer was last, and when the UI was last updated.
    private Vector2 lastPointer;
    private long lastUpdate;

    /// <summary>Where the pointer was and whether it was down as of the last update, in the camera's world space.</summary>
    public UIPointer Pointer { get; private set; }

    /// <summary>
    /// The size of the screen the UI was laid out for. Set, the whole UI is scaled so that a screen of that
    /// size just fits into whatever the camera sees: a layout made for 1600 by 900 comes out twice as big in a
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
    /// How much bigger than it was laid out the UI is drawn right now: <see cref="Scale"/> times whatever
    /// fitting the <see cref="DesignSize"/> comes to. A unit of the layout is this many units of the camera.
    /// </summary>
    public float UIScale { get; private set; } = 1.0f;

    // Scrolling that was asked for with Scroll and hasn't been handed out yet.
    private float pendingScroll;

    /// <summary>
    /// Scrolls whatever is under the pointer as if the mouse wheel had been turned, for input that isn't a
    /// wheel: the stick of a gamepad, or a test. Safe from any thread, it happens on the next update.
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

    // A skin asked for with SetSkin, waiting for the GL thread to load it.
    private (string Directory, string File, string? Theme)? requestedSkin;
    private readonly Lock skinLock = new();

    private readonly UIRenderer renderer = new();

    // Where the UI is drawn while it has effects that are on, rather than straight over the scene
    private readonly PostLayer layer = new();

    // Counts the updates for whoever works out how fast things are going, skipping one whenever there was a gap:
    // what is somewhere else after a break hasn't moved there
    private int motionFrame;
    private bool layerWarmed;

    // Whether what is on the layer is what the UI looks like right now, and what it was drawn with: seen
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
    /// The effects the UI goes through before it is laid over whatever is under it, none to begin with:
    /// <code>
    /// compositor.PostProcessing.Add(new MotionBlurEffect());
    /// </code>
    /// They only ever see the UI and leave what is behind it alone. While any of them is on the UI is drawn onto
    /// a layer of its own first (see <see cref="PostLayer"/>) and every component keeps track of how fast it is
    /// going across the screen, by where it ends up being drawn: layout, tweens and the place of its module all
    /// count. With none on there is no layer and no keeping track, the UI costs what it does without any of this,
    /// and an effect that is only for what moves is skipped (layer and all) whenever nothing in the UI does.
    /// What a component paints inside of its own bounds (the handle of a slider) goes as fast as the component.
    /// </summary>
    public PostProcessor PostProcessing => layer.Effects;

    // Replaced wholesale on every change, so the logic thread can walk the array it has while
    // another thread creates a module.
    private readonly Lock modulesLock = new();
    private UIModule[] modules = [];

    // The logic thread paints into `back`, then swaps it in as `front` for the render thread to upload.
    private readonly Lock frameLock = new();
    private UIDrawList back = new();
    private UIDrawList front = new();
    private int paintedFrame;
    private int uploadedFrame;

    private bool pointerWasDown;
    private UIComponent? hovered;
    private UIComponent? pressed;
    private UIComponent? focus;

    public IReadOnlyList<UIModule> Modules => modules;

    /// <summary>The look of the UI, null until <see cref="Initialize"/> has loaded it (or if it failed to).</summary>
    public UISkin? Skin { get; private set; }

    /// <summary>
    /// Where the pointer comes from. The mouse unless set to something else, which could be a cursor
    /// moved with a gamepad, or a recording.
    /// </summary>
    public Func<UIPointer>? PointerSource { get; set; }

    /// <summary>
    /// Whether the UI has the pointer: it is over a component that stops it, or it is still held
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
        }
    }

    public bool Enabled { get; set; }
    public string Name { get; set; } = "UI Compositor";
    public Entity Parent { get; set; }

    /// <param name="viewportCamera">The camera the UI is drawn with. What it sees is the screen the modules are laid out against.</param>
    /// <param name="theme">Which theme of the usual skin to draw the UI in ("red", "gold"), null for the one the skin says is its usual one.</param>
    public UICompositor(in Camera2D viewportCamera, string? theme = null)
        : this(viewportCamera, DEFAULT_SKIN_DIRECTORY, DEFAULT_SKIN_FILE, theme) { }

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

    public void Initialize()
    {
        // The one every UI with this skin draws with, see UISkin.Shared
        Skin = UISkin.Shared(skinDirectory, skinFile, skinTheme);

        renderer.Initialize();

        UIKeyboard.Hook();
    }

    /// <summary>
    /// Swaps the look of the whole UI for another skin, such as another theme of the same art. Safe
    /// from any thread: the skin is loaded the next time the UI is drawn, and if it can't be the
    /// current one stays.
    /// </summary>
    /// <param name="directory">The directory holding the skin definition and its art.</param>
    /// <param name="file">The name of the skin definition in that directory.</param>
    /// <param name="theme">Which of the skin's themes to use, null for the one it says is its usual one.</param>
    public void SetSkin(string directory, string file, string? theme = null)
    {
        lock (skinLock)
            requestedSkin = (directory, file, theme);
    }

    /// <summary>
    /// Swaps the UI over to another theme of the skin it has, see <see cref="UISkin.Themes"/> for the ones
    /// there are. Safe from any thread, like <see cref="SetSkin"/>.
    /// </summary>
    public void SetTheme(string? theme) => SetSkin(skinDirectory, skinFile, theme);

    private void LoadRequestedSkin()
    {
        (string Directory, string File, string? Theme)? request;
        lock (skinLock)
        {
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
    }

    public void UpdateState(float dt)
    {
        if (Skin is not { } skin)
            return;

        long now = Environment.TickCount64;
        motionFrame += now - lastUpdate > MOTION_BREAK ? 2 : 1;
        lastUpdate = now;

        var snapshot = modules;

        // What the camera sees. Its bounds start at the bottom left corner, the world being Y-up.
        var bounds = viewportCamera.Bounds;
        var screen = new UIRect(
            new Vector2(bounds.X, bounds.Y),
            new Vector2(bounds.X + bounds.Width, bounds.Y + bounds.Height));

        // Worked out before anything else uses it: the pointer has to be scaled the same way the layout is.
        float fit = DesignSize is { X: > 0.0f, Y: > 0.0f } design && !screen.IsEmpty
            ? MathF.Min(screen.Width / design.X, screen.Height / design.Y)
            : 1.0f;
        UIScale = MathF.Max(0.01f, fit * Scale);

        // The pointer is tested against the layout of the previous update, which is the one on screen.
        RoutePointer(snapshot);
        RouteKeyboard();

        back.Begin(skin, motionFrame, dt, layer.Effects.IsActive);
        foreach (var module in snapshot)
        {
            if (!module.Enabled)
                continue;

            module.Update(dt);
            module.Layout(screen, skin);
            module.Paint(back);

            // On top of the module and with its transform, so the layout lines up with what it is the layout of.
            if (module.ShowInLayoutDebugger && LayoutOverlay is { } overlay)
                UILayoutOverlay.Paint(back, module, overlay, module.ToLocal(lastPointer));

            // Whatever an editor has picked out is marked whether the debugger is open or not.
            if (Highlighted is { } highlighted && highlighted.Module == module)
                UILayoutOverlay.PaintHighlight(back, highlighted);
        }
        back.End();

        // Nothing happened: what was painted is what is on screen already, and nobody needs to hear about it
        if (paintedFrame > 0 && back.SameAs(front))
            return;

        lock (frameLock)
        {
            (front, back) = (back, front);
            paintedFrame++;
        }
    }

    public void UpdatePhysics(float dt)
    { }

    /* For whoever is working on a layout rather than using it, an editor say */

    /// <summary>
    /// What of its layout the UI draws over itself (the edges of its components, their padding, the gaps between
    /// them), null for none of it, which is what it is unless somebody sets it. Only the modules that have
    /// <see cref="UIModule.ShowInLayoutDebugger"/> on are drawn over. Works the same in every build.
    /// </summary>
    public UILayoutOverlayOptions? LayoutOverlay { get; set; }

    /// <summary>
    /// A component of this UI that is marked out from the rest, null for none: what an editor has selected.
    /// </summary>
    public UIComponent? Highlighted { get; set; }

    public void Render(float dt, object? obj = null)
    {
        // What a text box copied goes to the clipboard here, on the thread that may touch it
        UIKeyboard.FlushClipboard();

        LoadRequestedSkin();

        if (Skin is not { } skin)
            return;

        lock (frameLock)
        {
            // Art that was asked for while painting is stitched into the atlas now. A frame that was painted
            // while any was missing is skipped, what was shown before it stays up until the next one has it.
            skin.Update();

            if (uploadedFrame != paintedFrame)
            {
                // A frame painted with a skin that has been swapped out since is skipped too.
                if (front.Skin == skin && !front.Incomplete)
                {
                    renderer.Upload(front);
                    pictureCurrent = false;
                }

                uploadedFrame = paintedFrame;
            }

            // Nothing painted yet, or what was painted last is still waiting for its art: what is about to be
            // drawn is not the UI as it is meant to look. A scene that was just set isn't shown like that
            if (paintedFrame == 0 || front.Incomplete)
                GameEngine.Instance.SceneManager.ReportUnfinished();
        }

        // Onto its own layer and through its effects if it has any that are on, straight over the scene if not.
        // A UI that is standing still has nothing for a blur to do, and goes straight there as well
        // The very first frame goes through the layer whether anything moves or not: making the layer and what its
        // effects need takes a moment, which is better spent while the UI is loading than on the first frame
        // something in it moves
        // Seen through a camera that has changed, what was drawn before is not what the UI looks like any more
        if (viewportCamera.View != pictureView || viewportCamera.Projection != pictureProjection)
            pictureCurrent = false;

        // Nothing has changed since the picture on the layer was drawn: that is laid over the frame as it is,
        // and none of the UI is drawn again
        if (Retained && pictureCurrent && layer.Replay(dt))
            return;

        bool layered = layer.Begin(renderer.Moving || !layerWarmed, Retained);
        layerWarmed = true;
        renderer.Draw(viewportCamera);

        if (layered)
            layer.End(dt);

        // Only a picture that made it onto the layer is there to be shown again
        pictureCurrent = layered;
        (pictureView, pictureProjection) = (viewportCamera.View, viewportCamera.Projection);
    }

    /// <summary>
    /// Frees what the UI has on the GPU: the layer it is drawn onto for its effects, along with the effects.
    /// GL thread. Its skin is not its own (see <see cref="UISkin.Shared"/>) and stays.
    /// </summary>
    public void Dispose()
    {
        layer.Dispose();

        renderer.Clear();
        Skin = null;

        GC.SuppressFinalize(this);
    }

    private UIPointer ReadMouse()
    {
        // TODO: @bogz lets do something about this
        var mouseData = GameEngine.Instance.InputManager.MouseManager.GetData();

        return new UIPointer(
            viewportCamera.ScreenToWorld(mouseData.Position),
            (mouseData.Actions & VirtualAction.PrimaryAction) != 0);
    }

    private void RouteKeyboard()
    {
        // A component that left the UI takes the focus with it.
        if (focus is not null && focus.Module is not { Enabled: true })
            focus = null;

        if (focus is null)
            return;

        // A key that edits and is being held is typed again every so often
        UIKeyboard.Repeat();

        while (focus is not null && UIKeyboard.TryRead(out char character))
            focus.OnTextInput(character);
    }

    /// <summary>
    /// Hands the mouse wheel to whatever is under the pointer that has a use for it: the innermost component
    /// first, then the ones it is inside of.
    /// </summary>
    private void RouteScroll(UIModule[] snapshot, Vector2 pointer)
    {
        float delta = UIKeyboard.TakeScroll() + Interlocked.Exchange(ref pendingScroll, 0.0f);
        if (delta == 0.0f)
            return;

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (!snapshot[i].Enabled || !snapshot[i].Interactive)
                continue;

            // Whatever is open on top of the module is asked before what is under it.
            if (snapshot[i].Popup is { } popup && popup.PopupContains(snapshot[i].ToLocal(pointer)) && popup.OnScroll(delta))
                return;

            for (UIComponent? component = snapshot[i].FindAt(pointer); component is not null; component = component.Parent)
            {
                if (component.OnScroll(delta))
                    return;
            }
        }
    }

    private void RoutePointer(UIModule[] snapshot)
    {
        UIPointer pointer = PointerSource?.Invoke() ?? ReadMouse();
        lastPointer = pointer.Position;
        Pointer = pointer;

        RouteScroll(snapshot, pointer.Position);

        // A component that left the UI while it was held is forgotten.
        if (pressed is not null && pressed.Module is not { Enabled: true })
        {
            pressed.IsPressed = false;
            pressed = null;
        }

        // Modules are drawn in order, so the last one is on top and gets the first look.
        UIComponent? over = null;
        for (int i = snapshot.Length - 1; i >= 0 && over is null; i--)
        {
            if (snapshot[i].Enabled)
                over = snapshot[i].HitTest(pointer.Position);
        }

        IsPointerOverUI = over is not null || pressed is not null;

        // While something is held nothing else reacts to the pointer passing over it.
        UIComponent? hover = pressed is null || pressed == over ? over : null;
        if (hover != hovered)
        {
            if (hovered is not null)
                hovered.IsHovered = false;
            if (hover is not null)
                hover.IsHovered = true;

            hovered = hover;
        }

        if (pointer.Down && !pointerWasDown)
        {
            // Pressing anywhere but on what is open closes it.
            foreach (var module in snapshot)
            {
                if (module.Popup is { } popup && popup != over)
                    module.Popup = null;
            }

            // Pressing on something that isn't part of the UI at all leaves the focus where it is,
            // or a click meant for the game would stop the typing.
            if (over is not null)
                Focus = over.Focusable && over.EnabledInHierarchy ? over : null;

            if (over is not null && over.EnabledInHierarchy)
            {
                pressed = over;
                pressed.IsPressed = true;
                pressed.OnPointerDown(pressed.Module!.ToLocal(pointer.Position));
            }
        }
        else if (pointer.Down && pressed is not null)
        {
            pressed.OnPointerDrag(pressed.Module!.ToLocal(pointer.Position));
        }
        else if (!pointer.Down && pressed is not null)
        {
            var released = pressed;
            pressed = null;

            released.IsPressed = false;
            released.OnPointerUp(released.Module!.ToLocal(pointer.Position));

            if (released == over)
                released.OnClick();
        }

        pointerWasDown = pointer.Down;
    }
}
