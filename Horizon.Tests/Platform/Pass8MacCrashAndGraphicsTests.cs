using System.Buffers.Binary;
using System.Numerics;
using Horizon.Platform;
using Horizon.Platform.Desktop;
using Horizon.Rendering.Text;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.Platform;

/// <summary>
/// Regression tests for Pass 8 macOS "New File" crash fixes:
/// <list type="bullet">
/// <item><c>.ttc</c> TrueType Collection font loading in <see cref="DistanceFieldFont"/> &amp; <see cref="GlyphOutlines"/>.</item>
/// <item>Safe non-zero <see cref="UIFont.LineHeight"/> and <see cref="CodeEditor"/> layout/paint/scroll when a font fails to load.</item>
/// <item><see cref="MacMenuBarRules.ShouldRefillMenu"/> never clears an open menu, open submenu, Help-search menu, or key-down scan.</item>
/// </list>
/// </summary>
public sealed class Pass8MacCrashAndGraphicsTests
{
    private static byte[] BuildSingleFaceTtc(byte[] ttfBytes)
    {
        const int ttcHeaderSize = 16;
        byte[] ttc = new byte[ttcHeaderSize + ttfBytes.Length];
        // 'ttcf' tag, v1.0, numFonts = 1, offset[0] = 16
        ttc[0] = (byte)'t';
        ttc[1] = (byte)'t';
        ttc[2] = (byte)'c';
        ttc[3] = (byte)'f';
        BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(4, 4), 0x00010000u);
        BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(8, 4), 1u);
        BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(12, 4), ttcHeaderSize);

        ttfBytes.CopyTo(ttc.AsSpan(ttcHeaderSize));

        // OpenType table records use file-relative offsets from byte 0 of the collection.
        ushort numTables = BinaryPrimitives.ReadUInt16BigEndian(ttc.AsSpan(ttcHeaderSize + 4, 2));
        for (int i = 0; i < numTables; i++)
        {
            Span<byte> offsetField = ttc.AsSpan(ttcHeaderSize + 12 + i * 16 + 8, 4);
            uint oldOffset = BinaryPrimitives.ReadUInt32BigEndian(offsetField);
            BinaryPrimitives.WriteUInt32BigEndian(offsetField, oldOffset + ttcHeaderSize);
        }

        return ttc;
    }

    [Fact]
    public void DistanceFieldFont_and_GlyphOutlines_load_valid_ttc_collections_with_positive_metrics()
    {
        string cascadia = Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono", "cascadia_mono.ttf");
        byte[] ttf = File.ReadAllBytes(cascadia);
        byte[] ttc = BuildSingleFaceTtc(ttf);

        string tempDir = Path.Combine(Path.GetTempPath(), $"horizon-ttc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string ttcPath = Path.Combine(tempDir, "TestMono.ttc");
        try
        {
            File.WriteAllBytes(ttcPath, ttc);

            GlyphOutlines? outlines = GlyphOutlines.For(ttcPath);
            Assert.NotNull(outlines);
            byte[] scratch = [];
            Assert.True(outlines!.Rasterize('A', 16.0f, 0.0f, 0.0f, 0.0f, hint: true, ref scratch, out GlyphBox box));
            Assert.False(box.IsEmpty);

            // Without a Vulkan window in headless unit tests, Texture.FromPixels catches the missing device inside Load,
            // while BuildFromTrueType has already populated glyphs, kerning, and metrics.
            DistanceFieldFont field = DistanceFieldFont.Load(tempDir, "TestMono.ttc", DistanceFieldFont.DEFAULT_EM);
            Assert.NotEmpty(field.Glyphs);
            Assert.True(field.LineHeight > 0.0f);
            Assert.True(field.Base > 0.0f);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Missing_font_reports_unloaded_and_CodeEditor_never_throws_on_first_paint()
    {
        var missingFont = new UIFont(Path.GetTempPath(), "does-not-exist-menlo.ttc", 96);
        Assert.False(missingFont.IsLoaded);

        var skin = new UISkin(missingFont);
        var editor = new CodeEditor("line 1\nline 2\nline 3")
        {
            Font = missingFont,
            Size = new Vector2(800.0f, 600.0f),
        };

        var screen = new UIRect(Vector2.Zero, new Vector2(800.0f, 600.0f));
        editor.UpdateTree(1.0f / 60.0f);
        editor.MeasureTree(skin);
        editor.ArrangeTree(screen);

        var drawList = new UIDrawList();
        drawList.Begin(skin, 1, 1.0f / 60.0f, 1.0f / 60.0f);
        editor.PaintTree(drawList);
        drawList.End();

        UITextPosition hit = editor.PositionAt(new Vector2(20, 580));
        Assert.InRange(hit.Line, 0, 2);
        Vector2 place = editor.PlaceOf(new UITextPosition(1, 0));
        Assert.True(float.IsFinite(place.X) && float.IsFinite(place.Y));
    }

    [Theory]
    [InlineData(true, PlatformMenuRole.None, false, false, false, true)]
    [InlineData(false, PlatformMenuRole.None, false, false, false, false)]
    [InlineData(true, PlatformMenuRole.WindowList, false, false, false, false)]
    [InlineData(true, PlatformMenuRole.HelpSearch, false, false, false, false)]
    [InlineData(true, PlatformMenuRole.None, true, false, false, false)]
    [InlineData(true, PlatformMenuRole.None, false, true, false, false)]
    [InlineData(true, PlatformMenuRole.None, false, false, true, false)]
    public void MacMenuBar_refill_guard_skips_open_menus_help_search_and_key_down_scans(
        bool isDynamic,
        PlatformMenuRole role,
        bool isKeyDown,
        bool menuOrSubtreeOpen,
        bool helpMenuOpen,
        bool expected)
    {
        Assert.Equal(expected, MacMenuBarRules.ShouldRefillMenu(isDynamic, role, isKeyDown, menuOrSubtreeOpen, helpMenuOpen));
    }
}
