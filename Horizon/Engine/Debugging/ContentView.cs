using System.Numerics;

using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Engine.Debugging;

/// <summary>
/// What the game has on the GPU right now, as the content managers have it. The textures and the render targets
/// as pictures (what can be shown as one, a depth buffer or a grid of whole numbers is only named), the shaders
/// and the buffers as lists. Clicking one puts it in the inspector, a texture with a bigger picture of itself.
/// Looked up again twice a second, so a scene that is left takes its tiles with it while you watch.
/// </summary>
internal sealed class ContentView : UIComponent
{
    private const float COLUMN_WIDTH = 190.0f;
    private const float REFRESH = 0.5f;

    private enum Kind
    {
        Textures,
        RenderTargets,
        Shaders,
        Buffers
    }

    private static readonly string[] KindNames = ["Textures", "Render targets", "Shaders", "Buffers"];

    private readonly SkylineDebugger suite;
    private readonly StackPanel column;
    private readonly Button[] buttons = new Button[KindNames.Length];
    private readonly Label summary;
    private readonly ScrollPanel scroll;
    private readonly AssetGrid grid;

    private Kind kind;
    private float timer;

    public ContentView(SkylineDebugger suite)
    {
        this.suite = suite;

        column = Add(new StackPanel { Spacing = 4.0f, Stretch = true, Padding = new UIEdges(8.0f) });
        for (int i = 0; i < buttons.Length; i++)
        {
            Kind chosen = (Kind)i;
            buttons[i] = column.Add(new Button(KindNames[i])
            {
                Style = "button_flat",
                OnPressed = () =>
                {
                    kind = chosen;
                    scroll!.Offset = 0.0f;
                    Refresh();
                }
            });
        }

        column.Add(new Spacer { Size = new Vector2(1.0f, 6.0f) });
        summary = column.Add(new Label { Align = Origin.TopLeft, Anchor = Origin.Left, Color = DebugStyle.Dim });

        scroll = Add(new ScrollPanel { Padding = new UIEdges(6.0f) });
        grid = scroll.Add(new AssetGrid(suite));
    }

    protected override void Arrange(UIRect content)
    {
        float split = MathF.Min(COLUMN_WIDTH, content.Width * 0.4f);
        column.ArrangeTree(new UIRect(content.Min, new Vector2(content.Min.X + split, content.Max.Y)));
        scroll.ArrangeTree(new UIRect(new Vector2(content.Min.X + split, content.Min.Y), content.Max));
    }

    protected override void Update(float dt)
    {
        if ((timer -= dt) > 0.0f)
            return;

        timer = REFRESH;
        Refresh();
    }

    private void Refresh()
    {
        var objects = GameObject.Engine.ObjectManager;

        Texture[] textures = objects.Textures.CopyAll();
        RenderTarget[] targets = objects.RenderTargets.CopyAll();
        Shader[] shaders = objects.Shaders.CopyAll();
        GpuBuffer[] buffers = objects.Buffers.CopyAll();

        int[] counts = [textures.Length, targets.Length, shaders.Length, buffers.Length];
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Label = $"{KindNames[i]}  {counts[i]}";
            buttons[i].Selected = (Kind)i == kind;
        }

        long texels = 0;
        foreach (Texture texture in textures)
            texels += BytesOf(texture);

        long held = 0;
        foreach (GpuBuffer buffer in buffers)
            held += (long)buffer.Capacity;

        var statistics = GameObject.Engine.Graphics.Statistics;
        summary.Text =
            $"texels   {texels / (1024.0 * 1024.0):0.0} MB\n" +
            $"buffers  {held / (1024.0 * 1024.0):0.0} MB\n" +
            $"on the GPU {statistics.MemoryInUse / (1024.0 * 1024.0):0.0} MB\n" +
            $"in {statistics.MemoryBlocks} blocks";

