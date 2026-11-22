using System.Text;
using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

/// <summary>The Content-Length framing language servers and debuggers are talked to in, read and written a buffer at a time.</summary>
public class ContentLengthFramingTests
{
    // A stream that gives back a few bytes a read, the way a pipe gives what has come so far
    private sealed class Trickle(byte[] bytes, int most) : Stream
    {
        private int at;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int got = Math.Min(Math.Min(count, most), bytes.Length - at);
            Array.Copy(bytes, at, buffer, offset, got);
            at += got;
            return got;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string Body(ContentLengthReader reader, bool overBlankLinesFirst = false)
    {
        int? length = reader.ReadHeaders(overBlankLinesFirst);
        Assert.NotNull(length);
        return Encoding.UTF8.GetString(reader.ReadBody(length!.Value)!.Value.Span);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100000)]
    public void Messages_are_read_whole_however_the_bytes_come(int most)
    {
        string big = new('x', 40000);
        string text = "Content-Length: 7\r\n\r\n{\"a\":1}"
            + "content-length:   13  \r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n{\"b\":\"héé\"}"
            + $"Content-Length: {big.Length + 8}\n\n{{\"c\":\"{big}\"}}"
            + "Content-Length: 2\r\r\n\r\n{}";
        var reader = new ContentLengthReader(new Trickle(Encoding.UTF8.GetBytes(text), most));

        Assert.Equal("{\"a\":1}", Body(reader));
        Assert.Equal("{\"b\":\"héé\"}", Body(reader));
        Assert.Equal($"{{\"c\":\"{big}\"}}", Body(reader));
        Assert.Equal("{}", Body(reader));
        Assert.Null(reader.ReadHeaders(false));
    }

    [Fact]
    public void A_message_without_a_length_says_so_and_a_debugger_s_line_before_the_headers_is_passed_over()
    {
        var reader = new ContentLengthReader(new Trickle(Encoding.UTF8.GetBytes("X-Other: 1\r\n\r\n"), 3));
        Assert.Equal(-1, reader.ReadHeaders(false));

        reader = new ContentLengthReader(new Trickle(Encoding.UTF8.GetBytes("\r\n\r\nContent-Length: 2\r\n\r\n{}"), 3));
        Assert.Equal("{}", Body(reader, overBlankLinesFirst: true));
    }

    [Fact]
    public void The_end_of_the_stream_in_a_message_is_the_end()
    {
        var reader = new ContentLengthReader(new Trickle(Encoding.UTF8.GetBytes("Content-Length: 10\r\n\r\n{\"a\""), 4));
        Assert.Equal(10, reader.ReadHeaders(false));
        Assert.Null(reader.ReadBody(10));

        reader = new ContentLengthReader(new Trickle(Encoding.UTF8.GetBytes("Content-Len"), 4));
        Assert.Null(reader.ReadHeaders(false));
    }

    [Fact]
    public void A_message_is_written_with_its_length_in_bytes_and_read_back_the_same()
    {
        var stream = new MemoryStream();
        var writer = new ContentLengthWriter(stream);
        writer.Make(new JsonObject { ["text"] = "héllo" });
        writer.Write();
        writer.Make(new JsonObject { ["n"] = 2 });
        writer.Write();

        string written = Encoding.UTF8.GetString(stream.ToArray());
        Assert.StartsWith($"Content-Length: {Encoding.UTF8.GetByteCount(written[(written.IndexOf("\r\n\r\n") + 4)..written.IndexOf("Content-Length", 5)])}\r\n\r\n", written);
        Assert.EndsWith("Content-Length: 7\r\n\r\n{\"n\":2}", written);

        var reader = new ContentLengthReader(new MemoryStream(stream.ToArray()));
        Assert.Equal("héllo", JsonNode.Parse(Body(reader))!["text"]!.GetValue<string>());
        Assert.Equal(2, JsonNode.Parse(Body(reader))!["n"]!.GetValue<int>());
    }
}
