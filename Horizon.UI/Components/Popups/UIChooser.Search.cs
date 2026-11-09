namespace Horizon.UI.Components;

/// <summary>
/// The rows that fit what was typed, worked out by whoever asked for the chooser instead of narrowed down out of a list
/// it was handed, for a list too big to hold as rows (every declaration of a workspace). It's run off the UI thread, a
/// new one for every key typed with the last one's token cancelled, so it reads nothing of the UI's. The rows come back
/// best first and are shown in that order, no more of them than a chooser shows.
/// </summary>
public delegate List<UIChoice> UIChoiceSearch(string typed, CancellationToken cancel);

// A chooser whose list is searched rather than narrowed, see UIChoiceSearch
public static partial class UIChooser
{
    /// <summary>What the line under the box says while a search hasn't come back yet and nothing's there from the last one.</summary>
    private const string LOOKING = "Looking...";

    /// <summary>What it says for a search with nothing typed into it yet.</summary>
    private const string TYPE_SOMETHING = "Type a few letters of a name";
}
