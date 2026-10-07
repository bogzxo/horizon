using System.Numerics;

using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

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
        var test = selfTest = new HexPointerScript();
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
        test.Wait(0.5f);
        test.WaitFor(() => Near(chrome.Root.Bounds.Width * compositor.UIScale, Engine.WindowManager.ViewportSize.X, 1), 3.0f);
        test.Wait(0.3f);

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

        test.Click(At(() => editors["intro"]));
        test.Click(Option("intro", 1));
        test.Wait();
        test.Check("intro: a component can be told to pop in", () => button!.Intro == UIIntro.Pop && Code().Contains("intro: \"pop\""));
        test.Check("intro: and does so straight away, to be looked at", () => button!.Tweens.Count > 0);

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
        test.Wait(0.6f);
        test.Run(() =>
        {
            settled = Code();
            Type((TextBox)editors["label"], "Brawl");
        });
        test.Wait(0.6f);
        test.Check("undo: the button is lit once there is something to take back", () => (document.CanUndo || recordPending) && ((Button)button!).Label == "Brawl");

        Pick("Edit", () => "Undo");
        test.Wait(0.3f);
        test.Check("undo: takes the last change back, and only that one", () =>
            Code() == settled && document.Find("button1") is Button { Label: "Fight" });
        test.Check("undo: what was selected still is", () =>
            document.Selected == document.Find("button1") && stage.Highlighted == document.Selected && editors["label"] is TextBox { Text: "Fight" });

        Pick("Edit", () => "Redo");
        test.Wait(0.3f);
        test.Check("redo: makes the change again", () => document.Find("button1") is Button { Label: "Brawl" } && !document.CanRedo);

        Pick("Edit", () => "Undo");
        test.Wait(0.3f);
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

        // The colour is the last but one of what a label has, in a small window that is past the end of the inspector
        test.Run(() => inspectorScroll.Offset = inspectorScroll.MaxOffset);
        test.Wait();
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

        Pick("Edit", () => "Move up");
        test.Wait();
        test.Check("reorder: up puts it before its sibling, in the layout and in the code", () =>
            stack!.Children[0] == label && Code().IndexOf("label1") < Code().IndexOf("start_button ="));
        Pick("Edit", () => "Move down");
        test.Wait();
        test.Check("reorder: and down puts it back", () => stack!.Children[1] == label);

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
        test.Wait(0.3f);
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
        test.Wait(0.3f);
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
        test.Wait(0.4f);
        test.Check("bootstrap: the editor opens its own UI", () => document.Name == "editor" && documents.Count == 2 && document.Walk().Count() == layout.Parts.Count);
        test.Check("bootstrap: and writes it back the same", () => CheckRoundTrip(document));
        test.Check("bootstrap: with stand-ins where it fills things in itself", () => document.PreviewCount >= 20);

        Pick("File", () => "Save");
        test.Wait();
        test.Check("bootstrap: saving it makes a copy rather than touch the file the editor runs on", () =>
            document.Path == Path.Combine(workspace, "editor_edited.hor") && System.IO.File.Exists(document.Path) && System.IO.File.ReadAllText(EDITOR_LAYOUT) == own);

        /* Scrolling */

        test.Hover(Part("tree_scroll"), 0.2f);
        test.Run(() => compositor.Scroll(-3));
        test.Wait(0.2f);
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
        test.Wait(0.4f);
        test.Check("scale: the menu draws the editor and the layouts at the next size", () =>
            compositor.Scale == scaleAfter && compositor.UIScale == scaleAfter && stage.UIScale == scaleAfter);
        test.Check("scale: and the columns make do with the room that leaves", Fits);
        Pick("View", () => "Layout debugger");
        test.Wait();
        test.Check("scale: buttons are still where they are drawn", () => layoutDebugger.IsOn);
        Pick("View", () => "Layout debugger");
        Pick("View", () => DescribeScale(scaleBefore));
        test.Wait(0.4f);
        test.Check("scale: and back", () => compositor.UIScale == scaleBefore && Fits());

        /* The layout debugger */

        Pick("View", () => "Layout debugger");
        test.Wait();
        test.Check("debugger: the menu switches the layout debugger on", () => layoutDebugger.IsOn && stage.LayoutOverlay == layoutDebugger.Options);
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
        test.Wait(0.3f);
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

        test.Hover(Part("canvas", 0.04f, 0.06f));
    }
}
