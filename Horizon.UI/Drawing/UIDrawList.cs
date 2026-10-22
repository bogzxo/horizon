using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Graphics;
using Horizon.Rendering.Spriting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Drawing;

/// <summary>
/// Everything the UI wants on screen this frame, as quads in painter's order, what is added later is
/// drawn on top. Components paint into it on the simulation thread and the sprite renderer draws the result
/// (the quads are its <see cref="SpriteItem"/>s), the whole UI in a single call unless more than two
/// custom images are involved.
/// </summary>
public sealed partial class UIDrawList
{
    // The slots of the textures every run shares. Custom images take the ones after.
    private const uint ATLAS_SLOT = 0;
    private const uint FONT_SLOT = 1;
    private const uint IMAGE_SLOT = 2;

    // How much of an icon's height the label on it takes up.
    private const float ICON_LABEL_HEIGHT = 0.75f;

    /// <summary>
    /// A run of quads drawn in one call. Skin art, text and solid colours all share a run, only a quad
    /// showing a third custom image when the run already has two starts a new one.
    /// </summary>
    internal readonly record struct Run(int First, int Count, Texture? Image0, Texture? Image1);

    private static readonly UIRect Unclipped =
        new(new Vector2(float.NegativeInfinity), new Vector2(float.PositiveInfinity));

    private SpriteItem[] items = new SpriteItem[512];
    private int itemCount;

    private readonly List<Run> runs = [];
    private int runStart;
    private Texture? runImage0, runImage1;

    // The atlases of other people this list drew out of (or wanted to), for the renderer to bring up to date
    private readonly List<TextureAtlas> atlases = [];

    // The panes of glass in it, in the order they were painted, see Glass
    private readonly List<UIGlassPane> glass = [];

    // The fonts text is written in for now instead of the skin's, see PushFont
    private readonly Stack<UIFont?> fonts = new();
    private UIFont? font;

    private readonly Stack<UIRect> clips = new();
    private UIRect clip = Unclipped;

    private Vector2 origin;
    private Vector2 scale = Vector2.One;

    // How components that are being animated are drawn. Scaled and then moved, on top of where the layout put
    // them, and faded. Nested, each inside of the one before.
    private readonly Stack<(Vector2 Offset, Vector2 Scale, float Opacity)> visuals = new();
    private Vector2 visualOffset;
    private Vector2 visualScale = Vector2.One;
    private float opacity = 1.0f;

    private int frame;
    private float deltaTime;

    // Who is painting right now and how many quads they have painted, which is what every quad is keyed by, see
    // SpriteItem.Key. Nested, a component inside of a component
    private readonly Stack<(float Owner, int Painted)> owners = new();
    private float owner;
    private int ownerPainted;

    // How long the UI has been drawn for, for the icons that animate. Starts over now and then so it never grows coarse
    private float time;
    private const float TIME_WRAP = 3600.0f;

    /// <summary>How long a popup takes to come in, see <see cref="BeginEntrance"/>, in seconds.</summary>
    public const float ENTRANCE_TIME = 0.14f;

    /// <summary>Whether popups come in by moving (see <see cref="BeginEntrance"/>) or are there at once, for less motion.</summary>
    public static bool Motion { get; set; } = true;

    /// <summary>
    /// Starts the entrance of something that pops up (a menu, a list that drops down, a tooltip) when it's painted for the
    /// first time: grown from a little smaller than it is and faded in, slid in a few pixels from where it came from,
    /// eased out over <see cref="ENTRANCE_TIME"/>. Everything painted until <see cref="EndEntrance"/> goes with it, a pane
    /// of glass included. No garbage, the time it started is the caller's.
    /// </summary>
    /// <param name="started">When it started, by the list's own clock, less than 0 for not yet (which starts it now).</param>
    /// <param name="pivot">What it grows out of, the corner it hangs from.</param>
    /// <param name="from">How far away it starts, it slides in from there.</param>
    /// <returns>Whether it's still coming in, and <see cref="EndEntrance"/> is to be called.</returns>
    internal bool BeginEntrance(ref float started, Vector2 pivot, Vector2 from)
    {
        if (started < 0.0f)
            started = time;

        float t = (time - started) / ENTRANCE_TIME;

        // The clock starts over now and then, a popup open across that is simply there
        if (!Motion || t >= 1.0f || t < 0.0f)
            return false;

        float eased = 1.0f - (1.0f - t) * (1.0f - t) * (1.0f - t);
        float grown = 0.96f + 0.04f * eased;
        PushVisual(from * (1.0f - eased), new Vector2(grown), pivot, eased);
        return true;
    }

    /// <summary>Ends what <see cref="BeginEntrance"/> began, when it said it was still coming in.</summary>
    internal void EndEntrance() => PopVisual();

    /// <summary>How long a popup takes to go, quicker than it comes, see <see cref="BeginExit"/>, in seconds.</summary>
    public const float EXIT_TIME = 0.09f;

    /// <summary>
    /// The entrance the other way round, for something that was put away and is painted a moment longer going: shrunk a
    /// little, faded out and slid back the way it came, over <see cref="EXIT_TIME"/>.
    /// </summary>
    /// <returns>Whether it's still going, and <see cref="EndExit"/> is to be called, false once it's gone.</returns>
    internal bool BeginExit(ref float started, Vector2 pivot, Vector2 to)
    {
        if (started < 0.0f)
            started = time;

        float t = (time - started) / EXIT_TIME;
        if (!Motion || t >= 1.0f || t < 0.0f)
            return false;

        float eased = t * t;
        PushVisual(to * eased, new Vector2(1.0f - 0.04f * eased), pivot, 1.0f - eased);
        return true;
    }

