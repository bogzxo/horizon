using System.Numerics;
using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

/// <summary>
/// One layout being edited. A module of the UI holding the real components, the names they go by in the code,
/// and the code itself, which is written out of the components and never kept.
/// What is on screen is the layout, so there is nothing for the two to disagree about.
/// </summary>
internal sealed class HexDocument
{
    // Properties that aren't part of how a component is laid out: where it is in the tree (written as its parent),
    // other names for the same thing, and what only changes while the UI is in use.
    private static readonly HashSet<string> Unwritten = ["parent", "spr_scale", "on_press", "offset"];

    // How many items a container is ever shown with, whatever its layout asks for
    private const int MAX_PREVIEW = 64;

    // How many steps back there are to take
    private const int MAX_HISTORY = 200;

    private readonly Dictionary<UIComponent, string> names = [];

    // What stands in for the items a program will make. Built from the templates the containers of the layout
    // name, shown in the canvas and nowhere else. They aren't part of the layout and never get written.
    private readonly HashSet<UIComponent> previews = [];

    // What the layout was before each change and what it was before each of those was undone, as the code that
    // builds it. The code is the whole of a layout, so going back is building it again from what it was.
    private readonly List<string> undone = [], redone = [];
    private string recorded = string.Empty;

    public string Name { get; set; }

    /// <summary>The file the layout was loaded from or last saved to, null for one that has never been either.</summary>
    public string? Path { get; set; }

    /// <summary>The code of the layout as it was last opened or saved, which is what there is to compare it to.</summary>
    public string SavedCode { get; set; } = string.Empty;

    /// <summary>Whether the layout has been changed since it was opened or saved. The editor keeps this up, see HexScene.UpdateState.</summary>
    public bool Modified { get; set; }

    /// <summary>Where templates are looked for while the layout has no file of its own to be next to.</summary>
    public string FallbackDirectory { get; set; } = string.Empty;

    /// <summary>The module the layout lives in, shown in the canvas of the editor.</summary>
    public UIModule Module { get; }

    // What is selected, in the order it was picked: the last one is the one the inspector shows
    private readonly List<UIComponent> selection = [];

    /// <summary>Everything that is selected, the one picked last at the end.</summary>
    public IReadOnlyList<UIComponent> Selection => selection;

    /// <summary>The one component the inspector shows, the last one picked. Setting it selects that and nothing else.</summary>
    public UIComponent? Selected
    {
        get => selection.Count > 0 ? selection[^1] : null;
        set
        {
            selection.Clear();
            if (value is not null)
                selection.Add(value);
        }
    }

    public bool IsSelected(UIComponent component) => selection.Contains(component);

    /// <summary>Selects a component on top of what is selected, or lets go of it if it already was (ctrl or shift click).</summary>
    public void ToggleSelected(UIComponent component)
    {
        if (!selection.Remove(component))
            selection.Add(component);
    }

    /// <summary>
    /// The selected components that aren't inside of another selected one, in the order they are in the layout: what
    /// gets moved, deleted or grouped. Dragging a group and something inside of it would move that thing twice.
    /// </summary>
    public List<UIComponent> TopSelection()
    {
        var order = Walk().Select(pair => pair.Component).ToList();
        return selection
            .Where(component => !HasSelectedAncestor(component))
            .OrderBy(order.IndexOf)
            .ToList();
    }

    private bool HasSelectedAncestor(UIComponent component)
    {
        for (UIComponent? at = component.Parent; at is not null; at = at.Parent)
        {
            if (selection.Contains(at))
                return true;
        }

        return false;
    }

    /* What the layout was made for */

    /// <summary>The screen the layout says it was made for, null if it says nothing (and gets whatever screen it's shown on).</summary>
    public Vector2? Design => Module.DesignSize;

    /// <summary>How the layout goes onto a screen of another shape, see <see cref="UIFit"/>.</summary>
    public UIFit Fit => Module.Fit;

    /// <summary>Says what the layout is made for, which goes into the file as compositor.design (null takes it out).</summary>
    public void SetDesign(Vector2? size, UIFit fit)
    {
        Module.DesignSize = size;
        Module.Fit = fit;
    }

