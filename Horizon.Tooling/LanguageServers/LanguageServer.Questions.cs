using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>
/// A question a server asked whoever is at the keyboard (window/showMessageRequest), what it says and the answers it
/// takes. serve-d asks to download DCD this way, and answered with nothing it never completes a thing.
/// </summary>
public sealed class LanguageServerQuestion
{
    private readonly TaskCompletionSource<JsonNode?> answer;
    private readonly JsonArray? actions;

    internal LanguageServerQuestion(string server, string message, bool warning, JsonArray? actions, TaskCompletionSource<JsonNode?> answer)
    {
        Server = server;
        Message = message.Trim();
        Warning = warning;
        this.actions = actions;
        this.answer = answer;
        Actions = actions is null ? [] : [.. actions.OfType<JsonObject>().Select(action => action["title"]?.GetValue<string>() ?? string.Empty)];
    }

    /// <summary>The server that asked, by its name.</summary>
    public string Server { get; }

    /// <summary>What it asks.</summary>
    public string Message { get; }

    /// <summary>Whether it said it as an error or a warning, rather than as something to know.</summary>
    public bool Warning { get; }

    /// <summary>What it can be answered with, by their titles as the server wrote them.</summary>
    public IReadOnlyList<string> Actions { get; }

    /// <summary>Whether it's been answered, or let go of with the server.</summary>
    public bool IsAnswered => answer.Task.IsCompleted;

    /// <summary>Answers it with one of its actions by its title, or with nothing (null) for none of them.</summary>
    public void Answer(string? action)
    {
        JsonNode? picked = action is null || actions is null ? null
            : actions.OfType<JsonObject>().FirstOrDefault(each => each["title"]?.GetValue<string>() == action)?.DeepClone();
        answer.TrySetResult(picked);
    }
}

public sealed partial class LanguageServer
{
    // What the server asked and said for the editor's thread to take, see TryTakeQuestion and TryTakeSaid
    private readonly ConcurrentQueue<LanguageServerQuestion> questions = new();
    private readonly ConcurrentQueue<(string Text, bool Warning)> said = new();

    /// <summary>Takes the next question the server asked, false when there is none waiting.</summary>
    public bool TryTakeQuestion(out LanguageServerQuestion question) => questions.TryDequeue(out question!);

    /// <summary>
    /// Takes the next thing the server said for somebody to read (window/showMessage, and serve-d's word on how an install
    /// it was asked for is going), false when there is nothing waiting.
    /// </summary>
    public bool TryTakeSaid(out string text, out bool warning)
    {
        if (said.TryDequeue(out var next))
        {
            (text, warning) = next;
            return true;
        }

        text = string.Empty;
        warning = false;
        return false;
    }

    /// <summary>Helper method to hear what the server asks and says to whoever is at the keyboard, as it's made.</summary>
    private void ListenForTalk()
    {
        rpc.OnRequestLater = Asked;
        rpc.OnRequest = Answered;
        rpc.OnNotification("window/showMessage", parameters =>
        {
            if (parameters is JsonObject shown && shown["message"]?.GetValue<string>() is { Length: > 0 } message)
                Say(message, JsonNumbers.Int(shown["type"], 3) <= 2);
        });
        rpc.OnNotification("coded/logInstall", parameters =>
        {
            if (parameters is JsonValue line && line.TryGetValue(out string? text) && text.Length > 0)
                Say(text, false);
        });
    }

    /// <summary>Tells the server a file open in it was saved, which some only look the whole of a file over for then (serve-d's dub).</summary>
    public void Saved(string path)
    {
        string uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        Later(() => rpc.Notify("textDocument/didSave", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }));
    }

    // The server's requests answered on the spot. Its settings when it asks for them, a section each by its dotted name,
    // null for anything else
    private JsonNode? Answered(string method, JsonNode? parameters)
    {
        if (method != "workspace/configuration" || parameters?["items"] is not JsonArray items)
            return null;

        var sections = new JsonArray();
        foreach (JsonNode? item in items)
        {
            JsonNode? at = Settings;
            if (item?["section"]?.GetValue<string>() is { Length: > 0 } section)
            {
                foreach (string part in section.Split('.'))
                    at = at is JsonObject inside ? inside[part] : null;
            }

            sections.Add(at?.DeepClone());
        }

        return sections;
    }

    private void Say(string text, bool warning)
    {
        said.Enqueue((text.Trim(), warning));
        Woken?.Invoke();
    }

    // A question for whoever is at the keyboard, answered once they have, the reading thread going on meanwhile. Null for
    // any other request, which is answered on the spot
    private Task<JsonNode?>? Asked(string method, JsonNode? parameters)
    {
        if (method != "window/showMessageRequest" || parameters is not JsonObject asked)
            return null;

        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        asking[answer] = 0;
        answer.Task.ContinueWith(_ => asking.TryRemove(answer, out byte _), TaskScheduler.Default);

        string message = asked["message"]?.GetValue<string>() ?? string.Empty;
        bool warning = JsonNumbers.Int(asked["type"], 3) <= 2;
        questions.Enqueue(new LanguageServerQuestion(Name, message, warning, asked["actions"] as JsonArray, answer));
        Woken?.Invoke();
        return answer.Task;
    }
}
