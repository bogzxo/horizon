using Horizon.Content;
using Horizon.Graphics;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.Spriting;
using Horizon.UI.Drawing;

namespace Horizon.Tests.Content;

[Collection(ContentSourceCollection.NAME)]
public class ContentRetentionTests
{
    private static byte[] Asset(string path)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(ContentDecodeTests.Repo(), "Horizon", path));
        // These one-frame art fixtures have no animations. Give the first frame an idle tag for the sprite API.
        byte[] tag = new byte[39];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(tag, tag.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tag.AsSpan(4), 0x2018);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tag.AsSpan(6), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tag.AsSpan(33), 4);
        "idle"u8.CopyTo(tag.AsSpan(35));
        int frameLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(128));
        int end = 128 + frameLength;
        byte[] tagged = new byte[original.Length + tag.Length];
        original.AsSpan(0, end).CopyTo(tagged);
        tag.CopyTo(tagged.AsSpan(end));
        original.AsSpan(end).CopyTo(tagged.AsSpan(end + tag.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(tagged, tagged.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(tagged.AsSpan(128), frameLength + tag.Length);
        ushort chunks = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tagged.AsSpan(134));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tagged.AsSpan(134), (ushort)(chunks + 1));
        int newChunks = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(tagged.AsSpan(140));
        if (newChunks != 0)
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(tagged.AsSpan(140), newChunks + 1);
        return tagged;
    }
    private static MemoryContent Picture(byte[] bytes) => new(new Dictionary<string, byte[]> { ["art/hero.aseprite"] = bytes });

    [Fact]
    public void AnAbsoluteAliasStillOpensTheSameFileAfterResolvingAnOverlay()
    {
        string root = Path.Combine(Path.GetTempPath(), "content-alias-" + Guid.NewGuid().ToString("N"));
        string firstRoot = Path.Combine(root, "first"), secondRoot = Path.Combine(root, "second");
        var first = new MemoryContent(new Dictionary<string, byte[]> { ["art/hero.ase"] = [1] }, firstRoot);
        var second = new MemoryContent(new Dictionary<string, byte[]> { ["art/hero.ase"] = [2] }, secondRoot);
        var overlay = new OverlayContent(first, second);
        using var installed = new InstalledContent(overlay);
        string firstPath = Path.Combine(firstRoot, "art", "hero.ase"), secondPath = Path.Combine(secondRoot, "art", "hero.ase");

        FoundContent a = ContentFiles.Find(firstPath), b = ContentFiles.Find(secondPath);

        Assert.Equal(new byte[] { 1 }, a.ReadAllBytes());
        Assert.Equal(new byte[] { 2 }, b.ReadAllBytes());
        Assert.Equal(ContentFiles.ReadAllBytes(overlay, secondPath), ContentFiles.ReadAllBytes(overlay, overlay.ResolvePath(secondPath)));
        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(new byte[] { 1 }, ContentFiles.Find("art/hero.ase").ReadAllBytes());
    }

    [Fact]
    public void ASheetItsUiMaskAndQueuedAtlasKeepTheOriginalSource()
    {
        byte[] original = Asset("Assets/global/loading.aseprite");
        byte[] replacement = Asset("Assets/uix/adventure/art.aseprite");
        var packed = new List<byte[]>();
        using var atlas = new TextureAtlas(2048, 2048) { Trim = false, Recolour = pixels => packed.Add(pixels.ToArray()) };
        UISprite picture;
        SpriteSource frame;
        string animation;
        SpritePixels expected;
        using (new InstalledContent(Picture(original)))
        {
            picture = UISprite.Open("art/hero.aseprite");
            animation = picture.Sheet.Sprites.First();
            Assert.True(picture.Sheet.TryGetSprite(animation, null, out frame));
            frame = frame with { Frames = 1 };
            expected = Assert.IsType<SpritePixels>(frame.ReadFrame(0));
            var (x, y) = frame.FrameAt(0);
            atlas.Request("queued", frame.Path, x, y, frame.Width, frame.Height, frame.Layer);
        }

        using (new InstalledContent(Picture(replacement)))
        {
            AsepriteDocument.DropAllRendered();
            Assert.Equal(expected.Data, Assert.IsType<SpritePixels>(frame.ReadFrame(0)).Data);
            UISpriteMask mask = Assert.IsType<UISpriteMask>(picture.Mask(animation, 0));
            for (int y = 0; y < expected.Height; y++)
                for (int x = 0; x < expected.Width; x++)
                    Assert.Equal(expected.Data[(y * expected.Width + x) * 4 + 3] >= 32, mask.IsSolid(x, y));

            string key = atlas.Request(frame)[0];
            atlas.Pack();
            Assert.Equal(2, packed.Count);
            Assert.All(packed, bytes => Assert.Equal(expected.Data, bytes));

            UISprite other = UISprite.Open("art/hero.aseprite");
            Assert.NotSame(picture, other);
            Assert.True(other.Sheet.TryGetSprite(other.Sheet.Sprites.First(), null, out SpriteSource otherFrame));
            Assert.NotEqual(key, atlas.Request(otherFrame with { Frames = 1 })[0]);
        }
    }

    [Fact]
    public async Task AtlasArtStaysPendingWhilePackingAndUntilItIsUploaded()
    {
        var files = new MemoryContent(new Dictionary<string, byte[]>
        {
            ["art/pending.png"] = ImageFile.EncodePng([255, 0, 0, 255], 1, 1)
        });
        using var content = new InstalledContent(files);
        using var packing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var atlas = new TextureAtlas(32, 32)
        {
            Recolour = _ =>
            {
                packing.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Packing was not released.");
            }
        };
        Assert.False(atlas.HasPending);
        atlas.Request("pending", "art/pending.png", 0, 0, 1, 1);
        Assert.True(atlas.HasPending);
        Task worker = Task.Run(atlas.Pack);
        try
        {
            Assert.True(packing.Wait(TimeSpan.FromSeconds(5)), "Packing did not reach the image.");
            Assert.True(atlas.HasPending);
            Assert.False(atlas.TryGet("pending", out _));
        }
        finally
        {
            release.Set();
            await worker.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(atlas.HasPending);
        Assert.False(atlas.TryGet("pending", out _));
    }

    [Fact]
    public void ADefinitionKeepsTheSourceOfItsPngUntilTheAtlasReadsIt()
    {
        byte[] definition = "let sheet = { images: { art: { file: \"tile.png\", sprites: { tile: { x: 0, y: 0, w: 1, h: 1 } } } } };"u8.ToArray();
        MemoryContent Source(byte r, byte b) => new(new Dictionary<string, byte[]>
        {
            ["art/sheet.hor"] = definition,
            ["art/tile.png"] = ImageFile.EncodePng([r, 0, b, 255], 1, 1)
        });
        SpriteSource sprite;
        using (new InstalledContent(Source(255, 0)))
            Assert.True(SpriteSheetDefinition.Open("art/sheet.hor").TryGetSprite("tile", null, out sprite));
        using (new InstalledContent(Source(0, 255)))
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Assert.IsType<SpritePixels>(sprite.ReadFrame(0)).Data);
    }

    [Fact]
    public void UiSpritesRespectCaseSensitivePackageNamesIncludingMissingNames()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["art/Hero.aseprite"] = Asset("Assets/global/loading.aseprite"),
            ["art/hero.aseprite"] = Asset("Assets/uix/adventure/art.aseprite")
        };
        using var installed = new InstalledContent(new MemoryContent(files));

        UISprite upper = UISprite.Open("art/Hero.aseprite");
        UISprite lower = UISprite.Open("art/hero.aseprite");

        Assert.NotSame(upper, lower);
        Assert.NotEqual(upper.FrameSize, lower.FrameSize);
        Assert.Throws<HidlRuntimeException>(() => UISprite.Open("art/HERO.aseprite"));
    }
}
