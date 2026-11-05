using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>
/// The tabs of a panel kept and put back, which profile every pane had, the folder it was in (OSC 7 when the shell
/// said, the one it started in otherwise), how the tabs were split and how far along the edges were, which tab was
/// open and what a tab was renamed to. Written as HIDL, <c>let session = { current: 0, tabs: [...] }</c>, which Dusk
/// keeps in sessions.hor and Hex in its workspace file. A profile that isn't there any more is the usual one, a folder
/// that is gone is the panel's own.
/// </summary>
public static class TerminalSessions
{
    /// <summary>A structural copy of the fields written by <see cref="Write"/>.</summary>
    public sealed class Snapshot
    {
        internal Snapshot(int current, TabSnapshot[] tabs) => (Current, Tabs) = (current, tabs);
        internal int Current { get; }
        internal TabSnapshot[] Tabs { get; }
    }

    internal sealed class TabSnapshot(string title, NodeSnapshot tree)
    {
        internal string Title { get; } = title;
        internal NodeSnapshot Tree { get; } = tree;
    }

    internal sealed class NodeSnapshot(string? split, float ratio, string? profile, string? folder, string? name, NodeSnapshot? first, NodeSnapshot? second)
    {
        internal string? Split { get; } = split;
        internal float Ratio { get; } = ratio;
        internal string? Profile { get; } = profile;
        internal string? Folder { get; } = folder;
        internal string? Name { get; } = name;
        internal NodeSnapshot? First { get; } = first;
        internal NodeSnapshot? Second { get; } = second;
    }

    /// <summary>Copies the persisted fields once, after a change.</summary>
    public static Snapshot Capture(TerminalPanel panel)
    {
        var tabs = new List<TabSnapshot>(panel.Tabs.Count);
        foreach (TerminalTab tab in panel.Tabs)
            if (tab.Tree.Root is { } root)
                tabs.Add(new TabSnapshot(tab.Renamed ?? string.Empty, Capture(root)));
        return new Snapshot(Math.Max(0, panel.CurrentIndex), tabs.ToArray());
    }

    /// <summary>Compares exactly the fields the session writer persists, without allocating.</summary>
    public static bool Matches(TerminalPanel panel, Snapshot snapshot)
    {
        if (Math.Max(0, panel.CurrentIndex) != snapshot.Current)
            return false;
        int at = 0;
        for (int i = 0; i < panel.Tabs.Count; i++)
        {
            TerminalTab tab = panel.Tabs[i];
            if (tab.Tree.Root is not { } root)
                continue;
            if (at >= snapshot.Tabs.Length)
                return false;
            TabSnapshot saved = snapshot.Tabs[at++];
            if (!string.Equals(tab.Renamed ?? string.Empty, saved.Title, StringComparison.Ordinal) || !Matches(root, saved.Tree))
                return false;
        }
        return at == snapshot.Tabs.Length;
    }

    private static NodeSnapshot Capture(SplitNode<TerminalView> node)
    {
        if (node.IsLeaf)
        {
            TerminalView view = node.Content!;
            return new NodeSnapshot(null, 0, view.Profile?.Id ?? string.Empty,
                NormalizeFolder(view.WorkingDirectory), view.PaneName, null, null);
        }
        return new NodeSnapshot(node.Orientation == SplitOrientation.Horizontal ? "right" : "down",
            MathF.Round(node.Ratio, 3), null, null, null, Capture(node.First!), Capture(node.Second!));
    }

    private static bool Matches(SplitNode<TerminalView> node, NodeSnapshot saved)
    {
        if (node.IsLeaf)
        {
            if (saved.Split is not null || node.Content is not { } view)
                return false;
            return string.Equals(view.Profile?.Id ?? string.Empty, saved.Profile, StringComparison.Ordinal)
                && FolderMatches(view.WorkingDirectory, saved.Folder)
                && string.Equals(view.PaneName, saved.Name, StringComparison.Ordinal);
        }
        return saved.Split is not null
            && string.Equals(node.Orientation == SplitOrientation.Horizontal ? "right" : "down", saved.Split, StringComparison.Ordinal)
            && MathF.Round(node.Ratio, 3) == saved.Ratio
            && saved.First is not null && Matches(node.First!, saved.First)
            && saved.Second is not null && Matches(node.Second!, saved.Second);
    }

    private static string NormalizeFolder(string? folder) => (folder ?? string.Empty).Replace('\\', '/');

    private static bool FolderMatches(string? folder, string? saved)
    {
        ReadOnlySpan<char> value = folder.AsSpan();
        if (value.Length == 0)
            return saved is { Length: 0 };
        if (saved is null || value.Length != saved.Length)
            return false;
        for (int i = 0; i < value.Length; i++)
            if ((value[i] == '\\' ? '/' : value[i]) != saved[i])
                return false;
        return true;
    }

    /// <summary>The session of a panel as the object a file declares, for whoever puts it in a file of their own.</summary>
    public static string Object(TerminalPanel panel) => HIDLWriter.Write(Value(panel));

    /// <summary>The session of a panel as a value, the tabs and the tree of every one of them.</summary>
    public static ObjectValue Value(TerminalPanel panel)
    {
        var tabs = new List<IRuntimeValue>();
        foreach (TerminalTab tab in panel.Tabs)
        {
            if (tab.Tree.Root is null)
                continue;
            tabs.Add(Natives.Object(("title", new StringValue(tab.Renamed ?? string.Empty)), ("tree", Node(tab.Tree.Root))));
        }

        return Natives.Object(("current", new NumberValue(Math.Max(0, panel.CurrentIndex))), ("tabs", new ListValue(tabs)));
    }

