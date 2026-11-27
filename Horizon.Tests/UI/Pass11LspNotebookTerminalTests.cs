using System.Buffers;
using System.IO.Pipes;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Horizon.Rendering;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Regression tests for Pass 11 optimizations: streaming UTF-8 JSON-RPC parsing,
/// LSP background (method, uri) coalescing and string[] line synchronization,
/// whole-pixel translation reuse in <see cref="UIDrawList"/>, terminal vertical resize
/// array recycling, and lossless bounded <see cref="TerminalView"/> backpressure.
/// </summary>
public sealed class Pass11LspNotebookTerminalTests
{
    private static UISkin CreateSkin() =>
        new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));

    private static JsonObject ReadMessage(Stream stream)
    {
        var header = new List<byte>(64);
        int matched = 0;
        while (matched < 4)
        {
            int next = stream.ReadByte();
            if (next < 0)
                throw new EndOfStreamException("Unexpected end of JSON-RPC stream.");
            header.Add((byte)next);
            matched = (matched, (byte)next) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0
            };
        }

        string headers = Encoding.ASCII.GetString(header.ToArray());
        int length = 0;
        foreach (string line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line["Content-Length:".Length..].Trim());
        }

        byte[] payload = new byte[length];
        int read = 0;
        while (read < length)
        {
            int chunk = stream.Read(payload, read, length - read);
            if (chunk <= 0)
                throw new EndOfStreamException("Unexpected end of JSON-RPC payload.");
            read += chunk;
        }
        return JsonNode.Parse(payload)!.AsObject();
    }

    private static void WriteMessage(Stream stream, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    private static string ApplySynchronizedChange(string before, JsonNode change)
    {
        string replacement = change["text"]!.GetValue<string>();
        if (change["range"] is not JsonObject range)
            return replacement;

        int Offset(JsonNode position)
        {
            int line = position["line"]!.GetValue<int>();
            int character = position["character"]!.GetValue<int>();
            int at = 0;
            for (int seen = 0; seen < line; at++)
            {
                Assert.True(at < before.Length);
                if (before[at] == '\r')
                {
                    if (at + 1 < before.Length && before[at + 1] == '\n')
                        at++;
                    seen++;
                }
                else if (before[at] == '\n')
                {
                    seen++;
                }
            }
            return at + character;
        }

        int start = Offset(range["start"]!);
        int end = Offset(range["end"]!);
        Assert.False(start > 0 && start < before.Length && char.IsHighSurrogate(before[start - 1]) && char.IsLowSurrogate(before[start]));
        Assert.False(end > 0 && end < before.Length && char.IsHighSurrogate(before[end - 1]) && char.IsLowSurrogate(before[end]));
        return before[..start] + replacement + before[end..];
    }

    [Fact]
    public async Task JsonRpcConnection_RequestParsed_StreamsRawSliceAndPropagatesRemoteError()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        using var rpc = new JsonRpcConnection(toClientRead, toServer);

        Task<int> sumTask = rpc.RequestParsed("test/sum", new JsonObject { ["n"] = 1 }, static raw =>
        {
            var reader = new Utf8JsonReader(raw.Span);
            int total = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int v))
                    total += v;
            }
            return total;
        });

        JsonObject req1 = ReadMessage(toServerRead);
        Assert.Equal("test/sum", req1["method"]!.GetValue<string>());
        WriteMessage(toClient, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = req1["id"]!.DeepClone(),
            ["result"] = new JsonArray(10, 20, 30, 40)
        }.ToJsonString());

        Assert.Equal(100, await sumTask.WaitAsync(TimeSpan.FromSeconds(5)));

        Task<int> errTask = rpc.RequestParsed("test/fail", null, static _ => 0);
        JsonObject req2 = ReadMessage(toServerRead);
        WriteMessage(toClient, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = req2["id"]!.DeepClone(),
            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method missing" }
        }.ToJsonString());

        var ex = await Assert.ThrowsAsync<JsonRpcConnection.RemoteException>(() => errTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(-32601, ex.Code);
        Assert.Contains("method missing", ex.Message);
    }

    [Fact]
    public async Task LanguageServer_SemanticTokens_StreamsOver4096IntsAndCoalescesHeldRequests()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        using var server = LanguageServer.Over("sem", "fake", Path.GetTempPath(), toClientRead, toServer);

        JsonObject initialize = ReadMessage(toServerRead);
        WriteMessage(toClient, new JsonObject
        {
            ["id"] = initialize["id"]!.DeepClone(),
            ["result"] = new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["textDocumentSync"] = 2,
                    ["semanticTokensProvider"] = new JsonObject
                    {
                        ["full"] = true,
                        ["legend"] = new JsonObject
                        {
                            ["tokenTypes"] = new JsonArray("class", "method"),
                            ["tokenModifiers"] = new JsonArray("static")
                        }
                    }
                }
            }
        }.ToJsonString());
        Assert.Equal("initialized", ReadMessage(toServerRead)["method"]!.GetValue<string>());

        string file = Path.Combine(Path.GetTempPath(), "tokens.fake");
        server.Open(file, new[] { "line0", "line1" });
        Assert.Equal("textDocument/didOpen", ReadMessage(toServerRead)["method"]!.GetValue<string>());

        // Mark server as recently typed so HoldBackground queues background semantic token requests.
        server.Change(file, new[] { "line0!", "line1" });
        Assert.Equal("textDocument/didChange", ReadMessage(toServerRead)["method"]!.GetValue<string>());

        // Fire three duplicate (method, uri) semantic token requests during the typing window.
        Task<IReadOnlyList<SemanticSpan>?> first = server.SemanticTokens(file, 2);
        Task<IReadOnlyList<SemanticSpan>?> second = server.SemanticTokens(file, 2);
        Task<IReadOnlyList<SemanticSpan>?> third = server.SemanticTokens(file, 2);

        // Superseded tasks resolve promptly to null while the latest waits for quiet release.
        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await second.WaitAsync(TimeSpan.FromSeconds(5)));

        JsonObject tokenReq = await Task.Run(() => ReadMessage(toServerRead)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("textDocument/semanticTokens/full", tokenReq["method"]!.GetValue<string>());

        // 1,100 tokens * 5 ints = 5,500 ints (> 4,096 pooled initial rental).
        const int TOKEN_COUNT = 1100;
        var data = new JsonArray();
        for (int i = 0; i < TOKEN_COUNT; i++)
        {
            data.Add(i == 0 ? 0 : 1);
            data.Add(0);
            data.Add(3);
            data.Add(i % 2);
            data.Add(i % 2);
        }

        WriteMessage(toClient, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = tokenReq["id"]!.DeepClone(),
            ["result"] = new JsonObject { ["data"] = data }
        }.ToJsonString());

        IReadOnlyList<SemanticSpan>? spans = await third.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(spans);
        Assert.Equal(TOKEN_COUNT, spans.Count);
        Assert.Equal(0, spans[0].Line);
        Assert.Equal(TOKEN_COUNT - 1, spans[^1].Line);
    }

    [Fact]
    public void LanguageServer_StringArrayOpenAndChange_ComputesExactIncrementalRanges()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        using var server = LanguageServer.Over("lines-sync", "fake", Path.GetTempPath(), toClientRead, toServer);

        JsonObject initialize = ReadMessage(toServerRead);
        WriteMessage(toClient, new JsonObject
        {
            ["id"] = initialize["id"]!.DeepClone(),
            ["result"] = new JsonObject { ["capabilities"] = new JsonObject { ["textDocumentSync"] = 2 } }
        }.ToJsonString());
        Assert.Equal("initialized", ReadMessage(toServerRead)["method"]!.GetValue<string>());

        string file = Path.Combine(Path.GetTempPath(), "lines.fake");
        string[] initialLines = ["alpha", "beta😀gamma", "delta"];
        server.Open(file, initialLines);
        JsonObject opened = ReadMessage(toServerRead);
        string reconstructed = opened["params"]!["textDocument"]!["text"]!.GetValue<string>();
        Assert.Equal("alpha\nbeta😀gamma\ndelta", reconstructed);

        string[][] edits =
        [
            ["alpha", "beta😁gamma", "delta"],          // single-line surrogate edit
            ["alpha", "beta😁gamma", "inserted", "delta"], // middle line insert
            ["alpha", "delta"],                         // multi-line delete
            ["alpha", "delta", "tail1", "tail2"],       // EOF append
            ["alpha"],                                  // EOF truncate
            [],                                         // delete all lines
            ["reborn"]                                  // append from empty
        ];

        foreach (string[] nextLines in edits)
        {
            server.Change(file, nextLines);
            JsonObject changed = ReadMessage(toServerRead);
            JsonNode change = Assert.Single(changed["params"]!["contentChanges"]!.AsArray())!;
            Assert.IsType<JsonObject>(change["range"]);
            reconstructed = ApplySynchronizedChange(reconstructed, change);
            Assert.Equal(string.Join('\n', nextLines), reconstructed);
        }

        // Verify string overload interoperates seamlessly after array-based sync.
        server.Change(file, "reborn\nfinal");
        JsonObject stringChange = ReadMessage(toServerRead);
        reconstructed = ApplySynchronizedChange(reconstructed, stringChange["params"]!["contentChanges"]![0]!);
        Assert.Equal("reborn\nfinal", reconstructed);
    }

    private sealed class PaintHarnessControl : UIComponent
    {
        protected override void Paint(UIDrawList list)
        {
            UIRect b = Bounds;
            list.RoundRect(b, 6.0f, new Vector4(0.2f, 0.3f, 0.4f, 1.0f));
            list.PushClip(new UIRect(b.Min + new Vector2(4.0f, 4.0f), b.Max - new Vector2(4.0f, 4.0f)));
            list.Rect(new UIRect(b.Min + new Vector2(8.0f, 8.0f), b.Min + new Vector2(40.0f, 24.0f)), new Vector4(0.9f, 0.8f, 0.1f, 1.0f));
            list.Text("Hello", new Vector2(b.Min.X + 10.0f, b.Min.Y + 36.0f), 0.15f, Vector4.One, false, UITextStyle.Italic, default);
            list.PopClip();
        }
    }

    [Fact]
    public void UIDrawList_Reuse_RebasesWholePixelTranslationAndRejectsSubpixelShift()
    {
        var skin = CreateSkin();
        var list1 = new UIDrawList();
        var list2 = new UIDrawList { Previous = list1 };
        var expected = new UIDrawList();

        var control = new PaintHarnessControl();
        var bounds1 = new UIRect(new Vector2(10.0f, 20.0f), new Vector2(210.0f, 120.0f));
        var bounds2 = new UIRect(new Vector2(34.0f, 48.0f), new Vector2(234.0f, 148.0f));
        var boundsSubpixel = new UIRect(new Vector2(34.25f, 48.0f), new Vector2(234.25f, 148.0f));

        static void PaintAt(UIDrawList list, PaintHarnessControl owner, UIRect bounds)
        {
            list.PushOwner(owner);
            list.RoundRect(bounds, 6.0f, new Vector4(0.2f, 0.3f, 0.4f, 1.0f));
            list.PushClip(new UIRect(bounds.Min + new Vector2(4.0f, 4.0f), bounds.Max - new Vector2(4.0f, 4.0f)));
            list.RoundRect(new UIRect(bounds.Min + new Vector2(6.0f, 6.0f), bounds.Min + new Vector2(60.0f, 40.0f)), 5.0f, new Vector4(0.5f, 0.2f, 0.7f, 1.0f));
            list.Rect(new UIRect(bounds.Min + new Vector2(8.0f, 8.0f), bounds.Min + new Vector2(40.0f, 24.0f)), new Vector4(0.9f, 0.8f, 0.1f, 1.0f));
            list.Text("Hello", new Vector2(bounds.Min.X + 10.0f, bounds.Min.Y + 36.0f), 0.15f, Vector4.One, false, UITextStyle.Italic, default);
            list.PopClip();
            list.PopOwner();
        }

        // First paint at bounds1 (record).
        list1.Begin(skin, frame: 1, dt: 0.016f);
        int ctx1 = list1.PaintContext(bounds1);
        Assert.False(list1.TryReuse(control.PaintId, 1, ctx1, bounds1));
        list1.BeginRecord(bounds1);
        PaintAt(list1, control, bounds1);
        list1.EndRecord(control.PaintId, 1, ctx1, control);
        list1.End();

        // Reference direct paint at bounds2 (whole-pixel shifted).
        expected.Begin(skin, frame: 2, dt: 0.016f);
        PaintAt(expected, control, bounds2);
        expected.End();

        // Reused paint at bounds2 must hit whole-pixel translation rebase and match direct paint within sub-hundredth pixel ULP tolerance.
        list2.Begin(skin, frame: 2, dt: 0.016f);
        int ctx2 = list2.PaintContext(bounds2);
        Assert.NotEqual(ctx1, ctx2);
        Assert.True(list2.TryReuse(control.PaintId, 1, ctx2, bounds2));
        Assert.Equal(1, list2.Reused);
        list2.End();

        ReadOnlySpan<SpriteItem> actualItems = list2.Items;
        ReadOnlySpan<SpriteItem> expectedItems = expected.Items;
        Assert.Equal(expectedItems.Length, actualItems.Length);
        for (int i = 0; i < actualItems.Length; i++)
        {
            SpriteItem a = actualItems[i];
            SpriteItem b = expectedItems[i];
            Assert.Equal(b.Flags, a.Flags);
            Assert.Equal(b.Color, a.Color);
            Assert.Equal(b.Ring, a.Ring);
            Assert.Equal(b.TexMin, a.TexMin);
            Assert.True(Vector2.Distance(b.Origin, a.Origin) <= 0.002f);
            Assert.True(Vector2.Distance(b.AxisX, a.AxisX) <= 0.002f);
            Assert.True(Vector2.Distance(b.AxisY, a.AxisY) <= 0.002f);
            Assert.True(Vector2.Distance(b.TexMax, a.TexMax) <= 0.002f);
            Assert.True(Vector2.Distance(b.Key, a.Key) <= 0.002f);
        }
        Assert.Null(list2.FramesDiffer());

        // CheckReuse parity pass: must validate translated paint with no warnings/throws.
        var listChecked = new UIDrawList { Previous = list1 };
        bool prevCheck = UIDrawList.CheckReuse;
        try
        {
            UIDrawList.CheckReuse = true;
            listChecked.Begin(skin, frame: 2, dt: 0.016f);
            int ctxChecked = listChecked.PaintContext(bounds2);
            Assert.False(listChecked.TryReuse(control.PaintId, 1, ctxChecked, bounds2));
            listChecked.BeginRecord(bounds2);
            PaintAt(listChecked, control, bounds2);
            listChecked.EndRecord(control.PaintId, 1, ctxChecked, control);
            listChecked.End();
        }
        finally
        {
            UIDrawList.CheckReuse = prevCheck;
        }

        // Sub-pixel layout translation must be rejected to keep glyph snapping accurate.
        var list3 = new UIDrawList { Previous = list2 };
        list3.Begin(skin, frame: 3, dt: 0.016f);
        int ctx3 = list3.PaintContext(boundsSubpixel);
        Assert.False(list3.TryReuse(control.PaintId, 1, ctx3, boundsSubpixel));
        list3.End();
    }

    [Fact]
    public void TerminalScreen_VerticalResize_RecyclesRowsAndPreservesAlternateGrid()
    {
        var screen = new TerminalScreen(20, 8);
        screen.Feed("line1\r\nline2\r\nline3\r\n");
        screen.Feed("\u001b[?1049halt-row-0\r\nalt-row-1");
        Assert.True(screen.AlternateScreen);

        // Shrink and grow height without changing width (vertical resize recycling path).
        screen.Resize(20, 5);
        Assert.Equal(5, screen.Rows);
        Assert.StartsWith("alt-row-0", screen.Grid[0].Text());
        Assert.StartsWith("alt-row-1", screen.Grid[1].Text());

        screen.Resize(20, 10);
        Assert.Equal(10, screen.Rows);
        Assert.StartsWith("alt-row-0", screen.Grid[0].Text());
        Assert.StartsWith("alt-row-1", screen.Grid[1].Text());

        // Exit alternate screen and check main screen content remained intact.
        screen.Feed("\u001b[?1049l");
        Assert.False(screen.AlternateScreen);
        string mainText = screen.Text();
        Assert.Contains("line1", mainText);
        Assert.Contains("line3", mainText);
    }

    [Fact]
    public async Task TerminalView_ArrivedPooled_BackpressurePreservesEveryChunk()
    {
        using var view = new TerminalView();
        var received = new StringBuilder();
        view.OnOutput = bytes => received.Append(Encoding.ASCII.GetString(bytes));

        const int CHUNK_COUNT = 4600; // Exceeds MAX_PENDING_CHUNKS = 4096 to trigger bounded Wait/PulseAll backpressure.
        var producer = Task.Run(() =>
        {
            for (int i = 0; i < CHUNK_COUNT; i++)
            {
                byte[] text = Encoding.ASCII.GetBytes($"[{i}]");
                byte[] rented = ArrayPool<byte>.Shared.Rent(text.Length);
                Buffer.BlockCopy(text, 0, rented, 0, text.Length);
                view.ArrivedPooled(rented, text.Length);
            }
        });

        int expected = 0;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((!producer.IsCompleted || received.Length == 0 || expected < CHUNK_COUNT) && DateTime.UtcNow < deadline)
        {
            view.UpdateTree(0.016f);
            string current = received.ToString();
            if (current.Contains($"[{CHUNK_COUNT - 1}]", StringComparison.Ordinal))
            {
                expected = CHUNK_COUNT;
                break;
            }
            await Task.Delay(1);
        }

        await producer.WaitAsync(TimeSpan.FromSeconds(5));
        view.UpdateTree(0.016f);

        string full = received.ToString();
        Assert.Contains("[0][1]", full);
        Assert.Contains($"[{CHUNK_COUNT - 2}][{CHUNK_COUNT - 1}]", full);
    }
}
