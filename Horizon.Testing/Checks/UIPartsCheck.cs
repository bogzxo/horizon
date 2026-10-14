using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Testing.Checks;

/// <summary>
/// The parts of the UI that came in after the other checks were written, with a scripted pointer where they want
/// one. Edges that are dragged to resize a component, the components that show numbers without making garbage
/// (readouts, graphs, meters, bars in pieces), the button in a text box and the menu of art it opens, a dialog
/// with controls in it, the picker of skins, and the performance overlay put together out of its layout.
/// </summary>
internal sealed class UIPartsCheck : Scene, ISelfCheck
{
    public override Camera ActiveCamera { get; protected set; }

    private readonly UICompositor _compositor;
    private readonly UISelfTest _test = new();
    private UIModule _module = null!, _overlay = null!;

    // The box whose edges are dragged, the button that sits against its right edge, and what the dragging told us
    private Panel _box = null!;
    private Button _inside = null!;
    private int _insidePresses, _resized, _resizeEnded;
    private Vector2 _lastSize;

    // The parts made by a layout script
    private UILayout _parts = null!;

    private TextBox _field = null!;
    private int _fieldPresses;
    private string _picked = string.Empty;
    private ArtPicker? _picker;
    private UIDialog? _dialog;
    private bool _ticked;
    private SkinPicker _skins = null!;
    private PerformanceFace _face = null!;
    private readonly PerformanceSample _sample = new();

    public bool Finished => _test.Finished;
    public int Failed => _test.Failed;
    public int Count => _test.Count;

    public UIPartsCheck()
    {
        var camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

        // In the skin games always had, whatever the examples are set to, the numbers below were measured in it
        _compositor = AddComponent(new UICompositor(camera, UICompositor.DEFAULT_SKIN_DIRECTORY, UICompositor.DEFAULT_SKIN_FILE));
        _compositor.PointerSource = () => _test.Pointer;
    }

    public override void PostInit()
    {
        base.PostInit();
        Engine.Graphics.ClearColor = new Vector4(0.12f, 0.13f, 0.17f, 1.0f);

        Build();
        Script(_test);
    }

    private void Build()
    {
        _module = _compositor.CreateModule();

        // Held by its top left corner, so the edges that are dragged are the ones that move
        _box = _module.AddComponent(new Panel
        {
            Anchor = Origin.TopLeft,
            Position = new Vector2(60, -60),
            Size = new Vector2(300, 200),
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.95f),
            Resizable = UIResize.Right | UIResize.Bottom,
            MinSize = new Vector2(200, 150),
            MaxSize = new Vector2(420, 0),
            OnResized = size =>
            {
                _resized++;
                _lastSize = size;
            },
            OnResizeEnded = _ => _resizeEnded++
        });

        // Right up against the edge that is dragged, a press on the edge mustn't press this
        _inside = _box.Add(new Button("in the way") { Anchor = Origin.Right, Size = new Vector2(140, 60), LabelScale = 0.2f, OnPressed = () => _insidePresses++ });

        _parts = UILayout.LoadCode(_module, @"
            let parts = compositor.stack({ anchor: ""top_right"", pos: vec(-60, -60), padding: 20, spacing: 12, color: vec(0.1, 0.12, 0.17, 0.95), outline: vec(1, 1, 1, 0.4), radius: 8 });
            let number = compositor.readout({ parent: parts, size: vec(220, 30), align: ""right"", text: ""12"", unit: ""ms"", text_scale: 0.4, unit_scale: 0.25 });
            let words = compositor.readout({ parent: parts, size: vec(220, 24), text_scale: 0.25 });
            let meter = compositor.meter({ parent: parts, size: vec(220, 6), value: 0.4 });
            let graph = compositor.graph({ parent: parts, size: vec(220, 60), bars: 60, unit: ""ms"", budget: 2, line: 2 });
            let pieces = compositor.segments({ parent: parts, size: vec(220, 10) });
            let dragged = compositor.panel({ parent: parts, size: vec(220, 30), color: vec(0.2, 0.2, 0.3, 1), resize: ""right, bottom"", min_size: vec(100, 20), max_size: vec(300, 60) });
        ");

        var column = _module.AddComponent(new StackPanel { Anchor = Origin.BottomLeft, Position = new Vector2(60, 60), Spacing = 12 });
        _field = column.Add(new TextBox("button") { Size = new Vector2(360, 44), ButtonLabel = "...", OnButtonPressed = () => _fieldPresses++ });
        _skins = column.Add(new SkinPicker());

        // The performance overlay's own layouts, filled in the way the overlay fills them. Out of the way, it is
        // made for another skin and a lot smaller text than this one's
        _overlay = _compositor.CreateModule();
        _overlay.Position = new Vector2(0, -2000);
        _face = new PerformanceFace(_overlay, _sample);
    }

