using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.UI.Components;

using Environment = Horizon.HIDL.Runtime.Environment;

namespace Horizon.UI;

/// <summary>
/// A piece of UI out of a layout file. The components a HIDL script made, by the names the script gave them.
/// This is how a program gets its UI from files that can be changed (and drawn up in an editor) without
/// touching the code.
/// <code>
/// var layout = UILayout.Load(module, "Assets/ui/menu.hor");
/// layout.Get&lt;Button&gt;("play").OnPressed = Play;
/// </code>
/// Every layout runs in a scope of its own, so two files (or the same file twice) never trip over each
/// other's names. That is also what makes a layout usable as a template. What isn't known until the program
/// runs (a row per save game, a cell per character) is a container in the layout that names the file its
/// items are made from, and <see cref="Populate(Panel, int)"/> makes as many of them as are needed.
/// </summary>
public sealed class UILayout
{
    // Layouts that have been read before, by where they are. A template is read once however many items it makes.
    private static readonly ConcurrentDictionary<string, (DateTime Written, long CheckedAt, ProgramStatement Program)> programs = new();

    // How long a layout's file is taken to be what it was when it was last looked at, in milliseconds. An editor's
    // inspector makes a row out of the same template thirty times in a click, and asking the disk twice for every one
    // was most of what that click cost
    private const long RECHECK_MS = 1000;

    private readonly Dictionary<string, UIComponent> parts = [];
    private readonly List<UIComponent> roots = [];

    // What Bind has filled containers with, so the next call can keep them
    private readonly Dictionary<Panel, List<UILayout>> bound = [];

    /// <summary>The module the components are in.</summary>
    public UIModule Module { get; }

    /// <summary>The file the layout came from, empty for one that was made from code.</summary>
    public string Path { get; }

    /// <summary>The components the script didn't put inside of another one, the top of what it made.</summary>
    public IReadOnlyList<UIComponent> Roots => roots;

    /// <summary>Every component the script kept in a variable, by the name of the variable.</summary>
    public IReadOnlyDictionary<string, UIComponent> Parts => parts;

    /// <summary>
    /// The screen the layout says it was made for (<c>compositor.design</c>), null if it doesn't say. Loaded as the
    /// whole of a module it is also what the module goes by, see <see cref="UIModule.DesignSize"/>.
    /// </summary>
    public System.Numerics.Vector2? DesignSize { get; private set; }

    /// <summary>How the layout says it goes onto a screen of another shape, see <see cref="UIFit"/>.</summary>
    public UIFit Fit { get; private set; } = UIFit.Contain;

    private UILayout(UIModule module, string path)
    {
        Module = module;
        Path = path;
    }

    /// <summary>
    /// Builds the UI a layout file describes.
    /// </summary>
    /// <param name="module">The module the components go into.</param>
    /// <param name="parent">What the top of the layout is put inside of, null for the module itself (the screen).</param>
    /// <exception cref="Exception">The file isn't there or its script failed, the message says which and why.</exception>
    /// <param name="introDelay">Seconds the entrances of the layout wait on top of their own delays, see <see cref="UIComponent.Intro"/>.</param>
    public static UILayout Load(UIModule module, string path, UIComponent? parent = null, float introDelay = 0.0f)
    {
        return Run(module, path, Compile(path), parent, introDelay);
    }

    /// <summary>
    /// Builds the UI a layout script describes, for layouts that aren't in a file.
    /// </summary>
    /// <param name="path">Where to look for the templates the layout names, as if it had been loaded from there.</param>
    public static UILayout LoadCode(UIModule module, string code, UIComponent? parent = null, string path = "")
    {
        ProgramStatement program;
        try
        {
            program = new Parser().ProduceSyntaxTree(Lexer.Tokenize(code));
        }
        catch (Exception e)
        {
            throw new Exception($"The layout{Named(path)} can't be read: {e.Message}", e);
        }

        return Run(module, path, program, parent, 0.0f);
    }

    /// <summary>
    /// Plays the entrances of everything in the layout again, the way loading it did.
    /// </summary>
    public void PlayIntros(float delay = 0.0f)
    {
        foreach (var root in roots)
            root.PlayIntro(delay);
    }

