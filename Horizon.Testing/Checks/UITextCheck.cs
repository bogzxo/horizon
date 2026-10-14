using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Horizon.Testing.Checks;

/// <summary>
/// Text with more than one look to it. The tags in square brackets (bold, italic, a colour, a size and back), markdown
/// read by a label told it is markdown (headings, lists, quotes, code, rules, links), wrapping all of it, and a link
/// clicked by a scripted pointer. The right of the screen is one label of markdown, to look at.
/// </summary>
internal sealed class UITextCheck : Scene, ISelfCheck
{
    private const float SCALE = 0.3f;

    // Everything markdown can say, in one label
    private const string SHOWCASE = """
        # Markdown in a label
        Written with **bold**, *italic* and ***both***, ~~struck through~~, `code` and a [link](showcase).
        ## Lists
        - a bullet
        - another, long enough that it has to wrap onto the line under it and keep its indent when it gets there, which it does
          - one a level in
        1. numbered
        2. and the next
        > a quote, with its bar down the side
        ---
        ### Code
        ```
        let x = compositor.label({ markdown: true });
        ```
        And tags work in it too, [color:#ff8040]orange[/color], [size:1.4]bigger[/size] and [color:accent]the skin's accent[/color].
        """;

    public override Camera ActiveCamera { get; protected set; }

    private readonly UICompositor _compositor;
    private readonly UISelfTest _test = new();

    public bool Finished => _test.Finished;
    public int Failed => _test.Failed;
    public int Count => _test.Count;

    private Label _plain = null!, _bold = null!, _italic = null!, _big = null!, _unknown = null!, _unknownPlain = null!;
    private Label _heading = null!, _paragraph = null!, _list = null!, _wrapped = null!, _escaped = null!, _escapedPlain = null!;
    private Label _link = null!, _code = null!, _showcase = null!;
    private CodeEditor _editor = null!;
    private string _clicked = string.Empty;

    public UITextCheck()
    {
        var camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

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

    private Label Add(StackPanel column, string text, bool markdown = false, float width = 0.0f) =>
        column.Add(new Label(text) { TextScale = SCALE, Markdown = markdown, Anchor = Origin.Left, Align = Origin.TopLeft, Wrap = width > 0.0f, Size = new Vector2(width, 0.0f) });

    private void Build()
    {
        var module = _compositor.CreateModule();

        var column = module.AddComponent(new StackPanel
        {
            Anchor = Origin.TopLeft,
            Position = new Vector2(40, -40),
            Spacing = 8,
            Padding = new UIEdges(16),
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.95f)
        });

        _plain = Add(column, "Fight");
        _bold = Add(column, "[b]Fight[/b]");
        _italic = Add(column, "[i]Fight[/i]");
        _big = Add(column, "[size:2]Fight[/size]");
        _unknown = Add(column, "[nope]Fight");
        _unknownPlain = Add(column, "[nope]Fight");
        _heading = Add(column, "# Fight", markdown: true);
        _paragraph = Add(column, "Fight", markdown: true);
        _list = Add(column, "- one\n- two\n- three", markdown: true);
        _wrapped = Add(column, "[b]a good few words[/b] that will not fit on one line of this", width: 220);
        _escaped = Add(column, "\\*not italic\\*", markdown: true);
        _escapedPlain = Add(column, "*not italic*");
        _link = Add(column, "[click me](somewhere)", markdown: true);
        _link.OnLink = where => _clicked = where;

        // Code to edit, fed keys the way the keyboard hands them over
        _editor = module.AddComponent(new CodeEditor("let a = 1;")
        {
            Anchor = Origin.BottomLeft,
            Position = new Vector2(380, 40),
            Size = new Vector2(760, 220),
            TextScale = 0.25f
        });
        _code = Add(column, "```\nline one\nline two\n```", markdown: true);

        _showcase = module.AddComponent(new Label(SHOWCASE)
        {
            // Left of where the host's card of keys sits
            Anchor = Origin.TopLeft,
            Position = new Vector2(380, -40),
            Size = new Vector2(760, 0),
            TextScale = SCALE,
            Markdown = true,
            Wrap = true,
            Align = Origin.TopLeft,
            OnLink = where => _clicked = where
        });
    }