    /// <summary>Ends what <see cref="BeginExit"/> began, when it said it was still going.</summary>
    internal void EndExit() => PopVisual();

    /// <summary>How long the update the list is being painted in is, in seconds, for what eases as it's painted.</summary>
    internal float Delta => deltaTime;

    /// <summary>The skin the frame is painted with.</summary>
    public UISkin Skin { get; private set; } = null!;


    internal ReadOnlySpan<SpriteItem> Items => items.AsSpan(0, itemCount);
    internal ReadOnlySpan<Run> Runs => CollectionsMarshal.AsSpan(runs);
    internal ReadOnlySpan<TextureAtlas> Atlases => CollectionsMarshal.AsSpan(atlases);
    internal ReadOnlySpan<UIGlassPane> GlassPanes => CollectionsMarshal.AsSpan(glass);

    /// <param name="frame">Which update this is, counting up by one for as long as the UI is painted without a break.</param>
    /// <param name="dt">How long the update is, in seconds.</param>
    /// <param name="clock">
    /// The time the UI has been painted for, the one clock of whoever paints into more than one list, null for this list's
    /// own. A compositor paints into two lists in turn, and each kept its own clock, moved on only by the paints it got, so
    /// a popup that started coming in by one list's clock was a moment ahead or behind by the other's. Every other frame
    /// it was all the way in, a submenu blinked in rather than faded, and the icons that animate went in jerks.
    /// </param>
    internal void Begin(UISkin skin, int frame = 0, float dt = 0.0f, float? clock = null)
    {
        Skin = skin;

        this.frame = frame;
        deltaTime = dt;
        time = clock is { } given ? given % TIME_WRAP : (time + dt) % TIME_WRAP;

        itemCount = 0;
        runs.Clear();
        runStart = 0;
        runImage0 = runImage1 = null;
        atlases.Clear();
        glass.Clear();

        clips.Clear();
        clip = Unclipped;

        fonts.Clear();
        font = null;

        origin = Vector2.Zero;
        scale = Vector2.One;

        visuals.Clear();
        visualOffset = Vector2.Zero;
        visualScale = Vector2.One;
        opacity = 1.0f;
        fades.Clear();

        owners.Clear();
        owner = 0.0f;
        ownerPainted = 0;
        stepped = false;
    }

    // Whether what is painted now is shown in steps, see BeginSteps
    private bool stepped;

    /// <summary>
    /// Says that what is painted from here on until <see cref="EndSteps"/> goes from one look to the next in steps, so
    /// a frame between two ticks shows it as the newer tick has it rather than halfway between the two. For something
    /// whose quads stand for a different thing every tick, the cells of a terminal, whose rows hold another line of a
    /// file every tick a program scrolls it. Blended, a letter slid between two rows and a background stretched
    /// from one line's colour to the next.
    /// </summary>
    public void BeginSteps() => stepped = true;

    /// <summary>Goes back to what is painted being blended from one tick to the next, see <see cref="BeginSteps"/>.</summary>
    public void EndSteps() => stepped = false;

    /// <summary>
    /// Everything painted until <see cref="PopOwner"/> is keyed as painted by this object, the component that is
    /// painting, so that a snapshot of the UI blended with the next one blends a quad with the same quad and never
    /// with the one that merely sits where it did, see <see cref="SpriteItem.Key"/>.
    /// </summary>
    internal void PushOwner(object painter)
    {
        owners.Push((owner, ownerPainted));

        // The hash of the object, cut to what a float holds exactly, is as good a name as any and stays the same
        // for as long as the object lives
        owner = (RuntimeHelpers.GetHashCode(painter) & 0xFFFFFF) + 1;
        ownerPainted = 0;
        inParts = false;
    }

    internal void PopOwner()
    {
        (owner, ownerPainted) = owners.Pop();
        inParts = false;
    }

    // The component that is painting in parts, and how many quads it had painted before it started on them
    private bool inParts;
    private float partsOwner;
    private int partsPainted;

    /// <summary>
    /// Says that what a component paints from here on is one part of it, the row of a list or the item of a menu,
    /// by a number that stays the same for as long as the part does. Call it at the top of every row and
    /// <see cref="EndParts"/> after the last.
    /// <para>
    /// Quads are told apart from one tick to the next by who painted them and as which of its quads, which is how
    /// a frame between two ticks blends a quad with itself. A component that paints a quad more on some ticks
    /// (the highlight under the pointer) has every quad after that one counted one further along, and for a tick
    /// the rule between two groups of a menu was blended with the highlight of the item next to it. Counted a row
    /// at a time the extra quad only moves the count within its own row.
    /// </para>
    /// </summary>
    public void BeginPart(int part)
    {
        if (!inParts)
        {
            inParts = true;
            (partsOwner, partsPainted) = (owner, ownerPainted);
        }

        // Another name out of the component's own and the number of the part, cut to what a float holds exactly
        owner = (HashCode.Combine((int)partsOwner, part) & 0xFFFFFF) + 1;
        ownerPainted = 0;
    }

    /// <summary>Goes back to counting quads as the component's own, after the last of its parts.</summary>
    public void EndParts()
    {
        if (!inParts)
            return;

        inParts = false;
        (owner, ownerPainted) = (partsOwner, partsPainted);
    }

    internal void End()
    {
        CloseRun();

        // Art that was asked for while painting (or just before, by another UI with the same skin) isn't in
        // the atlas until the next frame is drawn, so this list may well show less than it should
        Incomplete = Skin.HasPendingArt;
    }