    /// <summary>
    /// Finds a part of the layout by its name.
    /// </summary>
    /// <exception cref="Exception">There is no part by that name, or it isn't what was asked for.</exception>
    public T Get<T>(string name) where T : UIComponent
    {
        if (!parts.TryGetValue(name, out var part))
            throw new Exception($"The layout{Named(Path)} has to have a {typeof(T).Name} called '{name}', the code is built around it.");

        return part as T
            ?? throw new Exception($"'{name}' of the layout{Named(Path)} has to be a {typeof(T).Name}, it is a {part.GetType().Name}.");
    }

    public bool TryGet<T>(string name, [NotNullWhen(true)] out T? part) where T : UIComponent
    {
        part = parts.TryGetValue(name, out var found) ? found as T : null;
        return part is not null;
    }

    /// <summary>
    /// Builds another layout inside of a component of this one. The file is looked for next to this layout.
    /// </summary>
    public UILayout Instantiate(string template, UIComponent parent, float introDelay = 0.0f) =>
        Load(Module, Resolve(template), parent, introDelay);

    /// <summary>
    /// Fills a container with items made from its <see cref="Panel.Template"/>, in place of whatever was in it.
    /// Each item comes back as a layout of its own, with its parts under the names the template gives them.
    /// Items make their entrances one after the other if the container has a <see cref="Panel.Stagger"/>.
    /// </summary>
    public IReadOnlyList<UILayout> Populate(Panel container, int count)
    {
        if (container.Template.Length == 0)
            throw new Exception($"A container of the layout{Named(Path)} is meant to be filled in but doesn't say what from: it needs a template.");

        foreach (var child in container.Children.ToArray())
            container.Remove(child);

        var items = new List<UILayout>(count);
        for (int i = 0; i < count; i++)
            items.Add(Item(container, container, i * container.Stagger));

        return items;
    }

    /// <summary>
    /// One item made from a container's template, into the container unless told where else (a row of a section
    /// rather than the list it was declared on). The template is looked for next to the file the container was
    /// written in, which is the included file's folder for a container of an included layout.
    /// </summary>
    public UILayout Item(Panel container, UIComponent? into = null, float introDelay = 0.0f)
    {
        if (container.Template.Length == 0)
            throw new Exception($"A container of the layout{Named(Path)} is meant to be filled in but doesn't say what from: it needs a template.");

        return Load(Module, OwnerOf(container).Resolve(container.Template), into ?? container, introDelay);
    }

    /// <inheritdoc cref="Populate(Panel, int)"/>
    public IReadOnlyList<UILayout> Populate(string container, int count) => Populate(Get<Panel>(container), count);

    /// <summary>
    /// Fills a container with an item per thing in a list, and has each one filled in. The usual loop, minus the
    /// bit everybody gets wrong.
    /// <code>
    /// layout.Populate("maps", maps, (item, map, i) => item.Get&lt;Label&gt;("name").Text = map.Name);
    /// </code>
    /// </summary>
    public IReadOnlyList<UILayout> Populate<T>(Panel container, IReadOnlyList<T> data, Action<UILayout, T, int> fill)
    {
        var items = Populate(container, data.Count);
        for (int i = 0; i < items.Count; i++)
            fill(items[i], data[i], i);

        return items;
    }

    /// <inheritdoc cref="Populate{T}(Panel, IReadOnlyList{T}, Action{UILayout, T, int})"/>
    public IReadOnlyList<UILayout> Populate<T>(string container, IReadOnlyList<T> data, Action<UILayout, T, int> fill) =>
        Populate(Get<Panel>(container), data, fill);

    /// <summary>
    /// Like <see cref="Populate{T}(Panel, IReadOnlyList{T}, Action{UILayout, T, int})"/>, but for a list that keeps
    /// changing (a lobby, a scoreboard, a load of save games). The items made last time are kept and filled in again,
    /// and only the difference is made or thrown away, so it's cheap to call every time the list changes and what's
    /// already on screen doesn't make its entrance all over again. Only new items do, staggered among themselves.
    /// </summary>
    public IReadOnlyList<UILayout> Bind<T>(Panel container, IReadOnlyList<T> data, Action<UILayout, T, int> fill)
    {
        if (container.Template.Length == 0)
            throw new Exception($"A container of the layout{Named(Path)} is meant to be filled in but doesn't say what from: it needs a template.");

        // Start again if this is the first time, or something else has been at the container since (Populate, say)
        if (!bound.TryGetValue(container, out var items) || items.Any(item => item.roots.Any(root => root.Parent != container)))
        {
            foreach (var child in container.Children.ToArray())
                container.Remove(child);

            bound[container] = items = [];
        }

        while (items.Count > data.Count)
        {
            foreach (var root in items[^1].roots)
                container.Remove(root);
            items.RemoveAt(items.Count - 1);
        }

        int kept = items.Count;
        while (items.Count < data.Count)
            items.Add(Item(container, container, (items.Count - kept) * container.Stagger));

        for (int i = 0; i < items.Count; i++)
            fill(items[i], data[i], i);

        return items;
    }

