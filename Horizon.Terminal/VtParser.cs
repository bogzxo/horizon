using System.Runtime.InteropServices;
using System.Text;

namespace Horizon.UI.Components;

/// <summary>
/// The parameters of a control sequence, CSI 1;2;3 m and the colon ones of SGR 38:2:r:g:b, as the parser read them.
/// A parameter that wasn't written is 0 (or whatever default is asked for), and <see cref="Sub"/> says whether a
/// parameter followed a colon rather than a semicolon, which is what tells 4:3 (a curly underline) from 4;3.
/// </summary>
public sealed class VtParams
{
    private const int MAX = 32;

    private readonly int[] values = new int[MAX];
    private readonly bool[] sub = new bool[MAX];

    public int Count { get; private set; }

    public int this[int index] => index < Count ? values[index] : 0;

    /// <summary>The parameter at an index, or a default when it wasn't written or was written as 0.</summary>
    public int At(int index, int otherwise) => index < Count && values[index] != 0 ? values[index] : otherwise;

    /// <summary>Whether the parameter at an index followed a colon.</summary>
    public bool Sub(int index) => index < Count && sub[index];

    internal void Clear()
    {
        Count = 0;
        Array.Clear(sub);
    }

    internal void Begin(bool colon)
    {
        if (Count < MAX)
        {
            values[Count] = 0;
            sub[Count] = colon;
            Count++;
        }
    }

    internal void Digit(int digit)
    {
        if (Count == 0)
            Begin(false);
        if (Count <= MAX)
        {
            int at = Count - 1;
            values[at] = Math.Min(65535, values[at] * 10 + digit);
        }
    }
}

/// <summary>What a <see cref="VtParser"/> tells about the bytes it reads.</summary>
public interface IVtHandler
{
    /// <summary>A character to put on the screen, as a code point.</summary>
    void Print(int codePoint);

    /// <summary>A contiguous run of printable ASCII bytes (0x20..0x7E) to put on the screen.</summary>
    void PrintAscii(ReadOnlySpan<byte> ascii)
    {
        foreach (byte b in ascii)
            Print(b);
    }

    /// <summary>A C0 control, bell, backspace, tab, line feed, carriage return and the rest.</summary>
    void Execute(byte control);

    /// <summary>An escape sequence, ESC and a final byte with intermediates before it (ESC 7, ESC ( 0).</summary>
    void Escape(ReadOnlySpan<byte> intermediates, byte final);

    /// <summary>A control sequence, CSI with its parameters, a private prefix (? > = &lt; or 0), intermediates and the final byte.</summary>
    void Csi(VtParams parameters, byte prefix, ReadOnlySpan<byte> intermediates, byte final);

    /// <summary>An operating system command, OSC and what was between it and its end, "0;title", "8;;url".</summary>
    void Osc(ReadOnlySpan<char> text);

    /// <summary>A device control string, DCS, its parameters and its body, passed on for whoever wants sixels one day.</summary>
    void Dcs(VtParams parameters, ReadOnlySpan<byte> intermediates, byte final, ReadOnlySpan<byte> body);
}

/// <summary>
/// The state machine of a DEC terminal, the one Paul Williams wrote down (vt100.net/emu/dec_ansi_parser) and xterm,
/// Alacritty, Kitty and Windows Terminal all follow, so what they do with a byte this does too, the strange cases
/// included (a CAN in the middle of a sequence throws it away, an ESC in an OSC ends it). Bytes go in, UTF-8 is put
/// back together on the way, and what they mean comes out as calls on an <see cref="IVtHandler"/>. It keeps no screen,
/// <see cref="TerminalScreen"/> is the handler that does.
/// </summary>
public sealed class VtParser
{
    private enum State { Ground, Escape, EscapeIntermediate, CsiEntry, CsiParam, CsiIntermediate, CsiIgnore, OscString, DcsEntry, DcsParam, DcsIntermediate, DcsPassthrough, DcsIgnore, SosPmApcString }