    private void Script(UISelfTest test)
    {
        bool Near(float a, float b, float within = 1.5f) => MathF.Abs(a - b) <= within;

        test.Wait();

        test.Check("tags: only the style tags make a text styled, an icon or a word in brackets doesn't", () =>
            UIRichText.HasTags("[b]x[/b]") && UIRichText.HasTags("a [color:red]b") && UIRichText.HasTags("[size:2]x")
            && !UIRichText.HasTags("[icon:pad_a] pick") && !UIRichText.HasTags("plain [x] text") && !UIRichText.HasTags("no tags"));

        test.Check("tags: bold is wider than plain and as tall", () =>
            _bold.DesiredSize.X > _plain.DesiredSize.X + 1.0f && Near(_bold.DesiredSize.Y, _plain.DesiredSize.Y));
        test.Check("tags: italic leans but takes as much room as plain", () =>
            Near(_italic.DesiredSize.X, _plain.DesiredSize.X) && Near(_italic.DesiredSize.Y, _plain.DesiredSize.Y));
        test.Check("tags: a size of two is twice as big", () =>
            Near(_big.DesiredSize.Y, _plain.DesiredSize.Y * 2.0f, 2.0f) && Near(_big.DesiredSize.X, _plain.DesiredSize.X * 2.0f, 3.0f));
        test.Check("tags: one it doesn't know is written as it is", () =>
            _unknown.DesiredSize.X > _plain.DesiredSize.X && Near(_unknown.DesiredSize.X, _unknownPlain.DesiredSize.X));

        test.Check("markdown: a heading is bigger than a paragraph", () =>
            _heading.DesiredSize.Y > _paragraph.DesiredSize.Y * 1.4f && _heading.DesiredSize.X > _paragraph.DesiredSize.X * 1.4f);
        test.Check("markdown: a paragraph with nothing in it is the plain text", () =>
            Near(_paragraph.DesiredSize.X, _plain.DesiredSize.X) && Near(_paragraph.DesiredSize.Y, _plain.DesiredSize.Y));
        test.Check("markdown: a bullet a line, indented past the bullet", () =>
            _list.LineCount == 3 && _list.DesiredSize.X > _plain.DesiredSize.X);
        test.Check("markdown: a backslash makes a star just a star", () => Near(_escaped.DesiredSize.X, _escapedPlain.DesiredSize.X));
        test.Check("markdown: a block of code is its lines, the fences aren't", () => _code.LineCount == 2);

        test.Check("wrap: styled text wraps to the width it is given", () =>
            _wrapped.LineCount > 1 && Near(_wrapped.DesiredSize.X, 220.0f) && _wrapped.DesiredSize.Y > _plain.DesiredSize.Y * 1.5f);
        test.Check("showcase: the lot lays out, wrapped to its width", () =>
            _showcase.LineCount >= 14 && Near(_showcase.DesiredSize.X, 760.0f) && _showcase.DesiredSize.Y > _plain.DesiredSize.Y * 13.0f);

        test.Click(() => _link.Module!.ToWorld(new Vector2(_link.Bounds.Min.X + 20.0f, _link.Bounds.Center.Y)));
        test.Check("link: a click on one says where it goes", () => _clicked == "somewhere");

        /* The editor of code, fed the keys the keyboard turns into (UIKeyboard) */

        void Keys(params char[] keys)
        {
            foreach (char key in keys)
                _editor.OnTextInput(key);
        }

        void Typed(string text)
        {
            foreach (char character in text)
                _editor.OnTextInput(character == '\n' ? UIKeyboard.ENTER : character);
        }

        test.Run(() =>
        {
            _editor.Focus();
            Keys(UIKeyboard.TEXT_END);
            Typed("\nlet b = {");
            Keys(UIKeyboard.ENTER);
            Typed("c: 2");
        });
        test.Wait();
        test.Check("code: typed in, a bracket closes itself and enter goes in a level", () =>
            _editor.Text == "let a = 1;\nlet b = {\n    c: 2\n}" && _editor.Document.Caret == new UITextPosition(2, 8));

        test.Run(() => Keys(UIKeyboard.UP, UIKeyboard.HOME, UIKeyboard.SELECT_DOWN, UIKeyboard.SELECT_END, UIKeyboard.TAB));
        test.Check("code: tab with a selection over lines indents them", () => _editor.Text == "let a = 1;\n    let b = {\n        c: 2\n}");
        test.Run(() => Keys(UIKeyboard.SHIFT_TAB));
        test.Check("code: shift tab takes them back out", () => _editor.Text == "let a = 1;\nlet b = {\n    c: 2\n}");

        test.Run(() => Keys(UIKeyboard.TEXT_START, UIKeyboard.TOGGLE_COMMENT));
        test.Check("code: control with slash makes a line a comment", () => _editor.Text.StartsWith("// let a = 1;"));
        test.Run(() => Keys(UIKeyboard.UNDO));
        test.Check("code: and undo takes it back", () => _editor.Text.StartsWith("let a = 1;"));

        test.Run(() =>
        {
            Keys(UIKeyboard.TEXT_END);
            Typed("\nlet = ;");
        });
        test.WaitFor(() => _editor.SyntaxError is not null, 2.0f);
        test.Check("code: what doesn't parse is found a moment later, on its line", () => _editor.SyntaxError is { Line: 4 });
        test.Run(() => Keys(UIKeyboard.SELECT_HOME, UIKeyboard.SELECT_HOME, UIKeyboard.BACKSPACE, UIKeyboard.BACKSPACE));
        test.WaitFor(() => _editor.SyntaxError is null, 2.0f);
        test.Check("code: and is gone once it is gone", () => _editor.SyntaxError is null && _editor.Document.LineCount == 4);

        test.Run(() =>
        {
            Keys(UIKeyboard.TEXT_END);
            _editor.OnPaste("\nlet pasted = [1, 2];");
        });
        test.Check("code: a paste goes in with its lines", () => _editor.Text.EndsWith("}\nlet pasted = [1, 2];") && _editor.Document.LineCount == 5);

        test.Run(() =>
        {
            Keys(UIKeyboard.TEXT_END);
            Typed("\nlet x = pas");
        });
        test.Wait();
        test.Check("complete: a word being typed is offered the names in the code", () =>
            _editor.CompletionOpen && _editor.Completions.Any(item => item.Text == "pasted"));
        test.Run(() => Keys(UIKeyboard.ENTER));
        test.Check("complete: enter picks it, and makes no new line", () =>
            !_editor.CompletionOpen && _editor.Text.EndsWith("let x = pasted") && _editor.Document.LineCount == 6);

        test.Run(() =>
        {
            Typed(" + ");
            Keys(UIKeyboard.COMPLETE);
        });
        test.Check("complete: control and space opens it with nothing typed, the language's words in it", () =>
            _editor.CompletionOpen && _editor.Completions.Any(item => item.Text == "let"));
        test.Run(() => Keys(UIKeyboard.LEFT));
        test.Check("complete: going somewhere else shuts it", () => !_editor.CompletionOpen);

        // A tab is as wide as four spaces, not as wide as a space (the font has none)
        test.Run(() => _editor.Text = "\tx\n    x");
        test.Wait();
        test.Check("code: a tab is drawn and pointed at as wide as four spaces", TabColumnsMatch);

        test.Run(() => _editor.Unfocus());
        test.Hover(() => new Vector2(-5000, -5000));
    }

    // Whether the x after a tab starts where the x after four spaces does, by which column the editor says points are in
    private bool TabColumnsMatch()
    {
        float? tabbed = null, spaced = null;
        UIRect bounds = _editor.Bounds;

        for (float x = bounds.Min.X; x < bounds.Min.X + 600.0f; x += 1.0f)
        {
            if (tabbed is null && _editor.PositionAt(new Vector2(x, ProbeY(0))).Column >= 2) tabbed = x;
            if (spaced is null && _editor.PositionAt(new Vector2(x, ProbeY(1))).Column >= 5) spaced = x;
        }

        return tabbed is { } a && spaced is { } b && MathF.Abs(a - b) <= 1.5f;
    }

    // The middle of a line of the editor, up and down
    private float ProbeY(int line)
    {
        UIRect bounds = _editor.Bounds;
        float lineHeight = (_compositor.Skin?.Font.LineHeight ?? 1.0f) * 0.25f;
        return bounds.Max.Y - _editor.Padding.Top - lineHeight * (line + 0.5f);
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        _test.Update(dt);
    }
}
