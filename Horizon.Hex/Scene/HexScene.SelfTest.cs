using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Horizon.Hex;

/// <summary>
/// The editor testing itself. A scripted pointer adds, selects, edits, drags, saves and opens the way somebody
/// with a mouse would, and every step of the way what the editor ended up with is checked and printed.
/// Started with <c>--selftest</c>, see <see cref="Program"/>.
/// </summary>
internal sealed partial class HexScene
{
    private HexPointerScript? selfTest;
    private bool selfTestReported;

    private void UpdateSelfTest(float dt)
    {
        if (selfTest is null)
            return;

        selfTest.Update(dt);

        if (selfTest.IsFinished && !selfTestReported)
        {
            selfTestReported = true;

            if (options.Exit)
                Quit(selfTest.Failed);
        }
    }

    private void StartSelfTest()
    {
        var test = selfTest = new HexPointerScript { Pace = options.Watch ? 8 : 1 };
        compositor.PointerSource = () => test.Pointer;

        string workspace = Path.GetFullPath(options.Workspace);
        string savedFile = Path.Combine(workspace, "selftest_menu.hor");

        // What the test makes along the way, to ask about later
        UIComponent? stack = null, button = null, label = null, panel = null;
        Vector2 last = Vector2.Zero;

        bool Near(float a, float b, float within = 0.5f) => MathF.Abs(a - b) <= within;

        // The middle of a component (or another point of it), wherever it has got to by the time the pointer goes there
        Func<Vector2> At(Func<UIComponent?> find, float x = 0.5f, float y = 0.5f) => () =>
        {
            if (find() is { Module: not null } component)
            {
                UIRect bounds = component.Bounds;
                last = component.Module.ToWorld(new Vector2(bounds.Min.X + bounds.Width * x, bounds.Min.Y + bounds.Height * y));
            }

            return last;
        };

        // Scrolls the properties to the row of a property, the way somebody would before clicking it. The rows are in
        // sections now and the ones further down are out of sight at the start
        void Reveal(string editor)
        {
            test.Run(() => inspectorScroll.ScrollIntoView(editors[editor]));
            test.Wait();
        }

        // Something out of one of the menus. A click on its word on the bar, and one on the item in the list that opens
        void Pick(string title, Func<string> item)
        {
            test.Click(() => chrome.ToWorld(menu.TitleBounds(title).Center));
            test.Click(() => chrome.ToWorld(menu.ItemBounds(item()).Center));
        }

        Func<Vector2> Part(string name, float x = 0.5f, float y = 0.5f) => At(() => layout.Get<UIComponent>(name), x, y);
        Func<Vector2> Kind(string kind) => At(() => paletteButtons[Array.IndexOf(Palette, kind)]);
        Func<Vector2> Row(Func<UIComponent?> of) => At(() => of() is { } component ? treeRows.GetValueOrDefault(component) : null);
        Func<Vector2> File(string name) => At(() => fileRows.Find(row => Path.GetFileName(row.Path) == name).Row);

        // A row of the list a dropdown of the inspector has open, and a point of one of the areas of a colour picker
        Func<Vector2> Option(string editor, int row) => () => chrome.ToWorld(((Dropdown)editors[editor]).RowBounds(row).Center);
        Func<Vector2> In(Func<UIRect> area, float x, float y) => () => chrome.ToWorld(area().Min + area().Size * new Vector2(x, y));

        // Whether the three columns of the editor are as tall as each other and reach the edges of the window
        bool Fits()
        {
            UIRect screen = chrome.Root.Bounds;
            float edge = root.Padding.Left;

            // The columns stand on the line the editor talks in, which is along the bottom of the window
            float gap = root.Spacing >= 0.0f ? root.Spacing : compositor.Skin?.Spacing ?? 0.0f;
            float floor = screen.Min.Y + root.Padding.Bottom + status.Bounds.Height + gap;

            return Near(left.Bounds.Height, center.Bounds.Height, 2) && Near(center.Bounds.Height, right.Bounds.Height, 2)
                && Near(body.Bounds.Min.Y, floor, 3) && Near(left.Bounds.Min.X, screen.Min.X + edge, 3)
                && (Near(right.Bounds.Max.X, screen.Max.X - edge, 3) || Near(canvas.Bounds.Width, MIN_CENTER, 1));
        }

        string Code() => document.GenerateCode();
        void Type(TextBox box, string text)
        {
            box.Clear();
            box.Insert(text);
        }

        // The first layout has to have happened before there is anything to point at, and a window that starts
        // out maximised takes a moment to get there
        test.Settle(0.5f);
        test.WaitFor(() => Near(chrome.Root.Bounds.Width * compositor.UIScale, Engine.WindowManager.ViewportSize.X, 1), 3.0f);
        test.Settle(0.3f);

        /* The editor itself */

        test.Check("layout: the editor is drawn at the scale it was asked for, layouts and all", () =>
        {
            Vector2 viewport = Engine.WindowManager.ViewportSize;
            UIRect screen = chrome.Root.Bounds;

            return compositor.UIScale == compositor.Scale && stage.UIScale == compositor.UIScale
                && Near(screen.Width * compositor.UIScale, viewport.X, 1) && Near(screen.Height * compositor.UIScale, viewport.Y, 1);
        });
        test.Check("layout: its columns take the room the window has", Fits);
        test.Check("layout: the editor reads in a font of its own, the layouts in the one games have", () =>
            compositor.Skin is { } own && stage.Skin is { } staged && own.Font.LineHeight != staged.Font.LineHeight);
        test.Check("bootstrap: the palette was filled from its template", () =>
            paletteButtons.Count == Palette.Length && paletteButtons[0].Label == "stack" && paletteButtons[0].Parent == layout.Get<GridPanel>("palette"));

        /* Adding */

        test.Click(Kind("stack"));
        test.Wait();
        test.Check("add: a stack onto the screen, selected", () =>
            (stack = document.Selected) is StackPanel && stack.Parent == document.Module.Root && document.Walk().Count() == 1);

        test.Click(Kind("button"));
        test.Wait();
        test.Check("add: a button goes into the selected container", () => (button = document.Selected) is Button && button.Parent == stack);

        test.Click(Kind("label"));
        test.Wait();
        test.Check("add: and next to the selection when that isn't a container", () =>
            (label = document.Selected) is Label && label.Parent == stack && document.Walk().Count() == 3);
        test.Check("tree: a row for each of them, the selected one lit", () =>
            treeRows.Count == 3 && treeRows[label!].Selected && !treeRows[button!].Selected);
        test.Check("code: written in the order they are in the layout", () =>
        {
            string written = Code();
            return written.Contains("let stack1 = compositor.stack(")
                && written.Contains("let button1 = compositor.button({ parent: stack1, label: \"Button\" });")
                && written.IndexOf("button1") < written.IndexOf("label1");
        });

        /* Selecting */

        test.Click(At(() => button));
        test.Wait();
        test.Check("canvas: clicking a component selects it", () => document.Selected == button);
        test.Check("canvas: and has the UI mark it out", () => stage.Highlighted == button);
        test.Check("inspector: a row for every property of a button", () =>
            editors.ContainsKey("label") && editors.ContainsKey("size") && editors.ContainsKey("anchor") && editors.ContainsKey("visible")
            && editors["label"] is TextBox { Text: "Button" } && !editors.ContainsKey("on_pressed"));

        test.Click(Row(() => label));
        test.Wait();
        test.Check("tree: clicking a row selects its component", () => document.Selected == label && editors["text"] is TextBox { Text: "Label" });

        test.Click(Row(() => button));
        test.Wait();

        /* Editing properties */

        test.Click(At(() => editors["label"]));
        test.Wait();
        test.Check("inspector: clicking a text box gives it the focus", () => editors["label"].IsFocused);

        test.Run(() => Type((TextBox)editors["label"], "Fight"));
        test.Wait();
        test.Check("inspector: typing changes the component", () => ((Button)button!).Label == "Fight");
        test.Check("inspector: and the code, on screen as well", () => Code().Contains("label: \"Fight\"") && code.Text.Contains("label: \"Fight\""));

        Vector2 grab = Vector2.Zero;
        test.Run(() => grab = At(() => ((StackPanel)editors["size"]).Children[0], 0.3f)());
        test.Drag(() => grab, () => grab + new Vector2(160, 0) * compositor.UIScale);
        test.Wait();
        test.Check("inspector: dragging across a number box changes the number", () => Near(button!.Size.X, 40, 1.5f) && Code().Contains("size: vec("));

        test.Click(At(() => editors["anchor"]));
        test.Wait();
        test.Check("dropdown: clicking it opens its list on top of the inspector", () =>
            editors["anchor"] is Dropdown { IsOpen: true } open && chrome.Popup == open);
        test.Click(Option("anchor", 1));
        test.Wait();
        test.Check("dropdown: clicking an option chooses it and closes the list", () =>
            button!.Anchor == Horizon.Rendering.Origin.TopLeft && Code().Contains("anchor: \"top_left\"") && editors["anchor"] is Dropdown { IsOpen: false });
        test.Click(At(() => editors["anchor"]));
        test.Click(Option("anchor", 0));
        test.Wait();
        test.Check("dropdown: and back, which takes it out of the code again", () => button!.Anchor == Horizon.Rendering.Origin.Center && !Code().Contains("anchor:"));
        test.Click(At(() => editors["anchor"]));
        test.Click(At(() => editors["kind"]));
        test.Wait();
        test.Check("dropdown: clicking anywhere else closes the list and changes nothing", () =>
            chrome.Popup is null && button!.Anchor == Horizon.Rendering.Origin.Center);

        /* Entrances */

        Reveal("intro_time");
        test.Click(At(() => editors["intro"]));
        test.Click(Option("intro", 1));
        test.Wait();
        test.Check("intro: a component can be told to pop in", () => button!.Intro == UIIntro.Pop && Code().Contains("intro: \"pop\""));
        test.Check("intro: and does so straight away, to be looked at", () => button!.Tweens.Count > 0);
        test.Run(() => document.SettleIntros());
        test.Wait();
        test.Click(At(() => layout.Get<Button>("play_button")));
        test.Wait();
        test.Check("intro: the play button next to the name of the layout plays them again", () => button!.Tweens.Count > 0);

        test.Click(At(() => ((StackPanel)editors["intro_time"]).Children[0]));
        test.Wait();
        test.Check("slider: clicking halfway along sets the number halfway", () =>
            Near(button!.IntroTime, 1.0f, 0.12f) && Code().Contains("intro_time: ")
            && ((StackPanel)editors["intro_time"]).Children[1] is NumberBox shown && Near(shown.Value, button.IntroTime, 0.001f));

        test.Run(() => Type((NumberBox)((StackPanel)editors["intro_time"]).Children[1], "0.35"));
        test.Click(At(() => editors["intro"]));
        test.Click(Option("intro", 0));
        test.Wait();
        test.Check("intro: put back, there is nothing about it in the code", () =>
            button!.Intro == UIIntro.None && Near(button.IntroTime, 0.35f, 0.001f) && !Code().Contains("intro"));

        /* Undoing */

        string settled = string.Empty;
        test.Settle(0.6f);
        test.Run(() =>
        {
            settled = Code();
            Type((TextBox)editors["label"], "Brawl");
        });
        test.Settle(0.6f);
        test.Check("undo: the button is lit once there is something to take back", () => (document.CanUndo || recordPending) && ((Button)button!).Label == "Brawl");

        Pick("Edit", () => "Undo");
        test.Settle(0.3f);
        test.Check("undo: takes the last change back, and only that one", () =>
            Code() == settled && document.Find("button1") is Button { Label: "Fight" });
        test.Check("undo: what was selected still is", () =>
            document.Selected == document.Find("button1") && stage.Highlighted == document.Selected && editors["label"] is TextBox { Text: "Fight" });

        Pick("Edit", () => "Redo");
        test.Settle(0.3f);
        test.Check("redo: makes the change again", () => document.Find("button1") is Button { Label: "Brawl" } && !document.CanRedo);

        Pick("Edit", () => "Undo");
        test.Settle(0.3f);
        test.Run(() =>
        {
            // Going back builds the layout again, what the test was holding on to is from before
            stack = document.Find("stack1");
            button = document.Find("button1");
            label = document.Find("label1");
        });
        test.Check("undo: and takes it back again", () => Code() == settled && button is Button { Label: "Fight" } && document.CanRedo);

        test.Click(At(() => editors["visible"], 0.9f));
        test.Wait();
        test.Check("inspector: hiding a component greys its row out", () => !button!.Visible && Code().Contains("visible: false"));
        test.Click(At(() => editors["visible"], 0.9f));
        test.Wait();

        test.Run(() => Type((TextBox)editors["name"], "start_button"));
        test.Wait();
        test.Check("rename: the code and the tree go by the new name", () =>
            document.NameOf(button!) == "start_button" && Code().Contains("let start_button = compositor.button(")
            && treeRows[button!].Children[0] is Label { Text: "start_button  (button)" });

        test.Run(() => Type((TextBox)editors["name"], "label1"));
        test.Wait();
        test.Check("rename: a name another component has is refused", () => document.NameOf(button!) != "label1" && document.NameOf(label!) == "label1");
        test.Run(() => Type((TextBox)editors["name"], "start_button"));
        test.Wait();

        // Resting on what a property is called says what it does, and so does resting on its editor
        UIComponent CaptionOf(string property) => editors[property].Parent!.Parent!.Children[0];
        test.Hover(At(() => CaptionOf("pos")));
        test.Settle(0.8f);
        test.Check("help: resting on a property's name brings up what it does", () =>
            compositor.TooltipOf == CaptionOf("pos") && compositor.TooltipOf!.Tooltip.Contains("anchor"));
        test.Hover(At(() => editors["anchor"]));
        test.Settle(0.8f);
        test.Check("help: and so does resting on its editor", () =>
            compositor.TooltipOf is { } helping && helping.Tooltip.Contains("parent") && helping != CaptionOf("pos"));
        test.Hover(Part("canvas", 0.5f, 0.5f));
        test.Wait();

        /* The canvas */

        test.Click(Part("canvas", 0.04f, 0.06f));
        test.Wait();
        test.Check("canvas: clicking where there is nothing selects nothing", () => document.Selected is null && editors.Count == 0);

        test.Click(Kind("panel"));
        test.Wait();
        test.Check("add: onto the screen when nothing is selected", () => (panel = document.Selected) is Panel && panel.Parent == document.Module.Root);

        Vector2 before = Vector2.Zero;
        Vector2 pull = new(120, -60);
        test.Run(() =>
        {
            before = panel!.Position;
            grab = At(() => panel, 0.5f, 0.9f)();
        });
        test.Drag(() => grab, () => grab + pull * compositor.UIScale);
        test.Wait();
        test.Check("canvas: dragging moves a component by as much of the layout as the pointer covered", () =>
            Vector2.Distance(panel!.Position - before, pull / document.Module.Scale.X) < 2.5f && Code().Contains("pos: vec("));
        test.Check("canvas: and the inspector shows where it ended up", () =>
            editors["pos"] is StackPanel row && row.Children[0] is NumberBox x && Near(x.Value, panel!.Position.X, 0.01f));

        /* Reordering and deleting */

        test.Click(Row(() => label));
        test.Wait();

        /* Colours */

        ColorPicker Picker() => (ColorPicker)((StackPanel)editors["color"]).Children[0];

        // The colour is the last but one of what a label has, in a small window that is past the end of what is in sight
        Reveal("color");
        test.Check("colour: a colour has a picker and its four numbers", () =>
            editors["color"] is StackPanel { Children: [ColorPicker, StackPanel { Children.Count: 4 }] });
        test.Click(In(() => Picker().AlphaBounds, 0.5f, 0.97f));
        test.Drag(In(() => Picker().ShadeBounds, 0.3f, 0.4f), In(() => Picker().ShadeBounds, 0.95f, 0.95f));
        test.Wait();
        test.Check("colour: dragging through the square picks a shade, the strip how solid it is", () =>
            ((Label)label!).Color is { X: > 0.8f, Y: < 0.2f, Z: < 0.2f, W: > 0.9f } && Code().Contains("color: vec("));
        test.Click(In(() => Picker().HueBounds, 0.5f, 0.34f));
        test.Wait();
        test.Check("colour: the other strip picks the hue", () => ((Label)label!).Color is { X: < 0.2f, Y: > 0.8f, W: > 0.9f });
        test.Check("colour: and the numbers follow", () =>
            ((StackPanel)editors["color"]).Children[1].Children[1] is NumberBox green && Near(green.Value, ((Label)label!).Color!.Value.Y, 0.002f));

        /* The sections of the properties, the tab key, and the tree keeping up with what is selected */

        Collapsible Section(string editor)
        {
            UIComponent? at = editors[editor];
            while (at is not null and not Collapsible)
                at = at.Parent;
            return (Collapsible)at!;
        }

        test.Check("sections: the properties are in sections by what they are about", () =>
            Section("intro").Title == "entrance" && Section("pos").Title == "placement" && Section("color").Title == "label");
        Reveal("intro");
        test.Click(In(() => Section("intro").HeaderBounds, 0.5f, 0.5f));
        test.WaitFor(() => Section("intro").Openness == 0.0f, 1.0f);
        test.Check("sections: a click on the bar folds it away, and only it", () =>
            Section("intro") is { Expanded: false, Openness: 0.0f } && Section("pos").Expanded);
        test.Run(() => Select(stack));
        test.Wait();
        test.Run(() => Select(label));
        test.Wait();
        test.Check("sections: and it stays folded for the next thing selected", () => !Section("intro").Expanded);
        Reveal("intro");
        test.Click(In(() => Section("intro").HeaderBounds, 0.5f, 0.5f));
        test.WaitFor(() => Section("intro").Openness == 1.0f, 1.0f);
        test.Check("sections: and out again", () => Section("intro") is { Expanded: true, Openness: 1.0f });

        Reveal("text");
        test.Click(At(() => editors["text"]));
        test.Wait();
        test.Run(() => compositor.FocusNext());
        test.Wait();
        test.Check("tab: the next box has all of what is in it picked out, to type over", () =>
            compositor.Focus is TextBox { Text.Length: > 0 } next && next != editors["text"] && next.Selection == next.Text);
        test.Run(() => compositor.FocusNext(-1));
        test.Wait();
        test.Check("tab: and shift goes back, all of it picked out again", () =>
            compositor.Focus == editors["text"] && ((TextBox)editors["text"]).Selection == ((TextBox)editors["text"]).Text);
        test.Run(() => compositor.Focus = null);

        test.Run(() => treeScroll.Offset = treeScroll.MaxOffset);
        test.Run(() => Select(stack));
        test.WaitFor(() => !treeReveal, 1.0f);
        test.Wait();
        test.Check("tree: selecting something scrolls its row into sight", () =>
            treeRows[stack!].Bounds is var row && treeScroll.Bounds.Contains(row.Center) && treeScroll.Bounds.Shrink(treeScroll.Padding).Max.Y >= row.Max.Y - 0.5f);
        test.Run(() => Select(label));
        test.Wait();

        Pick("Edit", () => "Move up");
        test.Wait();
        test.Check("reorder: up puts it before its sibling, in the layout and in the code", () =>
            stack!.Children[0] == label && Code().IndexOf("label1") < Code().IndexOf("start_button ="));
        Pick("Edit", () => "Move down");
        test.Wait();
        test.Check("reorder: and down puts it back", () => stack!.Children[1] == label);

        test.Click(Part("move_up"));
        test.Wait();
        test.Check("reorder: the buttons over the tree do the same", () => stack!.Children[0] == label && treeRows[label!].Selected);
        test.Click(Part("move_down"));
        test.Wait();
        test.Check("reorder: there and back", () => stack!.Children[1] == label);
        Pick("Edit", () => "Move down");
        test.Wait();
        test.Check("reorder: the last one stays the last one", () => stack!.Children[^1] == label);

        test.RightClick(Row(() => label));
        test.Click(() => chrome.ToWorld(contextMenu!.ItemBounds("Move up").Center));
        test.Wait();
        test.Check("reorder: the right click menu of a row moves it up", () => stack!.Children[0] == label);
        test.RightClick(Row(() => label));
        test.Click(() => chrome.ToWorld(contextMenu!.ItemBounds("Move down").Center));
        test.Wait();
        test.Check("reorder: and down again", () => stack!.Children[1] == label);

        Pick("Edit", () => "Move out");
        test.Wait();
        test.Check("reparent: out puts it after the container it was in", () =>
            label!.Parent == document.Module.Root
            && document.Module.Root.Children.ToList().IndexOf(label) == document.Module.Root.Children.ToList().IndexOf(stack!) + 1
            && document.Selected == label);
        Pick("Edit", () => "Move into");
        test.Wait();
        test.Check("reparent: and into puts it back in, at the end", () => label!.Parent == stack && stack!.Children[^1] == label && CheckRoundTrip(document));

        UIComponent? pasted = null;
        int rowsBefore = 0;
        test.Run(() => rowsBefore = treeRows.Count);
        Pick("Edit", () => "Copy");
        Pick("Edit", () => "Paste");
        test.Wait();
        test.Check("clipboard: a copy is pasted next to what's selected, with a name of its own, and selected", () =>
            (pasted = document.Selected) is Label && pasted != label && pasted.Parent == stack
            && document.NameOf(pasted) != document.NameOf(label!) && treeRows.Count == rowsBefore + 1);
        Pick("Edit", () => "Cut");
        test.Wait();
        test.Check("clipboard: cut takes it out again", () => pasted!.Parent is null && treeRows.Count == rowsBefore && document.Selected is null);
        test.Click(Row(() => label));
        test.Wait();

        Pick("Edit", () => "Delete");
        test.Wait();
        test.Check("delete: gone from the layout, the tree and the code", () =>
            label!.Parent is null && !treeRows.ContainsKey(label) && !Code().Contains("label1") && document.Selected is null && treeRows.Count == 3);

        /* Containers that are filled in by the program */

        test.Run(() => System.IO.File.WriteAllText(
            Path.Combine(workspace, "item.hor"),
            "let row = compositor.button({ label: \"an item\", style: \"button_flat\", size: vec(200, 40) });\n"));

        test.Click(Row(() => stack));
        test.Wait();
        test.Run(() =>
        {
            ((TextBox)editors["template"]).Insert("item.hor");
            Type((NumberBox)editors["preview_count"], "3");
        });
        test.Settle(0.3f);
        test.Check("templates: a container shows items made from its template", () => document.PreviewCount == 3 && stack!.Children.Count == 4);
        test.Check("templates: which are in neither the tree nor the code", () =>
            document.Walk().Count() == 3 && treeRows.Count == 3 && Code().Contains("template: \"item.hor\"") && !Code().Contains("an item"));

        test.Click(At(() => stack!.Children[^1]));
        test.Wait();
        test.Check("templates: clicking one of them selects the container", () => document.Selected == stack);

        /* Saving, tabs, opening */

        string saved = string.Empty;

        test.Click(Part("name_box"));
        test.Run(() => Type(nameBox, "selftest_menu"));
        test.Wait();
        test.Check("name: the tab goes by the name of the layout", () => tabButtons.Count == 1 && tabButtons[0].Label.TrimEnd(' ', '*') == "selftest_menu");

        test.Run(() => saved = Code());
        Pick("File", () => "Save");
        test.Wait();
        test.Check("save: the file is the code that is on screen", () =>
            System.IO.File.Exists(savedFile) && System.IO.File.ReadAllText(savedFile) == saved && document.Path == savedFile);

        Pick("File", () => "New");
        test.Wait();
        test.Check("tabs: a new layout has a tab of its own and is the one on screen", () =>
            documents.Count == 2 && tabButtons.Count == 2 && tabButtons[1].Selected && !documents[0].Module.Enabled && documents[1].Module.Enabled && treeRows.Count == 0);

        test.Click(At(() => tabButtons[0]));
        test.Wait();
        test.Check("tabs: clicking a tab shows its layout", () => document == documents[0] && nameBox.Text == "selftest_menu" && treeRows.Count == 3);

        Pick("File", () => "Close");
        test.Wait();
        test.Check("close: the layout is gone and the other one is shown", () => documents.Count == 1 && document.Path is null && tabButtons.Count == 1);

        Pick("File", () => "Open...");
        test.Wait();
        test.Check("open: the list shows the way up and the layouts of the folder", () =>
            browsing && fileRows.Count >= 3 && fileRows[0].IsDirectory
            && fileRows.Exists(row => !row.IsDirectory && Path.GetFileName(row.Path) == "selftest_menu.hor")
            && fileRows.Exists(row => Path.GetFileName(row.Path) == "item.hor"));

        test.Click(File("selftest_menu.hor"));
        test.Settle(0.3f);
        test.Check("open: the layout is back the way it was saved", () =>
            !browsing && documents.Count == 1 && document.Name == "selftest_menu" && Code() == saved && document.Walk().Count() == 3);
        test.Check("open: items and all", () => document.PreviewCount == 3);
        test.Check("open: opening it again shows the one that is open", () => Open(savedFile) && documents.Count == 1);

        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(workspace, "sub"));
            System.IO.File.WriteAllText(Path.Combine(workspace, "sub", "inner.hor"), "let inner = compositor.label({ text: \"inner\" });\n");
        });
        Pick("File", () => "Open...");
        test.Wait();
        test.Click(File("sub"));
        test.Wait();
        test.Check("open: clicking a folder goes into it", () =>
            browsing && browseDirectory == Path.Combine(workspace, "sub") && fileRows.Count == 2 && Path.GetFileName(fileRows[1].Path) == "inner.hor");
        test.Click(At(() => fileRows[0].Row));
        test.Wait();
        test.Check("open: and the first row goes back up", () => browseDirectory == workspace);
        Pick("File", () => "Open...");
        test.Wait();
        test.Check("open: pressing open again goes back to the layout", () => !browsing && treeRows.Count == 3);

        /* The editor in itself */

        string own = System.IO.File.ReadAllText(EDITOR_LAYOUT);

        Pick("File", () => "The editor's own UI");
        test.Settle(0.4f);
        test.Check("bootstrap: the editor opens its own UI", () => document.Name == "editor" && documents.Count == 2 && document.Walk().Count() == layout.Parts.Count);
        test.Check("bootstrap: and writes it back the same", () => CheckRoundTrip(document));
        test.Check("bootstrap: with stand-ins where it fills things in itself", () => document.PreviewCount >= 20);

        Pick("File", () => "Save");
        test.Wait();
        test.Check("bootstrap: saving it makes a copy rather than touch the file the editor runs on", () =>
            document.Path == Path.Combine(workspace, "editor_edited.hor") && System.IO.File.Exists(document.Path) && System.IO.File.ReadAllText(EDITOR_LAYOUT) == own);

        /* Scrolling */

        test.Hover(Part("tree_scroll"));
        test.Run(() => compositor.Scroll(-3));
        test.Settle(0.2f);
        test.Check("scroll: the wheel scrolls the list under the pointer", () => Near(treeScroll.Offset, 3 * treeScroll.WheelStep));

        if (options.Wheel)
        {
            float offset = 0;
            test.Run(() =>
            {
                offset = treeScroll.Offset;
                Console.WriteLine("[Hex self-test] waiting for the mouse wheel");
            });
            test.WaitFor(() => treeScroll.Offset != offset, 10.0f);
            test.Check("scroll: the real mouse wheel gets there too", () => treeScroll.Offset != offset);
        }

        /* The size of the editor */

        float scaleBefore = 0.0f, scaleAfter = 0.0f;
        test.Run(() =>
        {
            scaleBefore = compositor.Scale;
            scaleAfter = Scales[(Array.IndexOf(Scales, scaleBefore) + 1) % Scales.Length];
        });
        Pick("View", () => DescribeScale(scaleAfter));
        test.Settle(0.4f);
        test.Check("scale: the menu draws the editor and the layouts at the next size", () =>
            compositor.Scale == scaleAfter && compositor.UIScale == scaleAfter && stage.UIScale == scaleAfter);
        test.Check("scale: and the columns make do with the room that leaves", Fits);
        Pick("View", () => "Layout debugger");
        test.Wait();
        test.Check("scale: buttons are still where they are drawn", () => layoutDebugger.IsOn);
        Pick("View", () => "Layout debugger");
        Pick("View", () => DescribeScale(scaleBefore));
        test.Settle(0.4f);
        test.Check("scale: and back", () => compositor.UIScale == scaleBefore && Fits());

        /* The layout debugger */

        Pick("View", () => "Layout debugger");
        test.Wait();
        test.Check("debugger: the menu switches the layout debugger on", () => layoutDebugger.IsOn && stage.LayoutOverlay == layoutDebugger.Options);

        // Zoomed far in the layout is bigger than the canvas, and neither it nor the boxes the debugger draws
        // about it may run on out of the canvas over the lists either side
        test.Run(() => ZoomCanvas(8.0f, canvas.Bounds.Center));
        test.Settle(0.4f);
        test.Check("debugger: zoomed in, the layout and what is drawn about it are cut off at the canvas", () =>
            document.Module.Clip is { } cut && Near(cut.Width * document.Module.Scale.X, canvas.Bounds.Width, 1.0f)
            && Near(cut.Height * document.Module.Scale.Y, canvas.Bounds.Height, 1.0f) && document.Module.Frame.Width * document.Module.Scale.X > canvas.Bounds.Width);
        test.Run(ZoomToFit);
        test.Wait();
        Pick("View", () => "Layout debugger");
        test.Wait();
        test.Check("debugger: and off again", () => !layoutDebugger.IsOn && stage.LayoutOverlay is null);

        /* Lots of things at once: selecting more than one, groups, copies, what's on top and the right click menu */

        UIComponent? first = null, second = null, grouped = null;
        UIRect firstWas = default, secondWas = default;

        // Where something is on the screen, which grouping and ungrouping mustn't change
        UIRect Seen(UIComponent? component) => component is { Module: { } module }
            ? new UIRect(module.ToWorld(component.Bounds.Min), module.ToWorld(component.Bounds.Max))
            : default;
        bool Same(UIRect a, UIRect b) => Near(a.Center.X, b.Center.X, 1) && Near(a.Center.Y, b.Center.Y, 1) && Near(a.Width, b.Width, 1);
        Func<Vector2> MenuItem(string item) => () => chrome.ToWorld(contextMenu!.ItemBounds(item).Center);
        Func<Vector2> Nothing() => Part("canvas", 0.04f, 0.5f);

        Pick("File", () => "New");
        test.Settle(0.3f);
        test.Click(Kind("label"));
        test.Wait();
        test.Run(() => first = document.Selected);
        test.Click(Kind("button"));
        test.Wait();
        test.Run(() =>
        {
            second = document.Selected;
            second!.Position = new Vector2(0, -120);
            codeDirty = true;
        });
        test.Wait();
        test.Check("multiselect: two things to play with, next to each other", () => first is Label && second is Button && first.Parent == second.Parent);

        test.Click(At(() => first));
        test.Run(() => fakeModifier = true);
        test.Click(At(() => second));
        test.Run(() => fakeModifier = false);
        test.Wait();
        test.Check("multiselect: ctrl and a click adds to what's selected", () =>
            document.Selection.Count == 2 && stage.Highlights.Count == 2 && treeRows[first!].Selected && treeRows[second!].Selected);

        // The style of the button, picked out of the art of the skin. The button was picked last, the rows are its.
        // Its row is a good way down the properties by now, so it is brought into view first
        test.Run(() => inspectorScroll.ScrollIntoView(editors["style"]));
        test.Wait();
        test.Click(() => chrome.ToWorld(((TextBox)editors["style"]).ButtonBounds.Center));
        test.Settle(0.3f);
        test.Check("art: the button in the style box opens the art of the skin the layouts wear", () =>
            artPicker is { IsOpen: true, Tiles.Count: > 10 } && editors["style"] is TextBox { HasButton: true });
        test.Run(() => artPicker!.Filter = "button_flat");
        test.Settle(0.3f);
        test.Check("art: typing narrows it down", () => artPicker!.Tiles.Count is > 0 and < 40);
        test.Click(() => chrome.ToWorld(artPicker!.Tiles[0].Bounds.Center));
        test.Wait();
        test.Check("art: a click on a piece picks it, for the box, the component and the code", () =>
            !artPicker!.IsOpen && second is Button { Style: var style } && style.StartsWith("button_flat")
            && ((TextBox)editors["style"]).Text == style && Code().Contains($"style: \"{style}\""));
        test.Run(() => Set(second!, "style", new StringValue("button")));
        test.Wait();

        Vector2 firstAt = default;
        test.Run(() =>
        {
            firstAt = first!.Position;
            Set(second!, "intro", new StringValue("fade"));
            Set(second!, "intro_delay", new NumberValue(0.5f));
        });
        test.Check("multiselect: a property changed in the inspector is changed for everything selected", () =>
            first!.Intro == UIIntro.Fade && second!.Intro == UIIntro.Fade && Near(first.IntroDelay, 0.5f, 0.001f) && Near(second.IntroDelay, 0.5f, 0.001f));
        test.Run(() => ((NumberBox)((StackPanel)editors["pos"]).Children[0]).OnValueChanged!(33));
        test.Check("multiselect: one number of a position only changes that number for the rest", () =>
            Near(second!.Position.X, 33) && Near(first!.Position.X, 33) && Near(first.Position.Y, firstAt.Y) && !Near(first.Position.Y, second.Position.Y, 20));
        test.Run(() =>
        {
            Set(second!, "intro", new StringValue("none"));
            Set(second!, "intro_delay", new NumberValue(0.0f));
            document.SettleIntros();
        });
        test.Wait();

        Pick("Arrange", () => "Left edges");
        test.Wait();
        test.Check("arrange: left edges puts two things on the one line", () =>
            Near(first!.Bounds.Min.X, second!.Bounds.Min.X, 0.6f) && !Near(first.Bounds.Center.Y, second.Bounds.Center.Y, 20) && document.Selection.Count == 2);
        Pick("Arrange", () => "Centres across");
        test.Wait();
        test.Check("arrange: and centres across puts their middles on it", () => Near(first!.Bounds.Center.X, second!.Bounds.Center.X, 0.6f));

        test.Run(() =>
        {
            firstWas = Seen(first);
            secondWas = Seen(second);
        });
        Pick("Edit", () => "Group");
        test.Wait();
        test.Check("group: puts what's selected in a group, which is what's selected now", () =>
            (grouped = document.Selected) is Group && first!.Parent == grouped && second!.Parent == grouped && document.Selection.Count == 1);
        test.Check("group: and nothing moves on screen", () => Same(Seen(first), firstWas) && Same(Seen(second), secondWas));
        test.Check("group: which goes in the code", () => Code().Contains("compositor.group("));
        test.Check("group: and writes back the same", () => CheckRoundTrip(document));

        test.Click(Nothing());
        test.Click(At(() => first));
        test.Wait();
        test.Check("group: a click on what's in it picks the group", () => document.Selected == grouped && document.Selection.Count == 1);
        test.Click(At(() => first));
        test.Wait();
        test.Check("group: and another click goes in", () => document.Selected == first);

        test.Click(Nothing());
        test.RightClick(At(() => second));
        test.Wait();
        test.Check("menu: a right click on the canvas picks what's there and opens the menu", () =>
            document.Selected == grouped && contextMenu?.Showing is not null && chrome.Popup == contextMenu);
        test.Click(MenuItem("Ungroup"));
        test.Wait();
        test.Check("ungroup: the menu takes the group apart and selects what was in it", () =>
            grouped!.Parent is null && first!.Parent == document.Module.Root && second!.Parent == first.Parent && document.Selection.Count == 2);
        test.Check("ungroup: and nothing moves on screen", () => Same(Seen(first), firstWas) && Same(Seen(second), secondWas));

        Pick("Edit", () => "Duplicate");
        test.Wait();
        test.Check("duplicate: copies everything selected, and selects the copies", () =>
            document.Walk().Count() == 4 && document.Selection.Count == 2 && !document.IsSelected(first!) && !document.IsSelected(second!));
        test.Check("duplicate: with names of their own", () =>
            document.Selection.Select(document.NameOf).Distinct().Count() == 2 && !document.Selection.Any(copy => document.NameOf(copy) == document.NameOf(first!)));

        test.RightClick(Row(() => first));
        test.Wait();
        test.Click(MenuItem("Bring to front"));
        test.Wait();
        test.Check("order: the menu of a row of the tree brings it to the front", () =>
            document.Selected == first && document.Module.Root.Children[^1] == first);
        test.RightClick(At(() => first));
        test.Wait();
        test.Click(MenuItem("Send to back"));
        test.Wait();
        test.Check("order: and the one on the canvas sends it to the back, code and all", () =>
            document.Module.Root.Children[0] == first && Code().IndexOf($"let {document.NameOf(first!)} ") < Code().IndexOf($"let {document.NameOf(second!)} "));

        /* What a layout is made for, and the canvas */

        Pick("Layout", () => DescribeDesign(new Vector2(1600, 900)));
        test.Wait();
        test.Check("design: the layout menu says what it's made for, in the code before anything else", () =>
            document.Design == new Vector2(1600, 900) && Code().Contains("compositor.design({ size: vec(1600, 900), fit: \"contain\" });")
            && Code().IndexOf("compositor.design") < Code().IndexOf("let "));
        Pick("Layout", () => "Preview on 21:9, an ultrawide");
        test.Wait();
        test.Check("design: on an ultrawide it keeps its shape, in the middle", () =>
            document.Module.Viewport is { } seen && Near(seen.Width / seen.Height, 21.0f / 9.0f, 0.01f)
            && Near(document.Module.Frame.Width, 1600) && Near(document.Module.Frame.Height, 900) && Near(document.Module.Frame.Center.X, seen.Center.X));
        Pick("Layout", () => "Take the whole screen (stretch)");
        test.Wait();
        test.Check("design: stretched it takes the lot", () =>
            document.Fit == UIFit.Stretch && Near(document.Module.Frame.Width, document.Module.Viewport!.Value.Width) && Code().Contains("fit: \"stretch\""));
        Pick("Layout", () => "Preview on its own shape");
        Pick("Layout", () => "Made for any screen");
        test.Wait();
        test.Check("design: made for any screen there's nothing about it in the code", () => document.Design is null && !Code().Contains("compositor.design"));

        float fitted = 0;
        test.Run(() => fitted = document.Module.Scale.X);
        Pick("View", () => "Zoom in");
        test.Wait();
        test.Check("zoom: the view menu zooms the canvas in", () => canvasZoom > 1.0f && document.Module.Scale.X > fitted * 1.2f);
        Pick("View", () => "Zoom to fit");
        test.Wait();
        test.Check("zoom: and back to fit", () => canvasZoom == 1.0f && Near(document.Module.Scale.X, fitted, 0.001f));

        /* Tabs */

        TabPanel? tabbed = null;
        Pick("Edit", () => "Select nothing");
        test.Click(Kind("tabs"));
        test.Wait();
        test.Check("tabs: come with a page for each tab", () =>
            (tabbed = document.Selected as TabPanel) is { Children.Count: 2 } && string.Join(",", tabbed.Tabs) == "One,Two" && tabbed.Selected == 0);
        test.Click(() => document.Module.ToWorld(tabbed!.TabBounds(1).Center));
        test.Wait();
        test.Check("tabs: a click on a tab in the canvas opens its page", () => tabbed!.Selected == 1 && document.Selected == tabbed);
        test.Click(Kind("label"));
        test.Wait();
        test.Check("tabs: what's added goes onto the page that's open", () => document.Selected is Label added && added.Parent == tabbed!.Children[1]);
        test.Check("tabs: and the lot writes back the same", () => CheckRoundTrip(document));

        /* Closing with changes */

        int open = 0;
        test.Run(() => open = documents.Count);
        Pick("File", () => "Close");
        test.Wait();
        test.Check("close: a layout with changes asks first", () => chrome.Dialog is { IsOpen: true } && documents.Count == open);

        // The question pops up, and while it is still growing its buttons are not yet where they will be
        test.Settle(0.35f);

        // Where the answer is, remembered while the question is still up (the click takes it down)
        Vector2 dontSave = Vector2.Zero;
        test.Run(() => dontSave = chrome.Dialog is { } asked ? chrome.ToWorld(asked.Buttons[1].Bounds.Center) : Vector2.Zero);
        test.Click(() => dontSave);
        test.Wait();
        test.Check("close: and goes without saving when told to", () => chrome.Dialog is null && documents.Count == Math.Max(1, open - 1));

        /* Dragging edges, of what is in the canvas and of the editor's own panels */

        // A point of the editor or of the layout in the canvas, in their own units, as the pointer wants it
        Func<Vector2> InChrome(Func<Vector2> point) => () => chrome.ToWorld(point());
        Func<Vector2> InLayout(Func<Vector2> point) => () => document.Module.ToWorld(point());

        UIComponent? box = null;
        Vector2 boxSize = default, boxAt = default;
        Pick("File", () => "New");
        test.Settle(0.3f);
        test.Click(Kind("panel"));
        test.Wait();
        test.Run(() =>
        {
            box = document.Selected;
            Set(box!, "size", new Vector2Value(new Vector2(200, 120)));
        });
        test.Wait();
        test.Run(() => boxSize = box!.Bounds.Size);
        test.Check("handles: the one thing that is selected has a tab on every edge", () =>
            box is Panel && stage.Highlighted == box && stage.HighlightGrips == UIResize.All && Near(boxSize.X, 200) && Near(boxSize.Y, 120));

        test.Hover(InLayout(() => new Vector2(box!.Bounds.Max.X - 2, box.Bounds.Center.Y)));
        test.Check("handles: and the one under the pointer is lit", () => stage.HighlightLitGrips == UIResize.Right);
        test.Drag(
            InLayout(() => new Vector2(box!.Bounds.Max.X - 2, box.Bounds.Center.Y)),
            InLayout(() => new Vector2(box!.Bounds.Center.X + boxSize.X * 0.5f - 2 + 60, box.Bounds.Center.Y)));
        test.Wait();
        test.Check("handles: dragging the right edge makes it that much wider and no taller, code and all", () =>
            Near(box!.Size.X, 260, 1.5f) && Near(box.Size.Y, 120) && Code().Contains("size: vec(") && !dragging);

        test.Run(() => boxSize = box!.Bounds.Size);
        test.Drag(
            InLayout(() => new Vector2(box!.Bounds.Max.X - 2, box.Bounds.Max.Y - 2)),
            InLayout(() => new Vector2(box!.Bounds.Max.X - 2 + 30, box.Bounds.Max.Y - 2 + 40)));
        test.Wait();
        test.Check("handles: a corner drags both", () => box!.Size.X > boxSize.X + 10 && box.Size.Y > boxSize.Y + 10);

        test.Run(() => settings.Snap = 8.0f);
        test.Drag(
            InLayout(() => new Vector2(box!.Bounds.Max.X - 2, box.Bounds.Center.Y)),
            InLayout(() => new Vector2(box!.Bounds.Max.X - 2 + 21, box.Bounds.Center.Y)));
        test.Wait();
        test.Check("snap: with a grid a size lands on it", () => Near(box!.Size.X % 8.0f, 0.0f, 0.01f));
        test.Run(() => boxAt = box!.Position);
        test.Drag(At(() => box), InLayout(() => box!.Bounds.Center + new Vector2(21, 0)));
        test.Wait();
        test.Check("snap: and so does a move, by whole steps of it", () =>
            box!.Position.X > boxAt.X && Near((box.Position.X - boxAt.X) % 8.0f, 0.0f, 0.01f) && Near(box.Position.Y, boxAt.Y));
        test.Run(() => settings.Snap = 0.0f);

        test.Run(() => settings.CanvasHandles = false);
        test.Hover(InLayout(() => new Vector2(box!.Bounds.Max.X - 2, box.Bounds.Center.Y)));
        test.Check("handles: switched off there are none", () => stage.HighlightGrips == UIResize.None && stage.HighlightLitGrips == UIResize.None);
        test.Run(() => settings.CanvasHandles = true);

        test.Run(() => Set(box!, "resize", new StringValue("right, bottom")));
        test.Wait();
        test.Check("resize: a component that can be dragged in a game says so in its code", () =>
            box!.Resizable == (UIResize.Right | UIResize.Bottom) && Code().Contains("resize: \"right, bottom\""));
        test.Check("resize: and writes back the same", () => CheckRoundTrip(document));

        float listWide = 0, canvasTall = 0;
        test.Run(() =>
        {
            listWide = treeScroll.Size.X;
            canvasTall = canvas.Size.Y;
        });
        test.Hover(InChrome(() => new Vector2(treeScroll.Bounds.Max.X + 4, treeScroll.Bounds.Center.Y)));
        test.Check("panels: the edge between the lists and the canvas lights up under the pointer", () => treeScroll.ActiveGrip == UIResize.Right);
        test.Drag(
            InChrome(() => new Vector2(treeScroll.Bounds.Max.X + 4, treeScroll.Bounds.Center.Y)),
            InChrome(() => new Vector2(treeScroll.Bounds.Min.X + listWide + 4 + 70, treeScroll.Bounds.Center.Y)));
        test.Wait();
        test.Check("panels: dragging it makes the lists wider and the canvas make do", () =>
            Near(treeScroll.Size.X, listWide + 70, 2) && Near(settings.LeftWidth, treeScroll.Size.X) && Fits() && !treeScroll.IsResizing);
        test.Check("panels: and nothing in the canvas was touched by it", () => document.Selected == box && !dragging);
        test.Check("panels: the rows of the list and the buttons over it are as wide as the list is now", () =>
            tree.Children.Count > 0 && tree.Children.All(row => Near(row.Size.X, treeScroll.Size.X - 24, 1))
            && Near(paletteButtons[0].Size.X * 2 + 4, treeScroll.Size.X, 2) && tree.Children[0].Bounds.Max.X <= treeScroll.Bounds.Max.X);

        // The properties on the other side, dragged by their left edge. What a property is set with gets the room
        // Where to take hold of it is read once, before it moves. Asked for again on the way the edge has gone
        // along with the pointer and the layout is a tick behind, and the drag comes up short
        float propertiesWide = 0, editorWide = 0;
        Vector2 propertiesEdge = default;
        test.Run(() =>
        {
            propertiesWide = inspectorScroll.Size.X;
            editorWide = editors["name"].Bounds.Width;
            propertiesEdge = new Vector2(inspectorScroll.Bounds.Min.X - 4, inspectorScroll.Bounds.Center.Y);
        });
        test.Drag(InChrome(() => propertiesEdge), InChrome(() => propertiesEdge - new Vector2(90, 0)));
        test.Wait();
        test.Check("panels: dragging the properties wider makes what is in them wider with them", () =>
            Near(inspectorScroll.Size.X, propertiesWide + 90, 2) && Near(editors["name"].Bounds.Width, editorWide + 90, 2)
            && editors["name"].Bounds.Max.X <= inspectorScroll.Bounds.Max.X - 19.5f && Fits());
        test.Check("panels: every kind of editor in them, the ones that are two and four numbers across as well", () =>
            editors["pos"] is StackPanel pair && Near(pair.Bounds.Width, editors["name"].Bounds.Width, 2)
            && editors["padding"] is StackPanel four && Near(four.Bounds.Width, editors["name"].Bounds.Width, 2));

        // The canvas got narrower twice over and is the shape of a screen, so it is lower than it started as
        float narrowTall = 0, narrowCode = 0;
        Vector2 canvasEdge = default;
        test.Run(() =>
        {
            narrowTall = canvas.Size.Y;
            narrowCode = codeScroll.Size.Y;
            canvasEdge = new Vector2(canvas.Bounds.Center.X, canvas.Bounds.Min.Y - 4);
        });
        test.Drag(InChrome(() => canvasEdge), InChrome(() => canvasEdge - new Vector2(0, 50)));
        test.Wait();
        test.Check("panels: the bottom of the canvas drags it taller, out of the room the code had", () =>
            canvas.Size.Y > narrowTall + 30 && codeScroll.Size.Y < narrowCode - 30 && Fits());

        test.Run(ResetPanels);
        test.Wait();
        test.Check("panels: and they go back to what they come as", () =>
            Near(treeScroll.Size.X, listWide, 1) && Near(canvas.Size.Y, canvasTall, 2) && settings.LeftWidth == 0.0f && Fits());

        /* The settings, and the skins */

        UIDialog? asking = null;
        UIComponent Setting(int row) => ((StackPanel)asking!.Body.Children[row]).Children[1];

        Pick("File", () => "Settings...");
        test.Settle(0.4f);
        test.Check("settings: the file menu opens them, a row for everything there is to set", () =>
            (asking = settingsDialog) is { IsOpen: true } && chrome.Dialog == asking && asking.Body.Children.Count >= 7
            && Setting(0) is Selector { Value: "dark" } && Setting(2) is SkinPicker);

        test.Click(At(() => Setting(0), 0.9f));
        test.Settle(0.3f);
        test.Check("settings: the editor goes into the next theme of its skin there and then", () =>
            settings.EditorTheme != "dark" && compositor.Skin?.Theme == settings.EditorTheme && Setting(0) is Selector { Value: var shown } && shown == settings.EditorTheme);
        test.Check("settings: boxes, background and all", () =>
            compositor.Skin is { } worn && treeScroll.Color == worn.PanelColor with { W = 1.0f });
        test.Run(() => UseEditorTheme("light", save: false));
        test.Settle(0.3f);
        test.Check("settings: the light one is light, and what the editor says quietly can still be read on it", () =>
            compositor.Skin is { Theme: "light", PanelColor.X: > 0.8f, TextColor.X: < 0.3f } && DimColor.X < 0.3f);
        test.Run(() => UseEditorTheme("dark", save: false));

        test.Click(At(() => Setting(3), 0.9f));
        test.Wait();
        test.Check("settings: the grid is one of a few", () => settings.Snap == HexSettings.Grids[1]);
        test.Run(() => settings.Snap = 0.0f);

        test.Click(At(() => Setting(4), 0.1f));
        test.Wait();
        test.Check("settings: the resize handles switch off", () => !settings.CanvasHandles && Setting(4) is ToggleButton { State: false, Label: "off" });
        test.Click(At(() => Setting(4), 0.1f));
        test.Wait();
        test.Check("settings: and on again", () => settings.CanvasHandles);

        test.Click(At(() => ((SkinPicker)Setting(2)).Skins, 0.92f));
        test.Settle(0.4f);
        test.Check("settings: the layouts go into the next skin, the editor stays in its own", () =>
            UISkins.Current.Name != UISkins.DEFAULT && stage.Skin is { } worn && !ReferenceEquals(worn, compositor.Skin)
            && stage.FollowsSkins && !compositor.FollowsSkins);

        Vector2 done = Vector2.Zero;
        test.Run(() => done = chrome.ToWorld(asking!.Buttons[0].Bounds.Center));
        test.Click(() => done);
        test.Wait();
        test.Check("settings: done closes them", () => settingsDialog is null && chrome.Dialog is null);

        foreach (UISkinChoice skin in UISkins.Available)
        {
            // Every skin there is by its line of the menu, in the first and the last of its themes. Every theme
            // of every skin is the business of the check-ui-skins scene of the examples, which doesn't have to
            // open a menu thirty times to get at them
            string?[] themes = skin.Themes.Count > 0 ? [.. new[] { skin.Themes[0], skin.Themes[^1] }.Distinct()] : [null];
            foreach (string? theme in themes)
            {
                Pick("Skin", () => theme is null ? skin.Title : $"{skin.Title}, {theme}");
                test.WaitFor(() => stage.Skin is { } worn && worn.Theme == (theme ?? worn.Theme) && UISkins.Current == skin && !worn.HasPendingArt, 2.0f);
                test.Wait();
                test.Check($"skins: {skin.Name}{(theme is null ? string.Empty : " in " + theme)} comes up, with art for a button and a panel, or a colour for them", () =>
                    UISkins.Current == skin && stage.Skin is { } worn && (theme is null || worn.Theme == theme)
                    && (worn.Sheet is null || (worn.Sheet.TryGetSprite("button", worn.Theme, out _) && worn.Sheet.TryGetSprite("panel", worn.Theme, out _)))
                    && box!.Bounds.Width > 0);
            }
        }

        test.Run(() => UISkins.Use(UISkins.DEFAULT));
        test.Settle(0.3f);
        test.Check("skins: and back to the one games come in", () => UISkins.Current.Name == UISkins.DEFAULT && stage.Skin is { Sheet: not null });

        test.Hover(Part("canvas", 0.04f, 0.06f));
    }
}
