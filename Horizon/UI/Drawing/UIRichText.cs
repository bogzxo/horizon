using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Skinning;

namespace Horizon.UI.Drawing;

/// <summary>
/// Text with more to it than one look, laid out once and painted every tick out of what that came to. Tags in square
/// brackets work in any of it, and markdown on top of that when it is asked for.
/// <code>
/// "[b]bold[/b], [i]italic[/i], [u]under[/u], [s]struck[/s], [code]code[/code]"
/// "[color:#ff8040]orange[/color], [color:accent]the skin's accent[/color], [size:1.5]bigger[/size], [reset] back to plain"
///
/// # A heading, ## and ### smaller ones
/// **bold**, *italic* (or _italic_), ~~struck~~, `code`, [a link](somewhere), \* is a star
/// - a bullet, indented by two spaces a level
/// 1. a numbered one
/// > a quote
/// ---
/// ```
/// a block of code, written as it is
/// ```
/// </code>
/// The colours a tag can name are a hex (#rgb, #rrggbb, #rrggbbaa), the skin's (accent, highlight, text, dim) and the
/// usual words (red, green, blue, yellow, orange, purple, cyan, pink, white, black, grey). A size is a multiple of the
/// size the text is drawn at. <c>[/color]</c> and <c>[/size]</c> go back one, <c>[reset]</c> (or <c>[/]</c>) to plain.
/// A tag it doesn't know is written as it is. A single line break is a line break (the way a game writes text), an
/// empty line is the gap between two paragraphs. Icons (<c>[icon:pad_a]</c>, <c>[icons:set]</c>) work as everywhere.
/// <para>
/// Bold and italic are made out of the font's distance field as it is drawn (see <see cref="UITextStyle"/>), there is no
/// other face to load. Nothing is allocated while painting, the pieces are slices of the text it was given.
/// </para>
/// </summary>
public sealed class UIRichText
{
    // What a piece is, a run of letters or a box (a code background, a quote's bar, a bullet, a line under or through)
    private enum Kind : byte
    {
        Text,
        Box
    }

    /// <summary>Which colour a piece is drawn in, worked out from the skin as it is painted so a theme change is seen.</summary>
    private enum Shade : byte
    {
        Inherit,
        Explicit,
        Accent,
        Highlight,
        Text,
        Dim,
        Field,
        Border
    }

    private struct Piece
    {
        public Kind Kind;
        public int Start, Length;

        // Which line it is on (-1 for something as wide as the whole block, which no alignment moves), and where in the
        // block, down from its top left corner. Until its line is done Y is measured from the line's baseline
        public int Line;
        public float X, Y, Width, Height;

        public float Scale;
        public UITextStyle Style;
        public bool Markup;
        public Shade Shade;
        public Vector4 Color;
        public float Alpha;
        public int IconsStart, IconsLength;
        public int Link;
    }

    /// <summary>
    /// How much room a line of the text has, for text that wraps round something (<see cref="UIWrapShape"/>). Asked as
    /// every line starts with where it is, its top down from the top of the block and how tall a line of the text is,
    /// and says where across the block it may start and where it has to stop. Room the line has all of says 0 and the
    /// width it was built with.
    /// </summary>
    public delegate void Room(float top, float height, out float left, out float right);

    private struct LineInfo
    {
        public float Top, Height, Baseline, Width;
    }

    // What the text is written in at a point of it, the tags and the markdown before it having had their say
    private struct State
    {
        public UITextStyle Style;
        public bool Underline, Strike, Code;
        public Shade Shade;
        public Vector4 Color;
        public float Size;
        public float Alpha;
        public int Link;
        public int IconsStart, IconsLength;
    }

    // A piece of a word waiting to see whether the word fits on the line
    private struct Pending
    {
        public int Start, Length;
        public float Width;
        public State State;
        public float Scale;
    }

    private readonly List<Piece> pieces = [];
    private readonly List<LineInfo> lines = [];
    private readonly List<(int Start, int Length)> links = [];
    private readonly List<Pending> word = [];
    private readonly List<(Shade Shade, Vector4 Color)> colours = [];
    private readonly List<float> sizes = [];
    private readonly List<int> fullWidth = [];

    // What the last layout was of, so asking again for the same thing is nothing
    private string? builtText;
    private UIFont? builtFont;
    private float builtScale, builtWidth;
    private bool builtMarkdown;
    private int builtRoom;

    // What gives every line its room, and the room of the line going on, how far it is moved along and how wide it is
    private Room? room;
    private float lineShift, lineRoom;

