using Horizon.Rendering.Spriting;

namespace Horizon.Tests;

/// <summary>
/// Reading Aseprite files: what a file is said to have, what its frames are painted as, and the sprites a definition
/// makes of its tags and slices. All of it against the files of the Dead Revolver pack, which have every case there is
/// (groups of layers, tags, slices, frames that take different times).
/// </summary>
public class AsepriteTests
{
    private const string PACK = "Horizon.Rendering.UIX/Assets/uix/dead_revolver";

    private static string Repo()
    {
        for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
        {
            if (File.Exists(Path.Combine(at, "Horizon.sln")))
                return at;
        }

        throw new DirectoryNotFoundException("The tests have to run somewhere under the repo.");
    }

    private static string Art(string file) => Path.Combine(Repo(), PACK, file);

    /// <summary>
    /// Helper method to count the pixels of a painted frame that have anything on them.
    /// </summary>
    private static int Drawn(byte[] pixels)
    {
        int drawn = 0;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) drawn++;

        return drawn;
    }

    [Fact]
    public void AFileSaysWhatItHas()
    {
        var buttons = AsepriteDocument.Load(Art("Buttons.aseprite"));

        Assert.Equal((208, 240), (buttons.Width, buttons.Height));
        Assert.Equal(4, buttons.FrameCount);

        Assert.True(buttons.TryGetTag("Pressed", out var pressed));
        Assert.Equal((1, 1), (pressed.From, pressed.To));
        Assert.True(buttons.TryGetTag("highlighted", out var highlighted), "Tags are found whatever their case.");
        Assert.Equal(3, highlighted.From);

        // Every colour is a group with a layer in it
        Assert.Contains(buttons.Layers, layer => layer is { Name: "White", IsGroup: true, Depth: 0 });
        Assert.Contains(buttons.Layers, layer => layer is { Path: "Blue/Main", IsGroup: false, Depth: 1 });
        Assert.True(buttons.HasLayer("blue"));
        Assert.False(buttons.HasLayer("Pink"));

        Assert.True(buttons.TryGetSlice("ButtonA", out var slice));
        Assert.True(slice.Width > 0 && slice.Height > 0);
        Assert.True(slice.X + slice.Width <= buttons.Width && slice.Y + slice.Height <= buttons.Height);
    }

    [Fact]
    public void ALayerIsShownWhateverTheFileWasSavedShowing()
    {
        var buttons = AsepriteDocument.Load(Art("Buttons.aseprite"));
        string[] colours = ["White", "Black", "Gold", "Orange", "Red", "Purple", "Blue"];

        byte[] white = buttons.Render(0, LayerSelection.Only("White", colours));
        byte[] blue = buttons.Render(0, LayerSelection.Only("Blue", colours));

        Assert.Equal(buttons.Width * buttons.Height * 4, white.Length);
        Assert.True(Drawn(white) > 0, "The white buttons are drawn.");
        Assert.True(Drawn(blue) > 0, "The blue buttons are drawn.");
        Assert.False(white.AsSpan().SequenceEqual(blue), "Two colours of the same art aren't the same pixels.");

        // Nothing but hidden layers is nothing at all
        Assert.Equal(0, Drawn(buttons.Render(0, LayerSelection.Hiding(colours))));
    }

    [Fact]
    public void ATagPlaysItsFramesTheWayItSays()
    {
        Assert.Equal([2, 3, 4], new AsepriteTag("a", 2, 4, AsepriteDirection.Forward, 0).Sequence());
        Assert.Equal([4, 3, 2], new AsepriteTag("a", 2, 4, AsepriteDirection.Reverse, 0).Sequence());
        Assert.Equal([2, 3, 4, 3], new AsepriteTag("a", 2, 4, AsepriteDirection.PingPong, 0).Sequence());
        Assert.Equal([4, 3, 2, 3], new AsepriteTag("a", 2, 4, AsepriteDirection.PingPongReverse, 0).Sequence());
        Assert.Equal([7], new AsepriteTag("a", 7, 7, AsepriteDirection.PingPong, 0).Sequence());

        Assert.True(new AsepriteTag("a", 0, 1, AsepriteDirection.Forward, 0).Loops);
        Assert.False(new AsepriteTag("a", 0, 1, AsepriteDirection.Forward, 1).Loops);
    }

    [Fact]
    public void ASelectionGoesByNameOrByPath()
    {
        var selection = new LayerSelection("+White,-Blue/Main");

        Assert.True(selection.Wants("White", "White"));
        Assert.False(selection.Wants("Main", "Blue/Main"));
        Assert.Null(selection.Wants("Main", "White/Main"));
        Assert.Null(default(LayerSelection).Wants("White", "White"));
        Assert.True(default(LayerSelection).IsDefault);
    }

    [Fact]
    public void TheTagsAndSlicesOfAFileAreItsSprites()
    {
        var stars = SpriteSheetDefinition.FromAseprite(Art("Stars.aseprite"));

        Assert.True(stars.TryGetSprite("Activate", null, out var activate));
        Assert.Equal(5, activate.Frames);
        Assert.Equal([1, 2, 3, 4, 5], activate.ImageFrames!);
        Assert.True(activate.Trim);

        // Every frame is in the same place of the canvas, on a frame of the file of its own
        Assert.Equal((0, 0), activate.FrameAt(3));
        Assert.Equal(4, activate.ImageFrameAt(3));
        Assert.NotEqual(activate.KeyOf(0), activate.KeyOf(1));

        // One frame on its own is a sprite of one frame, on the frame of the file it was
        Assert.True(stars.TryGetSprite("Activate#2", null, out var third));
        Assert.Equal((1, 3), (third.Frames, third.ImageFrame));
        Assert.Null(third.ImageFrames);

        var panels = SpriteSheetDefinition.FromAseprite(Art("PanelsThemed.aseprite"));
        Assert.True(panels.TryGetSprite("Paper/PanelA", null, out var panel));
        Assert.False(panel.Trim);
        Assert.True(panel.Width > 0 && panel.Height > 0);
    }

    [Fact]
    public void AFrameCanBeReadBackAsPixels()
    {
        var stars = SpriteSheetDefinition.FromAseprite(Art("Stars.aseprite"));
        Assert.True(stars.TryGetSprite("Full", null, out var full));

        ImagePixels pixels = full.ReadFrame(0) ?? throw new Exception("The frame can't be read.");
        Assert.Equal((full.Width, full.Height), (pixels.Width, pixels.Height));
        Assert.True(Drawn(pixels.Data) > 0);
    }
}