    /// <summary>
    /// Whether another list draws exactly what this one does. The same quads in the same order, showing the same
    /// textures. Two updates of a UI that nothing happened in paint lists that are, and the second one needn't be drawn.
    /// </summary>
    internal bool SameAs(UIDrawList other)
    {
        if (itemCount != other.itemCount || Skin != other.Skin || Incomplete != other.Incomplete)
            return false;

        if (!CollectionsMarshal.AsSpan(runs).SequenceEqual(CollectionsMarshal.AsSpan(other.runs)) ||
            !CollectionsMarshal.AsSpan(glass).SequenceEqual(CollectionsMarshal.AsSpan(other.glass)))
            return false;

        // Byte for byte, an item is nothing but numbers
        return MemoryMarshal.AsBytes(items.AsSpan(0, itemCount)).SequenceEqual(MemoryMarshal.AsBytes(other.items.AsSpan(0, itemCount)));
    }

    /// <summary>
    /// Whether the list was painted while art of its skin was still on its way into the atlas, and so without it.
    /// </summary>
    internal bool Incomplete { get; private set; }

    /// <summary>
    /// Everything painted until the matching <see cref="PopVisual"/> is drawn scaled around a point, moved
    /// and faded, without the layout knowing. This is how a component that is being animated shows up.
    /// Clips are cut out before any of it, so a clip moves along with what it cuts.
    /// </summary>
    internal void PushVisual(Vector2 offset, Vector2 scale, Vector2 pivot, float opacity)
    {
        visuals.Push((visualOffset, visualScale, this.opacity));

        // A point p ends up at (p - pivot) * scale + pivot + offset, inside of whatever is around it already.
        visualOffset += (pivot * (Vector2.One - scale) + offset) * visualScale;
        visualScale *= scale;
        this.opacity *= Math.Clamp(opacity, 0.0f, 1.0f);
    }

    internal void PopVisual() => (visualOffset, visualScale, opacity) = visuals.Pop();

    /// <summary>
    /// Everything painted from here on is scaled and then moved, which is how a module places itself.
    /// </summary>
    internal void SetTransform(Vector2 origin, Vector2 scale)
    {
        this.origin = origin;
        this.scale = scale;
    }

    /// <summary>
    /// Cuts everything painted until the matching <see cref="PopClip"/> down to a rectangle. Clips nest.
    /// </summary>
    public void PushClip(UIRect rect)
    {
        clips.Push(clip);
        clip = clip.Intersect(rect);
    }

    public void PopClip() => clip = clips.Pop();

    /// <summary>
    /// Puts a pane of frosted glass down, a blur of whatever is drawn before it (the scene and everything painted so
    /// far) shown inside the rectangle, under whatever is painted after it. See <see cref="UIGlass"/>, a component
    /// that says <see cref="Components.UIComponent.Glass"/> gets one without asking for it.
    /// </summary>
    /// <param name="radius">How round its corners are, the same as the box painted over it.</param>
    /// <param name="blur">How far what is behind it is smeared, in the UI's pixels.</param>
    /// <param name="bend">How far what is behind is bent in at its edge, in the UI's pixels, see <see cref="UIGlass.Bend"/>.</param>
    public void Glass(UIRect rect, float radius, float blur, float bend = 0.0f)
    {
        if (rect.IsEmpty || opacity <= 0.0f || blur <= 0.0f || rect.Intersect(clip).IsEmpty)
            return;

        // Where the quads go, see Quad
        Vector2 drawn = visualScale * scale;
        Vector2 min = (rect.Min * visualScale + visualOffset) * scale + origin;
        Vector2 max = (rect.Max * visualScale + visualOffset) * scale + origin;

        // An unclipped list has a clip as big as there is, which is left that way rather than turned into a number
        Vector2 clipMin = float.IsFinite(clip.Min.X) && float.IsFinite(clip.Min.Y) ? (clip.Min * visualScale + visualOffset) * scale + origin : new Vector2(float.NegativeInfinity);
        Vector2 clipMax = float.IsFinite(clip.Max.X) && float.IsFinite(clip.Max.Y) ? (clip.Max * visualScale + visualOffset) * scale + origin : new Vector2(float.PositiveInfinity);

        glass.Add(new UIGlassPane(itemCount, min, max, clipMin, clipMax, radius * MathF.Abs(drawn.X), blur * MathF.Abs(scale.X), opacity, bend * MathF.Abs(drawn.X)));
    }

