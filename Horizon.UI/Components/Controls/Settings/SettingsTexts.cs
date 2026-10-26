using System.Globalization;

namespace Horizon.UI.Components;

/// <summary>
/// Everything a <see cref="SettingsPage"/> says of its own, in English unless a program gives it its own words
/// (<see cref="SettingsPage.Texts"/>). A name in braces is filled in, {value} with what a reset puts back, {count} with
/// how many, {section} and {part} with a section's and a part's name, {query} with what was typed, {least} and {most}
/// with a number's ends. Counts have a text for one and a text for more, the words around a number change with it.
/// </summary>
public sealed class SettingsTexts
{
    /// <summary>The words a page has when nobody gives it any.</summary>
    public static readonly SettingsTexts English = new();

    /// <summary>What the window and the page are called.</summary>
    public string Title { get; init; } = "Settings";

    /// <summary>What the search box says while nothing is typed in it.</summary>
    public string SearchPlaceholder { get; init; } = "Search settings (@modified, @id:, @section:)";

    /// <summary>A reset's tooltip when nobody says what it puts back.</summary>
    public string ResetToDefault { get; init; } = SettingsPage.RESET_TO_DEFAULT;

    /// <summary>A reset's tooltip with what it puts back, {value}.</summary>
    public string ResetTo { get; init; } = "Reset to {value}";

    /// <summary>A switch's two values as a reset says them.</summary>
    public string On { get; init; } = "On";

    public string Off { get; init; } = "Off";

    /// <summary>What a section with no name of its own is called.</summary>
    public string General { get; init; } = "General";

    /// <summary>A section's right click, every change in it reset, {count} and {section}.</summary>
    public string ResetSectionOne { get; init; } = "Reset {count} setting in {section}";

    public string ResetSectionMany { get; init; } = "Reset {count} settings in {section}";

    /// <summary>A part's right click, every change in it reset, {count} and {part}.</summary>
    public string ResetPartOne { get; init; } = "Reset {count} change in {part}";

    public string ResetPartMany { get; init; } = "Reset {count} changes in {part}";

    /// <summary>Every change on the page reset, after asking. On the funnel's menu and a part's right click.</summary>
    public string ResetAll { get; init; } = "Reset all settings...";

    /// <summary>The funnel's menu and what it says when the pointer rests on it.</summary>
    public string Filter { get; init; } = "Filter";

    public string FilterTooltip { get; init; } = "Filter settings";

    public string FilterModified { get; init; } = "Modified settings";

    public string FilterId { get; init; } = "Setting by ID";

    public string FilterSection { get; init; } = "Settings in a section";

    /// <summary>The x that empties the search, and the one that closes a page that has one.</summary>
    public string ClearTooltip { get; init; } = "Clear the search";

    public string CloseTooltip { get; init; } = "Close the settings";

    /// <summary>What the rows say when a search leaves none of them, {query}, and the button under it.</summary>
    public string NothingFound { get; init; } = "No settings match \"{query}\"";

    public string ClearSearch { get; init; } = "Clear search (Esc)";

    /// <summary>How many a search found, at the end of the box, {count}.</summary>
    public string FoundOne { get; init; } = "{count} setting found";

    public string FoundMany { get; init; } = "{count} settings found";

    /// <summary>A row's right click.</summary>
    public string ResetSetting { get; init; } = "Reset setting";

    public string CopyId { get; init; } = "Copy setting ID";

    public string CopyLine { get; init; } = "Copy as HIDL line";

    /// <summary>A number box's tooltip, {least} and {most}.</summary>
    public string Between { get; init; } = "Between {least} and {most}";

    /// <summary>What a list of choices says when there's nothing to choose.</summary>
    public string NothingToPick { get; init; } = "nothing to pick from";

    /// <summary>A row of pairs (files_associations), its button that adds one, the headings over its two columns and what each line's buttons say.</summary>
    public string AddItem { get; init; } = "Add Item";

    public string PairKey { get; init; } = "Item";

    public string PairValue { get; init; } = "Value";

    public string EditItem { get; init; } = "Edit item";

    public string RemoveItem { get; init; } = "Remove item";

    /// <summary>The buttons beside a pair being typed, the same as Enter and Escape.</summary>
    public string PairOk { get; init; } = "OK";

    public string PairCancel { get; init; } = "Cancel";

    /// <summary>What a program asks before every change is reset, {count} of them.</summary>
    public string ResetAllTitle { get; init; } = "Reset all settings?";

    public string ResetAllTextOne { get; init; } = "{count} setting goes back to what it comes as.";

    public string ResetAllTextMany { get; init; } = "{count} settings go back to what they come as.";

    public string ResetAllYes { get; init; } = "Reset all";

    public string ResetAllNo { get; init; } = "Cancel";

    /// <summary>A text with the names in braces filled in.</summary>
    public static string Fill(string text, params (string Name, string Value)[] filled)
    {
        foreach (var (name, value) in filled)
            text = text.Replace("{" + name + "}", value);
        return text;
    }

    /// <summary>One of two texts by a count, the one for one or the one for more, with {count} and the rest filled in.</summary>
    public static string Counted(string one, string many, int count, params (string Name, string Value)[] filled) =>
        Fill(Fill(count == 1 ? one : many, filled), ("count", count.ToString(CultureInfo.InvariantCulture)));

    /// <summary>How many a search found, "3 settings found".</summary>
    public string Found(int count) => Counted(FoundOne, FoundMany, count);
}
