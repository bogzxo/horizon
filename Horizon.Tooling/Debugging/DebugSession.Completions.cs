using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public readonly record struct DebugCompletion(string Label, string Text, string Detail, string Kind, int Start, int Length, int SelectionStart, int SelectionLength);

public sealed partial class DebugSession
{
    private string completionAdapter = CORECLR;
    public bool CanComplete => Can("supportsCompletionsRequest");

    /// <summary>Completes an expression in a paused frame, with zero-based UTF-16 replacement and selection offsets.</summary>
    public async Task<IReadOnlyList<DebugCompletion>?> Completions(string text, int column, int frame)
    {
        if (!CanComplete || State != DebugState.Stopped || column < 0 || column > text.Length || !Boundary(text, column)) return null;
        long epoch = Volatile.Read(ref threadEpoch), selection = Volatile.Read(ref threadSelection);
        var body = await connection.Request("completions", new JsonObject
        {
            ["frameId"] = frame, ["text"] = text, ["column"] = column + 1, ["line"] = 1
        }).ConfigureAwait(false);
        if (State != DebugState.Stopped || epoch != Volatile.Read(ref threadEpoch) || selection != Volatile.Read(ref threadSelection)) return null;
        var found = new List<(string Sort, DebugCompletion Item)>();
        if (body?["targets"] is JsonArray list)
            foreach (JsonObject item in list.OfType<JsonObject>())
            {
                string label = item["label"]?.GetValue<string>() ?? "";
                if (label.Length == 0) continue;
                string insert = item["text"]?.GetValue<string>() is { Length: > 0 } value ? value : label;
                int length = JsonNumbers.Int(item["length"], 0);
                // debugpy sends zero-based starts. LLDB omits start but returns the length of the preceding token.
                int start = item["start"] is null ? completionAdapter == "lldb-dap" ? column - length : column
                    : JsonNumbers.Int(item["start"], -1) - (completionAdapter == PYTHON ? 0 : 1);
                int selected = JsonNumbers.Int(item["selectionStart"], insert.Length), count = JsonNumbers.Int(item["selectionLength"], 0);
                if (start < 0 || start > text.Length || length < 0 || length > text.Length - start || !Boundary(text, start) || !Boundary(text, start + length)
                    || selected < 0 || selected > insert.Length || count < 0 || count > insert.Length - selected
                    || !Boundary(insert, selected) || !Boundary(insert, selected + count)) continue;
                string sort = item["sortText"]?.GetValue<string>() is { Length: > 0 } order ? order : label;
                found.Add((sort, new(label, insert, item["detail"]?.GetValue<string>() ?? "", item["type"]?.GetValue<string>() ?? "", start, length, selected, count)));
            }
        return found.OrderBy(item => item.Sort, StringComparer.Ordinal).Select(item => item.Item).ToArray();
    }

    private static bool Boundary(string text, int offset) => offset <= 0 || offset >= text.Length
        || !(char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]) || text[offset - 1] == '\r' && text[offset] == '\n');
}
