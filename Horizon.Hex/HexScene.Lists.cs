using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

namespace Horizon.Hex;

// The lists the editor fills in. The tabs, the tree of the layout, its layers, and the files of a folder when a layout is being opened.
internal sealed partial class HexScene
{
    private void RebuildTabs()
    {
        tabButtons.Clear();
        var items = layout.Populate(tabs, documents.Count);

        for (int i = 0; i < items.Count; i++)
        {
            HexDocument shown = documents[i];
            var tab = items[i].Get<Button>("tab");

            // With a star for as long as it has changes that aren't in its file
            tab.Label = shown.Modified ? $"{shown.Name} *" : shown.Name;
            tab.Selected = shown == document;
            tab.OnPressed = () => Show(shown);
            tabButtons.Add(tab);
        }
    }

    private void RebuildTree()
    {
        treeRows.Clear();
        fileRows.Clear();

        if (browsing)
        {
            RebuildFiles();
            return;
        }

        treeTitle.Text = "LAYOUT";

        var components = document.Walk().ToList();
        var items = layout.Populate(tree, components.Count);

        for (int i = 0; i < items.Count; i++)
        {
            var (component, depth) = components[i];
            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            // The text steps in with how deep in the layout the component is
            caption.Text = $"{document.NameOf(component)}  ({UIModule.KindOf(component)})";
            caption.Position = new Vector2(10 + depth * 14, 0);
            caption.Color = component.Visible && !component.IsHiddenByLayer ? null : DimColor;

            row.Selected = component == document.Selected;
            row.OnPressed = () => Select(component);
            treeRows[component] = row;
        }

        if (components.Count == 0)
            tree.Add(new Label("nothing yet, add something\nwith one of the buttons above")
            {
                Anchor = Origin.Left,
                Align = Origin.TopLeft,
                TextScale = EDITOR_TEXT,
                Color = DimColor
            });

        RebuildLayers();
    }

    /* Layers: parts of a layout that are shown and hidden together, so what is laid over something else can be put
       out of the way while that is worked on. Which layer a component is on is one of its properties and goes into
       the file, which of them are hidden right now is only how the editor is looking at the layout. */

    private void RebuildLayers()
    {
        layerRows.Clear();

        var names = document.Module.Layers;
        var items = layout.Populate(layers, names.Count);

        for (int i = 0; i < items.Count; i++)
        {
            string name = names[i];
            bool hidden = document.Module.IsLayerHidden(name);

            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            caption.Text = hidden ? $"{name}  (hidden)" : name;
            caption.Color = hidden ? DimColor : null;

            row.OnPressed = () => ToggleLayer(name);
            layerRows[name] = row;
        }

        if (names.Count == 0)
        {
            layers.Add(new Label("none yet: a component gets one\nin its properties, under layer")
            {
                Anchor = Origin.Left,
                Align = Origin.TopLeft,
                TextScale = EDITOR_TEXT,
                Color = DimColor
            });
        }
    }

    private void ToggleLayer(string name)
    {
        bool show = document.Module.IsLayerHidden(name);
        document.Module.SetLayerVisible(name, show);

        // The tree greys out what is out of sight
        treeDirty = true;
        Say(show ? $"showing the layer {name}" : $"hid the layer {name}, it is still in the layout and in the file");
    }

    private void ShowAllLayers()
    {
        document.Module.ShowAllLayers();
        treeDirty = true;
    }

    /// <summary>
    /// Helper to list the folder a layout is being opened from. The way up, the folders in it and its layouts.
    /// </summary>
    private void RebuildFiles()
    {
        treeTitle.Text = "OPEN A LAYOUT";
        treeScroll.Offset = 0;

        var entries = new List<(string Path, bool IsDirectory, string Text)>();
        try
        {
            if (Directory.GetParent(browseDirectory) is { } parent)
                entries.Add((parent.FullName, true, "../"));

            foreach (string directory in Directory.GetDirectories(browseDirectory).Order())
                entries.Add((directory, true, Path.GetFileName(directory) + "/"));

            foreach (string file in Directory.GetFiles(browseDirectory, "*.hor").Order())
                entries.Add((file, false, Path.GetFileName(file)));
        }
        catch (Exception e)
        {
            Say($"couldn't look into {browseDirectory}: {e.Message}", error: true);
        }

        var items = layout.Populate(tree, entries.Count);

        for (int i = 0; i < items.Count; i++)
        {
            var (path, isDirectory, text) = entries[i];
            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            caption.Text = text;
            caption.Color = isDirectory ? FolderColor : null;
            row.OnPressed = () => PickFile(path, isDirectory);
            fileRows.Add((path, isDirectory, row));
        }
    }

    private void PickFile(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            // New layouts are saved to wherever was looked at last
            browseDirectory = path;
            treeDirty = true;
            Say($"files of {browseDirectory}");
            return;
        }

        Open(path);
    }
}
