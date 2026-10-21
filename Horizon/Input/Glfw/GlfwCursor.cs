using Silk.NET.Core;
using Silk.NET.GLFW;
using Silk.NET.Input;

using GlfwCursorHandle = Silk.NET.GLFW.Cursor;

namespace Horizon.Input;

/// <summary>
/// The cursor of <see cref="GlfwMouse"/>. Its mode (shown, hidden, locked to the window, raw), one of the system's
/// shapes, or a picture of its own, as Silk's GLFW one does them. Thread of the window, as everything GLFW does to a cursor is.
/// </summary>
internal sealed unsafe class GlfwCursor : ICursor, IDisposable
{
    private static readonly Dictionary<StandardCursor, CursorShape> shapes = new()
    {
        [StandardCursor.Arrow] = CursorShape.Arrow,
        [StandardCursor.IBeam] = CursorShape.IBeam,
        [StandardCursor.Crosshair] = CursorShape.Crosshair,
        [StandardCursor.Hand] = CursorShape.Hand,
        [StandardCursor.HResize] = CursorShape.HResize,
        [StandardCursor.VResize] = CursorShape.VResize,
        [StandardCursor.ResizeAll] = CursorShape.AllResize,
        [StandardCursor.NeswResize] = CursorShape.NeswResize,
        [StandardCursor.NwseResize] = CursorShape.NwseResize,
        [StandardCursor.NotAllowed] = CursorShape.NotAllowed
    };

    private readonly Silk.NET.GLFW.Glfw glfw;
    private readonly WindowHandle* window;

    // The system's shapes, made once each and kept, and the picture of our own, made anew whenever it changes
    private readonly Dictionary<StandardCursor, nint> standard = [];
    private GlfwCursorHandle* custom;

    private CursorType type = CursorType.Standard;
    private StandardCursor standardCursor = StandardCursor.Default;
    private int hotspotX, hotspotY;
    private RawImage image;

    public GlfwCursor(Silk.NET.GLFW.Glfw glfw, WindowHandle* window)
    {
        this.glfw = glfw;
        this.window = window;
    }

    public CursorType Type
    {
        get => type;
        set
        {
            if (type == value) return;
            if (value is not (CursorType.Standard or CursorType.Custom))
                throw new InvalidOperationException("GLFW has no cursor of that type.");

            type = value;
            Apply();
        }
    }

    public StandardCursor StandardCursor
    {
        get => standardCursor;
        set
        {
            if (standardCursor == value) return;

            standardCursor = value;
            if (type == CursorType.Standard) Apply();
        }
    }

    public CursorMode CursorMode
    {
        get
        {
            var mode = (CursorModeValue)glfw.GetInputMode(window, CursorStateAttribute.Cursor);
            return mode switch
            {
                CursorModeValue.CursorHidden => CursorMode.Hidden,
                CursorModeValue.CursorDisabled => glfw.GetInputMode(window, CursorStateAttribute.RawMouseMotion) != 0 ? CursorMode.Raw : CursorMode.Disabled,
                _ => CursorMode.Normal
            };
        }
        set
        {
            CursorModeValue mode = value switch
            {
                CursorMode.Normal => CursorModeValue.CursorNormal,
                CursorMode.Hidden => CursorModeValue.CursorHidden,
                CursorMode.Disabled or CursorMode.Raw => CursorModeValue.CursorDisabled,
                _ => throw new ArgumentException("There is no cursor mode like that.", nameof(value))
            };

            glfw.SetInputMode(window, CursorStateAttribute.Cursor, mode);
            if (glfw.RawMouseMotionSupported())
                glfw.SetInputMode(window, CursorStateAttribute.RawMouseMotion, value == CursorMode.Raw);
        }
    }

    // GLFW can't keep a cursor inside the window without hiding it
    public bool IsConfined
    {
        get => false;
        set { }
    }

    public int HotspotX
    {
        get => hotspotX;
        set
        {
            if (hotspotX == value) return;

            hotspotX = value;
            if (type == CursorType.Custom) Apply();
        }
    }

    public int HotspotY
    {
        get => hotspotY;
        set
        {
            if (hotspotY == value) return;

            hotspotY = value;
            if (type == CursorType.Custom) Apply();
        }
    }

    public RawImage Image
    {
        get => image;
        set
        {
            if (image == value) return;

            image = value;
            if (type == CursorType.Custom) Apply();
        }
    }

    public bool IsSupported(CursorMode mode) => mode switch
    {
        CursorMode.Normal or CursorMode.Hidden or CursorMode.Disabled => true,
        CursorMode.Raw => glfw.RawMouseMotionSupported(),
        _ => false
    };

    public bool IsSupported(StandardCursor cursor)
    {
        // The diagonal and the not allowed shapes came with GLFW 3.4
        glfw.GetVersion(out int major, out int minor, out _);
        bool newer = major > 3 || (major == 3 && minor >= 4);

        return cursor switch
        {
            StandardCursor.Default or StandardCursor.Arrow or StandardCursor.IBeam or StandardCursor.Crosshair
                or StandardCursor.Hand or StandardCursor.HResize or StandardCursor.VResize => true,
            StandardCursor.NeswResize or StandardCursor.NwseResize or StandardCursor.ResizeAll or StandardCursor.NotAllowed => newer,
            _ => false
        };
    }

    /// <summary>
    /// Helper method to give the window the cursor that is asked for right now.
    /// </summary>
    private void Apply()
    {
        DestroyCustom();
        glfw.SetCursor(window, type == CursorType.Custom ? MakeCustom() : Standard());
    }

    private GlfwCursorHandle* Standard()
    {
        if (standardCursor == StandardCursor.Default) return null;
        if (!shapes.TryGetValue(standardCursor, out CursorShape shape))
            throw new InvalidOperationException("GLFW has no cursor of that shape.");

        if (!standard.TryGetValue(standardCursor, out nint made))
            standard[standardCursor] = made = (nint)glfw.CreateStandardCursor(shape);

        return (GlfwCursorHandle*)made;
    }

    private GlfwCursorHandle* MakeCustom()
    {
        // Set up a piece at a time, so the system's arrow stands in until the picture adds up (four bytes a pixel)
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length != image.Width * image.Height * 4)
            return null;

        fixed (byte* pixels = image.Pixels.Span)
        {
            var picture = new Silk.NET.GLFW.Image { Width = image.Width, Height = image.Height, Pixels = pixels };
            custom = glfw.CreateCursor(&picture, hotspotX, hotspotY);
        }

        return custom;
    }

    private void DestroyCustom()
    {
        if (custom is null) return;

        glfw.DestroyCursor(custom);
        custom = null;
    }

    public void Dispose()
    {
        try
        {
            glfw.SetCursor(window, null);
            DestroyCustom();
            foreach (nint made in standard.Values)
                glfw.DestroyCursor((GlfwCursorHandle*)made);
        }
        catch
        {
            // the window is on its way out, and GLFW takes every cursor with it when it ends
        }

        standard.Clear();
    }
}