    // The fewest em a line is cut down to by a shape, with less room than that beside one it runs over it instead
    private const float MIN_ROOM_EM = 5.0f;

    // The layout going on, see Build
    private string source = string.Empty;
    private UIFont font = null!;
    private float baseScale, maxWidth;
    private bool markdown;
    private State state;
    private int lineStartPiece;
    private float penX, lineIndent, wrapIndent, cursorY, pendingGap;
    private float quoteX = -1.0f;
    private int lineCount;

    // Whether the line going on was wrapped onto rather than started by the text
    private bool wrapped;

    /// <summary>How wide the text came out, the widest of its lines.</summary>
    public float Width { get; private set; }

    /// <summary>How tall the text came out, every line and every gap.</summary>
    public float Height { get; private set; }

    /// <summary>How many lines it came out as.</summary>
    public int LineCount => lineCount;

    /// <summary>
    /// Whether a text has anything in it that this does differently from plain text, a style tag. For a label that only
    /// lays text out this way when it has to.
    /// </summary>
    public static bool HasTags(string text)
    {
        for (int at = text.IndexOf('['); at >= 0 && at < text.Length; at = text.IndexOf('[', at + 1))
        {
            int end = text.IndexOf(']', at);
            if (end < 0)
                return false;

            if (IsStyleTag(text.AsSpan(at + 1, end - at - 1)))
                return true;
        }

        return false;
    }

    private static bool IsStyleTag(ReadOnlySpan<char> tag) =>
        tag is "b" or "/b" or "i" or "/i" or "u" or "/u" or "s" or "/s" or "code" or "/code" or "reset" or "/" or "/color" or "/size"
        || tag.StartsWith("color:") || tag.StartsWith("color=") || tag.StartsWith("size:") || tag.StartsWith("size=");

    /// <summary>
    /// Lays text out, unless it is what was laid out last time, the same string at the same size in the same room.
    /// </summary>
    /// <param name="maxWidth">How wide a line can be before it wraps, 0 or less for as wide as it likes.</param>
    /// <param name="markdown">Whether markdown counts as well as the tags.</param>
    public void Build(string text, UIFont font, float scale, float maxWidth, bool markdown) => Build(text, font, scale, maxWidth, markdown, null, 0);

    /// <summary>
    /// Lays text out with every line in the room it is given (see <see cref="Room"/>), cut short or moved along by
    /// whatever it wraps round. Laid out again when the key changes, which is the caller's way of saying the room did.
    /// </summary>
    /// <param name="roomKey">Anything that changes when the room does, where the shapes are, say.</param>
    public void Build(string text, UIFont font, float scale, float maxWidth, bool markdown, Room? room, int roomKey)
    {
        if (room is null) roomKey = 0;
        if (ReferenceEquals(text, builtText) && font == builtFont && scale == builtScale && maxWidth == builtWidth && markdown == builtMarkdown && roomKey == builtRoom)
            return;

        (builtText, builtFont, builtScale, builtWidth, builtMarkdown, builtRoom) = (text, font, scale, maxWidth, markdown, roomKey);
        this.room = room;

        source = text;
        this.font = font;
        baseScale = scale;
        this.maxWidth = maxWidth > 0.0f ? maxWidth : float.PositiveInfinity;
        this.markdown = markdown;

        pieces.Clear();
        lines.Clear();
        links.Clear();
        word.Clear();
        fullWidth.Clear();
        ResetState();

        lineStartPiece = 0;
        penX = lineIndent = wrapIndent = cursorY = pendingGap = 0.0f;
        quoteX = -1.0f;
        lineCount = 0;
        wrapped = false;
        StartRoom();

        LayOut();

        Width = 0.0f;
        foreach (var line in lines)
            Width = MathF.Max(Width, line.Width);

        // A rule and the back of a block of code are as wide as the room there is, or as the text if it has no limit
        float full = float.IsPositiveInfinity(this.maxWidth) ? Width : MathF.Max(Width, this.maxWidth);
        foreach (int index in fullWidth)
        {
            Piece piece = pieces[index];
            piece.Width = full - piece.X;
            pieces[index] = piece;
        }

        Width = MathF.Max(Width, fullWidth.Count > 0 ? full : 0.0f);
        Height = cursorY;
    }

    private void ResetState()
    {
        state = new State { Shade = Shade.Inherit, Size = 1.0f, Alpha = 1.0f, Link = -1 };
        colours.Clear();
        sizes.Clear();
    }

    /* Laying out, a line of the text at a time */

