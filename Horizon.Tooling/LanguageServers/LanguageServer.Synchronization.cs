using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public sealed partial class LanguageServer
{
    // Protected by versions; discarded on close so a reopened file starts with its own text.
    private readonly Dictionary<string, string> synchronizedText = new(Horizon.Core.Platform.PlatformPaths.Comparer);

    private bool IncrementalChanges
    {
        get
        {
            JsonNode? sync = serverCapabilities?["textDocumentSync"];
            return JsonNumbers.Int(sync is JsonObject options ? options["change"] : sync, 0) == 2;
        }
    }

    private JsonObject ContentChange(string path, string text)
    {
        if (!IncrementalChanges) return new JsonObject { ["text"] = text };
        synchronizedText.TryGetValue(path, out string? before);
        synchronizedText[path] = text;
        if (before is null) return new JsonObject { ["text"] = text };

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
