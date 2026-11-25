using System.Buffers.Binary;
using System.Text;

using Horizon.Rendering.Text;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public sealed class TextMateAndFontTests
{
    private static string FindHackTtfPath()
    {
        string inBase = Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "hack", "hack.ttf");
        if (File.Exists(inBase))
            return inBase;

        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "Horizon", "Assets", "fonts", "hack", "hack.ttf");
            if (File.Exists(candidate))
                return candidate;

            string nested = Path.Combine(dir, "horizon", "Horizon", "Assets", "fonts", "hack", "hack.ttf");
            if (File.Exists(nested))
                return nested;

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException("Could not locate Assets/fonts/hack/hack.ttf for TTC test.");
    }

    /// <summary>
    /// Wraps a raw TrueType font buffer inside a valid OpenType TrueType Collection (v1.0, 'ttcf') container,
    /// adjusting every TableRecord.offset so it remains relative to byte 0 of the .ttc file per the OpenType spec.
    /// </summary>
    private static byte[] WrapTtfAsTtc(byte[] ttf, int faceCount = 1)
    {
        int headerSize = 12 + faceCount * 4;
        var ttc = new byte[headerSize + ttf.Length];

        // 'ttcf' tag, majorVersion = 1, minorVersion = 0, numFonts = faceCount
        ttc[0] = (byte)'t';
        ttc[1] = (byte)'t';
        ttc[2] = (byte)'c';
        ttc[3] = (byte)'f';
        BinaryPrimitives.WriteUInt16BigEndian(ttc.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(ttc.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(8, 4), (uint)faceCount);

        for (int i = 0; i < faceCount; i++)
            BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(12 + i * 4, 4), (uint)headerSize);

        Buffer.BlockCopy(ttf, 0, ttc, headerSize, ttf.Length);

        // Shift every sfnt TableRecord.offset by +headerSize so offsets are from byte 0 of the TTC file
        ushort numTables = BinaryPrimitives.ReadUInt16BigEndian(ttc.AsSpan(headerSize + 4, 2));
        for (int t = 0; t < numTables; t++)
        {
            int recordOffset = headerSize + 12 + t * 16;
            uint oldOffset = BinaryPrimitives.ReadUInt32BigEndian(ttc.AsSpan(recordOffset + 8, 4));
            BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(recordOffset + 8, 4), oldOffset + (uint)headerSize);
        }

        return ttc;
    }

    [Fact]
    public void GlyphOutlines_TryGetFontOffset_ValidatesTtcBoundsAndRejectsMalformed()
    {
        Assert.False(GlyphOutlines.TryGetFontOffset([], 0, out _));
        Assert.False(GlyphOutlines.TryGetFontOffset(new byte[8], 0, out _));

        // Valid 2-face TTC header pointing to offsets 32 and 48 in a 64-byte buffer
        var valid = new byte[64];
        valid[0] = (byte)'t'; valid[1] = (byte)'t'; valid[2] = (byte)'c'; valid[3] = (byte)'f';
        BinaryPrimitives.WriteUInt16BigEndian(valid.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(valid.AsSpan(8, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(valid.AsSpan(12, 4), 32);
        BinaryPrimitives.WriteUInt32BigEndian(valid.AsSpan(16, 4), 48);

        Assert.True(GlyphOutlines.TryGetFontOffset(valid, 0, out int face0));
        Assert.Equal(32, face0);
        Assert.True(GlyphOutlines.TryGetFontOffset(valid, 1, out int face1));
        Assert.Equal(48, face1);
        Assert.False(GlyphOutlines.TryGetFontOffset(valid, -1, out _));
        Assert.False(GlyphOutlines.TryGetFontOffset(valid, 2, out _));

        // Malformed TTC: face offset points past EOF
        BinaryPrimitives.WriteUInt32BigEndian(valid.AsSpan(12, 4), 60);
        Assert.False(GlyphOutlines.TryGetFontOffset(valid, 0, out _));

        // Malformed TTC: claims 100 faces in a 64-byte buffer
        BinaryPrimitives.WriteUInt32BigEndian(valid.AsSpan(8, 4), 100);
        Assert.False(GlyphOutlines.TryGetFontOffset(valid, 50, out _));
    }

    [Fact]
    public void GlyphOutlines_LoadsTtcContainerIdenticallyToRawTtf()
    {
        string ttfPath = FindHackTtfPath();
        byte[] ttfBytes = File.ReadAllBytes(ttfPath);
        byte[] ttcBytes = WrapTtfAsTtc(ttfBytes, faceCount: 2);

        string tempTtc = Path.Combine(Path.GetTempPath(), $"horizon-ttc-test-{Guid.NewGuid():N}.ttc");
        try
        {
            File.WriteAllBytes(tempTtc, ttcBytes);

            GlyphOutlines? fromTtf = GlyphOutlines.For(ttfPath);
            GlyphOutlines? fromTtc = GlyphOutlines.For(tempTtc);

            Assert.NotNull(fromTtf);
            Assert.NotNull(fromTtc);

            byte[] ttfBuf = [];
            byte[] ttcBuf = [];

            foreach (char ch in new[] { 'A', 'g', 'x', '0', '{', '?' })
            {
                bool okTtf = fromTtf.Rasterize(ch, 16.0f, 0.0f, 0.0f, 0.0f, hint: true, ref ttfBuf, out GlyphBox ttfBox);
                bool okTtc = fromTtc.Rasterize(ch, 16.0f, 0.0f, 0.0f, 0.0f, hint: true, ref ttcBuf, out GlyphBox ttcBox);

                Assert.Equal(okTtf, okTtc);
                Assert.True(okTtc);
                Assert.Equal(ttfBox, ttcBox);

                int pixelCount = ttfBox.Width * ttfBox.Height;
                Assert.True(ttfBuf.AsSpan(0, pixelCount).SequenceEqual(ttcBuf.AsSpan(0, pixelCount)));
            }
        }
        finally
        {
            if (File.Exists(tempTtc))
                File.Delete(tempTtc);
        }
    }

    [Fact]
    public void CodeLanguages_ForFile_CachesPerExtensionAndHandlesCaseAndBackups()
    {
        ICodeHighlighter? json = CodeLanguages.ForFile("config.json");
        Assert.NotNull(json);
        var tm = Assert.IsType<TextMateHighlighter>(json);
        Assert.Equal("source.json", tm.ScopeName);
        Assert.True(CodeLanguages.IsScopeLoaded("source.json"));

        // Case-insensitive and backup-suffix paths hit the exact same cached highlighter
        Assert.Same(json, CodeLanguages.ForFile("CONFIG.JSON"));
        Assert.Same(json, CodeLanguages.ForFile("dir/settings.json.bak"));
        Assert.Same(json, CodeLanguages.ForFile("dir/settings.json.orig"));

        // Built-in HIDL extension resolves without requiring TextMate grammar lookup
        Assert.Same(CodeLanguages.Hidl, CodeLanguages.ForFile("layout.hor"));

        // Unknown extension returns null
        Assert.Null(CodeLanguages.ForFile("readme.definitely_not_a_language_xyz"));
    }

    [Fact]
    public void CutNear_SurrogatePairAtSingleCharWindow_AlwaysAdvancesWithoutSplitting()
    {
        // "😀abc" -> index 0 is high surrogate (\uD83D), index 1 is low surrogate (\uDE00)
        string emojiText = "😀abc";

        // When most == from + 1 on a surrogate pair, CutNear must advance to from + 2 instead of returning from (infinite loop!)
        int cut1 = TextMateHighlighter.CutNear(emojiText.AsSpan(), from: 0, most: 1);
        Assert.Equal(2, cut1);

        // When most == from + 2 on a surrogate pair, it keeps the pair intact
        int cut2 = TextMateHighlighter.CutNear(emojiText.AsSpan(), from: 0, most: 2);
        Assert.Equal(2, cut2);

        // Unpaired high surrogate at from with most == from + 1 still strictly advances
        string unpaired = "\uD83Dabc";
        int cutUnpaired = TextMateHighlighter.CutNear(unpaired.AsSpan(), from: 0, most: 1);
        Assert.Equal(1, cutUnpaired);

        // High surrogate at the end of a multi-char window steps back by 1 without splitting the pair
        string prefixAndEmoji = "aaaaaaaaa😀bbb"; // 9 'a's, then high surrogate at index 9, low at 10
        int cutBeforeEmoji = TextMateHighlighter.CutNear(prefixAndEmoji.AsSpan(), from: 0, most: 10);
        Assert.Equal(9, cutBeforeEmoji);
    }

    [Fact]
    public void TextMateHighlighter_LongSingleLineJsonWithSurrogatesAtPieceBoundaries_ReadsAndUpdatesIncrementally()
    {
        ICodeHighlighter? jsonHighlighter = CodeLanguages.ForExtension(".json");
        var tm = Assert.IsType<TextMateHighlighter>(jsonHighlighter);

        // Build a ~50KB single-line JSON string with emoji surrogate pairs placed right across PIECE (2048) boundaries
        // and long delimiter-free runs so CutNear exercises its surrogate boundary fallback.
        var sb = new StringBuilder(55_000);
        sb.Append("{\"items\":[");
        int targetLength = 50_000;
        int entry = 0;
        while (sb.Length < targetLength)
        {
            if (entry > 0)
                sb.Append(',');

            sb.Append("{\"k\":");
            sb.Append(entry);
            sb.Append(",\"v\":\"");

            // Pad so a surrogate pair straddles the next multiple of PIECE (2048)
            int nextBoundary = ((sb.Length / TextMateHighlighter.PIECE) + 1) * TextMateHighlighter.PIECE;
            int padNeeded = Math.Max(0, nextBoundary - 1 - sb.Length);
            if (padNeeded > 0 && padNeeded < 2500)
                sb.Append('a', padNeeded);

            // Place surrogate pairs right at and around the PIECE boundary
            sb.Append("😀🚀😀");
            sb.Append("tail\"}");
            entry++;
        }
        sb.Append("]}");
        string original = sb.ToString();
        Assert.True(original.Length > 50_000);

        // Full read of the 50KB single-line JSON
        var starts = new CodeLineStart[1];
        List<SyntaxSpan>[] initialSpans = tm.Read(original, 1, starts);
        var longSpans = Assert.IsType<LongLineSpans>(initialSpans[0]);
        Assert.True(longSpans.Whole);
        Assert.Equal(original.Length, longSpans.ReadTo);
        Assert.True(longSpans.Count > 0);
        AssertValidSpans(longSpans, original.Length);

        // Incremental edit in the middle of the 50KB line
        int editAt = original.Length / 2;
        string edited = original.Insert(editAt, "EDITED_😀_VALUE");
        var lastColouring = new CodeColouring([original], initialSpans, starts);
        var editedStarts = new CodeLineStart[1];
        List<SyntaxSpan>[] updatedSpans = tm.ReadAgain(edited, [edited], editedStarts, lastColouring);

        var updatedLong = Assert.IsType<LongLineSpans>(updatedSpans[0]);
        Assert.True(updatedLong.Whole);
        Assert.Equal(edited.Length, updatedLong.ReadTo);
        Assert.True(updatedLong.Count > 0);
        AssertValidSpans(updatedLong, edited.Length);

        // Partial ReadUpTo followed by an edit before ReadTo and then full ReadAgain must finish the whole line
        List<SyntaxSpan>? partial = tm.ReadUpTo(original, CodeLineStart.First, goal: 6000, budget: 3000, last: null, lastLine: null);
        var partialLong = Assert.IsType<LongLineSpans>(partial);
        Assert.False(partialLong.Whole);
        Assert.True(partialLong.ReadTo > 0 && partialLong.ReadTo < original.Length);

        string editedEarly = original.Insert(20, "X");
        var partialColouring = new CodeColouring([original], [partialLong], [CodeLineStart.First]);
        var fullAfterPartialStarts = new CodeLineStart[1];
        List<SyntaxSpan>[] fullAfterPartial = tm.ReadAgain(editedEarly, [editedEarly], fullAfterPartialStarts, partialColouring);
        var fullAfterPartialLong = Assert.IsType<LongLineSpans>(fullAfterPartial[0]);
        Assert.True(fullAfterPartialLong.Whole);
        Assert.Equal(editedEarly.Length, fullAfterPartialLong.ReadTo);
        AssertValidSpans(fullAfterPartialLong, editedEarly.Length);
    }

    private static void AssertValidSpans(List<SyntaxSpan> spans, int lineLength)
    {
        int prevEnd = 0;
        foreach (SyntaxSpan span in spans)
        {
            Assert.True(span.Start >= prevEnd, $"Span at {span.Start} overlaps previous end {prevEnd}.");
            Assert.True(span.Length > 0, $"Span at {span.Start} has non-positive length {span.Length}.");
            Assert.True(span.Start + span.Length <= lineLength, $"Span [{span.Start}..{span.Start + span.Length}) exceeds line length {lineLength}.");
            prevEnd = span.Start + span.Length;
        }
    }
}