    private void LayOut()
    {
        bool inCode = false;
        int codeFirstLine = 0;
        float codeIndent = 0.0f;
        bool first = true;

        int at = 0;
        while (at <= source.Length)
        {
            int end = source.IndexOf('\n', at);
            if (end < 0) end = source.Length;

            // A line ending in \r\n is the same line
            int length = end - at;
            if (length > 0 && source[at + length - 1] == '\r') length--;

            if (markdown)
                LayOutMarkdownLine(at, length, ref inCode, ref codeFirstLine, ref codeIndent, first);
            else
                LayOutParagraphLine(at, length, 0.0f, 0.0f);

            first = false;
            at = end + 1;
        }

        // A block of code nobody closed ends with the text
        if (inCode)
            CloseCodeBlock(codeFirstLine, codeIndent);
    }

    /// <summary>Helper method to lay out one line of the text the way it is written, wrapped where it has to be.</summary>
    private void LayOutParagraphLine(int start, int length, float indent, float hanging)
    {
        lineIndent = indent;
        wrapIndent = hanging;
        penX = indent;

        LayOutInline(start, length, inline: markdown);
        FlushWord();
        EndLine();
    }

    private void LayOutMarkdownLine(int start, int length, ref bool inCode, ref int codeFirstLine, ref float codeIndent, bool first)
    {
        ReadOnlySpan<char> line = source.AsSpan(start, length);
        ReadOnlySpan<char> trimmed = line.TrimStart();
        int leading = length - trimmed.Length;

        // A fence opens a block of code and closes it again, what is in it is written as it is
        if (trimmed.StartsWith("```"))
        {
            if (inCode)
            {
                CloseCodeBlock(codeFirstLine, codeIndent);
                inCode = false;
                pendingGap = font.LineHeight * baseScale * 0.3f;
            }
            else
            {
                inCode = true;
                codeFirstLine = lineCount;
                codeIndent = 0.0f;
                if (!first) pendingGap = MathF.Max(pendingGap, font.LineHeight * baseScale * 0.3f);
            }

            return;
        }

        quoteX = -1.0f;

        if (inCode)
        {
            ResetState();
            state.Code = true;

            float pad = font.LineHeight * baseScale * 0.4f;
            lineIndent = wrapIndent = penX = pad;
            LayOutLiteral(start, length);
            FlushWord();
            EndLine();
            return;
        }

        // Every line starts plain, a style doesn't run on from the line before in markdown
        ResetState();

        // An empty line is the gap between two paragraphs
        if (trimmed.IsEmpty)
        {
            pendingGap = font.LineHeight * baseScale * 0.5f;
            return;
        }

        // A heading, the more #s the smaller
        int hashes = 0;
        while (hashes < trimmed.Length && hashes < 6 && trimmed[hashes] == '#') hashes++;
        if (hashes > 0 && hashes < trimmed.Length && trimmed[hashes] == ' ')
        {
            if (!first) pendingGap = MathF.Max(pendingGap, font.LineHeight * baseScale * 0.35f);

            state.Size = hashes switch { 1 => 1.6f, 2 => 1.35f, 3 => 1.15f, _ => 1.0f };
            state.Style = UITextStyle.Bold;
            sizes.Add(state.Size);

            int from = start + leading + hashes + 1;
            LayOutParagraphLine(from, start + length - from, 0.0f, 0.0f);
            pendingGap = MathF.Max(pendingGap, font.LineHeight * baseScale * 0.15f);
            return;
        }

        // A rule, three or more of the same of -, * or _ and nothing else
        if (IsRule(trimmed))
        {
            float height = font.LineHeight * baseScale * 0.6f;
            float thickness = MathF.Max(1.0f, MathF.Round(font.LineHeight * baseScale * 0.06f));

            fullWidth.Add(pieces.Count);
            pieces.Add(new Piece
            {
                Kind = Kind.Box,
                Line = -1,
                X = 0.0f,
                Y = cursorY + pendingGap + MathF.Round((height - thickness) * 0.5f),
                Height = thickness,
                Shade = Shade.Dim,
                Alpha = 1.0f,
                Link = -1
            });

            cursorY += pendingGap + height;
            pendingGap = 0.0f;
            lineCount++;
            lines.Add(new LineInfo { Top = cursorY - height, Height = height, Baseline = 0.0f, Width = 0.0f });
            lineStartPiece = pieces.Count;
            return;
        }

        float em = font.LineHeight * baseScale;
        int at = start + leading;
        float indent = 0.0f;

        // A quote, indented with a bar down its left
        if (trimmed.StartsWith(">"))
        {
            quoteX = 0.0f;
            indent = em * 0.7f;
            state.Alpha = 0.72f;
            at++;
            if (at < start + length && source[at] == ' ') at++;

            ReadOnlySpan<char> rest = source.AsSpan(at, start + length - at);
            leading = rest.Length - rest.TrimStart().Length;
            at += leading;
            trimmed = rest.TrimStart();
        }

        int level = leading / 2;

        // A bullet, - * or + and a space, a level in for every two spaces in front of it
        if (trimmed.Length > 1 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
        {
            float marker = indent + level * em * 1.2f;
            float text = marker + em * 0.9f;

            lineIndent = wrapIndent = text;
            AddBullet(marker, em);
            penX = text;

            int from = at + 2;
            LayOutInline(from, start + length - from, inline: true);
            FlushWord();
            EndLine();
            return;
        }

        // A numbered one, the number and a dot (or a bracket) and a space
        int digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
        if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' ')
        {
            float marker = indent + level * em * 1.2f;
            float numberWidth = font.MeasureLine(trimmed[..(digits + 1)], baseScale, UITextStyle.None, default);
            float text = marker + MathF.Max(em * 1.3f, numberWidth + em * 0.4f);

            lineIndent = marker;
            wrapIndent = text;
            penX = marker;

            AddPiece(at, digits + 1, numberWidth, state, baseScale * state.Size);
            penX = text;

            int from = at + digits + 2;
            LayOutInline(from, start + length - from, inline: true);
            FlushWord();
            EndLine();
            return;
        }

        LayOutParagraphLine(at, start + length - at, indent, indent);
    }