    private const int MAX_INTERMEDIATES = 4, MAX_OSC = 65536, MAX_DCS = 1 << 20;

    private readonly IVtHandler handler;
    private readonly VtParams parameters = new();
    private readonly byte[] intermediates = new byte[MAX_INTERMEDIATES];
    private readonly StringBuilder osc = new();
    private readonly List<byte> dcs = [];
    private State state = State.Ground;
    private int intermediateCount;
    private byte prefix;
    private byte dcsFinal;

    // A UTF-8 character on its way in, how many bytes it still wants, what it is so far, and valid next-byte bounds
    private int pendingBytes, pending;
    private byte utf8Lower = 0x80, utf8Upper = 0xBF;

    // Whether the previous byte in an OSC/DCS/SOS/PM/APC string was ESC (0x1B), which may be followed by '\' (ST)
    private bool stringEscape;

    public VtParser(IVtHandler handler)
    {
        this.handler = handler;
    }

    /// <summary>Reads bytes, as many as there are, and tells the handler what they meant.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        int i = 0;
        int length = bytes.Length;
        while (i < length)
        {
            // Fast path for contiguous printable ASCII runs in Ground state with no pending UTF-8 bytes
            if (state == State.Ground && pendingBytes == 0)
            {
                ReadOnlySpan<byte> remaining = bytes.Slice(i);
                int run = remaining.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E);
                if (run < 0)
                {
                    handler.PrintAscii(remaining);
                    return;
                }
                if (run > 0)
                {
                    handler.PrintAscii(remaining.Slice(0, run));
                    i += run;
                }
            }

