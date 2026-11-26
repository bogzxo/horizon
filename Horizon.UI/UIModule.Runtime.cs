using Horizon.Content;
using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Horizon.UI;

public partial class UIModule
{
    private readonly record struct KindEntry(Type Type, Func<UIComponent> Create, Action<UIModule, UIComponent>? Made);

    private Dictionary<string, IRuntimeValue>? factories;
    private HIDLRuntime? runtime;
    private bool runtimeSetup;

    // What every kind of component is called in a script, and how to make one. Shared by every module so C#-constructed
    // UIModules allocate zero HIDLRuntimes and zero per-widget closures.
    private static readonly Dictionary<string, KindEntry> kinds = CreateBuiltInKinds();

    private static Dictionary<string, KindEntry> CreateBuiltInKinds()
    {
        var builtIn = new Dictionary<string, KindEntry>(48);

        void Add<T>(string name, Action<UIModule, UIComponent>? made = null) where T : UIComponent, new() =>
            builtIn[name] = new KindEntry(typeof(T), static () => new T(), made);

        Add<Button>("button");
        Add<ToggleButton>("toggle");
        Add<ProgressBar>("progress_bar");
        Add<Slider>("slider");
        Add<TextBox>("textbox");
        Add<Label>("label");
        Add<Image>("image");
        Add<Panel>("panel");
        Add<Group>("group");

        // `compositor.include({ parent: body, file: "shell/side_bar.hor" })` builds that file in here, its parts the
        // including layout's as well, so a UI is written as parts in files of their own, see Include
        Add<Include>("include", OnIncludeMade);
        Add<TabPanel>("tabs");
        Add<StackPanel>("stack");
        Add<Collapsible>("collapsible");
        Add<GridPanel>("grid");
        Add<ScrollPanel>("scroll");
        Add<NumberBox>("number_box");
        Add<Selector>("selector");
        Add<Dropdown>("dropdown");
        Add<ListBox>("list");
        Add<Divider>("divider");
        Add<Spacer>("spacer");
        Add<ColorPicker>("color_picker");
        Add<MenuBar>("menu_bar");
        Add<CodeView>("code");
        Add<CodeEditor>("code_editor");
        Add<Readout>("readout");
        Add<Meter>("meter");
        Add<Graph>("graph");
        Add<SegmentBar>("segments");
        Add<Symbol>("symbol");
        Add<Badge>("badge");
        Add<StatusBar>("status_bar");
        Add<TabStrip>("tab_strip");
        Add<Breadcrumbs>("breadcrumbs");
        Add<TitleBar>("title_bar");
        Add<WindowPanel>("window");
        Add<TreeView>("tree_view");
        Add<Notifications>("notifications");
        Add<Sparks>("sparks");
        Add<Table>("table");
        Add<DrawingPanel>("drawing");
        Add<FormPanel>("form");

        return builtIn;
    }

    private static void OnIncludeMade(UIModule module, UIComponent component)
    {
        var include = (Include)component;
        if (include.File.Length == 0)
            throw new Exception("compositor.include needs the file to build in it, from the folder of this layout.");

        UILayout inner = include.Reload(module, module.building);
        module.building?.Absorb(inner);
    }

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

    /// <summary>Whether this module has instantiated its HIDL scripting runtime yet, for unit tests.</summary>
    internal bool HasRuntime => runtime is not null;

    /// <summary>
    /// Lazily created HIDL scripting runtime for layout loading and scripting. C#-constructed modules that never
    /// touch <see cref="Runtime"/> or <see cref="LoadLayout"/> allocate neither a runtime nor widget factories.
    /// </summary>
    public HIDLRuntime Runtime
    {
        get
        {
            runtime ??= new HIDLRuntime().ReadingContent();
            if (!runtimeSetup)
                SetupRuntime();
            return runtime;
        }
        init
        {
            runtime = value;
            runtimeSetup = false;
        }
    }

    // While a layout is being built, what the components it doesn't put anywhere go into (the screen if
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

