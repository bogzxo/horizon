using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>A server edit awaiting the editor's validation and acknowledgement.</summary>
public sealed class LanguageServerEditRequest
{
    private readonly IReadOnlyDictionary<string, int?> expected;
    private readonly IReadOnlyDictionary<string, int> receivedVersions;
    private readonly TaskCompletionSource<JsonNode?> answer;

    internal LanguageServerEditRequest(IReadOnlyDictionary<string, IReadOnlyList<LanguageServerEdit>> edits,
        IReadOnlyDictionary<string, int?> expected, IReadOnlyDictionary<string, int> receivedVersions,
        TaskCompletionSource<JsonNode?> answer)
    {
        Edits = edits;
        this.expected = expected;
        this.receivedVersions = receivedVersions;
        this.answer = answer;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<LanguageServerEdit>> Edits { get; }
    public DateTime ReceivedAt { get; } = DateTime.UtcNow;
    public bool IsCompleted => answer.Task.IsCompleted;

    /// <summary>Explicit protocol versions and changes since receipt must both match.</summary>
    public bool VersionsMatch(Func<string, int?> current)
    {
        foreach (string file in Edits.Keys)
        {
            int? now = current(file);
            if (expected.TryGetValue(file, out int? wanted) && wanted is not null && wanted != now)
                return false;
            int? received = receivedVersions.TryGetValue(file, out int version) ? version : null;
            if (received != now)
                return false;
        }
        return true;
    }

    /// <summary>Call only after applying all edits, or rejecting without changing files.</summary>
    public void Complete(bool applied, string? failureReason = null) => answer.TrySetResult(new JsonObject
    {
        ["applied"] = applied,
        ["failureReason"] = applied ? null : failureReason
    });
}
