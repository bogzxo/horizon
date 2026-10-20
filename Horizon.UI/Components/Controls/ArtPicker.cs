using Horizon.Rendering;
using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A menu of every piece of art a skin has, each shown as it looks with its name under it, for picking one by eye
/// and not by remembering what it was called. It comes up over everything else in its module like a
/// <see cref="UIDialog"/> does, with a box to type part of a name into at the top, and goes again when a piece
/// is clicked, when escape is pressed or when anywhere outside of it is.
/// <code>
/// ArtPicker.Show(module, skin, button.Style, picked => button.Style = picked);
/// </code>
/// The skin whose art is shown doesn't have to be the one the menu is drawn in, an editor in a plain skin can show
/// the art of the skin a game uses. Art that stretches (it has a border) is shown stretched over the tile the way
/// a button or a panel would have it, anything else at its own shape. Only what is in view is asked of the skin,
/// which only stitches art into its atlas the first time somebody asks for it.
/// </summary>
public sealed class ArtPicker : Panel
{
    private const float TILE_WIDTH = 132.0f, TILE_HEIGHT = 92.0f;
    private const int COLUMNS = 5;
    private const float LIST_HEIGHT = 420.0f;

    private static readonly Vector4 Shade = new(0.0f, 0.0f, 0.0f, 0.55f);

    private readonly UISkin art;
    private readonly string[] names;
    private readonly string current;
    private readonly Action<string> picked;

    private readonly StackPanel card;
    private readonly TextBox filter;
    private readonly ScrollPanel scroll;
    private readonly GridPanel grid;
    private readonly Label count;

    /// <summary>Whether the menu is up.</summary>
    public bool IsOpen => Parent is not null;

    /// <summary>The tiles that are listed right now, for tests that want to click one.</summary>
    public IReadOnlyList<UIComponent> Tiles => grid.Children;

    /// <summary>What is typed into the box at the top, only the art with that in its name is listed.</summary>
    public string Filter
    {
        get => filter.Text;
        set
        {
            filter.Text = value;
            List();
        }
    }

