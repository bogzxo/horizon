using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Horizon.HIDL.Lexxing;
using Horizon.HIDL.Parsing;
using Horizon.Rendering.UIX.Components;

using Environment = Horizon.HIDL.Runtime.Environment;

namespace Horizon.Rendering.UIX;

/// <summary>
/// A piece of UI out of a layout file. The components a HIDL script made, by the names the script gave them.
/// This is how a program gets its UI from files that can be changed (and drawn up in an editor) without
/// touching the code:
/// <code>
/// var layout = UILayout.Load(module, "Assets/ui/menu.hor");
/// layout.Get&lt;Button&gt;("play").OnPressed = Play;
/// </code>
/// Every layout runs in a scope of its own, so two files (or the same file twice) never trip over each
/// other's names. That is also what makes a layout usable as a template: what isn't known until the program
/// runs (a row per save game, a cell per character) is a container in the layout that names the file its
/// items are made from, and <see cref="Populate(Panel, int)"/> makes as many of them as are needed.
/// </summary>
public sealed class UILayout
{
    // Layouts that have been read before, by where they are. A template is read once however many items it makes.
    private static readonly ConcurrentDictionary<string, (DateTime Written, ProgramStatement Program)> programs = new();

    private readonly Dictionary<string, UIComponent> parts = [];
    private readonly List<UIComponent> roots = [];

    /// <summary>The module the components are in.</summary>
    public UIModule Module { get; }

    /// <summary>The file the layout came from, empty for one that was made from code.</summary>
    public string Path { get; }

    /// <summary>The components the script didn't put inside of another one: the top of what it made.</summary>
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
        if (!File.Exists(path))
            throw new FileNotFoundException($"The layout '{path}' doesn't exist.");

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
            items.Add(Instantiate(container.Template, container, i * container.Stagger));

        return items;
    }

    /// <inheritdoc cref="Populate(Panel, int)"/>
    public IReadOnlyList<UILayout> Populate(string container, int count) => Populate(Get<Panel>(container), count);

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
        module.declaredDesign = null;
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
        }

        foreach (var (name, value) in scope.Variables)
        {
            if (UIComponent.FromScript(value) is { } component)
                layout.parts[name] = component;
        }

        // Whatever the layout says about how its components make their entrance happens now.
        layout.PlayIntros(introDelay);

        return layout;
    }

    private static ProgramStatement Compile(string path)
    {
        string key = System.IO.Path.GetFullPath(path);
        DateTime written = File.GetLastWriteTimeUtc(path);

        // Read again if the file was changed since, which is what lets an editor be used next to a running program.
        if (programs.TryGetValue(key, out var known) && known.Written == written)
            return known.Program;

        try
        {
            var program = new Parser().ProduceSyntaxTree(Lexer.Tokenize(File.ReadAllText(path)));
            programs[key] = (written, program);
            return program;
        }
        catch (Exception e)
        {
            throw new Exception($"The layout '{path}' can't be read: {e.Message}", e);
        }
    }

    private static string Named(string path) => path.Length > 0 ? $" '{path}'" : string.Empty;
}