    private static bool IsRule(ReadOnlySpan<char> line)
    {
        char mark = line[0];
        if (mark is not ('-' or '*' or '_'))
            return false;

        int count = 0;
        foreach (char character in line)
        {
            if (character == mark) count++;
            else if (character != ' ') return false;
        }

        return count >= 3;
    }

    private void AddBullet(float x, float em)
    {
        float side = MathF.Max(2.0f, MathF.Round(em * 0.22f));

        pieces.Add(new Piece
        {
            Kind = Kind.Box,
            Line = lineCount,
            X = x + MathF.Round(em * 0.2f),
            Y = -MathF.Round(em * 0.32f + side * 0.5f),
            Width = side,
            Height = side,
            Shade = state.Shade,
            Color = state.Color,
            Alpha = state.Alpha,
            Link = -1
        });
    }

    private void CloseCodeBlock(int firstLine, float indent)
    {
        if (firstLine >= lines.Count)
            return;

        float top = lines[firstLine].Top;
        float bottom = lines[^1].Top + lines[^1].Height;

        fullWidth.Add(pieces.Count);
        pieces.Add(new Piece
        {
            Kind = Kind.Box,
            Line = -1,
            X = indent,
            Y = top,
            Height = bottom - top,
            Shade = Shade.Field,
            Alpha = 1.0f,
            Link = -1
        });
    }

    /* A line's words, with the tags and the markdown that change how they are written */

    /// <summary>Helper method to lay out a line of a block of code, every character of it as it is written.</summary>
    private void LayOutLiteral(int start, int length)
    {
        int segment = start;
        for (int i = start; i < start + length; i++)
        {
            if (source[i] != ' ')
                continue;

            Take(segment, i - segment);
            FlushWord();
            Take(i, 1);
            FlushWord();
            segment = i + 1;
        }

        Take(segment, start + length - segment);
    }