    private ArtPicker(UISkin art, string current, Action<string> picked)
    {
        this.art = art;
        this.current = current;
        this.picked = picked;
        names = [.. art.ArtNames];

        // The shade over everything, which also stops the pointer getting through
        Fill = UIFill.Both;
        Color = Shade;

        card = Add(new StackPanel { Background = "panel", Padding = new UIEdges(18.0f, 16.0f), Spacing = 10.0f, Intro = UIIntro.Pop, IntroTime = 0.18f });

        var top = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Anchor = Origin.Left, Spacing = 12.0f });
        filter = top.Add(new TextBox { Placeholder = "part of a name", Size = new Vector2(280.0f, 0.0f), MaxLength = 40 });
        filter.OnChanged = _ => List();
        count = top.Add(new Label(string.Empty) { Align = Origin.Left });

        float width = COLUMNS * (TILE_WIDTH + 6.0f) + 14.0f;
        scroll = card.Add(new ScrollPanel { Size = new Vector2(width, LIST_HEIGHT), Padding = new UIEdges(4.0f) });
        grid = scroll.Add(new GridPanel { Columns = COLUMNS, Spacing = 6.0f, Anchor = Origin.TopLeft });

        List();
    }

    /// <summary>
    /// Shows the art of a skin over a module, to pick one of.
    /// </summary>
    /// <param name="art">The skin whose art it is, which may be another one than the module is drawn in.</param>
    /// <param name="current">The name that is picked already, which is marked, empty for none.</param>
    /// <param name="picked">Called with the name of what was picked, after the menu has gone. Not called if nothing was.</param>
    public static ArtPicker Show(UIModule module, UISkin art, string current, Action<string> picked)
    {
        // Only one at a time
        foreach (var other in module.Root.Children)
        {
            if (other is ArtPicker open)
            {
                open.Close();
                break;
            }
        }

        var picker = new ArtPicker(art, current, picked);
        module.AddComponent(picker);
        picker.filter.Focus();
        picker.PlayIntro();
        return picker;
    }

    /// <summary>Takes the menu down without picking anything.</summary>
    public void Close()
    {
        if (filter.IsFocused) filter.Unfocus();
        Parent?.Remove(this);
    }

    /// <summary>Picks a piece of art by its name, the way a click on its tile does.</summary>
    public void Pick(string name)
    {
        if (!IsOpen)
            return;

        Close();
        picked(name);
    }

    /// <summary>Helper method to list the art whose name has what was typed in it.</summary>
    private void List()
    {
        foreach (var tile in grid.Children.ToArray())
            grid.Remove(tile);

        string wanted = filter.Text.Trim();
        int shown = 0;
        foreach (string name in names)
        {
            if (wanted.Length > 0 && !name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            grid.Add(new Tile(this, name));
            shown++;
        }

        scroll.Offset = 0.0f;
        count.Text = shown == names.Length ? $"{shown} pieces of art" : $"{shown} of {names.Length}";
    }

    // A press on the shade, which is anywhere that isn't the menu, is no
    protected internal override void OnPointerDown(Vector2 point)
    {
        if (!card.Bounds.Contains(point))
            Close();
    }

    protected internal override void OnTextInput(char character)
    {
        if (character == UIKeyboard.ESCAPE)
            Close();
    }

    protected override Vector2 Measure(UISkin skin)
    {
        // Small enough that a name fits under its tile
        count.TextScale = skin.TextScale * 0.7f;
        return base.Measure(skin);
    }

    protected override void Paint(UIDrawList list)
    {
        // A skin without art for a panel gets a plain box instead, in whatever colour the skin has for one right
        // now, the skin can change under a dialog that is up (the one its theme is picked in, say)
        card.Color = list.Skin.TryGetRegion(card.Background, out _) ? null : list.Skin.PanelColor with { W = 1.0f };

        base.Paint(list);
    }

    /// <summary>One piece of art, as it looks, with its name under it.</summary>
    private sealed class Tile(ArtPicker owner, string name) : UIComponent
    {
        private const float NAME_HEIGHT = 20.0f;

        public string ArtName => name;

        protected override bool HitTestVisible => true;

        protected override Vector2 Measure(UISkin skin) => new(TILE_WIDTH, TILE_HEIGHT);

        protected internal override void OnClick() => owner.Pick(name);

        protected override void Paint(UIDrawList list)
        {
            // Out of view is not asked for, asking is what has the skin stitch the art into its atlas
            UIRect view = owner.scroll.Bounds;
            if (Bounds.Max.Y < view.Min.Y || Bounds.Min.Y > view.Max.Y)
                return;

            UISkin skin = list.Skin;
            bool chosen = name == owner.current;

            list.Box(Bounds, IsHovered ? skin.HoverColor : skin.FieldColor);
            if (chosen)
                list.Frame(Bounds, 2.0f, skin.HighlightColor);

            UIRect room = new(Bounds.Min + new Vector2(6.0f, NAME_HEIGHT + 2.0f), Bounds.Max - new Vector2(6.0f));

            if (owner.art.TryGetRegion(name, out var region) && region.TexelSize is { X: > 0.0f, Y: > 0.0f })
            {
                if (region.Border != default)
                {
                    // Stretched the way whatever uses it would, a button's worth of it
                    list.NineSlice(owner.art, region, room, Vector4.One);
                }
                else
                {
                    // At its own shape, as big as fits, and no bigger than three times its size
                    Vector2 size = region.TexelSize;
                    float fit = MathF.Min(MathF.Min(room.Width / size.X, room.Height / size.Y), 3.0f);
                    list.Region(owner.art, region, UIRect.FromCenter(room.Center, size * fit), Vector4.One);
                }
            }

            float scale = skin.TextScale * 0.6f;
            UIRect line = new(Bounds.Min + new Vector2(4.0f, 2.0f), new Vector2(Bounds.Max.X - 4.0f, Bounds.Min.Y + NAME_HEIGHT));
            list.PushClip(line);
            list.Text(name, line, Origin.Center, scale, chosen ? skin.HighlightColor : skin.TextColor, markup: false);
            list.PopClip();
        }
    }
}
