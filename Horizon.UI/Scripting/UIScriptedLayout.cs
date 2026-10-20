using System.Numerics;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;
using ValueType = Horizon.HIDL.Runtime.ValueType;

namespace Horizon.UI.Scripting;

/// <summary>
/// Whoever a <see cref="UIScriptedLayout"/> belongs to, a script with a runtime of its own (one of Hex's extensions),
/// which its handlers and its on_tick and the rest are called on. A call that goes wrong is said in <see cref="Failed"/>
/// and the owner is left alone after.
/// </summary>
public interface IUIScriptOwner
{
    /// <summary>What it's called, what its layouts say they're of.</summary>
    string Name { get; }

    /// <summary>What went wrong with it, null while nothing has.</summary>
    string? Failed { get; }

    /// <summary>Calls a function it handed over, with what it handed back, false if it went wrong.</summary>
    bool Invoke(IRuntimeValue function, IRuntimeValue[] arguments, out IRuntimeValue returned);
}

/// <summary>
/// A layout a script made and drives, a view, a page of a panel or a window, as the script sees it. <c>ui.get(name)</c>
/// is a part of it (its properties, as a layout's own code sees them), <c>ui.file</c> the file a view is of,
/// <c>ui.on_tick</c> and <c>ui.on_key</c> what to call as time goes by and for the keys while it is on screen,
/// <c>ui.on_resize</c> what to call when the room it has changed (and the first time it's on screen), <c>ui.close()</c>
/// takes a window or a page away, <c>ui.size</c> is how much room it has. A tick is a HIDL call every frame, so a UI that
/// only wants to know about its size says on_resize and leaves on_tick for when it has something going on (playing).
/// </summary>
public sealed class UIScriptedLayout(IUIScriptOwner owner, string kind, UILayout made, UIComponent root, string? file, Action? close, Func<Vector2>? room = null) : IHostValue
{
    public ValueType Type { get; init; } = ValueType.Host;

    public string TypeName => $"{kind} of {Owner.Name}";

    public IUIScriptOwner Owner { get; } = owner;

    public UILayout Made { get; } = made;

    /// <summary>What everything it made is inside of, shown and hidden as one.</summary>
    public UIComponent Root { get; } = root;

    public string? File { get; } = file;

    public IRuntimeValue? Tick { get; private set; }

    public IRuntimeValue? Key { get; private set; }

    public IRuntimeValue? Resize { get; private set; }

    /// <summary>The room it was last told it has, see <see cref="Room"/>.</summary>
    public Vector2 Sized { get; set; }

    /// <summary>How much room it has, what ui.size says.</summary>
    public Vector2 Room => room?.Invoke() ?? Root.Bounds.Size;

    /// <summary>Whether it was taken away, a window closed or its file.</summary>
    public bool Closed { get; set; }

    public IRuntimeValue Get(string member) => member switch
    {
        "get" => Natives.Function("get", args => Made.TryGet(args.Text(0), out UIComponent? part)
            ? part.Object
            : throw args.Wrong($"the layout has nothing called '{args.Text(0)}'.")),
        "file" => File is { } path ? new StringValue(path) : Values.Null,
        "on_tick" => Tick ?? Values.Null,
        "on_key" => Key ?? Values.Null,
        "on_resize" => Resize ?? Values.Null,
        "visible" => Values.Bool(Root.Visible),
        "size" => new Vector2Value(Room),
        "close" => Natives.Action("close", _ => close?.Invoke()),
        _ => Values.Null
    };

    public void Set(string member, IRuntimeValue value)
    {
        switch (member)
        {
            case "on_tick": Tick = value is NullValue ? null : value; break;
            case "on_key": Key = value is NullValue ? null : value; break;
            case "on_resize": Resize = value is NullValue ? null : value; Sized = default; break;
            default: throw new HidlRuntimeException($"{member} of a {kind} can't be set, on_tick, on_key and on_resize can.");
        }
    }

    /// <summary>
    /// Makes a layout of a script's folder inside of something, its handlers the script's to call (and the owner said
    /// to have failed through <paramref name="failed"/> when one goes wrong). Null, said through <paramref name="problem"/>,
    /// if the layout doesn't build.
    /// </summary>
    public static UIScriptedLayout? Make(UIModule module, IUIScriptOwner owner, string kind, string layoutPath, UIComponent into, string? file,
        Action? close, Action<string> problem, Action<IUIScriptOwner> failed, Func<Vector2>? room = null)
    {
        UILayout made;
        try
        {
            made = UILayout.Load(module, layoutPath, into);
        }
        catch (Exception e)
        {
            problem($"{owner.Name}: {Path.GetFileName(layoutPath)} didn't build, {e.Message}");
            return null;
        }

        into.ScriptCaller = (handler, arguments) =>
        {
            if (owner.Failed is null && !owner.Invoke(handler, arguments, out _))
                failed(owner);
        };

        return new UIScriptedLayout(owner, kind, made, into, file, close, room);
    }

