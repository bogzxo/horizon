using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Horizon.Hex;

// What the editor is set to (HexSettings), the dialog it is set in, and the edges of its panels that can be dragged.
internal sealed partial class HexScene
{
    // How wide a row of the settings is, what it is called and what it is set with
    private const float SETTING_NAME_WIDTH = 210.0f, SETTING_VALUE_WIDTH = 250.0f;

    // The least and the most the panels can be dragged to, in the editor's units
    private const float MIN_SIDE = 180.0f, MAX_SIDE = 640.0f;
    private const float MIN_LAYERS = 40.0f, MAX_LAYERS = 360.0f;

    // How near an edge of what is selected in the canvas the pointer has hold of it, inside and out, in the layout's units
    private const float HANDLE_INSIDE = 7.0f, HANDLE_OUTSIDE = 5.0f;

    private HexSettings settings = null!;
    private ScrollPanel layersScroll = null!;
    private UIDialog? settingsDialog;

    // The skin of the editor that its colours were last taken from, see ColourChrome
    private UISkin? colouredFor;

    // The sizes the layout gives the panels, which is what resetting them goes back to
    private float usualLeft, usualRight, usualLayers;

    // What is being dragged bigger in the canvas, null for nothing
    private UIComponent? resizing;

    /// <summary>
    /// Helper method to read the settings and put them in force, the theme of the editor, the sizes its panels were
    /// left at, and to give the panels the edges they are dragged by. Once, as the editor is made.
    /// </summary>
    private void LoadSettings()
    {
        // The editor testing itself, or only checking layouts, comes up the same every time and keeps nothing
        bool kept = !options.SelfTest && options.Check.Length == 0;
        settings = new HexSettings(kept ? Path.Combine(AppContext.BaseDirectory, HexSettings.FILE) : null);

        layersScroll = layout.Get<ScrollPanel>("layers_scroll");
        usualLeft = treeScroll.Size.X;
        usualRight = inspectorScroll.Size.X;
        usualLayers = layersScroll.Size.Y;

        ApplyPanelSizes();
        UseEditorTheme(settings.EditorTheme, save: false);

        // The edges between the parts of the editor, a tab on each to be found by. The hold is nearly all on the
        // outside, in the gap to the next panel, the inside of these edges is where their scroll bars are
        Grip(treeScroll, UIResize.Right, new Vector2(MIN_SIDE, 0.0f), new Vector2(MAX_SIDE, 0.0f), size => settings.LeftWidth = size.X);
        Grip(inspectorScroll, UIResize.Left, new Vector2(MIN_SIDE, 0.0f), new Vector2(MAX_SIDE, 0.0f), size => settings.RightWidth = size.X);
        Grip(layersScroll, UIResize.Top, new Vector2(0.0f, MIN_LAYERS), new Vector2(0.0f, MAX_LAYERS), size => settings.LayersHeight = size.Y);
        Grip(canvas, UIResize.Bottom, new Vector2(0.0f, MIN_FLEXIBLE), Vector2.Zero, size => settings.CanvasHeight = size.Y);
    }

    private void Grip(UIComponent panel, UIResize edge, Vector2 least, Vector2 most, Action<Vector2> keep)
    {
        panel.Resizable = edge;
        panel.MinSize = least;
        panel.MaxSize = most;
        panel.GripSize = 2.0f;
        panel.GripReach = 8.0f;
        panel.OnResized = keep;
        panel.OnResizeEnded = _ => SaveSettings();
    }

    /// <summary>Helper method to give the panels the sizes the settings have for them, the layout's own where they have none.</summary>
    private void ApplyPanelSizes()
    {
        treeScroll.Size = treeScroll.Size with { X = settings.LeftWidth > 0.0f ? Math.Clamp(settings.LeftWidth, MIN_SIDE, MAX_SIDE) : usualLeft };
        inspectorScroll.Size = inspectorScroll.Size with { X = settings.RightWidth > 0.0f ? Math.Clamp(settings.RightWidth, MIN_SIDE, MAX_SIDE) : usualRight };
        layersScroll.Size = layersScroll.Size with { Y = settings.LayersHeight > 0.0f ? Math.Clamp(settings.LayersHeight, MIN_LAYERS, MAX_LAYERS) : usualLayers };
    }

    private void SaveSettings()
    {
        if (settings.Save() is { } problem)
            Say(problem, error: true);
    }

    /// <summary>Helper method to draw the editor itself in another theme of its skin.</summary>
    private void UseEditorTheme(string theme, bool save)
    {
        settings.EditorTheme = theme;
        compositor.SetSkin(TOOL_SKIN_DIRECTORY, TOOL_SKIN, theme);

        if (save)
        {
            SaveSettings();
            Say($"the editor is in its {theme} theme");
        }
    }

    /// <summary>
    /// Helper method to take the colours the editor's layout has written into it (the boxes of its lists, what is
    /// behind everything) from its skin once that has changed, a layout can only say a colour, not "whatever the
    /// skin has for a panel". Every update, it does nothing until the skin is another one.
    /// </summary>
    private void ColourChrome()
    {
        if (compositor.Skin is not { } skin || ReferenceEquals(skin, colouredFor))
            return;

        colouredFor = skin;

        Vector4 box = skin.PanelColor with { W = 1.0f };
        treeScroll.Color = layersScroll.Color = codeScroll.Color = inspectorScroll.Color = box;

        // What the editor sits on, a little darker than its boxes
        Engine.Graphics.ClearColor = new Vector4(box.X * 0.65f, box.Y * 0.65f, box.Z * 0.65f, 1.0f);

        treeDirty = inspectorDirty = true;
    }