        var entries = new List<AssetGrid.Entry>();
        switch (kind)
        {
            case Kind.Textures:
            {
                var names = Names(objects.Textures.NamedAssets);
                foreach (Texture texture in textures)
                {
                    if (texture.IsDestroyed) continue;

                    // A file is known by its name, the folders it is in are in the inspector
                    string name = texture.Name ?? (names.TryGetValue(texture, out string? asked) ? asked : $"texture {texture.Handle}");
                    name = DebugStyle.FileName(name);
                    entries.Add(new AssetGrid.Entry(texture, name, $"{texture.Width}x{texture.Height} {texture.Definition.Format}", texture));
                }
                break;
            }

            case Kind.RenderTargets:
            {
                foreach (RenderTarget target in targets)
                {
                    if (target.IsDestroyed) continue;

                    entries.Add(new AssetGrid.Entry(target, target.Name ?? $"target {target.Handle}", $"{target.Width}x{target.Height}, {target.Attachments.Count} of them", target.Color));
                }
                break;
            }

            case Kind.Shaders:
            {
                var names = Names(objects.Shaders.NamedAssets);
                foreach (Shader shader in shaders)
                {
                    if (shader.IsDestroyed) continue;

                    string name = shader.Name ?? (names.TryGetValue(shader, out string? asked) ? asked : $"shader {shader.Handle}");
                    entries.Add(new AssetGrid.Entry(shader, name, shader.IsCompute ? "compute" : "drawn with", null));
                }
                break;
            }

            case Kind.Buffers:
            {
                foreach (GpuBuffer buffer in buffers)
                {
                    if (buffer.IsDestroyed) continue;

                    entries.Add(new AssetGrid.Entry(buffer, buffer.Name ?? $"buffer {buffer.Handle}", $"{(long)buffer.Capacity / 1024.0:0.#} KB  {buffer.Usage}", null));
                }
                break;
            }
        }

        grid.Show(entries, tiles: kind is Kind.Textures or Kind.RenderTargets);
    }

    /// <summary>Helper method to turn the names assets were asked for by around, so one can be looked up by the asset.</summary>
    private static Dictionary<T, string> Names<T>(IEnumerable<KeyValuePair<string, T>> named) where T : class
    {
        var names = new Dictionary<T, string>(ReferenceEqualityComparer.Instance);
        foreach (var (name, asset) in named)
            names[asset] = name;

        return names;
    }

    /// <summary>Roughly what a texture takes, its texels and a third again for the smaller copies of itself if it has those.</summary>
    private static long BytesOf(Texture texture)
    {
        if (texture.IsDestroyed)
            return 0;

        int each = texture.Definition.Format switch
        {
            PixelFormat.R8 => 1,
            PixelFormat.R16F => 2,
            PixelFormat.Rg32F or PixelFormat.Rg32Uint or PixelFormat.Rgba16F => 8,
            PixelFormat.Rgba32F => 16,
            _ => 4
        };

        long bytes = (long)texture.Width * texture.Height * each;
        return texture.Definition.Mipmaps ? bytes * 4 / 3 : bytes;
    }
}

/// <summary>
/// The things of one kind laid out to be looked at, tiles with a picture for what has one and a row each for the
/// rest. It paints itself and only what is in view, like the scene tree does.
/// </summary>
internal sealed class AssetGrid : UIComponent
{
    public readonly record struct Entry(object Asset, string Name, string Detail, Texture? Picture);

    private const float TILE_WIDTH = 112.0f;
    private const float TILE_HEIGHT = 124.0f;
    private const float PICTURE = 78.0f;
    private const float ROW_HEIGHT = 20.0f;
    private const float DETAIL_COLUMN = 0.56f;

    private readonly SkylineDebugger suite;
    private List<Entry> entries = [];
    private bool tiles;
    private Vector2 pressedAt;

    public AssetGrid(SkylineDebugger suite)
    {
        this.suite = suite;
    }

    public void Show(List<Entry> shown, bool tiles)
    {
        entries = shown;
        this.tiles = tiles;
    }

    protected override bool HitTestVisible => true;

    // As many tiles across as fit into the width it was last given, which is a layout behind and nobody can tell
    private int Columns => Math.Max(1, (int)(Bounds.Width / TILE_WIDTH));

    protected override Vector2 Measure(UISkin skin) =>
        new(0.0f, tiles ? MathF.Ceiling(entries.Count / (float)Columns) * TILE_HEIGHT : entries.Count * ROW_HEIGHT);