    /// <summary>How many stand-in items are being shown, see <see cref="RefreshPreviews"/>.</summary>
    public int PreviewCount => previews.Count;

    public bool CanUndo => undone.Count > 0;
    public bool CanRedo => redone.Count > 0;

    /// <summary>How many changes there are to undo.</summary>
    public int UndoCount => undone.Count;

    public HexDocument(string name, UIModule module)
    {
        Name = name;
        Module = module;

        // A layout that is being edited is looked at, not used. Clicking a button in it selects the button.
        Module.Interactive = false;
    }

    /// <summary>Every component of the layout with how deep in the tree it is, parents before their children.</summary>
    public IEnumerable<(UIComponent Component, int Depth)> Walk()
    {
        var found = new List<(UIComponent, int)>();
        foreach (var root in Module.Root.Children)
            Collect(root, 0, found);

        return found;
    }

    private void Collect(UIComponent component, int depth, List<(UIComponent, int)> found)
    {
        // Stand-ins aren't components of the layout, and neither is anything inside of them
        if (previews.Contains(component))
            return;

        found.Add((component, depth));
        foreach (var child in component.Children)
            Collect(child, depth + 1, found);
    }

    /// <summary>
    /// The component of the layout something in the canvas belongs to. Itself, or for a stand-in item the
    /// container it stands in.
    /// </summary>
    public UIComponent? OwnerOf(UIComponent? component)
    {
        UIComponent? owner = component;

        for (UIComponent? at = component; at is not null && at != Module.Root; at = at.Parent)
        {
            if (previews.Contains(at))
                owner = at.Parent;
        }

        return owner == Module.Root ? null : owner;
    }

    /// <summary>The name a component goes by in the code.</summary>
    /// <summary>The component that goes by a name in the code, null if none does.</summary>
    public UIComponent? Find(string name) => names.FirstOrDefault(pair => pair.Value == name).Key;

    public string NameOf(UIComponent component)
    {
        if (!names.TryGetValue(component, out string? name))
            names[component] = name = FreeName(UIModule.KindOf(component) ?? "component");

        return name;
    }

    /// <summary>
    /// Gives a component another name. It has to be one a variable can have and nothing else in the layout
    /// can have it already.
    /// </summary>
    public bool Rename(UIComponent component, string name)
    {
        if (!HIDLWriter.IsIdentifier(name) || name == "compositor")
            return false;

        foreach (var (other, taken) in names)
        {
            if (taken == name && other != component)
                return false;
        }

        names[component] = name;
        return true;
    }

    /// <summary>
    /// Adds a new component of a kind (see <see cref="UIModule.Kinds"/>) to the layout.
    /// </summary>
    /// <param name="parent">What to put it in, null for the screen itself.</param>
    public UIComponent? Add(string kind, UIComponent? parent)
    {
        if (UIModule.CreateDetached(kind) is not { } component)
            return null;

        var backdrop = new System.Numerics.Vector4(0.1f, 0.12f, 0.17f, 0.9f);

        // Something to look at: an empty label or a panel without a size can't be seen, let alone clicked.
        switch (component)
        {
            case Label label: label.Text = "Label"; break;
            case Button button: button.Label = "Button"; break;
            case ToggleButton toggle: toggle.Label = "Toggle"; break;
            case Selector selector: selector.Options = ["one", "two", "three"]; break;
            case Dropdown dropdown: dropdown.Options = ["one", "two", "three"]; break;
            case ListBox list: list.Items = ["one", "two", "three", "four"]; break;
            case Divider divider: divider.Size = new System.Numerics.Vector2(240, 0); break;
            case Spacer spacer: spacer.Space = 24; break;
            case ScrollPanel scroll: scroll.Size = new System.Numerics.Vector2(240, 160); scroll.Color = backdrop; break;
            case StackPanel stack: stack.Padding = new UIEdges(16); stack.Color = backdrop; break;
            case GridPanel grid: grid.Padding = new UIEdges(16); grid.Color = backdrop; break;
            case TabPanel tabs:
                tabs.Size = new System.Numerics.Vector2(480, 300);
                tabs.Tabs = ["One", "Two"];
                break;
            case Panel panel: panel.Size = new System.Numerics.Vector2(240, 160); panel.Color = backdrop; break;
        }

        (parent ?? Module.Root).Add(component);
        NameOf(component);

        // Tabs with nothing to switch between are a strip of nothing, they come with a page a tab
        if (component is TabPanel pages)
        {
            for (int i = 0; i < pages.Tabs.Length; i++)
                Add("stack", pages);
        }

        return component;
    }

