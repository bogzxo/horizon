using System.Numerics;
using System.Runtime.CompilerServices;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine.Components;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Engine.Debugging;

/// <summary>
/// Everything the engine is running as a tree, the engine at the top, what was added to it under it, the scene that
/// is on screen under the scene manager and so on down, with the components of every entity listed in front of its
/// children (in blue, they are what does the work). Click one and the inspector shows it, click the mark in front
/// of it (or it again) and it opens or folds away. What is switched off is greyed out.
/// <para>
/// It is one component that paints its own rows rather than a button a row, a scene with a few hundred things in
/// it would be a few thousand quads otherwise. Only the rows that are in view are painted at all. The tree is
/// walked again four times a second, on the simulation thread like the rest of the UI, so nothing changes under it.
/// </para>
/// </summary>
internal sealed class SceneTreeView : UIComponent
{
    private const float ROW_HEIGHT = 20.0f;
    private const float INDENT = 14.0f;
    private const float MARK_WIDTH = 14.0f;
    private const float LEFT = 6.0f;
    private const float REFRESH = 0.25f;

    // Past this many rows the rest are left out, with a row saying so
    private const int MOST_ROWS = 4000;

    private readonly record struct Row(object Target, int Depth, bool HasInside, bool Open, string Text, string Kind, bool Off, bool IsComponent);

    private readonly SkylineDebugger suite;
    private readonly List<Row> rows = [];

    // What is open. Made anew with every walk out of what was met, so nothing that is gone is held on to
    private HashSet<object> open = new(ReferenceEqualityComparer.Instance);

    // The scenes that were opened by themselves the first time they were met, once is enough
    private readonly ConditionalWeakTable<Scene, object> met = [];

    private float timer;
    private bool started;
    private Vector2 pressedAt;

    public SceneTreeView(SkylineDebugger suite)
    {
        this.suite = suite;
    }

    /// <summary>How many rows there are right now, for the title of the panel.</summary>
    public int Count => rows.Count;

    protected override bool HitTestVisible => true;

    protected override Vector2 Measure(UISkin skin) => new(0.0f, rows.Count * ROW_HEIGHT);

    protected override void Update(float dt)
    {
        if ((timer -= dt) > 0.0f)
            return;

        timer = REFRESH;
        Walk();
    }

    /// <summary>Opens the tree down to something and scrolls there, for whatever was selected from somewhere else.</summary>
    public void Reveal(object target)
    {
        for (Entity? at = target as Entity ?? (target as IGameComponent)?.Parent; at is not null; at = at.Parent)
            open.Add(at);

        // A scene hangs off the engine but is shown under the manager
        open.Add(GameObject.Engine.SceneManager);
        Walk();

        int index = rows.FindIndex(row => ReferenceEquals(row.Target, target));
        if (index >= 0 && Parent is ScrollPanel scroll)
            scroll.Offset = MathF.Max(0.0f, index * ROW_HEIGHT - scroll.Bounds.Height * 0.4f);
    }

    public void FoldAll()
    {
        open = new(ReferenceEqualityComparer.Instance) { GameObject.Engine };
        Walk();
    }

    private void Walk()
    {
        if (!started)
        {
            started = true;
            open.Add(GameObject.Engine);
            open.Add(GameObject.Engine.SceneManager);
        }

        rows.Clear();
        var kept = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Visit(GameObject.Engine, 0, kept);
        open = kept;
    }