    /// <summary>The session of a panel as a file of its own.</summary>
    public static string Write(TerminalPanel panel) => new HIDLDocument()
        .Comment("The tabs that were open when the terminal last closed, put back as it starts (restore_sessions in settings.hor).")
        .Define("dusk")
        .Let("session", Value(panel))
        .Write();

    private static ObjectValue Node(SplitNode<TerminalView> node)
    {
        if (node.IsLeaf)
        {
            TerminalView view = node.Content!;
            var leaf = new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal)
            {
                ["profile"] = new StringValue(view.Profile?.Id ?? string.Empty),
                ["folder"] = new StringValue((view.WorkingDirectory ?? string.Empty).Replace('\\', '/'))
            };
            if (view.PaneName is { } name)
                leaf["name"] = new StringValue(name);
            return new ObjectValue(leaf);
        }

        return Natives.Object(
            ("split", new StringValue(node.Orientation == SplitOrientation.Horizontal ? "right" : "down")),
            ("ratio", new NumberValue(MathF.Round(node.Ratio, 3))),
            ("first", Node(node.First!)),
            ("second", Node(node.Second!)));
    }

    /// <summary>The session a sessions file declares, null for one that doesn't read.</summary>
    public static ObjectValue? Read(string text)
    {
        try
        {
            HIDLRuntime runtime = new() { Output = null };
            var (success, _) = runtime.Evaluate(text);
            return success && runtime.UserScope.Lookup("session") is ObjectValue session ? session : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The tabs of a session opened in a panel, after whatever it has already. False when the session had no tabs to
    /// open. The animations are off while it is put together, it is meant to come up as it was and not to unfold.
    /// </summary>
    public static bool Restore(TerminalPanel panel, ObjectValue session, Func<string, TerminalProfile?> profileCalled)
    {
        if (!session.Properties.TryGetValue("tabs", out IRuntimeValue? listed) || listed is not ListValue { Count: > 0 } tabs)
            return false;

        float animation = panel.AnimationTime;
        panel.AnimationTime = 0.0f;
        int opened = 0, first = panel.Tabs.Count;
        try
        {
            foreach (IRuntimeValue item in tabs.Items)
            {
                if (item is not ObjectValue tab || !tab.Properties.TryGetValue("tree", out IRuntimeValue? tree) || tree is not ObjectValue root)
                    continue;

                var (profile, folder) = LeafOf(FirstLeaf(root), profileCalled);
                TerminalTab made = panel.NewTab(profile, folder);
                if (made.FocusedView is { } view)
                    Grow(panel, view, root, profileCalled);
                if (Text(tab, "title") is { Length: > 0 } title)
                    panel.Rename(made, title);
                if (made.Tree.Leaves.FirstOrDefault()?.Content is { } leftmost)
                    panel.FocusPane(leftmost);
                opened++;
            }

            int current = session.Properties.TryGetValue("current", out IRuntimeValue? at) && at is NumberValue number ? (int)number.Value : 0;
            if (opened > 0)
                panel.Select(first + Math.Clamp(current, 0, opened - 1));
        }
        finally
        {
            panel.AnimationTime = animation;
        }

        return opened > 0;
    }

    // The node a tab starts as, its view there already, split into what the session says
    private static void Grow(TerminalPanel panel, TerminalView view, ObjectValue node, Func<string, TerminalProfile?> profileCalled)
    {
        // a pane, the name it was given if it was
        if (Text(node, "split") is null && Text(node, "name") is { Length: > 0 } name)
            view.PaneName = name;

        if (Text(node, "split") is not { } split || node.Properties.GetValueOrDefault("first") is not ObjectValue first
            || node.Properties.GetValueOrDefault("second") is not ObjectValue second)
            return;

        panel.FocusPane(view);
        var (profile, folder) = LeafOf(FirstLeaf(second), profileCalled);
        TerminalView? made = panel.Split(split == "down" ? SplitOrientation.Vertical : SplitOrientation.Horizontal, profile, folder);
        if (made is null || panel.TabOf(made)?.Tree.Find(made)?.Parent is not { } parent)
            return;

        if (node.Properties.TryGetValue("ratio", out IRuntimeValue? ratio) && ratio is NumberValue share)
            parent.Ratio = Math.Clamp(share.Value, 0.05f, 0.95f);

        Grow(panel, view, first, profileCalled);
        Grow(panel, made, second, profileCalled);
    }

    private static ObjectValue FirstLeaf(ObjectValue node) =>
        node.Properties.GetValueOrDefault("first") is ObjectValue first ? FirstLeaf(first) : node;

    private static (TerminalProfile? Profile, string? Folder) LeafOf(ObjectValue leaf, Func<string, TerminalProfile?> profileCalled)
    {
        TerminalProfile? profile = Text(leaf, "profile") is { Length: > 0 } id ? profileCalled(id) : null;
        string? folder = Text(leaf, "folder") is { Length: > 0 } path && Directory.Exists(path) ? path : null;
        return (profile, folder);
    }

    private static string? Text(ObjectValue of, string key) =>
        of.Properties.TryGetValue(key, out IRuntimeValue? value) && value is StringValue text ? text.Value : null;
}