    /// <summary>Takes a component out of the layout, along with everything inside of it.</summary>
    public void Remove(UIComponent component)
    {
        var gone = new List<(UIComponent, int)>();
        Collect(component, 0, gone);

        foreach (var (removed, _) in gone)
        {
            names.Remove(removed);
            selection.Remove(removed);
        }

        component.Parent?.Remove(component);
    }

    /// <summary>Empties the layout.</summary>
    public void Clear()
    {
        foreach (var root in Module.Root.Children.ToArray())
            Module.Root.Remove(root);

        names.Clear();
        previews.Clear();
        selection.Clear();

        // What the layout is made for is part of the code, which is about to be built again
        Module.DesignSize = null;
        Module.Fit = UIFit.Contain;
    }

    /// <summary>
    /// Replaces the layout with what a script builds. The names the script gave its components are kept.
    /// </summary>
    /// <param name="path">Where the script is from, which is where its templates are looked for.</param>
    /// <returns>Whether the script ran, and what it had to say if it didn't.</returns>
    public (bool Success, string Message) Load(string code, string? path = null, bool keepHistory = false)
    {
        Clear();

        var result = Build(code, path);

        // A layout that was just opened has nothing to go back to
        if (!keepHistory)
            ResetHistory();

        return result;
    }

    private (bool Success, string Message) Build(string code, string? path)
    {

        try
        {
            // The same way a program loads it, so what the editor shows is what the program will get
            var layout = UILayout.LoadCode(Module, code, null, path ?? string.Empty);

            foreach (var (name, component) in layout.Parts)
            {
                if (component.Module == Module)
                    names[component] = name;
            }
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }

        // Whatever the script made without keeping it in a variable still needs a name to be written under.
        foreach (var (component, _) in Walk())
            NameOf(component);

        return (true, RefreshPreviews() ?? string.Empty);
    }

    /// <summary>
    /// Shows every container that names a template with as many items made from it as it asks to be previewed
    /// with, in place of the ones that were shown before.
    /// </summary>
    /// <returns>What was wrong with a template, null if nothing was.</returns>
    public string? RefreshPreviews()
    {
        foreach (var preview in previews)
            preview.Parent?.Remove(preview);
        previews.Clear();

        string directory = Path is not null
            ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) ?? string.Empty
            : FallbackDirectory;

        string? problem = null;
        foreach (var (component, _) in Walk().ToArray())
        {
            if (component is Panel { Template.Length: > 0, PreviewCount: > 0 } container)
                problem = FillPreview(container, directory, 0) ?? problem;
        }