    private void Script(UISelfTest test)
    {
        bool Near(float a, float b, float within = 1.5f) => MathF.Abs(a - b) <= within;

        Func<Vector2> In(UIComponent component, float x, float y) => () =>
        {
            UIRect bounds = component.Bounds;
            return component.Module!.ToWorld(new Vector2(bounds.Min.X + bounds.Width * x, bounds.Min.Y + bounds.Height * y));
        };

        // A point by how far it is from the edges of a component, in the layout's units
        Func<Vector2> Edge(UIComponent component, float fromRight, float fromBottom) => () =>
        {
            UIRect bounds = component.Bounds;
            return component.Module!.ToWorld(new Vector2(bounds.Max.X - fromRight, bounds.Min.Y + fromBottom));
        };

        // Dragging an edge moves the edge, so where a drag starts and ends is worked out once, before it starts,
        // and not asked of the component as it goes
        Vector2 grabbed = default;
        void DragEdge(UIComponent component, float fromRight, float fromBottom, float across, float up)
        {
            test.Run(() => grabbed = new Vector2(component.Bounds.Max.X - fromRight, component.Bounds.Min.Y + fromBottom));
            test.Drag(() => component.Module!.ToWorld(grabbed), () => component.Module!.ToWorld(grabbed + new Vector2(across, up)));
            test.Wait();
        }

        Func<Vector2> away = () => new Vector2(0, 0);
        test.Wait();

        /* Edges that are dragged */

        test.Hover(In(_box, 0.3f, 0.5f));
        test.Check("resize: nothing is lit while the pointer is in the middle of it", () => _box.ActiveGrip == UIResize.None && !_box.IsResizing);
        test.Hover(Edge(_box, 2, 100));
        test.Check("resize: the edge under the pointer lights up", () => _box.ActiveGrip == UIResize.Right);
        test.Hover(Edge(_box, 150, 2));
        test.Check("resize: and the bottom one when it is on that", () => _box.ActiveGrip == UIResize.Bottom);
        test.Hover(Edge(_box, 2, 2));
        test.Check("resize: the corner is both", () => _box.ActiveGrip == (UIResize.Right | UIResize.Bottom));
        test.Hover(In(_box, 0.0f, 0.5f));
        test.Check("resize: an edge that can't be dragged is not an edge", () => _box.ActiveGrip == UIResize.None);
        test.Check("resize: by a point, the way an editor asks", () =>
            _box.EdgesAt(new Vector2(_box.Bounds.Min.X + 1, _box.Bounds.Center.Y), UIResize.All, 6, 4) == UIResize.Left
            && _box.EdgesAt(new Vector2(_box.Bounds.Max.X + 3, _box.Bounds.Max.Y + 3), UIResize.All, 6, 4) == (UIResize.Right | UIResize.Top)
            && _box.EdgesAt(new Vector2(_box.Bounds.Max.X + 3, _box.Bounds.Max.Y + 30), UIResize.All, 6, 4) == UIResize.None
            && _box.EdgesAt(_box.Bounds.Center, UIResize.All, 6, 4) == UIResize.None);

        DragEdge(_box, 2, 100, 60, 0);
        test.Check("resize: dragging the right edge out makes it that much wider and no taller", () =>
            Near(_box.Size.X, 360) && Near(_box.Size.Y, 200) && Near(_box.Bounds.Width, 360));
        test.Check("resize: whoever listens was told along the way, and once that it was let go of", () =>
            _resized > 0 && _resizeEnded == 1 && Near(_lastSize.X, 360) && !_box.IsResizing);
        test.Check("resize: and the button under the edge was never pressed", () => _insidePresses == 0);

        DragEdge(_box, 2, 100, 400, 0);
        test.Check("resize: no bigger than the most it may be", () => Near(_box.Size.X, 420, 0.1f));
        DragEdge(_box, 2, 100, -600, 0);
        test.Check("resize: nor smaller than the least", () => Near(_box.Size.X, 200, 0.1f));

        DragEdge(_box, 100, 2, 0, -50);
        test.Check("resize: the bottom edge dragged down makes it taller", () => Near(_box.Size.Y, 250) && Near(_box.Size.X, 200, 0.1f));
        DragEdge(_box, 2, 2, 40, 30);
        test.Check("resize: a corner drags both ways at once", () => Near(_box.Size.X, 240) && Near(_box.Size.Y, 220) && _resizeEnded == 5);

        test.Click(In(_inside, 0.3f, 0.5f));
        test.Check("resize: the button is still a button anywhere but on the edge", () => _insidePresses == 1);

        test.Run(() => _box.Resizable = UIResize.None);
        DragEdge(_box, 2, 100, 60, 0);
        test.Check("resize: one that can't be dragged isn't", () => Near(_box.Size.X, 240) && _box.ActiveGrip == UIResize.None);

        // By hand, the way an editor does it for something that never asked to be dragged
        test.Run(() =>
        {
            _box.BeginResize(UIResize.Right, new Vector2(100, 0));
            _box.DragResize(new Vector2(113, 0), snap: 8.0f);
        });
        test.Check("resize: by hand, and onto a grid", () => _box.IsResizing && Near(_box.Size.X % 8.0f, 0.0f, 0.01f) && _box.Size.X > 240);
        test.Run(() => _box.EndResize());
        test.Check("resize: until it is let go of", () => !_box.IsResizing);

        var dragged = _parts.Get<Panel>("dragged");
        test.Check("resize: a layout says which edges, and the least and the most", () =>
            dragged.Resizable == (UIResize.Right | UIResize.Bottom) && dragged.MinSize == new Vector2(100, 20) && dragged.MaxSize == new Vector2(300, 60));
        test.Check("resize: and reads it back the way it was written", () =>
            ((Horizon.HIDL.Runtime.StringValue)((Horizon.HIDL.Runtime.NativeValue)dragged.Object.Properties["resize"]).AccessorCallback()).Value == "right, bottom");
        test.Check("resize: a word that is no edge is an error and not a guess", () =>
        {
            try
            {
                _module.Runtime.Evaluate("let nope = compositor.panel({ resize: \"sideways\" });");
                return _module.Runtime.UserScope.Lookup("nope") is null or Horizon.HIDL.Runtime.NullValue;
            }
            catch (Exception)
            {
                return true;
            }
        });

        /* Numbers without garbage */

        var number = _parts.Get<Readout>("number");
        var words = _parts.Get<Readout>("words");
        var meter = _parts.Get<Meter>("meter");
        var graph = _parts.Get<Graph>("graph");
        var pieces = _parts.Get<SegmentBar>("pieces");
        var parts = _parts.Get<StackPanel>("parts");

        test.Check("readout: a layout makes one, with what it says and what it is counted in", () =>
            number.Text == "12" && number.Unit == "ms" && number.Align == Origin.Right && Near(number.Bounds.Width, 220));
        test.Run(() =>
        {
            number.Set(3.14159, "0.00");
            words.Clear().Add("tick ").Add(42).Add(", ").Add(0.5, "0%");
        });
        test.Check("readout: a number is written the way a format says", () => number.Written.SequenceEqual("3.14") || number.Written.SequenceEqual("3,14"));
        test.Check("readout: or put together a piece at a time", () => words.Text.StartsWith("tick 42, 50") && words.Text.EndsWith('%'));
        test.Check("readout: not a number is a nought, nobody wants to read NaN", () =>
        {
            number.Set(double.NaN, "0");
            return number.Written.SequenceEqual("0");
        });
        test.Check("readout: more than it has room for is cut off, not an error", () =>
        {
            words.Write(new string('x', 500));
            return words.Written.Length is > 0 and <= 64;
        });
        test.Check("readout: writing a number into it makes no garbage, which is what it is for", () =>
        {
            number.Set(1.0, "0.00");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++)
            {
                number.Set(i * 0.37, "0.00");
                words.Clear().Add("frame ").Add(i);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before == 0;
        });
        test.Run(() => words.Write("a readout"));

        test.Check("meter: is as full as it is told, and no fuller than full", () =>
        {
            meter.Value = 7.0f;
            return Near(meter.Bounds.Width, 220) && Near(meter.Bounds.Height, 6) && meter.Value == 7.0f;
        });
        test.Run(() => meter.Value = 0.65f);

        test.Check("graph: has as many bars as a layout says", () => graph.Bars == 60 && graph.Unit == "ms" && graph.Budget == 2.0f && graph.Line == 2.0f);
        test.Run(() =>
        {
            graph.Count = 60;
            graph.Top = Graph.RoundUp(3.71f);
            for (int i = 0; i < 60; i++)
                graph.Set(i, 1.0f + MathF.Sin(i * 0.3f), i == 40 ? 9.0f : 0.0f);

            // Off either end is nobody's bar
            graph.Set(-1, 5.0f, 5.0f);
            graph.Set(60, 5.0f, 5.0f);
        });
        test.Check("graph: its top is a round number, and bars that aren't there are ignored", () => graph.Top == 5.0f && graph.Count == 60);
        test.Check("graph: round numbers are 1, 1.5, 2, 3, 5 and 7.5 times a power of ten", () =>
            Graph.RoundUp(0.7f) == 0.75f && Graph.RoundUp(1.0f) == 1.0f && Graph.RoundUp(20.8f) == 30.0f && Graph.RoundUp(160.0f) == 200.0f);
        test.Run(() => graph.Bars = 30);
        test.Check("graph: told to have fewer bars it has fewer, and no more filled than it has", () => graph.Bars == 30 && graph.Count == 30);

        test.Run(() =>
        {
            pieces.Clear();
            for (int i = 0; i < SegmentBar.MAX_SEGMENTS + 10; i++)
                pieces.Add(1.0f, SegmentBar.Shade(i), i);
        });
        test.Check("segments: takes as many pieces as it has room for and no more", () => pieces.Count == SegmentBar.MAX_SEGMENTS);
        test.Check("segments: its colours go round and round", () => SegmentBar.Shade(0) == SegmentBar.Shade(8) && SegmentBar.Shade(0) != SegmentBar.Shade(1) && SegmentBar.Shade(-1) == SegmentBar.Shade(7));
        test.Run(() =>
        {
            pieces.Clear();
            pieces.Add(3.0f, SegmentBar.Shade(0));
            pieces.Add(1.0f, SegmentBar.Shade(3));
            pieces.Whole = 8.0f;
        });
        test.Wait();
        test.Check("parts: a panel takes an outline from a layout, and holds the lot", () =>
            parts.Outline is { W: > 0.3f } && new UIComponent[] { number, words, meter, graph, pieces }.All(part => part.Bounds.Width > 0 && part.Bounds.Min.X >= parts.Bounds.Min.X));

        /* A button in a text box, and the menu of art it opens */

        test.Check("textbox: the button is in the box, on the left unless told otherwise", () =>
            _field.HasButton && _field.ButtonBounds.Min.X >= _field.Bounds.Min.X && _field.ButtonBounds.Max.X < _field.Bounds.Center.X);
        test.Click(() => _field.Module!.ToWorld(_field.ButtonBounds.Center));
        test.Check("textbox: a click on it presses it and doesn't start typing", () => _fieldPresses == 1 && !_field.IsFocused);
        test.Click(In(_field, 0.7f, 0.5f));
        test.Check("textbox: a click on the rest of the box is for typing", () => _fieldPresses == 1 && _field.IsFocused);
        test.Run(() =>
        {
            _field.Unfocus();
            _field.ButtonOnRight = true;
        });
        test.Wait();
        test.Check("textbox: told to, the button is on the right", () => _field.ButtonBounds.Min.X > _field.Bounds.Center.X);

        test.Run(() => _picker = ArtPicker.Show(_module, _compositor.Skin!, "button", name => _picked = name));
        test.Settle(0.3f);
        test.Check("art: the menu lists what the skin has, a lot of it", () => _picker is { IsOpen: true, Tiles.Count: > 50 });
        test.Run(() => _picker!.Filter = "button_pill");
        test.Wait();
        test.Check("art: typing part of a name narrows it down", () => _picker!.Tiles.Count is >= 1 and <= 8);
        test.Click(() => _module.ToWorld(_picker!.Tiles[0].Bounds.Center));
        test.Wait();
        test.Check("art: a click on a piece picks it and the menu goes", () => !_picker!.IsOpen && _picked.StartsWith("button_pill"));
        test.Run(() =>
        {
            _picked = string.Empty;
            _picker = ArtPicker.Show(_module, _compositor.Skin!, "button", name => _picked = name);
        });
        test.Settle(0.3f);
        test.Click(() => _module.ToWorld(new Vector2(_module.Root.Bounds.Min.X + 6, _module.Root.Bounds.Min.Y + 6)));
        test.Wait();
        test.Check("art: a click anywhere else closes it and picks nothing", () => !_picker!.IsOpen && _picked.Length == 0);

        /* A dialog with something in it */

        ToggleButton? tick = null;
        test.Run(() =>
        {
            _dialog = UIDialog.Show(_module, "Settings", string.Empty, new DialogChoice("Done"));
            tick = _dialog.Body.Add(new ToggleButton("a setting") { OnPressed = () => _ticked = true });
        });
        test.Settle(0.35f);
        test.Check("dialog: it has a body between its text and its buttons, for whatever there is to set", () =>
            _dialog is { IsOpen: true } && tick!.Parent == _dialog.Body && tick.Bounds.Width > 0 && _module.Dialog == _dialog);
        test.Click(() => _module.ToWorld(new Vector2(tick!.Bounds.Min.X + 10, tick.Bounds.Center.Y)));
        test.Check("dialog: and what is in it works", () => _ticked && tick!.State);
        test.Click(In(_inside, 0.3f, 0.5f));
        test.Check("dialog: nothing behind it can be clicked while it is up", () => _insidePresses == 1 && _dialog!.IsOpen);
        test.Click(() => _module.ToWorld(_dialog!.Buttons[0].Bounds.Center));
        test.Wait();
        test.Check("dialog: its button closes it", () => !_dialog!.IsOpen && _module.Dialog is null);

        /* The picker of skins */

        test.Check("skins: the picker lists every skin there is and shows the one that is set", () =>
            _skins.Skins.Options.Length == UISkins.Available.Count && _skins.Skins.Value == UISkins.Current.Title);
        test.Click(In(_skins.Skins, 0.92f, 0.5f));
        test.Wait();
        test.Check("skins: stepping it changes the game's setting, and the themes are the new skin's", () =>
            UISkins.Current.Name != UISkins.DEFAULT && _skins.Skins.Value == UISkins.Current.Title
            && (UISkins.Current.Themes.Count == 0 ? !_skins.Themes.Enabled : _skins.Themes.Options.SequenceEqual(UISkins.Current.Themes)));
        test.Run(() => UISkins.Use(UISkins.DEFAULT, "red"));
        test.Wait();
        test.Check("skins: and it shows a change somebody else made", () => _skins.Skins.Value == UISkins.Current.Title && _skins.Themes.Value == "red");
        test.Run(() => UISkins.Use(UISkins.DEFAULT));
        test.Check("skins: the UI of this check asked for a skin of its own and kept it through all of that", () => !_compositor.FollowsSkins && _compositor.Skin is { Theme: "blue" });

        /* The performance overlay, out of its layouts */

        test.Run(() =>
        {
            _sample.Read(Engine);
            _face.Show(PerformanceDetail.Full);
            _face.Show(PerformanceDetail.Compact);
        });
        test.Wait();
        test.Check("overlay: both of its layouts load and have every part the engine fills in", () =>
            _face.Card.Bounds.Width > 400 && _face.Card.Bounds.Height > 300 && _face.Pill.Bounds.Width > 100 && _face.Pill.Bounds.Height > 20);
        test.Check("overlay: filling them in makes no garbage, numbers, graphs and all", () =>
        {
            // Once through first, whatever is only made the first time is made
            for (int i = 0; i < 3; i++)
            {
                _sample.Read(Engine);
                _face.Show(PerformanceDetail.Full);
                _face.Show(PerformanceDetail.Compact);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                _face.Show(PerformanceDetail.Full);
                _face.ShowGraphs(PerformanceDetail.Full);
                _face.Show(PerformanceDetail.Compact);
            }

            long made = GC.GetAllocatedBytesForCurrentThread() - before;
            if (made != 0) Console.WriteLine($"[UI self-test] the overlay made {made} bytes of garbage in 200 turns");
            return made == 0;
        });
        test.Check("overlay: a short screen has its two columns side by side, a tall one has them down the side", () =>
        {
            var card = (StackPanel)_face.Card;

            _face.MaxHeight = 300;
            _face.Show(PerformanceDetail.Full);
            bool wide = card.Direction == UIDirection.Horizontal;

            _face.MaxHeight = 5000;
            _face.Show(PerformanceDetail.Full);
            return wide && card.Direction == UIDirection.Vertical;
        });

        /* The loading screen, which was up before any of this and is long gone */

        test.Check("loading: the screen the window opens on is gone once a scene draws", () =>
            Engine.LoadingScreen is { Enabled: true, IsUp: false });

        test.Hover(away);
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        _test.Update(dt);
    }
}