    private void LayOutInline(int start, int length, bool inline)
    {
        int end = start + length;
        int segment = start;

        for (int i = start; i < end; i++)
        {
            char character = source[i];

            // Inside `code` everything is what it says up to the closing tick
            if (state.Code && inline && character != '`')
            {
                if (character == ' ')
                {
                    Take(segment, i - segment);
                    FlushWord();
                    Take(i, 1);
                    FlushWord();
                    segment = i + 1;
                }

                continue;
            }

            if (character == ' ')
            {
                Take(segment, i - segment);
                FlushWord();
                Take(i, 1);
                FlushWord();
                segment = i + 1;
                continue;
            }

            if (character == '[')
            {
                int close = source.IndexOf(']', i);
                if (close > i && close < end)
                {
                    ReadOnlySpan<char> tag = source.AsSpan(i + 1, close - i - 1);

                    // An icon is a piece of a word, as wide as its art
                    if (tag.StartsWith("icon:"))
                    {
                        i = close;
                        continue;
                    }

                    if (tag.StartsWith("icons:"))
                    {
                        Take(segment, i - segment);
                        state.IconsStart = i + 7;
                        state.IconsLength = close - i - 7;
                        segment = close + 1;
                        i = close;
                        continue;
                    }

                    if (IsStyleTag(tag))
                    {
                        Take(segment, i - segment);
                        ApplyTag(tag);
                        segment = close + 1;
                        i = close;
                        continue;
                    }

                    // [a link](where), the words underlined in the accent colour
                    if (inline && close + 1 < end && source[close + 1] == '(')
                    {
                        int closeUrl = source.IndexOf(')', close + 1);
                        if (closeUrl > close && closeUrl < end)
                        {
                            Take(segment, i - segment);

                            links.Add((close + 2, closeUrl - close - 2));
                            State outside = state;
                            state.Link = links.Count - 1;
                            state.Underline = true;
                            state.Shade = Shade.Accent;

                            LayOutInline(i + 1, close - i - 1, inline: true);

                            state = outside;
                            segment = closeUrl + 1;
                            i = closeUrl;
                            continue;
                        }
                    }
                }
            }

            if (!inline)
                continue;

            // A backslash makes the next character only itself
            if (character == '\\' && i + 1 < end)
            {
                Take(segment, i - segment);
                segment = i + 1;
                i++;
                continue;
            }

            if (character == '`')
            {
                Take(segment, i - segment);
                state.Code = !state.Code;
                segment = i + 1;
                continue;
            }

            if (character == '*' && i + 1 < end && source[i + 1] == '*')
            {
                Take(segment, i - segment);
                state.Style ^= UITextStyle.Bold;
                segment = i + 2;
                i++;
                continue;
            }

            if (character == '~' && i + 1 < end && source[i + 1] == '~')
            {
                Take(segment, i - segment);
                state.Strike = !state.Strike;
                segment = i + 2;
                i++;
                continue;
            }

            if (character == '_' && i + 1 < end && source[i + 1] == '_' && AtWordEdge(i, 2, end))
            {
                Take(segment, i - segment);
                state.Style ^= UITextStyle.Bold;
                segment = i + 2;
                i++;
                continue;
            }

            // A single * is italic anywhere, a single _ only at the edge of a word, snake_case is left alone
            if (character == '*' || (character == '_' && AtWordEdge(i, 1, end)))
            {
                Take(segment, i - segment);
                state.Style ^= UITextStyle.Italic;
                segment = i + 1;
            }
        }

        Take(segment, end - segment);
    }

    /// <summary>Helper method to say whether a mark of some length at a point is at the start or the end of a word.</summary>
    private bool AtWordEdge(int at, int length, int end)
    {
        bool before = at == 0 || !char.IsLetterOrDigit(source[at - 1]);
        bool after = at + length >= end || !char.IsLetterOrDigit(source[at + length]);
        return before || after;
    }