        return problem;
    }

    private string? FillPreview(Panel container, string directory, int depth)
    {
        string file = System.IO.Path.Combine(directory, container.Template);
        if (!File.Exists(file))
            return $"the template '{container.Template}' isn't next to the layout";

        for (int i = 0; i < Math.Min(container.PreviewCount, MAX_PREVIEW); i++)
        {
            UILayout item;
            try
            {
                // One after the other if the container says so, the way a program filling it would have them
                item = UILayout.Load(Module, file, container, i * container.Stagger);
            }
            catch (Exception e)
            {
                return e.Message;
            }

            foreach (var root in item.Roots)
            {
                // Only the top of an item is noted, what is inside of it goes wherever it goes
                if (depth == 0)
                    previews.Add(root);
            }

            // An item can have containers of its own to fill, but a template that names itself would never end
            if (depth >= 2)
                continue;

            string itemDirectory = System.IO.Path.GetDirectoryName(file) ?? string.Empty;
            foreach (var part in item.Parts.Values)
            {
                if (part is Panel { Template.Length: > 0, PreviewCount: > 0 } nested)
                    FillPreview(nested, itemDirectory, depth + 1);
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the layout as the script that builds it. One component after the other, each inside of the one
    /// before it that it belongs to, with everything about it that isn't how it starts out anyway.
    /// </summary>
    public string GenerateCode()
    {
        var code = new StringBuilder();
        code.Append("// ").Append(Name).AppendLine(", laid out with Horizon.Hex.");
        code.AppendLine("// Load it with UILayout.Load, every component is there under the name it has here.");
        code.Append(GenerateBody());

        return code.ToString();
    }

    // The components without the lines on top, which say what the layout is called and nothing about what is in it.
    private string GenerateBody()
    {
        var code = new StringBuilder();

        if (Design is { } design)
        {
            code.AppendLine();
            code.Append("compositor.design({ size: ").Append(HIDLWriter.Write(new Vector2Value(design)))
                .Append(", fit: \"").Append(Fit.ToString().ToLowerInvariant()).AppendLine("\" });");
        }

        foreach (var (component, _) in Walk())
        {
            code.AppendLine();
            WriteComponent(code, component);
        }

        return code.ToString();
    }

    /* Undoing */

    /// <summary>
    /// Notes how the layout is now as something to come back to, if it has changed since the last time. The
    /// editor calls this whenever a change has come to rest, so dragging something across the canvas is one
    /// step back rather than a hundred.
    /// </summary>
    /// <returns>Whether there was anything new to note.</returns>
    public bool Record()
    {
        string now = GenerateBody();
        if (now == recorded)
            return false;

        undone.Add(recorded);
        if (undone.Count > MAX_HISTORY)
            undone.RemoveAt(0);

        // What was undone before this change was undone from a layout that is no more.
        redone.Clear();
        recorded = now;
        return true;
    }

    /// <summary>Forgets every step there was to go back to. The layout as it is now is where it starts.</summary>
    public void ResetHistory()
    {
        undone.Clear();
        redone.Clear();
        recorded = GenerateBody();
    }

    /// <summary>Takes the last change back.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Undo() => Step(undone, redone);

    /// <summary>Makes the change that was last taken back again.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Redo() => Step(redone, undone);

    private bool Step(List<string> from, List<string> to)
    {
        // A change that hasn't come to rest yet is a step of its own, and the one that is taken back first.
        if (from == undone)
            Record();

        if (from.Count == 0)
            return false;

        to.Add(recorded);
        recorded = from[^1];
        from.RemoveAt(from.Count - 1);

        // Whatever was selected is selected again if it is still there, components are found by their names.
        var selected = selection.Select(component => names.GetValueOrDefault(component)).OfType<string>().ToList();

        Load(recorded, Path, keepHistory: true);

        foreach (string name in selected)
        {
            if (Find(name) is { } found)
                selection.Add(found);
        }

        // Going back and forth is not the time for everything to make its entrance again.
        SettleIntros();
        return true;
    }

    /* Entrances */

    /// <summary>Plays the entrance of everything in the layout that has one, the way loading it in a program does.</summary>
    public void PlayIntros()
    {
        SettleIntros();
        Module.Root.PlayIntro();
    }

    /// <summary>Stops whatever is still on its way in and shows everything where it belongs.</summary>
    public void SettleIntros() => Settle(Module.Root);

    private static void Settle(UIComponent component)
    {
        component.Tweens.KillAll();
        component.VisualOffset = System.Numerics.Vector2.Zero;
        component.VisualScale = System.Numerics.Vector2.One;
        component.Opacity = 1.0f;

        foreach (var child in component.Children)
            Settle(child);
    }

    private void WriteComponent(StringBuilder code, UIComponent component, Func<UIComponent, string>? nameOf = null, UIComponent? top = null)
    {
        nameOf ??= NameOf;

        string? kind = UIModule.KindOf(component);
        if (kind is null)
        {
            code.Append("// A ").Append(component.GetType().Name).AppendLine(" can't be made by a script, it was left out.");
            return;
        }

        var properties = new List<string>();

        if (component != top && component.Parent is { } parent && parent != Module.Root)
            properties.Add($"parent: {nameOf(parent)}");

        // Only what was changed is written, against a component of the same kind nobody has touched.
        UIComponent untouched = UIModule.CreateDetached(kind)!;
        bool hasHandlers = false;

        foreach (var (name, value) in ReadProperties(component))
        {
            if (value is FunctionValue or AnonymousFunctionValue or NativeFunctionValue)
            {
                hasHandlers = true;
                continue;
            }

            if (!HIDLWriter.CanWrite(value))
                continue;

            string written = HIDLWriter.Write(value);
            if (TryRead(untouched, name, out var usual) && HIDLWriter.CanWrite(usual) && HIDLWriter.Write(usual) == written)
                continue;

            properties.Add($"{name}: {written}");
        }

        if (hasHandlers)
            code.AppendLine("// The handlers this component had were functions, which can't be written back out.");

        code.Append("let ").Append(nameOf(component)).Append(" = compositor.").Append(kind).Append('(');

        if (properties.Count == 0)
        {
            code.AppendLine(");");
        }
        else if (properties.Count <= 2 && properties.Sum(property => property.Length) < 70)
        {
            code.Append("{ ").Append(string.Join(", ", properties)).AppendLine(" });");
        }
        else
        {
            code.AppendLine("{");
            for (int i = 0; i < properties.Count; i++)
                code.Append("    ").Append(properties[i]).AppendLine(i < properties.Count - 1 ? "," : string.Empty);
            code.AppendLine("});");
        }
    }

    /// <summary>
    /// Everything about a component a script can set, by name, as it is right now. This is what the inspector
    /// shows and what the code is written from.
    /// </summary>
    public static IEnumerable<(string Name, IRuntimeValue Value)> ReadProperties(UIComponent component)
    {
        foreach (var (name, property) in component.Object.Properties)
        {
            if (Unwritten.Contains(name) || property is not NativeValue native)
                continue;

            IRuntimeValue value;
            try
            {
                value = native.AccessorCallback();
            }
            catch
            {
                continue;
            }

            yield return (name, value);
        }
    }

    public static bool TryRead(UIComponent component, string name, out IRuntimeValue value)
    {
        if (component.Object.Properties.TryGetValue(name, out var property) && property is NativeValue native)
        {
            value = native.AccessorCallback();
            return true;
        }

        value = new NullValue();
        return false;
    }

    /// <summary>
    /// Sets a property of a component the way a script would.
    /// </summary>
    /// <returns>What went wrong, null if nothing did.</returns>
    public static string? Write(UIComponent component, string name, IRuntimeValue value)
    {
        if (!component.Object.Properties.TryGetValue(name, out var property) || property is not NativeValue native)
            return $"There is no property called '{name}'.";

        try
        {
            native.MutatorCallback(value);
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    private string FreeName(string kind, ICollection<string>? taken = null)
    {
        for (int number = 1; ; number++)
        {
            string name = $"{kind}{number}";
            if (!names.ContainsValue(name) && taken?.Contains(name) != true)
                return name;
        }
    }

    /* Rearranging: grouping, copying, and what goes on top of what */

    /// <summary>
    /// Puts components into a new <see cref="Group"/>, keeping every one of them exactly where it is on screen. They
    /// have to be in the same container (or all on the screen). Each one is placed from the middle of the group from
    /// then on, whatever it was anchored to before, which is what makes the group one thing you can move about.
    /// </summary>
    /// <returns>The group, null with the reason why if it can't be done.</returns>
    public Group? Group(IReadOnlyList<UIComponent> components, out string problem)
    {
        problem = string.Empty;
        if (components.Count == 0)
        {
            problem = "select something to group first";
            return null;
        }

        UIComponent parent = components[0].Parent ?? Module.Root;
        if (components.Any(component => (component.Parent ?? Module.Root) != parent))
        {
            problem = "only things in the same container can be grouped, they'd jump about otherwise";
            return null;
        }

        var ordered = components.OrderBy(component => IndexIn(parent, component)).ToList();

        UIRect union = ordered[0].Bounds;
        foreach (var component in ordered.Skip(1))
            union = new UIRect(Vector2.Min(union.Min, component.Bounds.Min), Vector2.Max(union.Max, component.Bounds.Max));

        UIRect area = parent.Bounds.Shrink(parent.Padding);
        var group = new Group { Anchor = Origin.Center, Position = union.Center - area.Center };
        parent.Insert(IndexIn(parent, ordered[0]), group);

        foreach (var component in ordered)
        {
            Vector2 center = component.Bounds.Center;

            // Stretching to fill the group would make it as big as the group is, which is as big as they are: round and round
            if (component.Fill != UIFill.None)
            {
                component.Size = component.Bounds.Size;
                component.Fill = UIFill.None;
            }

            component.Anchor = Origin.Center;
            component.Pivot = null;
            component.Position = center - union.Center;
            group.Add(component);
        }

        NameOf(group);
        return group;
    }

    /// <summary>
    /// Takes a group apart: what was in it goes into the group's container where the group was, everything staying where
    /// it is on screen.
    /// </summary>
    /// <returns>What was in it.</returns>
    public List<UIComponent> Ungroup(Group group)
    {
        UIComponent parent = group.Parent ?? Module.Root;
        UIRect area = parent.Bounds.Shrink(parent.Padding);
        int index = IndexIn(parent, group);

        var freed = group.Children.ToList();
        foreach (var component in freed)
        {
            Vector2 center = component.Bounds.Center;

            component.Anchor = Origin.Center;
            component.Pivot = null;
            component.Position = center - area.Center;
            parent.Insert(index++, component);
        }

        parent.Remove(group);
        names.Remove(group);
        selection.Remove(group);
        return freed;
    }

    /// <summary>
    /// Makes a copy of a component and everything in it, next to it and nudged down and to the right so it can be seen.
    /// Made the way a layout file is loaded, so the copy is everything the original would be written out as.
    /// </summary>
    public UIComponent? Duplicate(UIComponent original, out string problem)
    {
        problem = string.Empty;

        var subtree = new List<(UIComponent, int)>();
        Collect(original, 0, subtree);

        // Fresh names for the lot, none of them taken and none of them the same as each other
        var fresh = new Dictionary<UIComponent, string>();
        foreach (var (component, _) in subtree)
            fresh[component] = FreeName(UIModule.KindOf(component) ?? "component", fresh.Values);

        var code = new StringBuilder();
        foreach (var (component, _) in subtree)
            WriteComponent(code, component, component => fresh.TryGetValue(component, out var name) ? name : NameOf(component), top: original);

        UIComponent? parent = original.Parent is { } owner && owner != Module.Root ? owner : null;

        UILayout made;
        try
        {
            made = UILayout.LoadCode(Module, code.ToString(), parent, Path ?? string.Empty);
        }
        catch (Exception e)
        {
            problem = e.Message;
            return null;
        }

        foreach (var (name, part) in made.Parts)
            names[part] = name;

        if (!made.Parts.TryGetValue(fresh[original], out var copy))
        {
            problem = "the copy came out without its top, which shouldn't be possible";
            return null;
        }

        UIComponent container = copy.Parent ?? Module.Root;
        container.MoveChild(copy, IndexIn(container, original) + 1);
        copy.Position += new Vector2(16, -16);

        SettleIntros();
        RefreshPreviews();
        return copy;
    }

    /// <summary>Where a component goes among its siblings, which is the order they're drawn in (the last one on top).</summary>
    public enum Order { Front, Forward, Backward, Back }

    /// <summary>Moves a component up or down the order it is drawn in among its siblings.</summary>
    public bool Reorder(UIComponent component, Order order)
    {
        if (component.Parent is not { } parent)
            return false;

        int index = IndexIn(parent, component);
        int last = parent.Children.Count - 1;

        return parent.MoveChild(component, order switch
        {
            Order.Front => last,
            Order.Forward => Math.Min(last, index + 1),
            Order.Backward => Math.Max(0, index - 1),
            _ => 0
        });
    }

    private static int IndexIn(UIComponent parent, UIComponent child)
    {
        var children = parent.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] == child)
                return i;
        }

        return -1;
    }
}
