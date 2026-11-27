using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public sealed partial class LanguageServer
{
    // Protected by versions; discarded on close so a reopened file starts with its own text.
    private readonly Dictionary<string, string> synchronizedText = new(Horizon.Core.Platform.PlatformPaths.Comparer);
    private readonly Dictionary<string, string[]> synchronizedLines = new(Horizon.Core.Platform.PlatformPaths.Comparer);
    private volatile bool incrementalChanges;

    private bool IncrementalChanges => incrementalChanges;

    private void UpdateSyncCapabilities()
    {
        JsonNode? sync = serverCapabilities?["textDocumentSync"];
        incrementalChanges = JsonNumbers.Int(sync is JsonObject options ? options["change"] : sync, 0) == 2;
    }

    private JsonObject ContentChange(string path, string text)
    {
        if (!IncrementalChanges) return new JsonObject { ["text"] = text };
        if (!synchronizedText.TryGetValue(path, out string? before) && synchronizedLines.Remove(path, out string[]? beforeLines))
            before = string.Join('\n', beforeLines);
        else
            synchronizedLines.Remove(path);
        synchronizedText[path] = text;
        if (before is null) return new JsonObject { ["text"] = text };
        return TextContentChange(before, text);
    }

    private JsonObject ContentChange(string path, string[] lines)
    {
        if (!IncrementalChanges) return new JsonObject { ["text"] = string.Join('\n', lines) };
        if (!synchronizedLines.TryGetValue(path, out string[]? beforeLines))
        {
            synchronizedLines[path] = lines;
            if (synchronizedText.Remove(path, out string? beforeText))
                return TextContentChange(beforeText, string.Join('\n', lines));
            return new JsonObject { ["text"] = string.Join('\n', lines) };
        }
        synchronizedLines[path] = lines;
        return LinesContentChange(beforeLines, lines);
    }

    private static JsonObject TextContentChange(string before, string text)
    {
        int start = before.AsSpan().CommonPrefixLength(text.AsSpan());
        if (SplitsCharacter(before, start) || SplitsCharacter(text, start)) start--;
        int end = before.Length, newEnd = text.Length;
        while (end > start && newEnd > start && before[end - 1] == text[newEnd - 1])
        {
            end--;
            newEnd--;
        }
        if (SplitsCharacter(before, end) || SplitsCharacter(text, newEnd))
        {
            end++;
            newEnd++;
        }

        return new JsonObject
        {
            ["range"] = new JsonObject { ["start"] = SyncPosition(before, start), ["end"] = SyncPosition(before, end) },
            ["text"] = text[start..newEnd]
        };
    }

    private static JsonObject LinesContentChange(string[] before, string[] after)
    {
        int limit = Math.Min(before.Length, after.Length);
        int top = 0;
        while (top < limit && (ReferenceEquals(before[top], after[top]) || string.Equals(before[top], after[top], StringComparison.Ordinal)))
            top++;

        if (top == before.Length && top == after.Length)
        {
            JsonObject eof = before.Length == 0
                ? new JsonObject { ["line"] = 0, ["character"] = 0 }
                : new JsonObject { ["line"] = before.Length - 1, ["character"] = before[^1].Length };
            return new JsonObject
            {
                ["range"] = new JsonObject { ["start"] = eof, ["end"] = eof.DeepClone() },
                ["text"] = string.Empty
            };
        }

        int bEnd = before.Length, aEnd = after.Length;
        while (bEnd > top && aEnd > top
            && (ReferenceEquals(before[bEnd - 1], after[aEnd - 1]) || string.Equals(before[bEnd - 1], after[aEnd - 1], StringComparison.Ordinal)))
        {
            bEnd--;
            aEnd--;
        }

        // Single-line edit (the overwhelming typing hot path): diff just that line by character.
        if (bEnd == top + 1 && aEnd == top + 1)
        {
            string beforeLine = before[top], afterLine = after[top];
            int start = beforeLine.AsSpan().CommonPrefixLength(afterLine.AsSpan());
            if (SplitsCharacter(beforeLine, start) || SplitsCharacter(afterLine, start)) start--;
            int end = beforeLine.Length, newEnd = afterLine.Length;
            while (end > start && newEnd > start && beforeLine[end - 1] == afterLine[newEnd - 1])
            {
                end--;
                newEnd--;
            }
            if (SplitsCharacter(beforeLine, end) || SplitsCharacter(afterLine, newEnd))
            {
                end++;
                newEnd++;
            }

            return new JsonObject
            {
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = top, ["character"] = start },
                    ["end"] = new JsonObject { ["line"] = top, ["character"] = end }
                },
                ["text"] = afterLine[start..newEnd]
            };
        }

        if (bEnd < before.Length)
        {
            string inserted = aEnd > top ? string.Join('\n', after, top, aEnd - top) + "\n" : string.Empty;
            return new JsonObject
            {
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = top, ["character"] = 0 },
                    ["end"] = new JsonObject { ["line"] = bEnd, ["character"] = 0 }
                },
                ["text"] = inserted
            };
        }

        // Change reaches EOF: since suffix loop stripped identical trailing count k from both, aEnd == after.Length here too.
        if (bEnd == top)
        {
            JsonObject eofStart = before.Length == 0
                ? new JsonObject { ["line"] = 0, ["character"] = 0 }
                : new JsonObject { ["line"] = before.Length - 1, ["character"] = before[^1].Length };
            string appended = before.Length == 0
                ? string.Join('\n', after, top, aEnd - top)
                : "\n" + string.Join('\n', after, top, aEnd - top);
            return new JsonObject
            {
                ["range"] = new JsonObject { ["start"] = eofStart, ["end"] = eofStart.DeepClone() },
                ["text"] = appended
            };
        }

        JsonObject eofEnd = before.Length == 0
            ? new JsonObject { ["line"] = 0, ["character"] = 0 }
            : new JsonObject { ["line"] = before.Length - 1, ["character"] = before[^1].Length };
        if (aEnd == top)
        {
            JsonObject delStart = after.Length == 0
                ? new JsonObject { ["line"] = 0, ["character"] = 0 }
                : new JsonObject { ["line"] = top - 1, ["character"] = before[top - 1].Length };
            return new JsonObject
            {
                ["range"] = new JsonObject { ["start"] = delStart, ["end"] = eofEnd },
                ["text"] = string.Empty
            };
        }

        return new JsonObject
        {
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = top, ["character"] = 0 },
                ["end"] = eofEnd
            },
            ["text"] = string.Join('\n', after, top, aEnd - top)
        };
    }

    // A range must not start or end halfway through a surrogate pair or CRLF line ending.
    private static bool SplitsCharacter(string text, int at) => at > 0 && at < text.Length
        && (text[at - 1] == '\r' && text[at] == '\n' || char.IsHighSurrogate(text[at - 1]) && char.IsLowSurrogate(text[at]));

    private static JsonObject SyncPosition(string text, int offset)
    {
        ReadOnlySpan<char> prefix = text.AsSpan(0, offset);
        int line, character;
        if (!prefix.Contains('\r'))
        {
            line = prefix.Count('\n');
            character = offset - prefix.LastIndexOf('\n') - 1;
        }
        else
        {
            line = character = 0;
            for (int i = 0; i < offset; i++)
            {
                if (text[i] == '\r' || text[i] == '\n')
                {
                    if (text[i] == '\r' && i + 1 < offset && text[i + 1] == '\n') i++;
                    line++;
                    character = 0;
                }
                else character++;
            }
        }
        return new JsonObject { ["line"] = line, ["character"] = character };
    }
}
