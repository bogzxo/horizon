using System.Numerics;

using Horizon.Rendering;

namespace Horizon.UI.Components;

// The chips under the search, the filters a press away: All, Modified with how many there are, Commonly used. They were
// words to know (@modified, @common) in the placeholder and a funnel's menu, and with hundreds of settings what's been
// changed is the first thing anybody looks for. A chip lit is its word in the search, pressing it again takes it out
public partial class SettingsPage
{
    private const float CHIP_HEIGHT = 26.0f, CHIP_GAP = 6.0f;

    private StackPanel chips = null!;
    private Button chipAll = null!, chipModified = null!, chipCommon = null!;

    // How many were changed when the chip last said so, and how long since it was counted
    private int chipCount = -1;
    private float chipCounted;

    /// <summary>Whether the chips are under the search, a page with sections down the left (a settings window's) and rows of full size.</summary>
    private bool ShowChips => ShowNav && !CompactRows;

    private float ChipHeight => CHIP_HEIGHT * zoom;

    // What the chips and the gap under them take off the top of the rows, nothing while they're not shown
    private float ChipsRoom => ShowChips ? ChipHeight + GAP * 0.75f : 0.0f;

    private void MakeChips()
    {
        chips = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = CHIP_GAP, Anchor = Origin.Left, Pivot = Origin.Left });
        chipAll = chips.Add(Chip(() => SetFilterWords(modified: false, common: false)));
        chipModified = chips.Add(Chip(() => SetFilterWords(modified: !HasWord(MODIFIED), common: HasWord(COMMON))));
        chipCommon = chips.Add(Chip(() => SetFilterWords(modified: HasWord(MODIFIED), common: !HasWord(COMMON))));
    }

    private static Button Chip(Action pressed) => new()
    {
        Style = "button_flat",
        Animated = false,
        Align = Origin.Center,
        OnPressed = pressed
    };

    // Whether a filter word is in the search now, scanned without allocating on every tick
    private bool HasWord(string word)
    {
        ReadOnlySpan<char> text = search.Text.AsSpan();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && text[i] == ' ')
                i++;
            int start = i;
            while (i < text.Length && text[i] != ' ')
                i++;
            if (i > start && text.Slice(start, i - start).Equals(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // The search's own words kept, the two filter words put in or taken out as asked, the keyboard back in the box
    private void SetFilterWords(bool modified, bool common)
    {
        var words = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !word.Equals(MODIFIED, StringComparison.OrdinalIgnoreCase) && !word.Equals(COMMON, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (common)
            words.Insert(0, COMMON);
        if (modified)
            words.Insert(0, MODIFIED);
        string typed = string.Join(' ', words);
        Find(typed.Length > 0 ? typed + " " : string.Empty);
        search.Focus();
    }

    // Every tick, which chip is lit, and now and then how many are changed (counting asks every setting)
    private void UpdateChips(float dt)
    {
        chips.Visible = ShowChips;
        if (!ShowChips)
            return;

        bool modified = HasWord(MODIFIED), common = HasWord(COMMON);
        chipAll.Selected = !modified && !common;
        chipModified.Selected = modified;
        chipCommon.Selected = common;
        chipCommon.Visible = this.common.Length > 0;

        chipCounted += dt;
        if (chipCount < 0 || chipCounted >= 0.5f || navStale)
        {
            chipCounted = 0.0f;
            int count = CountModifiedRows();
            if (count != chipCount || chipModified.Label.Length == 0)
            {
                chipCount = count;
                FitChip(chipAll, texts.ChipAll);
                FitChip(chipModified, SettingsTexts.Fill(texts.ChipModified, ("count", count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                FitChip(chipCommon, texts.CommonlyUsed);
            }
        }
    }

    private int CountModifiedRows()
    {
        if (IsModified is null)
            return 0;

        int count = 0;
        foreach (SettingRow row in rowOf.Values)
        {
            if (row.Modified)
                count++;
        }
        return count;
    }

    // A chip as wide as its words, at the page's zoom
    private void FitChip(Button chip, string label)
    {
        chip.Label = label;
        chip.LabelScale = sentenceSize * 0.92f;
        float words = Module?.Compositor.Skin is { } skin ? skin.Font.Measure(label, chip.LabelScale > 0.0f ? chip.LabelScale : skin.TextScale, markup: false).X : label.Length * 7.0f;
        chip.Size = new Vector2(MathF.Ceiling(words + 24.0f * zoom), ChipHeight);
    }

    /// <summary>The chips under the search, each one's words, whether it's lit and where it is. For a test.</summary>
    public IReadOnlyList<(string Label, bool Lit, UIRect Bounds)> FilterChips =>
        [.. chips.Children.OfType<Button>().Where(chip => chip.Visible).Select(chip => (chip.Label, chip.Selected, chip.Bounds))];
}