            Feed(bytes[i++]);
        }
    }

    public void Feed(byte b)
    {
        // UTF-8 is only text, it goes together in the ground state and in an OSC string
        if (pendingBytes > 0)
        {
            if (b >= utf8Lower && b <= utf8Upper)
            {
                pending = (pending << 6) | (b & 0x3F);
                utf8Lower = 0x80;
                utf8Upper = 0xBF;
                if (--pendingBytes == 0)
                    Character(pending);
                return;
            }

            // Not a valid continuation after all: reset decoder, emit U+FFFD, and reprocess this byte anew
            pendingBytes = 0;
            pending = 0;
            utf8Lower = 0x80;
            utf8Upper = 0xBF;
            Character(0xFFFD);
        }

        // Complete a 7-bit String Terminator (ESC \) or transition to Escape if ESC was followed by anything else
        if (stringEscape)
        {
            stringEscape = false;
            FinishString();
            if (b == 0x5C)
                return;
            Enter(State.Escape);
        }

        // C1 String Terminator (0x9C) terminates strings and aborts in-flight escape/CSI sequences
        if (b == 0x9C && state != State.Ground)
        {
            FinishString();
            return;
        }

        if (b >= 0x80 && state is State.Ground or State.OscString)
        {
            if (b is >= 0xC2 and <= 0xDF) { pending = b & 0x1F; pendingBytes = 1; utf8Lower = 0x80; utf8Upper = 0xBF; }
            else if (b == 0xE0) { pending = 0; pendingBytes = 2; utf8Lower = 0xA0; utf8Upper = 0xBF; }
            else if (b is (>= 0xE1 and <= 0xEC) or (>= 0xEE and <= 0xEF)) { pending = b & 0x0F; pendingBytes = 2; utf8Lower = 0x80; utf8Upper = 0xBF; }
            else if (b == 0xED) { pending = b & 0x0F; pendingBytes = 2; utf8Lower = 0x80; utf8Upper = 0x9F; }
            else if (b == 0xF0) { pending = 0; pendingBytes = 3; utf8Lower = 0x90; utf8Upper = 0xBF; }
            else if (b is >= 0xF1 and <= 0xF3) { pending = b & 0x07; pendingBytes = 3; utf8Lower = 0x80; utf8Upper = 0xBF; }
            else if (b == 0xF4) { pending = b & 0x07; pendingBytes = 3; utf8Lower = 0x80; utf8Upper = 0x8F; }
            else Character(0xFFFD);
            return;
        }

        // Anywhere, these start over
        switch (b)
        {
            case 0x1B:
                if (state is State.OscString or State.DcsEntry or State.DcsParam or State.DcsIntermediate or State.DcsPassthrough or State.DcsIgnore or State.SosPmApcString)
                {
                    stringEscape = true;
                    return;
                }
                Enter(State.Escape);
                return;
            case 0x18 or 0x1A:
                handler.Execute(b);
                Enter(State.Ground);
                return;
        }

        switch (state)
        {
            case State.Ground:
                if (b < 0x20 || b == 0x7F) handler.Execute(b);
                else handler.Print(b);
                break;

            case State.Escape:
                if (b < 0x20) { handler.Execute(b); break; }
                if (b <= 0x2F) { Collect(b); state = State.EscapeIntermediate; break; }
                switch (b)
                {
                    case 0x5B: Enter(State.CsiEntry); break;
                    case 0x5D: Enter(State.OscString); break;
                    case 0x50: Enter(State.DcsEntry); break;
                    case 0x58 or 0x5E or 0x5F: Enter(State.SosPmApcString); break;
                    default:
                        if (b != 0x7F)
                            handler.Escape(intermediates.AsSpan(0, intermediateCount), b);
                        state = State.Ground;
                        break;
                }
                break;

            case State.EscapeIntermediate:
                if (b < 0x20) { handler.Execute(b); break; }
                if (b <= 0x2F) { Collect(b); break; }
                if (b != 0x7F)
                    handler.Escape(intermediates.AsSpan(0, intermediateCount), b);
                state = State.Ground;
                break;

            case State.CsiEntry:
                if (b < 0x20) { handler.Execute(b); break; }
                if (b <= 0x2F) { Collect(b); state = State.CsiIntermediate; break; }
                if (b <= 0x39) { parameters.Digit(b - 0x30); state = State.CsiParam; break; }
                if (b == 0x3A) { parameters.Begin(true); state = State.CsiParam; break; }
                if (b == 0x3B) { parameters.Begin(false); parameters.Begin(false); state = State.CsiParam; break; }
                if (b <= 0x3F) { prefix = b; state = State.CsiParam; break; }
                if (b == 0x7F) break;
                Dispatch(b);
                break;

            case State.CsiParam:
                if (b < 0x20) { handler.Execute(b); break; }
                if (b <= 0x2F) { Collect(b); state = State.CsiIntermediate; break; }
                if (b <= 0x39) { parameters.Digit(b - 0x30); break; }
                if (b == 0x3A) { parameters.Begin(true); break; }
                if (b == 0x3B) { if (parameters.Count == 0) parameters.Begin(false); parameters.Begin(false); break; }
                if (b <= 0x3F) { state = State.CsiIgnore; break; }
                if (b == 0x7F) break;
                Dispatch(b);
                break;

            case State.CsiIntermediate:
                if (b < 0x20) { handler.Execute(b); break; }
                if (b <= 0x2F) { Collect(b); break; }
                if (b <= 0x3F) { state = State.CsiIgnore; break; }
                if (b == 0x7F) break;
                Dispatch(b);
                break;

            case State.CsiIgnore:
                if (b < 0x20) { handler.Execute(b); break; }
                if (b >= 0x40 && b <= 0x7E) state = State.Ground;
                break;

            case State.OscString:
                if (b == 0x07) { EndOsc(); break; }
                if (b >= 0x20 && osc.Length < MAX_OSC) osc.Append((char)b);
                break;

            case State.DcsEntry:
                if (b < 0x20 || b == 0x7F) break;
                if (b <= 0x2F) { Collect(b); state = State.DcsIntermediate; break; }
                if (b <= 0x39) { parameters.Digit(b - 0x30); state = State.DcsParam; break; }
                if (b == 0x3A) { parameters.Begin(true); state = State.DcsParam; break; }
                if (b == 0x3B) { parameters.Begin(false); parameters.Begin(false); state = State.DcsParam; break; }
                if (b <= 0x3F) { prefix = b; state = State.DcsParam; break; }
                dcsFinal = b;
                state = State.DcsPassthrough;
                break;

            case State.DcsParam:
                if (b < 0x20 || b == 0x7F) break;
                if (b <= 0x2F) { Collect(b); state = State.DcsIntermediate; break; }
                if (b <= 0x39) { parameters.Digit(b - 0x30); break; }
                if (b == 0x3A) { parameters.Begin(true); break; }
                if (b == 0x3B) { if (parameters.Count == 0) parameters.Begin(false); parameters.Begin(false); break; }
                if (b <= 0x3F) { state = State.DcsIgnore; break; }
                dcsFinal = b;
                state = State.DcsPassthrough;
                break;

            case State.DcsIntermediate:
                if (b < 0x20 || b == 0x7F) break;
                if (b <= 0x2F) { Collect(b); break; }
                if (b <= 0x3F) { state = State.DcsIgnore; break; }
                dcsFinal = b;
                state = State.DcsPassthrough;
                break;

            case State.DcsPassthrough:
                if (b != 0x7F && dcs.Count < MAX_DCS) dcs.Add(b);
                break;

            case State.DcsIgnore:
            case State.SosPmApcString:
                break;
        }
    }

    // A whole character, printed or kept in the string being read
    private void Character(int codePoint)
    {
        switch (state)
        {
            case State.OscString:
                if (osc.Length < MAX_OSC)
                {
                    int character = codePoint <= 0x10FFFF && codePoint is not (>= 0xD800 and <= 0xDFFF) ? codePoint : 0xFFFD;
                    // A string a character was made for each one of a title or a path, the plane's own chars written as they are
                    if (character <= 0xFFFF)
                        osc.Append((char)character);
                    else
                        osc.Append(char.ConvertFromUtf32(character));
                }
                break;
            case State.DcsPassthrough:
                break;
            case State.SosPmApcString:
                break;
            default:
                handler.Print(codePoint <= 0x10FFFF && codePoint is not (>= 0xD800 and <= 0xDFFF) ? codePoint : 0xFFFD);
                break;
        }
    }

    private void Enter(State next)
    {
        state = next;
        if (next is State.Escape or State.CsiEntry or State.DcsEntry or State.OscString or State.SosPmApcString or State.Ground)
        {
            parameters.Clear();
            intermediateCount = 0;
            prefix = 0;
            osc.Clear();
            dcs.Clear();
            stringEscape = false;
            pendingBytes = 0;
            pending = 0;
            utf8Lower = 0x80;
            utf8Upper = 0xBF;
        }
    }

    private void FinishString()
    {
        if (state == State.OscString)
        {
            EndOsc();
        }
        else if (state == State.DcsPassthrough)
        {
            EndDcs();
            Enter(State.Ground);
        }
        else
        {
            Enter(State.Ground);
        }
    }

    private void Collect(byte b)
    {
        if (intermediateCount < MAX_INTERMEDIATES)
            intermediates[intermediateCount++] = b;
    }

    private void Dispatch(byte final)
    {
        handler.Csi(parameters, prefix, intermediates.AsSpan(0, intermediateCount), final);
        state = State.Ground;
    }

    private void EndOsc()
    {
        Span<char> buffer = osc.Length <= 1024 ? stackalloc char[osc.Length] : new char[osc.Length];
        osc.CopyTo(0, buffer, osc.Length);
        handler.Osc(buffer);
        osc.Clear();
        state = State.Ground;
    }

    private void EndDcs()
    {
        handler.Dcs(parameters, intermediates.AsSpan(0, intermediateCount), dcsFinal, CollectionsMarshal.AsSpan(dcs));
        dcs.Clear();
    }
}
