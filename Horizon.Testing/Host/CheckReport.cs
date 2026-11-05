using System.Text;
using System.Text.Json;

namespace Horizon.Testing;

/// <summary>How one check scene went in a run of every check.</summary>
/// <param name="Id">Its id in the catalog.</param>
/// <param name="Count">How many checks it made.</param>
/// <param name="Failed">How many of them didn't hold.</param>
/// <param name="Skipped">How many it left out because the platform can't do what they check, not part of Count.</param>
/// <param name="Seconds">How long it took from being started to being judged.</param>
/// <param name="Failures">The ones that didn't hold, by name.</param>
/// <param name="Skips">The ones left out, by name and why.</param>
/// <param name="Note">Anything else worth knowing, why it never finished for one.</param>
internal sealed record CheckOutcome(string Id, int Count, int Failed, int Skipped, double Seconds, IReadOnlyList<string> Failures, IReadOnlyList<string> Skips, string? Note = null)
{
    public int Passed => Count - Failed;
}

/// <summary>
/// Every check scene of a run, in the order they ran, written out as text to read and JSON for a script. The host makes
/// one as it runs the checks and hands it over at the end, a desktop prints it, a phone writes it for adb pull too.
/// </summary>
internal sealed class CheckReport(string platform)
{
    private readonly List<CheckOutcome> outcomes = [];

    public string Platform { get; } = platform;
    public DateTime Started { get; } = DateTime.UtcNow;
    public IReadOnlyList<CheckOutcome> Outcomes => outcomes;

    public int Count => outcomes.Sum(outcome => outcome.Count);
    public int Failed => outcomes.Sum(outcome => outcome.Failed);
    public int Skipped => outcomes.Sum(outcome => outcome.Skipped);
    public int Passed => Count - Failed;

    public void Add(CheckOutcome outcome) => outcomes.Add(outcome);

    /// <summary>A line a check scene, the failures and skips under it, and the totals last.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append("Horizon.Testing checks on ").Append(Platform).Append(", started ").Append(Started.ToString("u")).Append('\n');
        foreach (CheckOutcome outcome in outcomes)
        {
            text.Append(outcome.Failed == 0 ? "PASS " : "FAIL ").Append(outcome.Id).Append(", ").Append(outcome.Passed).Append(" of ").Append(outcome.Count).Append(" held");
            if (outcome.Skipped > 0) text.Append(", ").Append(outcome.Skipped).Append(" skipped");
            text.Append(", ").Append(outcome.Seconds.ToString("0.0")).Append(" s");
            if (outcome.Note is { } note) text.Append(", ").Append(note);
            text.Append('\n');
            foreach (string failure in outcome.Failures) text.Append("    failed  ").Append(failure).Append('\n');
            foreach (string skip in outcome.Skips) text.Append("    skipped ").Append(skip).Append('\n');
        }
        text.Append(Passed).Append(" of ").Append(Count).Append(" checks passed, ").Append(Failed).Append(" failed, ").Append(Skipped).Append(" skipped\n");
        return text.ToString();
    }

    /// <summary>The same as JSON.</summary>
    public void WriteJson(Stream stream)
    {
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("platform", Platform);
        json.WriteString("started", Started);
        json.WriteNumber("count", Count);
        json.WriteNumber("passed", Passed);
        json.WriteNumber("failed", Failed);
        json.WriteNumber("skipped", Skipped);
        json.WriteStartArray("checks");
        foreach (CheckOutcome outcome in outcomes)
        {
            json.WriteStartObject();
            json.WriteString("id", outcome.Id);
            json.WriteNumber("count", outcome.Count);
            json.WriteNumber("passed", outcome.Passed);
            json.WriteNumber("failed", outcome.Failed);
            json.WriteNumber("skipped", outcome.Skipped);
            json.WriteNumber("seconds", Math.Round(outcome.Seconds, 2));
            if (outcome.Note is { } note) json.WriteString("note", note);
            json.WriteStartArray("failures");
            foreach (string failure in outcome.Failures) json.WriteStringValue(failure);
            json.WriteEndArray();
            json.WriteStartArray("skips");
            foreach (string skip in outcome.Skips) json.WriteStringValue(skip);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
