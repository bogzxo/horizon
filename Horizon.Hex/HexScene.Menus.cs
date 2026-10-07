using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Key = Silk.NET.Input.Key;

namespace Horizon.Hex;

// The palette and the menu bar, which is everything there is to do that isn't done to something on screen directly.
internal sealed partial class HexScene
{
    private void BuildPalette()
    {
        // One button per kind of component, each made from the template the palette names
        var items = layout.Populate("palette", Palette.Length);

        for (int i = 0; i < items.Count; i++)
        {
            string kind = Palette[i];
            var button = items[i].Get<Button>("button");

            button.Label = kind.Replace('_', ' ');
            button.Tooltip = Describe(kind);
            button.OnPressed = () => AddComponentOf(kind);
            paletteButtons.Add(button);
        }
    }

    /// <summary>
    /// Helper method for what a kind of component is for, the line the palette shows when the pointer rests on its button.
    /// </summary>
    private static string Describe(string kind) => kind switch
    {
        "stack" => "A row or a column of things, with a gap between them",
        "grid" => "Cells in rows and columns, so many across",
        "panel" => "A box to put things in, or a backdrop",
        "scroll" => "A panel that scrolls when there is more than fits",
        "label" => "A piece of text, which can wrap",
        "button" => "A button with a label, pressed by a click or the navigator",
        "toggle" => "A box that is ticked or isn't",
        "textbox" => "A line somebody types into",
        "number_box" => "A number, dragged across or typed",
        "selector" => "One of a few options, stepped through with arrows",
        "dropdown" => "One of a list, which drops down",
        "list" => "A list of texts in a box, one chosen",
        "slider" => "A number between two ends, slid along",
        "color_picker" => "A colour, picked by hue, shade and how solid it is",
        "progress_bar" => "How full something is, a health bar say",
        "image" => "A picture, a sprite of the skin or a file",
        "tabs" => "Pages with a strip of tabs along the top",
        "divider" => "A line between things",
        "spacer" => "Nothing, of a size, for a gap",
        _ => string.Empty
    };

    /* The menu bar: everything there is to do that isn't done to something on screen directly */

    /// <summary>
    /// Helper method to set up the keys of the editor. The ones with control work whatever is going on,
    /// the others keep out of the way while something is being typed.
    /// </summary>
    private void BuildShortcuts()
    {
        shortcuts.Add(Key.Z, Undo, control: true, whileTyping: true);
        shortcuts.Add(Key.Y, Redo, control: true, whileTyping: true);
        shortcuts.Add(Key.S, Save, control: true, whileTyping: true);
        shortcuts.Add(Key.O, OpenPressed, control: true, whileTyping: true);
        shortcuts.Add(Key.N, () => Show(NewDocument()), control: true, whileTyping: true);
        shortcuts.Add(Key.W, Close, control: true, whileTyping: true);

        shortcuts.Add(Key.Delete, DeleteAll);

        // The arrows move whatever is selected a pixel at a time, for lining things up by eye
        shortcuts.Add(Key.Left, () => Nudge(-1, 0));
        shortcuts.Add(Key.Right, () => Nudge(1, 0));
        shortcuts.Add(Key.Up, () => Nudge(0, 1));
        shortcuts.Add(Key.Down, () => Nudge(0, -1));
    }

    private void BuildMenus()
    {
        fileMenu = menu.AddMenu("File");
        RebuildFileMenu();

        Menu edit = menu.AddMenu("Edit");
        edit.Add("Undo", Undo, "Ctrl+Z").IsEnabled = () => document.CanUndo || recordPending;
        edit.Add("Redo", Redo, "Ctrl+Y").IsEnabled = () => document.CanRedo;
        edit.AddSeparator();
        edit.Add("Move up", () => MoveSelected(-1)).IsEnabled = () => document.Selected is not null;
        edit.Add("Move down", () => MoveSelected(1)).IsEnabled = () => document.Selected is not null;
        edit.Add("Delete", DeleteAll, "Del").IsEnabled = () => document.Selected is not null;
        edit.Add("Duplicate", DuplicateSelected, "Ctrl+D").IsEnabled = () => document.Selected is not null;
        edit.AddSeparator();
        edit.Add("Group", GroupSelected, "Ctrl+G").IsEnabled = () => document.Selected is not null;
        edit.Add("Ungroup", UngroupSelected, "Ctrl+Shift+G").IsEnabled = () => document.Selection.Any(component => component is Group);
        edit.AddSeparator();
        edit.Add("Bring to front", () => ReorderSelected(HexDocument.Order.Front), "Ctrl+]").IsEnabled = () => document.Selected is not null;
        edit.Add("Send to back", () => ReorderSelected(HexDocument.Order.Back), "Ctrl+[").IsEnabled = () => document.Selected is not null;
        edit.AddSeparator();
        edit.Add("Select all", SelectAll, "Ctrl+A");
        edit.Add("Select nothing", () => Select(null), "Esc").IsEnabled = () => document.Selected is not null;

        Menu view = menu.AddMenu("View");
        view.Add("Play intros", () => document.PlayIntros());
        view.AddSeparator();
        view.Add("Zoom in", () => ZoomCanvas(ZOOM_STEP * ZOOM_STEP, canvas.Bounds.Center), "Ctrl+=");
        view.Add("Zoom out", () => ZoomCanvas(1.0f / (ZOOM_STEP * ZOOM_STEP), canvas.Bounds.Center), "Ctrl+-");
        view.Add("Zoom to fit", ZoomToFit, "Ctrl+0");
        view.Add("Actual size", ZoomToActualSize, "Ctrl+1");
        view.AddSeparator();
        layoutDebugger.AddTo(view, () => Say(layoutDebugger.IsOn ? "layout debugger on: padding is green, gaps are orange" : "layout debugger off"));
        view.AddSeparator();
        view.Add("Show all layers", ShowAllLayers).IsEnabled = () => document.Module.HiddenLayers.Count > 0;
        view.AddSeparator();

        // The sizes it can be are the ones the editor knows how to lay itself out at
        foreach (float scale in Scales)
            view.Add(DescribeScale(scale), () => SetScale(scale, remember: true)).IsChecked = () => compositor.Scale == scale;
    }

    /// <summary>
    /// Puts the file menu together. What it always has, and under that the layouts that were worked on last.
    /// Again whenever those change.
    /// </summary>
    private void RebuildFileMenu()
    {
        fileMenu.Clear();

        fileMenu.Add("New", () => Show(NewDocument()), "Ctrl+N");
        fileMenu.Add("Open...", OpenPressed, "Ctrl+O");
        fileMenu.AddSeparator();
        fileMenu.Add("Save", Save, "Ctrl+S");
        fileMenu.Add("Save as...", SaveAs);
        fileMenu.Add("Close", Close, "Ctrl+W");

        if (recent.Count > 0)
        {
            fileMenu.AddSeparator();

            for (int i = 0; i < recent.Count; i++)
            {
                // Numbered, two of them can well have the same name in different folders
                string path = recent[i];
                fileMenu.Add($"{i + 1}  {Path.GetFileName(path)}", () => Open(path));
            }

            fileMenu.Add("Forget these", ForgetRecent);
        }

        fileMenu.AddSeparator();
        fileMenu.Add("The editor's own UI", () => Open(EDITOR_LAYOUT));
    }
}