    // The layout being built right now, which is what an include is named from and what gets its parts
    internal UILayout? building;

    protected void SetupRuntime()
    {
        if (runtimeSetup)
            return;
        runtimeSetup = true;

        runtime ??= new HIDLRuntime().ReadingContent();
        factories ??= [];

        runtime.UserScope.DeclareSystem("compositor", new ObjectValue(factories));

        // `compositor.design({ size: vec(1600, 900), fit: "contain" })` says what screen the layout was made for. Only
        // means something for a layout that is the whole module (not one built inside a component, a template)
        factories.TryAdd("design", new NativeFunctionValue((args, _) =>
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
        }));

        // `compositor.hidden_layers(["code", "table"])` has the layers named start out hidden (see SetLayerVisible), the
        // screens of a menu that aren't the first, or the parts of an editor that take turns with each other, which an
        // editor shows and hides by the layer to work on each. Only for a layout that is the whole module, like design
        factories.TryAdd("hidden_layers", new NativeFunctionValue((args, _) =>
        {
            if (args.Length != 1)
                throw new Exception("compositor.hidden_layers expects one list of the layers that start out hidden: [\"code\", \"table\"].");

            if (captureParent is null)
            {
                foreach (string layer in UIScript.ToTexts(args[0], "hidden_layers"))
                    SetLayerVisible(layer.Trim(), false);
            }

            return new NullValue();
        }));

        KeyValuePair<string, KindEntry>[] snapshot;
        lock (kinds)
            snapshot = [.. kinds];

        foreach (var (name, entry) in snapshot)
        {
            if (!factories.ContainsKey(name))
                factories[name] = CreateFactory(this, name, entry);
        }
    }

    private static NativeFunctionValue CreateFactory(UIModule module, string name, KindEntry entry) =>
        new((args, _) => module.InvokeFactory(name, entry.Create, entry.Made, args));

    private IRuntimeValue InvokeFactory(string name, Func<UIComponent> create, Action<UIModule, UIComponent>? made, IRuntimeValue[] args)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] is not ObjectValue))
            throw new Exception($"compositor.{name} expects nothing, or one object with the properties to set.");

        UIComponent component = create();
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

        made?.Invoke(this, component);
        return component.Object;
    }

    /// <summary>
    /// A kind of component of another assembly's (the terminal's) made known to layouts and scripts in every module made
    /// from now on, as the engine's own kinds are.
    /// </summary>
    public static void AddKind<T>(string name) where T : UIComponent, new()
    {
        lock (kinds)
            kinds[name] = new KindEntry(typeof(T), static () => new T(), null);
    }

    /// <summary>
    /// Lets scripts create a kind of component as compositor.name({ ... }). The object is optional and
    /// can set any property the component exposes, including parent to put it inside another component,
    /// a component without a parent is added to the module itself.
    /// </summary>
    public void Register<T>(string name) where T : UIComponent, new() => Register<T>(name, null);

    /// <param name="made">What to do with one once the script's properties are on it and it is in its parent.</param>
    public void Register<T>(string name, Action<T>? made) where T : UIComponent, new()
    {
        Action<UIModule, UIComponent>? callback = made is null ? null : (_, component) => made((T)component);
        var entry = new KindEntry(typeof(T), static () => new T(), callback);

        lock (kinds)
            kinds[name] = entry;

        factories ??= [];
        factories[name] = CreateFactory(this, name, entry);
    }

    /// <summary>
    /// Attempts to construct a UI using HIDL.
    /// </summary>
    /// <param name="definitionPath">The path to the definition file.</param>
    /// <exception cref="FileNotFoundException">If the defintion could not be found</exception>
    public (bool success, string result) Load(string definitionPath) => !ContentFiles.Exists(definitionPath) ? throw new FileNotFoundException($"The file '{definitionPath}' could not be found!") : Runtime.Evaluate(ContentFiles.ReadAllText(definitionPath));
}