    /// <summary>
    /// The silver lining of a pane of glass, a thin lit edge round a rounded rectangle, brightest along the top and the
    /// left and faint along the bottom and the right, the light taken to come from the top left. Paint it over the
    /// background and under what is in it. See <see cref="UIGlass.Rim"/>.
    /// </summary>
    /// <param name="strength">How bright it is at its brightest, as the alpha of white.</param>
    public void SilverLining(UIRect rect, float radius, float strength)
    {
        if (strength <= 0.0f || rect.IsEmpty)
            return;

        radius = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) * 0.5f);

        // A pixel of the screen wide whatever the UI is scaled by, a lining is a hairline or it's a border
        float thickness = 1.0f / MathF.Max(0.01f, MathF.Abs((visualScale * scale).X));
        float lit = Math.Clamp(strength, 0.0f, 1.0f);
        uint top = SpriteItem.PackColor(new Vector4(1.0f, 1.0f, 1.0f, lit));
        uint side = SpriteItem.PackColor(new Vector4(1.0f, 1.0f, 1.0f, lit * 0.45f));
        uint under = SpriteItem.PackColor(new Vector4(1.0f, 1.0f, 1.0f, lit * 0.18f));
        var (min, max) = rect;

        if (radius < 0.5f)
        {
            Quad(new UIRect(new Vector2(min.X, max.Y - thickness), max), Vector2.Zero, Vector2.Zero, top, SpriteItem.NoTexture);
            Quad(new UIRect(min, new Vector2(max.X, min.Y + thickness)), Vector2.Zero, Vector2.Zero, under, SpriteItem.NoTexture);
            Quad(new UIRect(new Vector2(min.X, min.Y + thickness), new Vector2(min.X + thickness, max.Y - thickness)), Vector2.Zero, Vector2.Zero, side, SpriteItem.NoTexture);
            Quad(new UIRect(new Vector2(max.X - thickness, min.Y + thickness), new Vector2(max.X, max.Y - thickness)), Vector2.Zero, Vector2.Zero, under, SpriteItem.NoTexture);
            return;
        }

        thickness = MathF.Min(thickness, radius);

        // The straight runs between the corners, then the corners, each the colour of the edges it joins
        Quad(new UIRect(new Vector2(min.X + radius, max.Y - thickness), new Vector2(max.X - radius, max.Y)), Vector2.Zero, Vector2.Zero, top, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(min.X + radius, min.Y), new Vector2(max.X - radius, min.Y + thickness)), Vector2.Zero, Vector2.Zero, under, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(min.X, min.Y + radius), new Vector2(min.X + thickness, max.Y - radius)), Vector2.Zero, Vector2.Zero, side, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(max.X - thickness, min.Y + radius), new Vector2(max.X, max.Y - radius)), Vector2.Zero, Vector2.Zero, under, SpriteItem.NoTexture);

        Vector2 size = new(radius);
        uint flags = SpriteItem.NoTexture | SpriteItem.CornerFlag;
        float ring = thickness / radius;
        Quad(new UIRect(new Vector2(min.X, max.Y - radius), new Vector2(min.X + radius, max.Y)), Vector2.One, Vector2.Zero, top, flags, ring);
        Quad(new UIRect(max - size, max), Vector2.UnitY, Vector2.UnitX, side, flags, ring);
        Quad(new UIRect(min, min + size), Vector2.UnitX, Vector2.UnitY, side, flags, ring);
        Quad(new UIRect(new Vector2(max.X - radius, min.Y), new Vector2(max.X, min.Y + radius)), Vector2.Zero, Vector2.One, under, flags, ring);
    }

    // How many bands a sheen is laid in, how far down the glass it reaches as a share of its height, and how bright it is
    // at the top for a rim of 1
    private const int SHEEN_BANDS = 6;
    private const float SHEEN_REACH = 0.45f, SHEEN_STRENGTH = 0.065f;

    /// <summary>
    /// The sheen of a pane of glass, a soft light down from its top edge that fades out before its middle, the light the
    /// silver lining catches caught by the face of the glass as well. It's what has glass read as glass over something
    /// dark, where there's nothing behind it to blur. Laid in bands of the rounded box a little further down each, too
    /// faint each to see a step between them.
    /// </summary>
    /// <param name="strength">How bright it is, as the rim of the glass, 0 for none.</param>
    public void Sheen(UIRect rect, float radius, float strength)
    {
        if (strength <= 0.0f || rect.IsEmpty)
            return;

        var band = new Vector4(1.0f, 1.0f, 1.0f, SHEEN_STRENGTH * Math.Clamp(strength, 0.0f, 1.0f) / SHEEN_BANDS);
        for (int i = 1; i <= SHEEN_BANDS; i++)
        {
            float down = rect.Height * SHEEN_REACH * i / SHEEN_BANDS;
            PushClip(new UIRect(new Vector2(rect.Min.X, rect.Max.Y - down), rect.Max));
            RoundRect(rect, radius, band);
            PopClip();
        }
    }

    // How far the shadow under a popup reaches and how far down it's moved, in the UI's pixels, and how dark it is on a
    // dark panel and on a light one (on light it's a hint, on dark it has to be there to be seen at all)
    private const float POPUP_SOFTNESS = 22.0f, POPUP_DROP = 6.0f, POPUP_SHADOW_DARK = 0.5f, POPUP_SHADOW_LIGHT = 0.2f;

    /// <summary>
    /// What every popup sits on, the same for all of them. A soft shadow that lifts it off what's behind, the glass
    /// when there is any (see <see cref="UIGlass"/>), the panel's colour over that, a hairline round it in the border's
    /// colour, and the silver lining on glass. Rounded by the skin's <see cref="UISkin.PopupRadius"/>. The accent is
    /// left for what is picked inside of it, it used to be the frame of every popup and so meant nothing.
    /// </summary>
    public void Popup(UIRect rect, UIGlass? glass = null)
    {
        if (rect.IsEmpty)
            return;

        float radius = Skin.PopupRadius;
        Vector4 panel = Skin.Palette.Raised;
        Lifted(rect, radius);

        if (glass is { } frosted)
        {
            Glass(rect, radius, frosted.Blur, frosted.Bend);
            panel = frosted.Over(panel);
        }

        RoundRect(rect, radius, panel);

        // A pixel of the screen wide, a hairline whatever the UI is scaled by
        float hairline = 1.0f / MathF.Max(0.01f, MathF.Abs((visualScale * scale).X));
        RoundOutline(rect, radius, hairline, Skin.BorderColor);

        if (glass is { Rim: > 0.0f } lined)
        {
            Sheen(rect, radius, lined.Rim);
            SilverLining(rect, radius, lined.Rim);
        }
    }

    /// <summary>
    /// The shadow of something lifted off what's behind it (a popup, a dialog's card), as dark as the skin's panels need
    /// for it to be seen and dropped a little for light from above. See <see cref="Shadow"/>.
    /// </summary>
    /// <param name="softness">How far out it fades, the popups' own unless said.</param>
    public void Lifted(UIRect rect, float radius, float softness = POPUP_SOFTNESS)
    {
        Vector4 panel = Skin.PanelColor;
        float lightness = 0.2126f * panel.X + 0.7152f * panel.Y + 0.0722f * panel.Z;
        Shadow(rect, radius, softness, new Vector4(0.0f, 0.0f, 0.0f, lightness > 0.5f ? POPUP_SHADOW_LIGHT : POPUP_SHADOW_DARK),
            new Vector2(0.0f, -POPUP_DROP * softness / POPUP_SOFTNESS));
    }

    /// <summary>
    /// A soft shadow under a rounded rectangle, what lifts a popup off whatever it's over. Nine quads, the middle solid,
    /// the edges and the corners fading out over <paramref name="softness"/> (worked out per pixel, see SOFT_FLAG in
    /// sprites.slang). Paint it before the box it's the shadow of.
    /// </summary>
    /// <param name="radius">How round the corners of the box are.</param>
    /// <param name="softness">How far out the shadow fades to nothing.</param>
    /// <param name="offset">How far it's moved, down a little for light from above.</param>
    public void Shadow(UIRect rect, float radius, float softness, Vector4 color, Vector2 offset = default)
    {
        if (rect.IsEmpty || color.W <= 0.0f || softness <= 0.0f)
            return;

        radius = MathF.Max(0.0f, MathF.Min(radius, MathF.Min(rect.Width, rect.Height) * 0.5f));
        float reach = radius + softness;
        Vector2 min = rect.Min + offset - new Vector2(softness), max = rect.Max + offset + new Vector2(softness);

        // The solid middle, as far in from the outside as the fade reaches, nothing when the box is smaller than that
        Vector2 inMin = min + new Vector2(reach), inMax = max - new Vector2(reach);
        if (inMax.X < inMin.X) inMin.X = inMax.X = (min.X + max.X) * 0.5f;
        if (inMax.Y < inMin.Y) inMin.Y = inMax.Y = (min.Y + max.Y) * 0.5f;

        uint packed = SpriteItem.PackColor(color);
        uint soft = SpriteItem.NoTexture | SpriteItem.CornerFlag | SpriteItem.SoftFlag;

        Quad(new UIRect(inMin, inMax), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);

        // The edges fade one way, a disc's distance along one axis only
        Quad(new UIRect(new Vector2(inMin.X, inMax.Y), new Vector2(inMax.X, max.Y)), Vector2.UnitY, Vector2.Zero, packed, soft, 1.0f);
        Quad(new UIRect(new Vector2(inMin.X, min.Y), new Vector2(inMax.X, inMin.Y)), Vector2.Zero, Vector2.UnitY, packed, soft, 1.0f);
        Quad(new UIRect(new Vector2(min.X, inMin.Y), new Vector2(inMin.X, inMax.Y)), Vector2.UnitX, Vector2.Zero, packed, soft, 1.0f);
        Quad(new UIRect(new Vector2(inMax.X, inMin.Y), new Vector2(max.X, inMax.Y)), Vector2.Zero, Vector2.UnitX, packed, soft, 1.0f);

        // The corners every way, the same as a rounded box's
        Quad(new UIRect(new Vector2(min.X, inMax.Y), new Vector2(inMin.X, max.Y)), Vector2.One, Vector2.Zero, packed, soft, 1.0f);
        Quad(new UIRect(inMax, max), Vector2.UnitY, Vector2.UnitX, packed, soft, 1.0f);
        Quad(new UIRect(min, inMin), Vector2.UnitX, Vector2.UnitY, packed, soft, 1.0f);
        Quad(new UIRect(new Vector2(inMax.X, min.Y), new Vector2(max.X, inMin.Y)), Vector2.Zero, Vector2.One, packed, soft, 1.0f);
    }

    /// <summary>Fills a rectangle with a colour.</summary>
    public void Rect(UIRect rect, Vector4 color) =>
        Quad(rect, Vector2.Zero, Vector2.Zero, SpriteItem.PackColor(color), SpriteItem.NoTexture);

    /// <summary>Draws the edge of a rectangle, on the inside.</summary>
    public void Outline(UIRect rect, float thickness, Vector4 color)
    {
        uint packed = SpriteItem.PackColor(color);
        var (min, max) = rect;

        Quad(new UIRect(new Vector2(min.X, max.Y - thickness), max), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(min, new Vector2(max.X, min.Y + thickness)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(min.X, min.Y + thickness), new Vector2(min.X + thickness, max.Y - thickness)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(max.X - thickness, min.Y + thickness), new Vector2(max.X, max.Y - thickness)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
    }

    /// <summary>
    /// Fills a rectangle with rounded corners. The corners are worked out for every pixel as they are drawn,
    /// so they are as crisp at any size as the screen allows and need no art.
    /// </summary>
    /// <param name="radius">How big the corners are. No more than half of the shorter side, which makes a pill of it.</param>
    public void RoundRect(UIRect rect, float radius, Vector4 color)
    {
        radius = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) * 0.5f);
        if (radius < 0.5f)
        {
            Rect(rect, color);
            return;
        }

        uint packed = SpriteItem.PackColor(color);
        var (min, max) = rect;

        // A column down the middle and a strip either side of it between the corners
        Quad(new UIRect(new Vector2(min.X + radius, min.Y), new Vector2(max.X - radius, max.Y)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(min.X, min.Y + radius), new Vector2(min.X + radius, max.Y - radius)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(max.X - radius, min.Y + radius), new Vector2(max.X, max.Y - radius)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);

        Corners(rect, radius, 1.0f, packed);
    }

    /// <summary>Draws the edge of a rectangle with rounded corners, on the inside. See <see cref="RoundRect"/>.</summary>
    public void RoundOutline(UIRect rect, float radius, float thickness, Vector4 color)
    {
        radius = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) * 0.5f);
        if (radius < 0.5f)
        {
            Outline(rect, thickness, color);
            return;
        }

        thickness = MathF.Min(thickness, radius);

        uint packed = SpriteItem.PackColor(color);
        var (min, max) = rect;

        Quad(new UIRect(new Vector2(min.X + radius, max.Y - thickness), new Vector2(max.X - radius, max.Y)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(min.X + radius, min.Y), new Vector2(max.X - radius, min.Y + thickness)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(min.X, min.Y + radius), new Vector2(min.X + thickness, max.Y - radius)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(max.X - thickness, min.Y + radius), new Vector2(max.X, max.Y - radius)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);

        Corners(rect, radius, thickness / radius, packed);
    }

    /// <summary>
    /// Fills a rectangle the way the skin has its flat controls, which is with rounded corners if it says so (see <see cref="UISkin.CornerRadius"/>).
    /// What a component draws itself with when the skin has no art for it.
    /// </summary>
    public void Box(UIRect rect, Vector4 color) => RoundRect(rect, Skin.CornerRadius, color);

    /// <summary>Draws the edge of what <see cref="Box"/> fills.</summary>
    public void Frame(UIRect rect, float thickness, Vector4 color) => RoundOutline(rect, Skin.CornerRadius, thickness, color);

    /// <summary>
    /// Helper method to draw the four corners of a rounded rectangle, each a quarter of a disc with its middle
    /// at the corner of the quad that points into the rectangle.
    /// </summary>
    /// <param name="ring">How much of the discs is filled from their edge inwards, see <see cref="SpriteItem.Ring"/>.</param>
    private void Corners(UIRect rect, float radius, float ring, uint color)
    {
        var (min, max) = rect;
        Vector2 size = new(radius);
        uint flags = SpriteItem.NoTexture | SpriteItem.CornerFlag;

        // What the top left and the bottom right of each quad are told is how far they are from the middle of their disc
        Quad(new UIRect(new Vector2(min.X, max.Y - radius), new Vector2(min.X + radius, max.Y)), Vector2.One, Vector2.Zero, color, flags, ring);
        Quad(new UIRect(max - size, max), Vector2.UnitY, Vector2.UnitX, color, flags, ring);
        Quad(new UIRect(min, min + size), Vector2.UnitX, Vector2.UnitY, color, flags, ring);
        Quad(new UIRect(new Vector2(max.X - radius, min.Y), new Vector2(max.X, min.Y + radius)), Vector2.Zero, Vector2.One, color, flags, ring);
    }

    /// <summary>Draws a region of the skin stretched over a rectangle.</summary>
    public void Region(in UIRegion region, UIRect rect, Vector4 tint) =>
        Quad(rect, region.Position, region.Position + region.TexelSize, SpriteItem.PackColor(tint * RegionTint(region)), ATLAS_SLOT);

    /// <summary>
    /// Draws a region of the skin over a rectangle of any size without distorting its edges. The
    /// corners keep their size, the edges stretch along their length and only the middle stretches both ways.
    /// </summary>
    public void NineSlice(in UIRegion region, UIRect rect, Vector4 tint)
    {
        if (rect.IsEmpty)
            return;

        UIEdges border = region.Border;
        if (border == default)
        {
            Region(region, rect, tint);
            return;
        }

        // The border is measured in texels, on screen it is as big as the art is drawn.
        float left = border.Left * region.Scale, right = border.Right * region.Scale;
        float top = border.Top * region.Scale, bottom = border.Bottom * region.Scale;

        // A rectangle too small for its borders squeezes them rather than letting them cross over.
        float squeezeX = MathF.Min(1.0f, rect.Width / (left + right));
        float squeezeY = MathF.Min(1.0f, rect.Height / (top + bottom));

        Vector2 texMin = region.Position;
        Vector2 texMax = region.Position + region.TexelSize;

        // Left to right and top to bottom, on screen and in the texture.
        ReadOnlySpan<float> xs = [rect.Min.X, rect.Min.X + left * squeezeX, rect.Max.X - right * squeezeX, rect.Max.X];
        ReadOnlySpan<float> ys = [rect.Max.Y, rect.Max.Y - top * squeezeY, rect.Min.Y + bottom * squeezeY, rect.Min.Y];
        ReadOnlySpan<float> us = [texMin.X, texMin.X + border.Left, texMax.X - border.Right, texMax.X];
        ReadOnlySpan<float> vs = [texMin.Y, texMin.Y + border.Top, texMax.Y - border.Bottom, texMax.Y];

        uint packed = SpriteItem.PackColor(tint * RegionTint(region));
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                Quad(
                    new UIRect(new Vector2(xs[column], ys[row + 1]), new Vector2(xs[column + 1], ys[row])),
                    new Vector2(us[column], vs[row]),
                    new Vector2(us[column + 1], vs[row + 1]),
                    packed,
                    ATLAS_SLOT);
            }
        }
    }

    /// <summary>
    /// Draws a region of another skin than the one the list is drawn in, stretched over a rectangle. For showing
    /// the art of one skin in a UI that wears another, an editor showing what a game's skin has.
    /// </summary>
    public void Region(UISkin from, in UIRegion region, UIRect rect, Vector4 tint)
    {
        if (ReferenceEquals(from, Skin))
        {
            Region(region, rect, tint);
            return;
        }

        // Its atlas is kept up with ours from here on, what was just asked of it is stitched in before it is drawn
        if (!atlases.Contains(from.Atlas)) atlases.Add(from.Atlas);
        if (from.Atlas.Texture.Handle == 0) return;

        Image(from.Atlas.Texture, rect, region.Position, region.Position + region.TexelSize, tint * RegionTint(region));
    }

    /// <summary>
    /// The same for art that stretches, see <see cref="NineSlice(in UIRegion, UIRect, Vector4)"/>. The corners keep
    /// their size and the edges stretch along their length.
    /// </summary>
    public void NineSlice(UISkin from, in UIRegion region, UIRect rect, Vector4 tint)
    {
        if (rect.IsEmpty)
            return;

        if (ReferenceEquals(from, Skin))
        {
            NineSlice(region, rect, tint);
            return;
        }

        UIEdges border = region.Border;
        if (border == default)
        {
            Region(from, region, rect, tint);
            return;
        }

        if (!atlases.Contains(from.Atlas)) atlases.Add(from.Atlas);
        if (from.Atlas.Texture.Handle == 0) return;

        float left = border.Left * region.Scale, right = border.Right * region.Scale;
        float top = border.Top * region.Scale, bottom = border.Bottom * region.Scale;
        float squeezeX = MathF.Min(1.0f, rect.Width / (left + right));
        float squeezeY = MathF.Min(1.0f, rect.Height / (top + bottom));

        Vector2 texMin = region.Position;
        Vector2 texMax = region.Position + region.TexelSize;

        ReadOnlySpan<float> xs = [rect.Min.X, rect.Min.X + left * squeezeX, rect.Max.X - right * squeezeX, rect.Max.X];
        ReadOnlySpan<float> ys = [rect.Max.Y, rect.Max.Y - top * squeezeY, rect.Min.Y + bottom * squeezeY, rect.Min.Y];
        ReadOnlySpan<float> us = [texMin.X, texMin.X + border.Left, texMax.X - border.Right, texMax.X];
        ReadOnlySpan<float> vs = [texMin.Y, texMin.Y + border.Top, texMax.Y - border.Bottom, texMax.Y];

        Vector4 colour = tint * RegionTint(region);
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                Image(
                    from.Atlas.Texture,
                    new UIRect(new Vector2(xs[column], ys[row + 1]), new Vector2(xs[column + 1], ys[row])),
                    new Vector2(us[column], vs[row]),
                    new Vector2(us[column + 1], vs[row + 1]),
                    colour);
            }
        }
    }

    /// <summary>Draws a whole texture that isn't part of the skin, such as a portrait or an icon.</summary>
    public void Image(Texture texture, UIRect rect, Vector4 tint) =>
        Image(texture, rect, Vector2.Zero, new Vector2(texture.Width, texture.Height), tint);

    /// <summary>
    /// A region of somebody else's atlas (a character's, say), by the key it was asked for under. Nothing is drawn
    /// until the atlas has it, which the renderer sees to the next time this is drawn. Mirrored flips it left to right.
    /// </summary>
    public void Image(TextureAtlas atlas, string key, UIRect rect, Vector4 tint, bool mirrored = false)
    {
        if (!atlases.Contains(atlas)) atlases.Add(atlas);

        if (!atlas.TryGet(key, out var region) || atlas.Texture.Handle == 0) return;

        // An atlas that trims kept part of the frame, and the rectangle is the whole frame, the part goes where it
        // was. The frame counts its rows from the top and the UI has its Y going up, so the part hangs from the top
        // of the rectangle. Counted from the bottom a fighter who stands at the foot of their frame floats at the
        // top of the box with all the empty air under their feet
        if (region.Trimmed)
        {
            Vector2 from = region.Offset / region.FrameSize, to = (region.Offset + region.Size) / region.FrameSize;
            if (mirrored) (from.X, to.X) = (1.0f - to.X, 1.0f - from.X);
            rect = new UIRect(
                new Vector2(rect.Min.X + rect.Width * from.X, rect.Max.Y - rect.Height * to.Y),
                new Vector2(rect.Min.X + rect.Width * to.X, rect.Max.Y - rect.Height * from.Y));
        }

        Vector2 topLeft = region.Position, bottomRight = region.Position + region.Size;
        if (mirrored) (topLeft.X, bottomRight.X) = (bottomRight.X, topLeft.X);

        Image(atlas.Texture, rect, topLeft, bottomRight, tint);
    }

    /// <summary>
    /// Draws a part of a texture that isn't part of the skin, such as one sprite out of a sheet.
    /// </summary>
    /// <param name="texTopLeft">The top left corner of the part, in pixels from the top left of the texture.</param>
    /// <param name="texBottomRight">Its bottom right corner.</param>
    public void Image(Texture texture, UIRect rect, Vector2 texTopLeft, Vector2 texBottomRight, Vector4 tint) =>
        Quad(rect, texTopLeft, texBottomRight, SpriteItem.PackColor(tint), ImageSlot(texture));

    /// <summary>
    /// Draws an icon of <see cref="UISymbols"/> (Codicons) filling a square, the biggest that fits in the rectangle,
    /// in the middle of it. Nothing if the icons haven't loaded.
    /// </summary>
    public void Symbol(char code, UIRect rect, Vector4 color)
    {
        if (!UISymbols.TryGetGlyph(code, out var glyph))
            return;

        // The em of the font is the square every icon is drawn in, its picture is somewhere inside of that
        float side = MathF.Round(MathF.Min(rect.Width, rect.Height));
        float scale = side / UISymbols.Em;
        Vector2 corner = Vector2.Round(rect.Center - new Vector2(side * 0.5f, -side * 0.5f));

        Vector2 topLeft = new(corner.X + glyph.Offset.X * scale, corner.Y - glyph.Offset.Y * scale);
        Vector2 size = glyph.Size * scale;
        var box = new UIRect(new Vector2(topLeft.X, topLeft.Y - size.Y), new Vector2(topLeft.X + size.X, topLeft.Y));

        Quad(box, glyph.Position, glyph.Position + glyph.TexelSize, SpriteItem.PackColor(color),
            ImageSlot(UISymbols.Texture) | SpriteItem.FieldFlag, UISymbols.FieldSlope);
    }

    /// <summary>Draws an icon of <see cref="UISymbols"/> by name, nothing if there is none by it.</summary>
    public void Symbol(ReadOnlySpan<char> name, UIRect rect, Vector4 color)
    {
        if (UISymbols.TryGet(name, out char code))
            Symbol(code, rect, color);
    }

    /// <summary>
    /// Helper method for the slot of a texture that isn't the skin's in the run being made. Two of them fit in a run
    /// next to the atlas and the font, a third means a new run.
    /// </summary>
    private uint ImageSlot(Texture texture)
    {
        uint slot;
        if (texture == runImage0)
            slot = IMAGE_SLOT;
        else if (texture == runImage1)
            slot = IMAGE_SLOT + 1;
        else if (runImage0 is null)
        {
            runImage0 = texture;
            slot = IMAGE_SLOT;
        }
        else if (runImage1 is null)
        {
            runImage1 = texture;
            slot = IMAGE_SLOT + 1;
        }
        else
        {
            CloseRun();
            runImage0 = texture;
            slot = IMAGE_SLOT;
        }

        return slot;
    }

    // A region that was made without a tint has none, rather than one that makes it invisible.
    private static Vector4 RegionTint(in UIRegion region) => region.Tint == default ? Vector4.One : region.Tint;

    // texTopLeft is the texel the top left corner of the rectangle shows, texBottomRight the bottom right.
    private void Quad(UIRect rect, Vector2 texTopLeft, Vector2 texBottomRight, uint color, uint flags, float ring = 0.0f) =>
        Quad(rect, texTopLeft, texBottomRight, color, flags, ring, 0.0f, 0.0f);

    /// <summary>
    /// Helper method to add a quad that leans, its top moved across by <paramref name="slant"/> for every unit it is
    /// above <paramref name="upright"/> (and its bottom the other way below it), which is an italic letter leaning
    /// over the line it stands on. It is clipped as the upright rectangle it was before it leant.
    /// </summary>
    private void Quad(UIRect rect, Vector2 texTopLeft, Vector2 texBottomRight, uint color, uint flags, float ring, float slant, float upright)
    {
        UIRect visible = rect.Intersect(clip);
        if (visible.IsEmpty)
        {
            // Cut away altogether, but it still counts as one of its owner's quads. A quad is told apart from
            // one tick to the next by who painted it and as which of theirs, and if the ones that are out of
            // sight weren't counted, every quad after one that slides out under a clip would be taken for the
            // one before it and blended with that for a tick. Panning a layout about under the edge of an
            // editor's canvas was a flicker of boxes turning into their neighbours
            ownerPainted++;
            return;
        }

        // Everything is axis aligned, so clipping is just a matter of trimming the quad and showing
        // the matching part of the texture.
        if (visible != rect)
        {
            Vector2 texelsPerUnit = (texBottomRight - texTopLeft) / rect.Size;
            Vector2 trimmedTopLeft = new(visible.Min.X - rect.Min.X, rect.Max.Y - visible.Max.Y);
            Vector2 trimmedBottomRight = new(rect.Max.X - visible.Max.X, visible.Min.Y - rect.Min.Y);

            texTopLeft += trimmedTopLeft * texelsPerUnit;
            texBottomRight -= trimmedBottomRight * texelsPerUnit;
        }

        if (itemCount == items.Length)
            Array.Resize(ref items, items.Length * 2);

        // Faded by the animation it's in and the edges it's near, see PushFade
        float shown = fades.Count > 0 ? opacity * FadeOf(visible) : opacity;
        if (shown < 1.0f)
        {
            // Alpha is the top byte, see SpriteItem.PackColor.
            uint alpha = (uint)((color >> 24) * shown + 0.5f);
            color = (color & 0x00FFFFFF) | alpha << 24;
        }

        // Art that is drawn at a size it wasn't made for has its texels blended at the edges, or pixel art
        // would shimmer its way through an animation and come out uneven in a UI that is scaled to its window.
        // Text has a filter of its own.
        Vector2 drawn = visualScale * scale;
        bool whole = drawn.X == MathF.Round(drawn.X) && drawn.Y == MathF.Round(drawn.Y);
        // By the whole of the texture bits. This masked eight of the sixteen and compared them with all
        // sixteen set, which can't ever be equal, so every flat box in a scaled UI was told to smooth a
        // texture it hasn't got. The shader knew better, but it was still bollocks
        if (!whole && SpriteItem.TextureOf(flags) != SpriteItem.NoTexture && (flags & SpriteItem.CoverageFlag) == 0)
            flags |= SpriteItem.SmoothFlag;

        Vector2 min = (visible.Min * visualScale + visualOffset) * scale + origin;
        Vector2 max = (visible.Max * visualScale + visualOffset) * scale + origin;

        ref SpriteItem item = ref items[itemCount++];
        item = SpriteItem.Rectangle(min, max, texTopLeft, texBottomRight, color, flags);
        item.Ring = ring;

        if (slant != 0.0f)
        {
            // Where the leaning starts, in the same space as the corners, and the corners slid across about it
            float pivot = (upright * visualScale.Y + visualOffset.Y) * scale.Y + origin.Y;
            item.Origin.X += slant * (min.Y - pivot);
            item.AxisY.X = slant * (max.Y - min.Y);
        }
        item.Key = new Vector2(stepped ? -owner : owner, ownerPainted++);
    }

    private void CloseRun()
    {
        int count = itemCount - runStart;
        if (count > 0)
            runs.Add(new Run(runStart, count, runImage0, runImage1));

        runStart = itemCount;
        runImage0 = runImage1 = null;
    }
}