    private int EntryAt(Vector2 point)
    {
        if (point.X < Bounds.Min.X || point.X > Bounds.Max.X)
            return -1;

        int row = (int)MathF.Floor((Bounds.Max.Y - point.Y) / (tiles ? TILE_HEIGHT : ROW_HEIGHT));
        int index = tiles ? row * Columns + (int)((point.X - Bounds.Min.X) / TILE_WIDTH) : row;

        if (tiles && (int)((point.X - Bounds.Min.X) / TILE_WIDTH) >= Columns)
            return -1;

        return row >= 0 && index < entries.Count ? index : -1;
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = skin.TextScale;
        UIRect view = Parent?.Bounds ?? Bounds;

        int hovered = IsHovered && Module is { } owner ? EntryAt(owner.ToLocal(owner.Compositor.Pointer.Position)) : -1;
        object? selected = suite.Selected;

        if (entries.Count == 0)
        {
            list.Text("none right now", new UIRect(new Vector2(Bounds.Min.X + 6.0f, view.Max.Y - 30.0f), view.Max), Origin.Left, scale, DebugStyle.Dim, markup: false);
            return;
        }

        if (!tiles)
        {
            int first = Math.Max(0, (int)((Bounds.Max.Y - view.Max.Y) / ROW_HEIGHT));
            int last = Math.Min(entries.Count - 1, (int)((Bounds.Max.Y - view.Min.Y) / ROW_HEIGHT));

            for (int i = first; i <= last; i++)
            {
                list.BeginPart(i);
                Entry entry = entries[i];
                float top = Bounds.Max.Y - i * ROW_HEIGHT;
                var area = new UIRect(new Vector2(Bounds.Min.X, top - ROW_HEIGHT), new Vector2(Bounds.Max.X, top));

                if (ReferenceEquals(entry.Asset, selected)) list.Rect(area, skin.AccentColor with { W = 0.28f });
                else if (i == hovered) list.Rect(area, skin.HoverColor);

                float split = area.Min.X + area.Width * DETAIL_COLUMN;
                list.Text(DebugStyle.Fit(skin, entry.Name, split - area.Min.X - 12.0f), new UIRect(new Vector2(area.Min.X + 6.0f, area.Min.Y), area.Max), Origin.Left, scale, skin.TextColor, markup: false);
                list.Text(entry.Detail, new UIRect(new Vector2(split, area.Min.Y), area.Max), Origin.Left, scale, DebugStyle.Dim, markup: false);
            }

            list.EndParts();
            return;
        }

        int columns = Columns;
        int firstRow = Math.Max(0, (int)((Bounds.Max.Y - view.Max.Y) / TILE_HEIGHT));
        int lastRow = (int)((Bounds.Max.Y - view.Min.Y) / TILE_HEIGHT);

        for (int i = firstRow * columns; i < entries.Count && i / columns <= lastRow; i++)
        {
            list.BeginPart(i);
            Entry entry = entries[i];
            Vector2 corner = new(Bounds.Min.X + i % columns * TILE_WIDTH, Bounds.Max.Y - i / columns * TILE_HEIGHT);
            var tile = new UIRect(new Vector2(corner.X + 3.0f, corner.Y - TILE_HEIGHT + 3.0f), new Vector2(corner.X + TILE_WIDTH - 3.0f, corner.Y - 3.0f));

            if (ReferenceEquals(entry.Asset, selected)) list.Box(tile, skin.AccentColor with { W = 0.28f });
            else if (i == hovered) list.Box(tile, skin.HoverColor);

            var frame = UIRect.FromCenter(new Vector2(tile.Center.X, tile.Max.Y - 6.0f - PICTURE * 0.5f), new Vector2(PICTURE));
            list.Rect(frame, new Vector4(0.0f, 0.0f, 0.0f, 0.4f));

            if (entry.Picture is { } picture && SkylineDebugger.CanShow(picture))
            {
                float fit = MathF.Min(PICTURE / picture.Width, PICTURE / picture.Height);
                list.Image(picture, UIRect.FromCenter(frame.Center, new Vector2(picture.Width, picture.Height) * fit), Vector4.One);
            }
            else
            {
                list.Text("no picture", frame, Origin.Center, scale * 0.9f, DebugStyle.Dim with { W = 0.6f }, markup: false);
            }

            var name = new UIRect(new Vector2(tile.Min.X, frame.Min.Y - 18.0f), new Vector2(tile.Max.X, frame.Min.Y - 2.0f));
            list.Text(DebugStyle.Fit(skin, entry.Name, tile.Width - 6.0f, scale * 0.92f), name, Origin.Center, scale * 0.92f, skin.TextColor, markup: false);

            var detail = new UIRect(new Vector2(tile.Min.X, name.Min.Y - 15.0f), new Vector2(tile.Max.X, name.Min.Y));
            list.Text(DebugStyle.Fit(skin, entry.Detail, tile.Width - 6.0f, scale * 0.85f), detail, Origin.Center, scale * 0.85f, DebugStyle.Dim, markup: false);
        }

        list.EndParts();
    }

    protected internal override void OnPointerDown(Vector2 point) => pressedAt = point;

    protected internal override void OnClick()
    {
        int index = EntryAt(pressedAt);
        if (index >= 0)
            suite.Select(entries[index].Asset, reveal: false);
    }
}
