using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>
/// The messages of a stream framed the way the language server protocol and the debug adapter protocol frame them, a
/// Content-Length header, a blank line, the message, read off the stream a buffer at a time. A pipe isn't buffered on
/// Linux and macOS, and the headers were read a byte at a time, a call into the system for every byte of them.
/// Only one thread reads, the connection's reader.
/// </summary>
internal sealed class ContentLengthReader(Stream input)
{
    // A body bigger than this (a whole file a server sent) isn't kept for the next message
    private const int KEEP_MOST = 1 << 20;

    private readonly byte[] buffer = new byte[16384];
    private int start, end;
    private byte[] line = new byte[256];
    private byte[] body = [];

    /// <summary>
    /// The headers of one message to the blank line after them, the length its Content-Length says or -1 for none,
    /// null at the end of the stream. A blank line before any Content-Length is passed over when asked to be, which
    /// is a debugger that writes a line of its own before the first header.
    /// </summary>
    public int? ReadHeaders(bool overBlankLinesFirst)
    {
        int length = -1;
        while (true)
        {
            int count = ReadLine();
            if (count < 0)
                return null;

            // A line to its line feed, the carriage returns at its end let go of
            while (count > 0 && line[count - 1] == '\r')
                count--;
            if (count == 0)
            {
                if (overBlankLinesFirst && length < 0)
                    continue;
                return length;
            }

            if (ContentLength(line.AsSpan(0, count)) is int parsed)
                length = parsed;
        }
    }

    /// <summary>The body of a message as long as its header said, null at the end of the stream, good until the next one is read.</summary>
    public ReadOnlyMemory<byte>? ReadBody(int length)
    {
        if (body.Length < length || body.Length > KEEP_MOST)
            body = new byte[Math.Max(length, Math.Min(body.Length, KEEP_MOST))];

        // What's buffered first, then the rest straight off the stream
        int read = Math.Min(length, end - start);
        buffer.AsSpan(start, read).CopyTo(body);
        start += read;
        while (read < length)
        {
            int got = input.Read(body, read, length - read);
            if (got <= 0)
                return null;
            read += got;
        }

        return body.AsMemory(0, length);
    }

    // The bytes of one line into line, how many there are, -1 at the end of the stream
    private int ReadLine()
    {
        int count = 0;
        while (true)
        {
            if (start == end)
            {
                start = 0;
                end = input.Read(buffer, 0, buffer.Length);
                if (end <= 0)
                {
                    end = 0;
                    return -1;
                }
            }

            ReadOnlySpan<byte> rest = buffer.AsSpan(start, end - start);
            int feed = rest.IndexOf((byte)'\n');
            ReadOnlySpan<byte> part = feed < 0 ? rest : rest[..feed];
            if (count + part.Length > line.Length)
                Array.Resize(ref line, Math.Max(line.Length * 2, count + part.Length));
            part.CopyTo(line.AsSpan(count));
            count += part.Length;

            if (feed >= 0)
            {
                start += feed + 1;
                return count;
            }
            start = end;
        }
    }

    // The length a Content-Length header says, null for another header or an invalid value, parsed directly from UTF-8/ASCII
    // bytes with no string or char-buffer allocation.
    private static int? ContentLength(ReadOnlySpan<byte> header)
    {
        ReadOnlySpan<byte> name = "content-length:"u8;
        if (header.Length < name.Length)
            return null;

        for (int i = 0; i < name.Length; i++)
        {
            byte b = header[i];
            if ((uint)(b - 'A') <= ('Z' - 'A'))
                b |= 0x20;
            if (b != name[i])
                return null;
        }

        ReadOnlySpan<byte> value = header[name.Length..];
        int first = 0;
        while (first < value.Length && IsAsciiWhiteSpace(value[first]))
            first++;
        int last = value.Length - 1;
        while (last >= first && IsAsciiWhiteSpace(value[last]))
            last--;
        if (first > last)
            return null;

        value = value[first..(last + 1)];
        return Utf8Parser.TryParse(value, out int parsed, out int consumed) && consumed == value.Length && parsed >= 0
            ? parsed
            : null;
    }

    private static bool IsAsciiWhiteSpace(byte b) => b == ' ' || (uint)(b - '\t') <= ('\r' - '\t');
}

/// <summary>
/// A message written with its Content-Length header, written node by node (the serializer proper wants reflection,
/// which a trimmed application hasn't got) into a buffer kept for the next one. Whoever writes holds the lock around it.
/// </summary>
internal sealed class ContentLengthWriter(Stream output)
{
    // A buffer grown past this for one big message (a whole file opened) is let go of after it
    private const int KEEP_MOST = 1 << 20;

    private ArrayBufferWriter<byte> buffer = new(4096);
    private Utf8JsonWriter? json;

    /// <summary>Makes the body of a message, what <see cref="Write"/> writes next, given back for a trace.</summary>
    public ReadOnlySpan<byte> Make(JsonNode message)
    {
        buffer.ResetWrittenCount();
        if (json is null)
            json = new Utf8JsonWriter(buffer);
        else
            json.Reset(buffer);
        message.WriteTo(json);
        json.Flush();
        return buffer.WrittenSpan;
    }

    /// <summary>Writes the message made last, header and body, and flushes it.</summary>
    public void Write()
    {
        ReadOnlySpan<byte> body = buffer.WrittenSpan;
        Span<byte> header = stackalloc byte[48];
        "Content-Length: "u8.CopyTo(header);
        body.Length.TryFormat(header[16..], out int digits);
        "\r\n\r\n"u8.CopyTo(header[(16 + digits)..]);

        output.Write(header[..(16 + digits + 4)]);
        output.Write(body);
        output.Flush();
    }

    /// <summary>Lets go of a buffer one big message grew, once it's written.</summary>
    public void Settle()
    {
        if (buffer.Capacity > KEEP_MOST)
        {
            buffer = new ArrayBufferWriter<byte>(4096);
            json?.Dispose();
            json = null;
        }
    }
}