    /// <summary>
    /// A little window over everything out of a script's layout, dragged by its title, bigger by its corner, gone with
    /// its x. <paramref name="closed"/> once it's gone, however it went.
    /// </summary>
    public static UIScriptedLayout? MakeWindow(UIModule module, IUIScriptOwner owner, string title, string layoutPath, Vector2 size,
        Action<string> problem, Action<IUIScriptOwner> failed, Action<UIScriptedLayout>? closed = null)
    {
        var window = module.Root.Add(new WindowPanel { Title = title, Size = size, Closable = true, ResizeGrip = true, Padding = new UIEdges(8) });
        window.Color = module.Compositor.Skin is { } skin ? skin.PanelColor with { W = 1.0f } : null;

        UIScriptedLayout? made = null;
        void Close()
        {
            window.Parent?.Remove(window);
            if (made is null || made.Closed)
                return;
            made.Closed = true;
            closed?.Invoke(made);
        }

        made = Make(module, owner, "window", layoutPath, window, null, Close, problem, failed);
        if (made is null)
        {
            window.Parent?.Remove(window);
            return null;
        }

        window.OnClosed = Close;
        return made;
    }

    /// <summary>
    /// A tick of one on screen, told the room it has when that changed (on_resize), then how long the tick was (on_tick).
    /// <paramref name="number"/> is made once for every one ticked this frame, and only when one wants it.
    /// </summary>
    public void Update(float dt, ref NumberValue? number, Action<IUIScriptOwner> failed)
    {
        // One that went wrong is said about the once and left alone after, not every tick
        if (Owner.Failed is not null)
            return;

        if (Resize is { } resize && Room is var now && now != Sized && now.X > 0 && now.Y > 0)
        {
            Sized = now;
            if (!Owner.Invoke(resize, [this, new Vector2Value(now)], out _))
            {
                failed(Owner);
                return;
            }
        }

        if (Tick is { } tick && !Owner.Invoke(tick, [this, number ??= new NumberValue(dt)], out _))
            failed(Owner);
    }

    /// <summary>
    /// A key handed to it, true if it took it (its on_key handed back true), which is how a program lets Ctrl+Z undo a
    /// view's own changes rather than the code's.
    /// </summary>
    public bool Press(string name, Action<IUIScriptOwner> failed)
    {
        if (Key is not { } handler || Owner.Failed is not null)
            return false;

        if (!Owner.Invoke(handler, [this, new StringValue(name)], out IRuntimeValue taken))
        {
            failed(Owner);
            return false;
        }

        return taken is BooleanValue { Value: true };
    }

    /// <summary>
    /// A key handed to it with what's held, "shift+left" first when shift is held, then "left" if it didn't want that.
    /// The names with shift and alt are made once a key, so a press makes nothing.
    /// </summary>
    public void Press(string name, bool shift, bool alt, Action<IUIScriptOwner> failed)
    {
        if (shift || alt)
        {
            if (!HeldNames.TryGetValue((name, shift, alt), out string? held))
                HeldNames[(name, shift, alt)] = held = (alt ? "alt+" : string.Empty) + (shift ? "shift+" : string.Empty) + name;
            if (Press(held, failed))
                return;
        }

        Press(name, failed);
    }

    private static readonly Dictionary<(string, bool, bool), string> HeldNames = [];

    /// <summary>
    /// The keys a scripted view is handed by name, the arrows and the like, letters, digits and a few signs, the ones
    /// a view of a file (Hex's move editor) has a use for.
    /// </summary>
    public static readonly (Key Key, string Name)[] Keys = BuildKeys();

    private static (Key, string)[] BuildKeys()
    {
        var keys = new List<(Key, string)>
        {
            (Silk.NET.Input.Key.Left, "left"), (Silk.NET.Input.Key.Right, "right"), (Silk.NET.Input.Key.Up, "up"), (Silk.NET.Input.Key.Down, "down"),
            (Silk.NET.Input.Key.Home, "home"), (Silk.NET.Input.Key.End, "end"), (Silk.NET.Input.Key.Space, "space"), (Silk.NET.Input.Key.Delete, "delete"),
            (Silk.NET.Input.Key.Enter, "enter"), (Silk.NET.Input.Key.PageUp, "page_up"), (Silk.NET.Input.Key.PageDown, "page_down")
        };
        for (char letter = 'a'; letter <= 'z'; letter++)
            keys.Add((Silk.NET.Input.Key.A + (letter - 'a'), letter.ToString()));
        for (char digit = '0'; digit <= '9'; digit++)
            keys.Add((Silk.NET.Input.Key.Number0 + (digit - '0'), digit.ToString()));
        keys.Add((Silk.NET.Input.Key.Comma, ","));
        keys.Add((Silk.NET.Input.Key.Period, "."));
        keys.Add((Silk.NET.Input.Key.LeftBracket, "["));
        keys.Add((Silk.NET.Input.Key.RightBracket, "]"));
        keys.Add((Silk.NET.Input.Key.Escape, "escape"));
        keys.Add((Silk.NET.Input.Key.Backspace, "backspace"));
        keys.Add((Silk.NET.Input.Key.Tab, "tab"));
        keys.Add((Silk.NET.Input.Key.Slash, "/"));
        return [.. keys];
    }
}
