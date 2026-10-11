using System.Numerics;

using Horizon.Rendering;
using Horizon.Graphics;
using Horizon.Rendering.Voxels;
using Horizon.UI;
using Horizon.UI.Components;

namespace AutoVoxel.Gameplay;

/// <summary>
/// What is on screen over the world. The crosshair, the hotbar (the blocks drawn with their own tiles of the atlas),
/// the name of the block in hand for a moment after it changes, and the debug lines (F4) with where you are, what
/// you're looking at and what the world costs.
/// </summary>
public sealed class Hud
{
    private static readonly Vector4 SlotColor = new(0.08f, 0.09f, 0.12f, 0.62f);
    private static readonly Vector4 SelectedColor = new(0.95f, 0.95f, 0.9f, 0.9f);

    private readonly UIModule module;
    private readonly Panel[] slots = new Panel[9];
    private readonly Image[] icons = new Image[9];
    private readonly Panel[] swatches = new Panel[9];
    private readonly Label blockName, debug;
    private readonly VoxelPalette palette;
    private readonly Texture atlas;
    private float nameShown;

    public bool DebugVisible
    {
        get => debug.Visible;
        set => debug.Visible = value;
    }

    public bool Visible
    {
        set => module.Enabled = value;
    }

    public Hud(UICompositor ui, VoxelPalette palette, string atlasPath)
    {
        this.palette = palette;
        module = ui.CreateModule();
        atlas = Texture.Load("voxel atlas", atlasPath, new TextureDefinition(PixelFormat.Rgba8Srgb, Smooth: false));

        // A plus in the middle, two thin bars
        module.AddComponent(new Panel { Color = new Vector4(1, 1, 1, 0.85f), Size = new Vector2(18, 2) });
        module.AddComponent(new Panel { Color = new Vector4(1, 1, 1, 0.85f), Size = new Vector2(2, 18) });

        var bar = module.AddComponent(new StackPanel
        {
            Anchor = Origin.Bottom,
            Position = new Vector2(0, 28),
            Direction = UIDirection.Horizontal,
            Spacing = 6,
            Color = new Vector4(0.0f, 0.0f, 0.0f, 0.25f),
            Radius = 8,
            Padding = new UIEdges(6)
        });

        for (int i = 0; i < 9; i++)
        {
            slots[i] = bar.Add(new Panel { Size = new Vector2(58, 58), Color = SlotColor, Radius = 6 });
            swatches[i] = slots[i].Add(new Panel { Size = new Vector2(38, 38), Radius = 4 });
            icons[i] = slots[i].Add(new Image { Size = new Vector2(40, 40), Texture = atlas, SourceSize = new Vector2(16, 16) });
            slots[i].Add(new Label((i + 1).ToString()) { Anchor = Origin.TopLeft, Position = new Vector2(5, -3), TextScale = 0.16f, Color = new Vector4(1, 1, 1, 0.6f) });
        }

        blockName = module.AddComponent(new Label { Anchor = Origin.Bottom, Position = new Vector2(0, 112), TextScale = 0.3f });
        debug = module.AddComponent(new Label
        {
            Anchor = Origin.TopLeft,
            Position = new Vector2(16, -16),
            TextScale = 0.2f,
            Align = Origin.Left,
            Color = new Vector4(1, 1, 1, 0.92f),
            Visible = false
        });
    }

    /// <summary>Shows the hotbar as it is, which block where and which is in hand.</summary>
    public void ShowHotbar(byte[] hotbar, int selected, bool announce)
    {
        for (int i = 0; i < 9; i++)
        {
            byte block = hotbar[i];
            VoxelMaterial m = palette[block];
            byte tile = m.SideTile != VoxelPalette.NO_TILE ? m.SideTile : m.TopTile;

            bool tiled = tile != VoxelPalette.NO_TILE;
            icons[i].Visible = tiled;
            if (tiled) icons[i].SourcePosition = new Vector2(tile % palette.AtlasTilesPerRow * 16, tile / palette.AtlasTilesPerRow * 16);

            // Blocks with no tile (water, glass, the foliage) are their colour
            swatches[i].Visible = !tiled;
            Vector3 c = Vector3.SquareRoot(m.Color);
            swatches[i].Color = new Vector4(c, m.Kind == VoxelKind.Glass ? 0.45f : 1.0f);

            slots[i].Color = i == selected ? SelectedColor : SlotColor;
            slots[i].Scale = new Vector2(i == selected ? 1.12f : 1.0f);
        }

        if (announce)
        {
            blockName.Text = palette[hotbar[selected]].Name;
            nameShown = 1.6f;
        }
    }

    public void Update(float dt)
    {
        nameShown = MathF.Max(nameShown - dt, 0.0f);
        blockName.Opacity = Math.Clamp(nameShown / 0.4f, 0.0f, 1.0f);
    }

    public void SetDebug(string text) => debug.Text = text;
}
