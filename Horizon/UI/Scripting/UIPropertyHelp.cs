using System.Collections.Generic;

namespace Horizon.UI.Scripting;

/// <summary>
/// What every property a layout can set does, in a sentence or two, for an editor to show when somebody rests
/// the pointer on one (Hex does, on the rows of its properties). Kept here next to the scripting and not in Hex,
/// whatever else puts a layout's properties in front of somebody can have them as well.
/// <code>
/// string help = UIPropertyHelp.Describe("progress_bar", "ghost");
/// </code>
/// A property means the same thing on every kind that has it unless the kind says otherwise, "value" on a slider
/// is a number and on a selector the option it shows, so those are looked up as "kind.name" first. A property
/// that is in neither comes back empty, add it here when a component gets a new one.
/// </summary>
public static class UIPropertyHelp
{
    private static readonly Dictionary<string, string> Help = new()
    {
        // ---- what every component has ----

        ["name"] = "what the component is called, which is the name of its variable in the code and what the program finds it by with layout.Get",
        ["kind"] = "what sort of component it is, which can't be changed here, delete it and add another to change it",
        ["pos"] = "where it is, measured from its anchor in the parent, x to the right and y up",
        ["size"] = "how big it is, 0 either way for as big as what is in it needs",
        ["anchor"] = "which point of the parent it hangs off, center, top_left, bottom and so on",
        ["pivot"] = "which point of the component is put on the anchor, left alone it is the same point as the anchor",
        ["fill"] = "stretches it to the width, the height or both of what its parent has room for, whatever its size says",
        ["padding"] = "room kept free inside its edges before what is in it starts, one number for all four or left, top, right, bottom",
        ["scale"] = "multiplies the size it works out for itself, for when it has no size of its own",
        ["visible"] = "whether it is drawn at all, a hidden component takes no room either",
        ["enabled"] = "whether it can be used, a disabled control is drawn greyed out and ignores the pointer and the pad",
        ["z"] = "above 0 it is drawn after everything else in the layout, the higher the more on top, and nothing it is inside of cuts it off (a badge over a card)",
        ["layer"] = "a name it shares with other parts of the layout so the program can show and hide them together",
        ["tooltip"] = "what comes up when the pointer rests on it, like this",
        ["intro"] = "how it makes its entrance when the layout is shown, a fade, a pop or a slide, none for just being there",
        ["intro_delay"] = "how many seconds after the layout appears its entrance starts, add it up down a list for one after the other",
        ["intro_time"] = "how many seconds its entrance takes",
        ["intro_offset"] = "where a slide comes in from, measured from where it belongs, (-300, 0) is from the left",
        ["hover"] = "what it does while the pointer is over it or anything in it, or a pad is on it, grow, shrink, lift, nudge, pulse or glow, none for nothing",
        ["hover_amount"] = "how much of the hover, 0 for what suits it, a fraction of its size for grow, shrink and pulse, units for lift and nudge, times the skin's hover colour for glow",
        ["hover_time"] = "how many seconds the hover takes to come on, and to go off again",
        ["intro_easing"] = "how the entrance is timed, out_cubic, out_back and the rest of them, empty for what the entrance does by itself",
        ["resize"] = "which of its edges the pointer can drag to make it bigger or smaller, each gets a tab in its middle",
        ["min_size"] = "the smallest a drag on its edges can make it",
        ["max_size"] = "the biggest a drag on its edges can make it, 0 either way for no limit",
        ["color"] = "the colour it is drawn in, the skin's own if it is left at nothing",
        ["text"] = "what it says",
        ["text_scale"] = "how big its text is written, 0 for the skin's size, numbers mean sizes in Dead Revolver's font whatever the skin",
        ["style"] = "which piece of the skin's art it wears, the ... button shows them all to pick one by eye. A style is the name less what its pieces end in (_hover, _on, _frame)",
        ["align"] = "where in its box the text sits, left, center, right, top_left and so on",

        // ---- containers ----

        ["background"] = "a piece of the skin's art to draw behind it as a frame, the ... button shows them all",
        ["radius"] = "how round the corners of a panel filled with a colour are, in pixels",
        ["outline"] = "a thin line round it in this colour, the edge of a card, nothing for none",
        ["template"] = "a layout file every item of this container is made from, for content the program only knows about when it runs (a row per save game)",
        ["preview_count"] = "how many items of the template are shown here to stand in for the real ones, the program never sees them",
        ["stagger"] = "how many seconds later each child makes its entrance than the one before, so a list pops up a row at a time",
        ["spacing"] = "the gap between one child and the next, below 0 for the skin's own",
        ["direction"] = "which way the children go, vertical is down, horizontal is across",
        ["stretch"] = "makes every child as wide as a vertical stack, or as tall as a horizontal one",
        ["columns"] = "how many children go side by side before a new row starts",
        ["offset"] = "how far down it is scrolled",
        ["wheel_step"] = "how far one notch of the mouse wheel scrolls it",
        ["bar_gap"] = "the room between the scroll bar and what is in it",
        ["space"] = "how much room it takes up, both ways, the skin's spacing if it is left at 0",
        ["thickness"] = "how thick the line is",
        ["divider.direction"] = "which way the line runs, horizontal across or vertical up and down",
        ["divider.color"] = "the colour of the line, the skin's if it is left at nothing",

        // ---- folding ----

        ["title"] = "what the bar over it says",
        ["expanded"] = "whether what is below the bar is showing, a click on the bar folds it away and back out",
        ["collapse_time"] = "how many seconds it takes to fold away or out, 0 to snap",
        ["collapsible.header_height"] = "how tall the bar is, 0 for as tall as a line of text needs",
        ["collapsible.style"] = "which piece of the skin's art the bar wears, with _hover on the end under the pointer, a flat bar if the skin has none",
        ["title_color"] = "the colour of the title and the arrow, nothing for the skin's",
        ["on_toggled"] = "the function the program has called when it is folded away or opened",

        // ---- tabs ----

        ["tabs"] = "the names of the tabs, in order, one page for each, the children of the tabs being the pages",
        ["tabs.selected"] = "which page is open, counted from 0",
        ["header_height"] = "how tall the strip of tabs is, 0 for as tall as a line of text needs",
        ["prev_hint"] = "text at the left end of the strip, the button that goes to the tab before (LB)",
        ["next_hint"] = "text at the right end of the strip, the button that goes to the next tab (RB)",
        ["accent"] = "the colour of the open tab's name and the line under it, nothing for the skin's accent",

        // ---- buttons and other controls ----

        ["label"] = "the words on it",
        ["lbl_scale"] = "how big the words on it are, 0 for the skin's size",
        ["selected"] = "draws it as the chosen one of a group, the way a pad sees which button it is on",
        ["animated"] = "whether it gives a little, a bump when it lights up and a squeeze when it is pressed",
        ["state"] = "whether it is on",
        ["toggle.style"] = "which pair of the skin's art its box wears, the name less the _on and _off its two pieces end in",
        ["options"] = "what it can be set to, in the order it steps through them",
        ["value"] = "what it is set to",
        ["selector.value"] = "which of the options it shows",
        ["dropdown.value"] = "which of the options is picked",
        ["list.value"] = "the item that is picked",
        ["slider.value"] = "where the handle is, between min and max",
        ["number_box.value"] = "the number in it",
        ["meter.value"] = "how full it is, from 0 to 1",
        ["max_rows"] = "how many options the open list shows at once, the mouse wheel gets to the rest",
        ["items"] = "the rows of the list",
        ["index"] = "which row is picked, counted from 0, -1 for none",
        ["rows"] = "how many rows are in sight at once, the rest scroll",
        ["list.style"] = "a piece of the skin's art to draw behind the rows, nothing for the plain box of a field",
        ["min"] = "the value at the left end",
        ["max"] = "the value at the right end",
        ["step"] = "what the value goes up and down in, 0 for anything in between",
        ["handle"] = "which piece of the skin's art the handle wears, the bar under it goes by style",
        ["placeholder"] = "the faint text shown while it is empty, to say what goes in it",
        ["max_length"] = "the most characters it takes, typing past it does nothing",
        ["button"] = "the words on a button inside the box at one end, nothing for no button",
        ["button_side"] = "which end of the box its button sits at, left or right",
        ["button_width"] = "how wide the button in the box is",
        ["drag_step"] = "how much dragging across the box by a unit changes the number",
        ["show_alpha"] = "whether it has a strip for how see-through the colour is",

        // ---- bars, numbers and pictures ----

        ["progress"] = "how full it is, from 0 to 1",
        ["ghost"] = "how full the fill behind the fill is, 0 to 1, what a hit took off showing for a moment before it catches up. The program tweens it, the bar doesn't by itself",
        ["ghost_color"] = "the colour of the ghost behind the fill",
        ["fill_color"] = "the colour of the fill, nothing for the skin's art or accent",
        ["reverse"] = "fills from the right instead of the left, for the bar of the player on the right",
        ["segments"] = "how many pieces the fill is cut into, 1 for one bar, 4 for a meter that is spent a quarter at a time",
        ["show_text"] = "writes the percentage on the bar",
        ["progress_bar.style"] = "which set of the skin's art the bar wears, the name less the _frame and _fill its pieces end in",
        ["slider.style"] = "which set of the skin's art the bar under the handle wears, the name less _frame and _fill",
        ["unit"] = "written after the number, smaller (ms, fps, %)",
        ["graph.unit"] = "what the values are counted in, written after the top of the graph in its corner",
        ["unit_color"] = "the colour of the unit, the number's fainter if it is left at nothing",
        ["unit_scale"] = "how big the unit is written, 0 for the skin's size, which next to a big number is smaller than it",
        ["wrap"] = "breaks text that is wider than the label into more lines instead of running off the edge",
        ["region"] = "which piece of the skin's art it shows, the ... button shows them all",
        ["file"] = "a PNG to show instead of a piece of the skin, the path from the game's folder",
        ["tint"] = "a colour the picture is multiplied by, white for as it is",
        ["track_color"] = "the colour of the part of the bar with nothing in it",
        ["whole"] = "how much the whole bar stands for, 0 for everything in it added up",
        ["marker"] = "a line across the bar this far along, in what its pieces are measured in, below 0 for none",
        ["ahead_opacity"] = "how much of itself what is past the marker is drawn with, less to have it fill up as the marker goes",
        ["bars"] = "how many bars it has room for",
        ["bar_width"] = "how wide a bar is with the gap after it, 0 for as wide as it takes to fill the graph",
        ["top"] = "the value the top of the graph stands for",
        ["line"] = "where a dashed line is drawn across, 0 for none",
        ["budget"] = "what a bar may come to before it changes colour, 0 for bars that never do",
        ["boxed"] = "sits it in a box with a line halfway up, the way a graph on a card does",
        ["peaks_only"] = "draws only the worst of every bar, as one solid bar",
        ["line_numbers"] = "numbers the lines down the left",

        // ---- what a program hands it, which Hex can't edit ----

        ["on_pressed"] = "the function the program has called when it is pressed",
        ["on_changed"] = "the function the program has called when what it is set to changes",
        ["on_submitted"] = "the function the program has called when enter is pressed in it",
        ["on_activated"] = "the function the program has called when a row is chosen with a double click or the pad's A",
        ["on_button"] = "the function the program has called when the button in the box is pressed",
        ["on_value_changed"] = "the function the program has called when the number changes",
        ["on_resize"] = "the function the program has called when an edge is dragged",

        // ---- old names, still read so old layouts load, never written ----

        ["spr_scale"] = "the old name of scale, still read so old layouts load",
        ["on_press"] = "the old name of on_pressed, still read so old layouts load",
    };

    /// <summary>
    /// What a property does, "kind.name" first and the name on its own after that, empty for one nobody wrote
    /// anything down for.
    /// </summary>
    /// <param name="kind">What a layout calls the component (UIModule.KindOf), "progress_bar", "slider", null if unknown.</param>
    public static string Describe(string? kind, string name)
    {
        if (kind is not null && Help.TryGetValue(kind + "." + name, out string? special))
            return special;

        return Help.TryGetValue(name, out string? help) ? help : string.Empty;
    }

    /// <summary>Whether anything is written down for a property on its own, for a test that every one has something.</summary>
    public static bool Knows(string name) => Help.ContainsKey(name);
}