    /* The dialog */

    private void OpenSettings()
    {
        var dialog = settingsDialog = UIDialog.Show(chrome, "Settings", string.Empty, new DialogChoice("Done"));
        dialog.Closed = () => settingsDialog = null;

        StackPanel Row(string name, string tooltip)
        {
            var row = dialog.Body.Add(new StackPanel { Anchor = Origin.Left, Direction = UIDirection.Horizontal, Spacing = 12.0f });
            row.Add(new Label(name) { Size = new Vector2(SETTING_NAME_WIDTH, EDITOR_HEIGHT), Align = Origin.Left, TextScale = EDITOR_TEXT, Tooltip = tooltip });
            return row;
        }

        Selector Choice(StackPanel row, string[] choices, int chosen, Action<int> picked)
        {
            var selector = row.Add(new Selector(choices) { Size = new Vector2(SETTING_VALUE_WIDTH, EDITOR_HEIGHT), TextScale = EDITOR_TEXT, Index = chosen });
            selector.OnChanged = _ => picked(selector.Index);
            return selector;
        }

        void Switch(string name, string tooltip, bool on, Action<bool> flipped)
        {
            ToggleButton toggle = null!;
            toggle = Row(name, tooltip).Add(new ToggleButton(on ? "on" : "off", on) { LabelScale = EDITOR_TEXT });
            toggle.OnPressed = () =>
            {
                toggle.Label = toggle.State ? "on" : "off";
                flipped(toggle.State);
                SaveSettings();
            };
        }

        // The editor itself
        string[] themes = [.. UISkins.Find("flat")?.Themes ?? []];
        if (themes.Length > 0)
        {
            Choice(Row("Editor theme", "The colours of the editor itself"), themes, Math.Max(0, Array.IndexOf(themes, settings.EditorTheme)),
                index => UseEditorTheme(themes[index], save: true));
        }

        string[] sizes = [.. Scales.Select(DescribeScale)];
        Choice(Row("Editor size", "How big the editor draws itself and the layouts in it"), sizes, Math.Max(0, Array.IndexOf(Scales, compositor.Scale)),
            index => SetScale(Scales[index], remember: true));

        // What the layouts are shown in, which is the game's setting and not the editor's own
        Row("Layouts are shown in", "The skin and the theme the layouts in the canvas are drawn in, the same as the Skin menu")
            .Add(new SkinPicker { TextScale = EDITOR_TEXT });

        string[] grids = [.. HexSettings.Grids.Select(grid => grid > 0.0f ? $"every {grid:0}" : "off")];
        Choice(Row("Snap to a grid", "What positions and sizes land on when something is dragged in the canvas"), grids,
            Math.Max(0, Array.IndexOf(HexSettings.Grids, settings.Snap)),
            index =>
            {
                settings.Snap = HexSettings.Grids[index];
                SaveSettings();
            });

        Switch("Resize handles", "Whether what is selected in the canvas can be dragged bigger by its edges", settings.CanvasHandles,
            on => settings.CanvasHandles = on);
        Switch("Open the last layout", "Whether the editor starts with the layout that was worked on last", settings.ReopenLast,
            on => settings.ReopenLast = on);

        Row("Panels", "The lists, the properties, the canvas and the layers, as wide and as tall as they come")
            .Add(new Button("Put them back") { Size = new Vector2(SETTING_VALUE_WIDTH, EDITOR_HEIGHT), LabelScale = EDITOR_TEXT, OnPressed = ResetPanels });
    }

    private void ResetPanels()
    {
        settings.LeftWidth = settings.RightWidth = settings.CanvasHeight = settings.LayersHeight = 0.0f;
        ApplyPanelSizes();
        SaveSettings();
        Say("the panels are the size they come at");
    }

    /* Dragging what is selected in the canvas bigger */

    /// <summary>
    /// Helper method to let the one thing that is selected be dragged bigger by its edges, the tabs on them are the
    /// compositor's to draw. Returns whether the pointer is busy with that, in which case it is nobody else's.
    /// </summary>
    private bool UpdateCanvasHandles(UIPointer pointer, Vector2 inLayout, bool overCanvas, bool pressed)
    {
        if (resizing is { } held)
        {
            if (pointer.Down)
            {
                if (held.DragResize(inLayout, settings.Snap))
                    codeDirty = true;
            }
            else
            {
                held.EndResize();
                resizing = null;

                // The numbers in the inspector are from before the drag
                inspectorDirty = true;
                stage.HighlightLitGrips = UIResize.None;
            }

            return true;
        }

        UIComponent? only = settings.CanvasHandles && document.Selection.Count == 1 ? document.Selected : null;
        stage.HighlightGrips = only is null ? UIResize.None : UIResize.All;

        UIResize on = only is not null && overCanvas ? only.EdgesAt(inLayout, UIResize.All, HANDLE_INSIDE, HANDLE_OUTSIDE) : UIResize.None;
        stage.HighlightLitGrips = on;

        if (!pressed || on == UIResize.None)
            return false;

        only!.BeginResize(on, inLayout);
        stage.HighlightLitGrips = on;
        resizing = only;
        return true;
    }

    /// <summary>Helper method to have a move land on the grid, if there is one. What is left over stays with the pointer.</summary>
    private Vector2 Snapped(Vector2 moved)
    {
        if (settings.Snap <= 0.0f)
            return moved;

        return new Vector2(MathF.Truncate(moved.X / settings.Snap), MathF.Truncate(moved.Y / settings.Snap)) * settings.Snap;
    }
}