    private void Visit(Entity entity, int depth, HashSet<object> kept)
    {
        // The suite is not part of the game it is looking at
        if (ReferenceEquals(entity, suite) || rows.Count >= MOST_ROWS)
            return;

        // The scene that is on screen isn't a child of anything, the manager shows it and that is where it goes
        Scene? shown = entity is SceneManager manager ? manager.CurrentInstance : null;

        var components = entity.Components;
        var children = entity.Children;
        bool hasInside = components.Count > 0 || children.Count > 0 || shown is not null;

        // A scene nobody has seen yet opens up by itself, it is what was come for
        if (entity is Scene scene && !met.TryGetValue(scene, out _))
        {
            met.Add(scene, this);
            open.Add(scene);
        }

        // Kept open while it has nothing in it too, the manager before its first scene, say
        bool wanted = open.Contains(entity);
        if (wanted) kept.Add(entity);
        bool isOpen = hasInside && wanted;

        string kind = entity.GetType().Name;
        string name = entity.Name.Length > 0 ? entity.Name : kind;
        rows.Add(new Row(entity, depth, hasInside, isOpen, name, name == kind ? string.Empty : kind, !entity.Enabled, false));

        if (!isOpen)
            return;

        foreach (IGameComponent component in components)
        {
            string componentKind = component.GetType().Name;
            string componentName = string.IsNullOrEmpty(component.Name) ? componentKind : component.Name;
            rows.Add(new Row(component, depth + 1, false, false, componentName, componentName == componentKind ? string.Empty : componentKind, !component.Enabled, true));
        }

        foreach (Entity child in children)
            Visit(child, depth + 1, kept);

        if (shown is not null)
            Visit(shown, depth + 1, kept);

        if (rows.Count == MOST_ROWS)
            rows.Add(new Row(this, 0, false, false, "... and more, fold something away", string.Empty, true, false));
    }

    private float RowTop(int index) => Bounds.Max.Y - index * ROW_HEIGHT;

    private int RowAt(Vector2 point)
    {
        int index = (int)MathF.Floor((Bounds.Max.Y - point.Y) / ROW_HEIGHT);
        return index >= 0 && index < rows.Count ? index : -1;
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = skin.TextScale;

        // Only what the panel this is scrolled in shows
        UIRect view = Parent?.Bounds ?? Bounds;
        int first = Math.Max(0, (int)((Bounds.Max.Y - view.Max.Y) / ROW_HEIGHT));
        int last = Math.Min(rows.Count - 1, (int)((Bounds.Max.Y - view.Min.Y) / ROW_HEIGHT));

        int hovered = IsHovered && Module is { } owner ? RowAt(owner.ToLocal(owner.Compositor.Pointer.Position)) : -1;
        object? selected = suite.Selected;

        for (int i = first; i <= last; i++)
        {
            // A row at a time, see UIDrawList.BeginPart
            list.BeginPart(i);
            Row row = rows[i];
            float top = RowTop(i);
            var area = new UIRect(new Vector2(Bounds.Min.X, top - ROW_HEIGHT), new Vector2(Bounds.Max.X, top));

            if (ReferenceEquals(row.Target, selected))
                list.Rect(area, skin.AccentColor with { W = 0.28f });
            else if (i == hovered)
                list.Rect(area, skin.HoverColor);

            float x = Bounds.Min.X + LEFT + row.Depth * INDENT;
            if (row.HasInside)
                list.Text(row.Open ? "-" : "+", new UIRect(new Vector2(x, area.Min.Y), new Vector2(x + MARK_WIDTH, area.Max.Y)), Origin.Left, scale, DebugStyle.Dim, markup: false);

            x += MARK_WIDTH;
            Vector4 colour = row.Off ? DebugStyle.Dim with { W = 0.6f } : row.IsComponent ? DebugStyle.Component : skin.TextColor;
            list.Text(row.Text, new UIRect(new Vector2(x, area.Min.Y), area.Max), Origin.Left, scale, colour, markup: false);

            if (row.Kind.Length > 0)
            {
                x += skin.Font.Measure(row.Text, scale, markup: false).X + 8.0f;
                list.Text(row.Kind, new UIRect(new Vector2(x, area.Min.Y), area.Max), Origin.Left, scale, DebugStyle.Dim with { W = row.Off ? 0.4f : 0.75f }, markup: false);
            }
        }

        list.EndParts();
    }

    protected internal override void OnPointerDown(Vector2 point) => pressedAt = point;

    protected internal override void OnClick()
    {
        int index = RowAt(pressedAt);
        if (index < 0 || ReferenceEquals(rows[index].Target, this))
            return;

        Row row = rows[index];
        float mark = Bounds.Min.X + LEFT + row.Depth * INDENT;
        bool onMark = pressedAt.X < mark + MARK_WIDTH;

        // The mark folds and unfolds, and so does clicking what is selected already
        if (row.HasInside && (onMark || ReferenceEquals(suite.Selected, row.Target)))
        {
            if (!open.Remove(row.Target))
                open.Add(row.Target);
            Walk();
        }

        if (!onMark)
            suite.Select(row.Target, reveal: false);
    }
}