    /// <inheritdoc cref="Bind{T}(Panel, IReadOnlyList{T}, Action{UILayout, T, int})"/>
    public IReadOnlyList<UILayout> Bind<T>(string container, IReadOnlyList<T> data, Action<UILayout, T, int> fill) =>
        Bind(Get<Panel>(container), data, fill);

    /// <summary>The parts of an included layout taken on as this one's, see <see cref="Components.Include"/>.</summary>
    internal void Absorb(UILayout included)
    {
        this.included.Add(included);
        foreach (var (name, part) in included.parts)
            parts.TryAdd(name, part);
    }

    private readonly List<UILayout> included = [];

    /// <summary>The layout a part was written in, this one or one it included (or one that included).</summary>
    public UILayout OwnerOf(UIComponent part)
    {
        foreach (UILayout inner in included)
        {
            if (inner.parts.ContainsValue(part) || inner.included.Count > 0 && inner.OwnerOf(part) != inner)
                return inner.OwnerOf(part);
        }

        return this;
    }

    /// <summary>Where a file named by this layout is. Next to it, unless the name says exactly where.</summary>
    public string Resolve(string file) =>
        System.IO.Path.IsPathRooted(file) || Path.Length == 0
            ? file
            : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? string.Empty, file);

    private static UILayout Run(UIModule module, string path, ProgramStatement program, UIComponent? parent, float introDelay)
    {
        var layout = new UILayout(module, path);

        // A scope of its own on top of the module's, so the layout can use what the module offers (the
        // compositor, whatever the program declared) without leaving its names behind for the next one.
        var scope = new Environment(module.Runtime.UserScope);

        var outerDesign = module.declaredDesign;
        var outerBuilding = module.building;
        module.declaredDesign = null;
        module.building = layout;
        try
        {
            module.Capture(parent, layout.roots, () => module.Runtime.Interpreter.Evaluate(program, scope));

            if (module.declaredDesign is { } design)
                (layout.DesignSize, layout.Fit) = design;
        }
        catch (Exception e)
        {
            // Half a layout is no use to anybody.
            foreach (var root in layout.roots)
                root.Parent?.Remove(root);

            throw new Exception($"The layout{Named(path)} stopped halfway: {e.Message}", e);
        }
        finally
        {
            module.declaredDesign = outerDesign;
            module.building = outerBuilding;
        }

        // The layout's own names win over an included file's
        foreach (var (name, value) in scope.Variables)
        {
            if (UIComponent.FromScript(value) is { } component)
                layout.parts[name] = component;
        }

        // Whatever the layout says about how its components make their entrance happens now.
        layout.PlayIntros(introDelay);

        return layout;
    }

    /// <summary>
    /// Has a layout's file read again the next time it's loaded, for a program that has just written it (an editor saving
    /// one) and can't wait the second it would otherwise be taken to be what it was.
    /// </summary>
    public static void Forget(string path) => programs.TryRemove(System.IO.Path.GetFullPath(path), out _);

    private static ProgramStatement Compile(string path)
    {
        string key = System.IO.Path.GetFullPath(path);
        long now = System.Environment.TickCount64;
        bool known = programs.TryGetValue(key, out var cached);
        if (known && now - cached.CheckedAt < RECHECK_MS)
            return cached.Program;

        if (!File.Exists(path))
            throw new FileNotFoundException($"The layout '{path}' doesn't exist.");

        // Read again if the file was changed since, which is what lets an editor be used next to a running program.
        DateTime written = File.GetLastWriteTimeUtc(path);
        if (known && cached.Written == written)
        {
            programs[key] = cached with { CheckedAt = now };
            return cached.Program;
        }

        try
        {
            var program = new Parser().ProduceSyntaxTree(Lexer.Tokenize(File.ReadAllText(path)));
            programs[key] = (written, now, program);
            return program;
        }
        catch (Exception e)
        {
            throw new Exception($"The layout '{path}' can't be read: {e.Message}", e);
        }
    }

    private static string Named(string path) => path.Length > 0 ? $" '{path}'" : string.Empty;
}