    private void ApplyTag(ReadOnlySpan<char> tag)
    {
        switch (tag)
        {
            case "b": state.Style |= UITextStyle.Bold; return;
            case "/b": state.Style &= ~UITextStyle.Bold; return;
            case "i": state.Style |= UITextStyle.Italic; return;
            case "/i": state.Style &= ~UITextStyle.Italic; return;
            case "u": state.Underline = true; return;
            case "/u": state.Underline = false; return;
            case "s": state.Strike = true; return;
            case "/s": state.Strike = false; return;
            case "code": state.Code = true; return;
            case "/code": state.Code = false; return;

            case "reset" or "/":
                int link = state.Link;
                (int iconsStart, int iconsLength) = (state.IconsStart, state.IconsLength);
                ResetState();
                (state.Link, state.IconsStart, state.IconsLength) = (link, iconsStart, iconsLength);
                return;

            case "/color":
                if (colours.Count > 0) colours.RemoveAt(colours.Count - 1);
                (state.Shade, state.Color) = colours.Count > 0 ? colours[^1] : (Shade.Inherit, default);
                return;

            case "/size":
                if (sizes.Count > 0) sizes.RemoveAt(sizes.Count - 1);
                state.Size = sizes.Count > 0 ? sizes[^1] : 1.0f;
                return;
        }

        ReadOnlySpan<char> value = tag[(tag.IndexOfAny(':', '=') + 1)..].Trim();

        if (tag.StartsWith("color") && TryReadColour(value, out Shade shade, out Vector4 colour))
        {
            colours.Add((shade, colour));
            (state.Shade, state.Color) = (shade, colour);
        }
        else if (tag.StartsWith("size") && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float size) && size > 0.0f)
        {
            sizes.Add(size);
            state.Size = size;
        }
    }

    /// <summary>Reads a colour a tag names, a hex or a word, false for one it doesn't know.</summary>
    private static bool TryReadColour(ReadOnlySpan<char> value, out Shade shade, out Vector4 colour)
    {
        shade = Shade.Explicit;
        colour = Vector4.One;

        if (value.StartsWith("#"))
            return TryReadHex(value[1..], out colour);

        switch (value)
        {
            case "accent": shade = Shade.Accent; return true;
            case "highlight": shade = Shade.Highlight; return true;
            case "text": shade = Shade.Text; return true;
            case "dim": shade = Shade.Dim; return true;
            case "red": colour = new Vector4(0.95f, 0.3f, 0.28f, 1.0f); return true;
            case "green": colour = new Vector4(0.36f, 0.85f, 0.4f, 1.0f); return true;
            case "blue": colour = new Vector4(0.35f, 0.58f, 1.0f, 1.0f); return true;
            case "yellow": colour = new Vector4(1.0f, 0.86f, 0.3f, 1.0f); return true;
            case "orange": colour = new Vector4(1.0f, 0.58f, 0.22f, 1.0f); return true;
            case "purple": colour = new Vector4(0.7f, 0.45f, 0.95f, 1.0f); return true;
            case "cyan": colour = new Vector4(0.3f, 0.88f, 0.95f, 1.0f); return true;
            case "pink": colour = new Vector4(1.0f, 0.5f, 0.75f, 1.0f); return true;
            case "white": colour = Vector4.One; return true;
            case "black": colour = new Vector4(0.0f, 0.0f, 0.0f, 1.0f); return true;
            case "grey" or "gray": colour = new Vector4(0.6f, 0.6f, 0.62f, 1.0f); return true;
        }

        return false;
    }

    private static bool TryReadHex(ReadOnlySpan<char> hex, out Vector4 colour)
    {
        colour = Vector4.One;

        // #rgb is #rrggbb with every digit twice
        Span<char> full = stackalloc char[8];
        int digits;
        if (hex.Length is 3 or 4)
        {
            for (int i = 0; i < hex.Length; i++)
                full[i * 2] = full[i * 2 + 1] = hex[i];
            digits = hex.Length * 2;
        }
        else if (hex.Length is 6 or 8)
        {
            hex.CopyTo(full);
            digits = hex.Length;
        }
        else
        {
            return false;
        }

        if (!uint.TryParse(full[..digits], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
            return false;

        if (digits == 6)
            packed = packed << 8 | 0xFF;

        colour = new Vector4((packed >> 24) & 0xFF, (packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF) / 255.0f;
        return true;
    }

    /* Words onto lines */

    /// <summary>Helper method to put a run of the text, all in the style it is in now, into the word being put together.</summary>
    private void Take(int start, int length)
    {
        if (length <= 0)
            return;

        float scale = baseScale * state.Size;
        ReadOnlySpan<char> icons = state.IconsLength > 0 ? source.AsSpan(state.IconsStart, state.IconsLength) : default;
        float width = font.MeasureLine(source.AsSpan(start, length), scale, state.Style, icons, markup: !state.Code);

        word.Add(new Pending { Start = start, Length = length, Width = width, State = state, Scale = scale });
    }

    /// <summary>
    /// Helper method to put the word that was put together onto the line, or onto the next one if it doesn't fit. A
    /// space is a word of its own, and none goes at the start of a line that was wrapped onto.
    /// </summary>
    private void FlushWord()
    {
        if (word.Count == 0)
            return;

        float width = 0.0f;
        foreach (var part in word)
            width += part.Width;

        bool space = word.Count == 1 && word[0].Length == 1 && source[word[0].Start] == ' ';
        bool lineEmpty = pieces.Count == lineStartPiece || OnlyMarkers();

        if (!space && !lineEmpty && penX + width > lineRoom)
        {
            // The word that didn't fit waits for the next line, the one it didn't fit on ends without it
            FinishLine();
            penX = lineIndent = wrapIndent;
            wrapped = true;
        }

        // The space a line was broken at stays at the end of the line before, not at the start of this one
        if (space && wrapped && pieces.Count == lineStartPiece)
        {
            word.Clear();
            return;
        }

        wrapped = false;

        foreach (var part in word)
            AddPiece(part.Start, part.Length, part.Width, part.State, part.Scale);

        word.Clear();
    }

    // Whether the line has nothing on it but a bullet, which a word still goes next to rather than under
    private bool OnlyMarkers()
    {
        for (int i = lineStartPiece; i < pieces.Count; i++)
        {
            if (pieces[i].Kind == Kind.Text)
                return false;
        }

        return true;
    }

    private void AddPiece(int start, int length, float width, in State style, float scale)
    {
        pieces.Add(new Piece
        {
            Kind = Kind.Text,
            Start = start,
            Length = length,
            Line = lineCount,
            X = penX,
            Width = width,
            Height = font.LineHeight * scale,

            // Measured up from the baseline until the line knows where that is
            Y = -font.Baseline * scale,
            Scale = scale,
            Style = style.Style,
            Markup = !style.Code,
            Shade = style.Shade,
            Color = style.Color,
            Alpha = style.Alpha,
            IconsStart = style.IconsStart,
            IconsLength = style.IconsLength,
            Link = style.Link
        });

        // What is drawn round a piece rather than in it, which the line puts in place with the rest of it. A space is
        // underlined (the line runs on between two words of a link) but has no code behind it
        Piece text = pieces[^1];
        bool blank = length == 1 && source[start] == ' ';

        if (style.Code && !blank)
            pieces.Add(Decoration(text, scale, -font.Baseline * scale, font.LineHeight * scale, Shade.Field, style, padded: true));
        if (style.Underline)
            pieces.Add(Decoration(text, scale, MathF.Round(font.LineHeight * scale * 0.06f), Thickness(scale), style.Shade, style));
        if (style.Strike)
            pieces.Add(Decoration(text, scale, -MathF.Round(font.Baseline * scale * 0.32f), Thickness(scale), style.Shade, style));

        penX += width;
    }

    private float Thickness(float scale) => MathF.Max(1.0f, MathF.Round(font.LineHeight * scale * 0.06f));

    private Piece Decoration(in Piece text, float scale, float y, float height, Shade shade, in State style, bool padded = false)
    {
        float pad = padded ? MathF.Round(font.LineHeight * scale * 0.08f) : 0.0f;

        return new Piece
        {
            Kind = Kind.Box,
            Line = lineCount,
            X = text.X - pad,
            Y = y,
            Width = text.Width + pad * 2.0f,
            Height = height,
            Shade = shade,
            Color = style.Color,
            Alpha = style.Alpha,
            Link = -1
        };
    }

    /// <summary>
    /// Helper method to finish a line. How tall it is comes out of the biggest thing on it, every piece is put on the one
    /// baseline, and a quote gets its bar.
    /// </summary>
    private void EndLine()
    {
        FlushWordWithoutWrapping();
        FinishLine();
    }

    /// <summary>Helper method to finish the line going on with whatever is on it, see <see cref="EndLine"/>.</summary>
    private void FinishLine()
    {

        float baseline = 0.0f, below = 0.0f, width = 0.0f;
        for (int i = lineStartPiece; i < pieces.Count; i++)
        {
            Piece piece = pieces[i];
            if (piece.Kind != Kind.Text)
                continue;

            baseline = MathF.Max(baseline, font.Baseline * piece.Scale);
            below = MathF.Max(below, (font.LineHeight - font.Baseline) * piece.Scale);
            width = MathF.Max(width, piece.X + piece.Width);
        }

        // An empty line is as tall as a line of what it would have been written in
        if (baseline <= 0.0f)
        {
            float scale = baseScale * state.Size;
            baseline = font.Baseline * scale;
            below = (font.LineHeight - font.Baseline) * scale;
        }

        float top = cursorY + pendingGap;
        for (int i = lineStartPiece; i < pieces.Count; i++)
        {
            Piece piece = pieces[i];
            if (piece.Line != lineCount)
                continue;

            // Along to where the room of the line starts, for one that starts after something it wraps round
            piece.Y = top + baseline + piece.Y;
            piece.X += lineShift;
            pieces[i] = piece;
        }

        if (width > 0.0f) width += lineShift;

        if (quoteX >= 0.0f)
        {
            float em = font.LineHeight * baseScale;
            pieces.Add(new Piece
            {
                Kind = Kind.Box,
                Line = lineCount,
                X = quoteX,
                Y = top,
                Width = MathF.Max(2.0f, MathF.Round(em * 0.12f)),
                Height = baseline + below,
                Shade = Shade.Accent,
                Alpha = 1.0f,
                Link = -1
            });
        }

        lines.Add(new LineInfo { Top = top, Height = baseline + below, Baseline = baseline, Width = width });
        cursorY = top + baseline + below;
        pendingGap = 0.0f;
        lineCount++;
        lineStartPiece = pieces.Count;
        wrapped = false;
        StartRoom();
    }

    // The room of the line that starts now, all of the width unless something it wraps round is in the way. A shape that
    // leaves less than a few em beside it is written over, a line of one word a row reads worse than that
    private void StartRoom()
    {
        lineShift = 0.0f;
        lineRoom = maxWidth;
        if (room is null || float.IsPositiveInfinity(maxWidth))
            return;

        float height = font.LineHeight * baseScale;
        room(cursorY + pendingGap, height, out float left, out float right);
        left = MathF.Max(0.0f, left);
        right = MathF.Min(maxWidth, right);
        if (right - left < MIN_ROOM_EM * height)
            return;

        lineShift = left;
        lineRoom = right - left;
    }

    // What is still waiting at the end of a line goes on it whatever its width, there is nowhere else for it
    private void FlushWordWithoutWrapping()
    {
        foreach (var part in word)
            AddPiece(part.Start, part.Length, part.Width, part.State, part.Scale);

        word.Clear();
    }

    /* Painting */

    /// <summary>
    /// Draws what was laid out, lined up inside an area the way a label lines its text up. The block of lines is placed
    /// by the alignment and every line is lined up the same way inside of it.
    /// </summary>
    /// <param name="color">What the text is written in where it says nothing else.</param>
    public void Paint(UIDrawList list, UIRect area, Origin align, Vector4 color)
    {
        Vector2 topLeft = TopLeft(area, align);
        float side = align.ToVector().X;
        UISkin skin = list.Skin;

        // Behind the letters first, the back of the code and the bars, then the letters over them
        for (int pass = 0; pass < 2; pass++)
        {
            Kind kind = pass == 0 ? Kind.Box : Kind.Text;

            for (int i = 0; i < pieces.Count; i++)
            {
                Piece piece = pieces[i];
                if (piece.Kind != kind)
                    continue;

                float shift = piece.Line >= 0 && piece.Line < lines.Count ? (side + 0.5f) * (Width - lines[piece.Line].Width) : 0.0f;
                Vector2 corner = new(topLeft.X + shift + piece.X, topLeft.Y - piece.Y);
                Vector4 ink = ShadeOf(piece, skin, color);

                if (kind == Kind.Box)
                {
                    if (piece.Width > 0.0f && piece.Height > 0.0f)
                        list.Rect(new UIRect(new Vector2(corner.X, corner.Y - piece.Height), new Vector2(corner.X + piece.Width, corner.Y)), ink);
                    continue;
                }

                ReadOnlySpan<char> icons = piece.IconsLength > 0 ? source.AsSpan(piece.IconsStart, piece.IconsLength) : default;
                list.Text(source.AsSpan(piece.Start, piece.Length), corner, piece.Scale, ink, piece.Markup, piece.Style, icons);
            }
        }
    }

    private Vector2 TopLeft(UIRect area, Origin align)
    {
        Vector2 size = new(Width, Height);
        Vector2 center = area.PointAt(align) - align.ToVector() * size;
        return center + new Vector2(-0.5f, 0.5f) * size;
    }

    private static Vector4 ShadeOf(in Piece piece, UISkin skin, Vector4 inherited)
    {
        Vector4 colour = piece.Shade switch
        {
            Shade.Explicit => piece.Color,
            Shade.Accent => skin.AccentColor,
            Shade.Highlight => skin.HighlightColor,
            Shade.Text => skin.TextColor,
            Shade.Dim => skin.TextColor * new Vector4(1.0f, 1.0f, 1.0f, 0.55f),
            Shade.Field => skin.FieldColor.W > 0.0f ? skin.FieldColor : new Vector4(0.0f, 0.0f, 0.0f, 0.3f),
            Shade.Border => skin.BorderColor,
            _ => inherited
        };

        return colour with { W = colour.W * piece.Alpha * (piece.Shade == Shade.Field ? 1.0f : inherited.W) };
    }

    /// <summary>
    /// The link at a point, as where it goes, empty for none. The point is in the same units as the area the text was
    /// painted into, lined up the same way.
    /// </summary>
    public ReadOnlySpan<char> LinkAt(Vector2 point, UIRect area, Origin align)
    {
        Vector2 topLeft = TopLeft(area, align);
        float side = align.ToVector().X;

        foreach (var piece in pieces)
        {
            if (piece.Kind != Kind.Text || piece.Link < 0)
                continue;

            float shift = piece.Line >= 0 && piece.Line < lines.Count ? (side + 0.5f) * (Width - lines[piece.Line].Width) : 0.0f;
            float left = topLeft.X + shift + piece.X, top = topLeft.Y - piece.Y;

            if (point.X >= left && point.X <= left + piece.Width && point.Y <= top && point.Y >= top - piece.Height)
            {
                var (start, length) = links[piece.Link];
                return source.AsSpan(start, length);
            }
        }

        return default;
    }

    /// <summary>Whether any of it is a link.</summary>
    public bool HasLinks => links.Count > 0;
}
