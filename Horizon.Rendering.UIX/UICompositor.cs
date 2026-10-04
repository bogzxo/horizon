using System.Numerics;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Input;
using Horizon.Rendering.UIX.Components;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX;

/// <summary>
/// Runs a UI: owns the skin and the modules, routes the pointer to the component under it, and draws
/// everything on top of the scene.
/// The work is split the way the engine splits it. Input, layout and painting happen in
/// <see cref="UpdateState"/> on the logic thread and produce a list of quads; <see cref="Render"/>
/// only uploads the latest list and draws it, so components never touch the GPU.
/// </summary>
public class UICompositor : IGameComponent
{
    private const string DEFAULT_SKIN_DIRECTORY = "Assets/uix/";
    private const string DEFAULT_SKIN_FILE = "skin.hor";

    internal readonly Camera2D viewportCamera;
    private readonly string skinDirectory;
    private readonly string skinFile;

    private readonly UIRenderer renderer = new();

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

    public UICompositor(in Camera2D viewportCamera)
        : this(viewportCamera, DEFAULT_SKIN_DIRECTORY, DEFAULT_SKIN_FILE) { }

    /// <param name="viewportCamera">The camera the UI is drawn with. What it sees is the screen the modules are laid out against.</param>
    /// <param name="skinDirectory">The directory holding the skin definition and its texture.</param>
    /// <param name="skinFile">The name of the skin definition in that directory.</param>
    public UICompositor(in Camera2D viewportCamera, string skinDirectory, string skinFile)
    {
        this.viewportCamera = viewportCamera;
        this.skinDirectory = skinDirectory;
        this.skinFile = skinFile;
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
        Skin = UISkin.Load(skinDirectory, skinFile);

        if (Skin is not null)
            renderer.Initialize(Skin);

        UIKeyboard.Hook();
    }

    public void UpdateState(float dt)
    {
        if (Skin is not { } skin)
            return;

        var snapshot = modules;

        // The pointer is tested against the layout of the previous update, which is the one on screen.
        RoutePointer(snapshot);
        RouteKeyboard();

        // What the camera sees. Its bounds start at the bottom left corner, the world being Y-up.
        var bounds = viewportCamera.Bounds;
        var screen = new UIRect(
            new Vector2(bounds.X, bounds.Y),
            new Vector2(bounds.X + bounds.Width, bounds.Y + bounds.Height));

        back.Begin(skin);
        foreach (var module in snapshot)
        {
            if (!module.Enabled)
                continue;

            module.Update(dt);
            module.Layout(screen, skin);
            module.Paint(back);
        }
        back.End();

        lock (frameLock)
        {
            (front, back) = (back, front);
            paintedFrame++;
        }
    }

    public void UpdatePhysics(float dt)
    { }

    public void Render(float dt, object? obj = null)
    {
        if (Skin is not { } skin)
            return;

        lock (frameLock)
        {
            if (uploadedFrame != paintedFrame)
            {
                renderer.Upload(front);
                uploadedFrame = paintedFrame;
            }
        }

        renderer.Draw(viewportCamera, skin);
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

        while (focus is not null && UIKeyboard.TryRead(out char character))
            focus.OnTextInput(character);
    }

    private void RoutePointer(UIModule[] snapshot)
    {
        UIPointer pointer = PointerSource?.Invoke() ?? ReadMouse();

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
