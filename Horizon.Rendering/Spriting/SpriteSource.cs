using System.Numerics;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// Where a named sprite is to be found: which image it is in and which part of it.
/// The image can be an Aseprite file, which has frames and layers of its own: then the sprite also says which of the
/// file's frames it is on and which layers it is drawn with.
/// </summary>
/// <param name="Path">The image file.</param>
/// <param name="X">The left edge of the first frame, in pixels from the left of the image.</param>
/// <param name="Y">The top edge, in pixels from the top of the image.</param>
/// <param name="Border">
/// How much of every edge (left, top, right, bottom) keeps its size when the sprite is stretched as a nine-slice,
/// zero for sprites that are simply scaled.
/// </param>
/// <param name="Content">
/// How far in from every edge (left, top, right, bottom) whatever is put on top of the sprite goes, like the label of a
/// button whose art has a lip along the bottom. Zero for sprites that leave it to whoever draws them.
/// </param>
/// <param name="Frames">How many frames there are. Where each one is comes out of <see cref="FrameAt"/> and <see cref="ImageFrameAt"/>.</param>
/// <param name="FrameTime">How long every frame of an animation is shown for, in seconds. See <see cref="TimeOf"/> for sprites whose frames don't all take as long.</param>
/// <param name="Step">How far the next frame is from the one before, when the frames are laid out evenly. One sprite's width to the right unless the sheet says.</param>
/// <param name="FramePositions">Where every frame after the first is, for frames that aren't laid out evenly. Null when they are.</param>
/// <param name="Layers">Which layers of a layered image the sprite is drawn with, the ones the file was saved showing if it doesn't say.</param>
/// <param name="ImageFrame">Which frame of the image the sprite is on, for an image that has frames of its own (an Aseprite file).</param>
/// <param name="ImageFrames">Which frame of the image every frame of the sprite is on, for an animation out of an Aseprite tag. Null when they are all on <see cref="ImageFrame"/>.</param>
/// <param name="FrameTimes">How long every frame is shown for in seconds, for frames that don't all take as long. Null when they do.</param>
/// <param name="Trim">
/// Whether the see-through edges of every frame are left out of the atlas, each frame for itself. The sprite is drawn
/// exactly as if they were there (see <see cref="AtlasRegion.Offset"/>), it only takes up a lot less of the atlas. For big
/// frames with a small drawing on them, which is what every frame of a character is.
/// </param>
/// <param name="Loops">Whether the animation goes round and round, false for one that plays once and stays on its last frame.</param>
/// <param name="Pivot">The point the sprite turns around in pixels from its top left, for a sprite out of an Aseprite slice that has one.</param>
public readonly record struct SpriteSource(
    string Path, int X, int Y, int Width, int Height, Vector4 Border, Vector4 Content, int Frames, float FrameTime,
    (int X, int Y) Step = default, (int X, int Y)[]? FramePositions = null,
    LayerSelection Layers = default, int ImageFrame = 0, int[]? ImageFrames = null, float[]? FrameTimes = null,
    bool Trim = false, bool Loops = true, Vector2? Pivot = null)
{
    /// <summary>
    /// The top left corner of a frame, the first being where the sprite is.
    /// </summary>
    public (int X, int Y) FrameAt(int frame)
    {
        if (frame <= 0) return (X, Y);

        if (FramePositions is { } positions)
            return frame - 1 < positions.Length ? positions[frame - 1] : (X, Y);

        // The frames of an Aseprite tag are all in the same place, on another frame of the file each
        if (ImageFrames is not null) return (X, Y);

        (int stepX, int stepY) = Step == default ? (Width, 0) : Step;
        return (X + frame * stepX, Y + frame * stepY);
    }

    /// <summary>
    /// Which frame of the image a frame of the sprite is on. Always the same one unless the image has frames of its own.
    /// </summary>
    public int ImageFrameAt(int frame) =>
        ImageFrames is { } frames && frame >= 0 && frame < frames.Length ? frames[frame] : ImageFrame;

    /// <summary>
    /// How long a frame of the sprite is shown for, in seconds.
    /// </summary>
    public float TimeOf(int frame) =>
        FrameTimes is { } times && frame >= 0 && frame < times.Length ? times[frame] : FrameTime;

    /// <summary>
    /// How long the whole animation takes to play through once, in seconds.
    /// </summary>
    public float Duration
    {
        get
        {
            if (FrameTimes is null) return FrameTime * Math.Max(1, Frames);

            float total = 0.0f;
            foreach (float time in FrameTimes) total += time;
            return total;
        }
    }

    /// <summary>
    /// The name a frame of the sprite goes by in a <see cref="TextureAtlas"/>. The same pixels asked for by two sprites
    /// (or under two names) come to the same key, and are only put in once.
    /// </summary>
    public string KeyOf(int frame)
    {
        (int x, int y) = FrameAt(frame);
        return TextureAtlas.KeyFor(Path, x, y, Width, Height, ImageFrameAt(frame), Layers);
    }

    /// <summary>
    /// Reads the pixels of a frame off the image, for whoever wants to look at the art rather than draw it (working out
    /// a hitbox, say). This reads the image file every time for anything but an Aseprite file, so it is not for every frame of a game.
    /// </summary>
    /// <returns>Null if the image can't be read, which has been logged.</returns>
    public ImagePixels? ReadFrame(int frame)
    {
        if (ImagePixels.Load(Path, ImageFrameAt(frame), Layers) is not { } image) return null;

        (int x, int y) = FrameAt(frame);
        return new ImagePixels(image.Cut(x, y, Width, Height), Width, Height);
    }
}
