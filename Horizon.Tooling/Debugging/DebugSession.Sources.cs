using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>A source descriptor owned by one debug session. Positive references require the adapter's source request.</summary>
public sealed class DebugSource
{
    private readonly JsonObject descriptor;
    internal DebugSession Owner { get; }
    public int Reference { get; }
    public string Name { get; }
    public string? Path { get; }
    public string? Origin { get; }
    public string? PresentationHint { get; }

    internal DebugSource(DebugSession owner, JsonObject source)
    {
        Owner = owner;
        descriptor = (JsonObject)source.DeepClone();
        Reference = Math.Max(0, JsonNumbers.Int(source["sourceReference"], 0));
        Name = String(source["name"]) ?? "";
        Path = String(source["path"]);
        Origin = String(source["origin"]);
        PresentationHint = String(source["presentationHint"]);
    }
    internal JsonObject Description() => (JsonObject)descriptor.DeepClone();
    private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

public sealed record DebugSourceContent(DebugSource Source, string Text, string? MimeType);

public sealed partial class DebugSession
{
    public bool OwnsSource(DebugSource source) => ReferenceEquals(source.Owner, this) && source.Reference > 0 && state != DebugState.Ended;

    /// <summary>Read native source content. Source references cannot be reused by another session or after it ends.</summary>
    public async Task<DebugSourceContent?> ReadSource(DebugSource source, CancellationToken cancel = default)
    {
        if (!ReferenceEquals(source.Owner, this)) throw new ArgumentException("The source belongs to another debug session.", nameof(source));
        if (source.Reference <= 0) throw new ArgumentException("The source has no adapter reference.", nameof(source));
        if (state == DebugState.Ended) return null;
        var body = await connection.Request("source", new JsonObject
        {
            ["source"] = source.Description(), ["sourceReference"] = source.Reference
        }).WaitAsync(cancel).ConfigureAwait(false);
        if (state == DebugState.Ended || cancel.IsCancellationRequested) return null;
        if (body is not JsonObject content || Text(content["content"]) is not {} text)
            throw new InvalidDataException("The debugger did not return source text.");
        return new(source, text, Text(content["mimeType"]));
    }
}
