using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.UIX.Components;
using Horizon.Rendering.UIX.Scripting;

namespace Horizon.Rendering.UIX;

public partial class UIModule
{
    private readonly Dictionary<string, IRuntimeValue> factories = [];

    // What every kind of component is called in a script, and how to make one. Shared by every module.
    private static readonly Dictionary<string, (Type Type, Func<UIComponent> Create)> kinds = [];

    /// <summary>The names scripts make components by, as in compositor.name({ ... }).</summary>
    public static IReadOnlyCollection<string> Kinds
    {
        get
        {
            lock (kinds)
                return [.. kinds.Keys];
        }
    }

    /// <summary>The name scripts make a component like this one by, null if they can't.</summary>
    public static string? KindOf(UIComponent component)
    {
        lock (kinds)
        {
            foreach (var (name, kind) in kinds)
            {
                if (kind.Type == component.GetType())
                    return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Makes a component of a kind scripts know, with everything about it as it starts out and without
    /// putting it anywhere. Null for a kind nobody registered.
    /// </summary>
    public static UIComponent? CreateDetached(string kind)
    {
        lock (kinds)
            return kinds.TryGetValue(kind, out var known) ? known.Create() : null;
    }

    public HIDLRuntime Runtime { get; init; } = new();

    // While a layout is being built: what the components it doesn't put anywhere go into (the screen if
    // nothing), and a note of each of them.
    private readonly Lock captureLock = new();
    private UIComponent? captureParent;
    private List<UIComponent>? captured;

    /// <summary>
    /// Runs something that makes components (a layout script) with the ones it doesn't give a parent going
    /// into a component of the caller's choosing rather than onto the screen.
    /// </summary>
    /// <param name="created">Gets every component that was put there.</param>
    internal void Capture(UIComponent? parent, List<UIComponent> created, Action build)
    {
        lock (captureLock)
        {
            // A layout can set off another one (a handler that fills a container), which gets its turn and hands back.
            var (outerParent, outerCaptured) = (captureParent, captured);
            (captureParent, captured) = (parent, created);

            try
            {
                build();
            }
            finally
            {
                (captureParent, captured) = (outerParent, outerCaptured);
            }
        }
    }

    // What the layout that is being built said about the screen it was made for, see compositor.design
    internal (System.Numerics.Vector2 Size, UIFit Fit)? declaredDesign;

    protected void SetupRuntime()
    {
        Runtime.UserScope.DeclareSystem("compositor", new ObjectValue(factories));

        // compositor.design({ size: vec(1600, 900), fit: "contain" }): the screen the layout was made for. Only
        // means something for a layout that is the whole module (not one built inside a component, a template)
        factories["design"] = new NativeFunctionValue((args, _) =>
        {
            if (args.Length != 1 || args[0] is not ObjectValue { Properties: { } properties })
                throw new Exception("compositor.design expects one object: { size: vec(1600, 900), fit: \"contain\" or \"stretch\" }.");

            if (!properties.TryGetValue("size", out var sizeValue))
                throw new Exception("compositor.design needs the size of the screen the layout was made for.");

            var size = UIScript.ToVector2(sizeValue, "size");
            if (size.X <= 0.0f || size.Y <= 0.0f)
                throw new Exception("compositor.design needs a size bigger than nothing either way.");

            UIFit fit = properties.TryGetValue("fit", out var fitValue) ? UIScript.ToEnum<UIFit>(fitValue, "fit") : UIFit.Contain;

            declaredDesign = (size, fit);
            if (captureParent is null)
            {
                DesignSize = size;
                Fit = fit;
            }

            return new NullValue();
        });

        Register<Button>("button");
        Register<ToggleButton>("toggle");
        Register<ProgressBar>("progress_bar");
        Register<Slider>("slider");
        Register<TextBox>("textbox");
        Register<Label>("label");
        Register<Image>("image");
        Register<Panel>("panel");
        Register<Group>("group");
        Register<TabPanel>("tabs");
        Register<StackPanel>("stack");
        Register<GridPanel>("grid");
        Register<ScrollPanel>("scroll");
        Register<NumberBox>("number_box");
        Register<Selector>("selector");
        Register<Dropdown>("dropdown");
        Register<ColorPicker>("color_picker");
        Register<MenuBar>("menu_bar");
        Register<CodeView>("code");
    }

    /// <summary>
    /// Lets scripts create a kind of component as compositor.name({ ... }). The object is optional and
    /// can set any property the component exposes, including parent to put it inside another component;
    /// a component without a parent is added to the module itself.
    /// </summary>
    public void Register<T>(string name) where T : UIComponent, new()
    {
        lock (kinds)
            kinds[name] = (typeof(T), static () => new T());

        factories[name] = new NativeFunctionValue((args, _) =>
        {
            if (args.Length > 1 || (args.Length == 1 && args[0] is not ObjectValue))
                throw new Exception($"compositor.{name} expects nothing, or one object with the properties to set.");

            var component = new T();
            try
            {
                if (args.Length == 1)
                    component.ApplyScript((ObjectValue)args[0]);
            }
            catch
            {
                // Don't leave a half configured component behind in whatever parent it was given.
                component.Parent?.Remove(component);
                throw;
            }

            if (component.Parent is null)
            {
                (captureParent ?? Root).Add(component);
                captured?.Add(component);
            }

            return component.Object;
        });
    }

    /// <summary>
    /// Attempts to construct a UI using HIDL.
    /// </summary>
    /// <param name="definitionPath">The path to the definition file.</param>
    /// <exception cref="FileNotFoundException">If the defintion could not be found</exception>
    public (bool success, string result) Load(string definitionPath) => !File.Exists(definitionPath) ? throw new FileNotFoundException($"The file '{definitionPath}' could not be found!") : Runtime.Evaluate(File.ReadAllText(definitionPath));
}
