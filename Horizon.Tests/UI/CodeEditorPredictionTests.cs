using System.Numerics;
using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// A prediction of what's typed next shown in a code editor: ghost text at the caret and diffs elsewhere, Tab taking it as
/// one undo step, typing through it, a word of it at a time, and everything that lets go of it.
/// </summary>
[Collection("UI focus restoration")]
public class CodeEditorPredictionTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));
    private static readonly UIRect Screen = new(Vector2.Zero, new Vector2(600.0f, 400.0f));

    private sealed class TypingEditor : CodeEditor { public void Type(char character) => OnTextInput(character); }

    private static TypingEditor Made(UICompositor compositor, string text, UITextPosition caret)
    {
        var editor = compositor.CreateModule().AddComponent(new TypingEditor());
        editor.CompletionSource = _ => null;
        editor.Text = text;
        editor.Document.MoveTo(caret);
        return editor;
    }

    private static UICodePrediction Ghost(UITextPosition at, string text) => new([new UITextEdit(at, at, text)]);

    private static void Paint(UIDrawList list, UIDrawList? previous, UIComponent root, int tick)
    {
        root.MeasureTree(Skin);
        root.ArrangeTree(Screen);
        list.Previous = previous;
        list.Begin(Skin, tick, 1.0f / 120.0f, tick / 120.0f);
        root.PaintTree(list);
        list.End();
    }

    [Fact]
    public void Ghost_text_at_the_caret_is_ghost_and_a_change_elsewhere_is_not()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = Made(compositor, "int x = ", new UITextPosition(0, 8));
        Assert.False(editor.HasPrediction);
        Assert.Equal(-1, editor.PredictionLine);

        editor.SetPrediction(Ghost(new UITextPosition(0, 8), "42;"));
        Assert.True(editor.HasPrediction);
        Assert.True(editor.PredictionIsGhost);
        Assert.Equal(0, editor.PredictionLine);
        Assert.Equal("int x = ", editor.Text);

        editor.SetPrediction(new UICodePrediction([new UITextEdit(new UITextPosition(0, 0), new UITextPosition(0, 3), "long")]));
        Assert.True(editor.HasPrediction);
        Assert.False(editor.PredictionIsGhost);
    }

    [Fact]
    public void Typing_what_the_ghost_text_says_shortens_it_and_says_so()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = Made(compositor, "int x = ", new UITextPosition(0, 8));
        int typedThrough = 0, ended = 0;
        editor.OnPredictionTypedThrough = () => typedThrough++;
        editor.OnPredictionEnded = _ => ended++;
        editor.SetPrediction(Ghost(new UITextPosition(0, 8), "42;"));

        editor.Type('4');
        Assert.Equal("int x = 4", editor.Text);
        Assert.True(editor.PredictionIsGhost);
        Assert.Equal("2;", editor.PredictionText());
        Assert.Equal(1, typedThrough);
        Assert.Equal(0, ended);

        // Ticked, the caret is where the ghost text is and it stays
        editor.UpdateTree(1.0f / 60.0f);
        Assert.True(editor.HasPrediction);
    }

    [Fact]
    public void A_different_character_lets_go_of_it()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = Made(compositor, "int x = ", new UITextPosition(0, 8));
        bool? taken = null;
        editor.OnPredictionEnded = was => taken = was;
        editor.SetPrediction(Ghost(new UITextPosition(0, 8), "42;"));

        editor.Type('7');
        Assert.Equal("int x = 7", editor.Text);
        Assert.False(editor.HasPrediction);
        Assert.False(taken);
    }

    [Fact]
    public void Tab_takes_it_as_one_undo_step()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = Made(compositor, "int x = ", new UITextPosition(0, 8));
        bool? taken = null;
        editor.OnPredictionEnded = was => taken = was;
        editor.SetPrediction(Ghost(new UITextPosition(0, 8), "Compute(1, 2);"));

        editor.Type(UIKeyboard.TAB);
        Assert.Equal("int x = Compute(1, 2);", editor.Text);
        Assert.Equal(new UITextPosition(0, 22), editor.Document.Caret);
        Assert.True(taken);
        Assert.False(editor.HasPrediction);

        editor.Type(UIKeyboard.UNDO);
        Assert.Equal("int x = ", editor.Text);
    }

    [Fact]
    public void Ctrl_right_takes_a_word_at_a_time_and_the_last_ends_it_taken()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = Made(compositor, "var a = ", new UITextPosition(0, 8));
        bool? taken = null;
        editor.OnPredictionEnded = was => taken = was;
        editor.SetPrediction(Ghost(new UITextPosition(0, 8), "new Thing"));

        editor.Type(UIKeyboard.WORD_RIGHT);
        Assert.Equal("var a = new", editor.Text);
        Assert.Equal(" Thing", editor.PredictionText());
        Assert.Null(taken);

        editor.Type(UIKeyboard.WORD_RIGHT);
        Assert.Equal("var a = new Thing", editor.Text);
        Assert.False(editor.HasPrediction);
        Assert.True(taken);
    }

    [Fact]
    public void Escape_lets_go_of_it()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = Made(compositor, "int x = ", new UITextPosition(0, 8));
        bool? taken = null;
        editor.OnPredictionEnded = was => taken = was;
        editor.SetPrediction(Ghost(new UITextPosition(0, 8), "42;"));

        editor.Type(UIKeyboard.ESCAPE);
        Assert.False(editor.HasPrediction);
        Assert.False(taken);
        Assert.Equal("int x = ", editor.Text);
    }

    [Fact]
    public void The_caret_moving_off_ghost_text_or_new_text_lets_go_of_it()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = Made(compositor, "int x = ", new UITextPosition(0, 8));
        editor.SetPrediction(Ghost(new UITextPosition(0, 8), "42;"));
        editor.Document.MoveTo(new UITextPosition(0, 2));
        editor.UpdateTree(1.0f / 60.0f);
        Assert.False(editor.HasPrediction);

        editor.Document.MoveTo(new UITextPosition(0, 8));
        editor.SetPrediction(Ghost(new UITextPosition(0, 8), "42;"));
        editor.Text = "something else";
        Assert.False(editor.HasPrediction);
    }

    [Fact]
    public void More_lines_of_ghost_text_have_rows_of_their_own_until_it_goes()
    {
        var panel = new ScrollPanel();
        var editor = panel.Add(new CodeEditor("void Run()\n{\n}") { Size = new Vector2(560.0f, 300.0f) });
        editor.Document.MoveTo(new UITextPosition(0, 10));
        var list = new UIDrawList();
        Paint(list, null, panel, 1);
        Assert.Equal(3, editor.RowCount);

        editor.SetPrediction(Ghost(new UITextPosition(0, 10), " {\n    Go();\n    Stop();"));
        Paint(list, null, panel, 2);
        Assert.Equal(5, editor.RowCount);

        // A point on a row of the prediction's is the end of the line it's under, never inside it
        Assert.Equal(new UITextPosition(0, 10), editor.Document.Caret);
        editor.ClearPrediction();
        Paint(list, null, panel, 3);
        Assert.Equal(3, editor.RowCount);

        editor.SetPrediction(Ghost(new UITextPosition(0, 10), " {\n    Go();"));
        Paint(list, null, panel, 4);
        Assert.Equal(4, editor.RowCount);
        Assert.True(editor.AcceptPrediction());
        Paint(list, null, panel, 5);
        Assert.Equal("void Run() {\n    Go();\n{\n}", editor.Text);
        Assert.Equal(4, editor.RowCount);
        Assert.Equal(new UITextPosition(1, 9), editor.Document.Caret);
    }

    [Fact]
    public void A_diff_of_two_edits_is_taken_as_one_undo_step_with_the_caret_where_it_says()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = Made(compositor, "Old(1);\nOld(2);\nOld(3);", new UITextPosition(0, 0));
        editor.SetPrediction(new UICodePrediction(
        [
            new UITextEdit(new UITextPosition(2, 0), new UITextPosition(2, 3), "New"),
            new UITextEdit(new UITextPosition(1, 0), new UITextPosition(1, 3), "New"),
        ], CaretAfter: new UITextPosition(2, 7)));
        Assert.False(editor.PredictionIsGhost);
        Assert.Equal(1, editor.PredictionLine);

        // The caret isn't where the diff is, and moving it doesn't let go of a diff
        editor.Document.MoveTo(new UITextPosition(0, 3));
        editor.UpdateTree(1.0f / 60.0f);
        Assert.True(editor.HasPrediction);

        Assert.True(editor.AcceptPrediction());
        Assert.Equal("Old(1);\nNew(2);\nNew(3);", editor.Text);
        Assert.Equal(new UITextPosition(2, 7), editor.Document.Caret);

        editor.Type(UIKeyboard.UNDO);
        Assert.Equal("Old(1);\nOld(2);\nOld(3);", editor.Text);
    }

    [Fact]
    public void Without_a_caret_to_go_to_it_ends_at_the_end_of_the_last_edit()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = Made(compositor, "a\nbb\nccc", new UITextPosition(0, 0));
        editor.SetPrediction(new UICodePrediction(
        [
            new UITextEdit(new UITextPosition(0, 1), new UITextPosition(1, 1), "X\nY\nZ"),
            new UITextEdit(new UITextPosition(1, 2), new UITextPosition(2, 0), "-"),
        ]));
        Assert.True(editor.AcceptPrediction());
        Assert.Equal("aX\nY\nZb-ccc", editor.Text);
        Assert.Equal(new UITextPosition(2, 3), editor.Document.Caret);
    }

    [Fact]
    public void The_completion_list_open_keeps_Tab()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = Made(compositor, "va", new UITextPosition(0, 2));
        editor.CompletionSource = _ => new(0, [new("value")]);
        editor.SetPrediction(Ghost(new UITextPosition(0, 2), "r x = 1;"));
        editor.Complete();
        Assert.True(editor.CompletionOpen);
        Assert.True(editor.HasPrediction);

        editor.Type(UIKeyboard.TAB);
        Assert.Equal("value", editor.Text);
        Assert.False(editor.HasPrediction);
    }

    [Fact]
    public void Tab_with_it_out_of_sight_goes_there_first_and_takes_it_the_second_time()
    {
        var panel = new ScrollPanel();
        var editor = panel.Add(new TypingEditor { Size = new Vector2(560.0f, 200.0f), CompletionSource = _ => null });
        editor.Text = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"Call({i});"));
        editor.Document.MoveTo(new UITextPosition(0, 0));
        var list = new UIDrawList();
        Paint(list, null, panel, 1);

        editor.SetPrediction(new UICodePrediction([new UITextEdit(new UITextPosition(150, 0), new UITextPosition(150, 4), "Invoke")]));
        Assert.False(editor.PredictionInSight);
        Assert.Equal(150, editor.PredictionLine);

        editor.Type(UIKeyboard.TAB);
        Assert.True(editor.HasPrediction);
        Assert.Equal(150, editor.Document.Caret.Line);
        Assert.StartsWith("Call(0);", editor.Text);

        editor.Type(UIKeyboard.TAB);
        Assert.False(editor.HasPrediction);
        Assert.Equal("Invoke(150);", editor.Document.LineAt(150));
    }

    [Fact]
    public void Edits_that_do_not_fit_the_text_are_ignored()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = Made(compositor, "short", new UITextPosition(0, 5));
        editor.SetPrediction(new UICodePrediction([new UITextEdit(new UITextPosition(3, 0), new UITextPosition(3, 2), "x")]));
        Assert.False(editor.HasPrediction);

        editor.SetPrediction(new UICodePrediction(
        [
            new UITextEdit(new UITextPosition(0, 0), new UITextPosition(0, 3), "a"),
            new UITextEdit(new UITextPosition(0, 2), new UITextPosition(0, 4), "b"),
        ]));
        Assert.False(editor.HasPrediction);

        editor.SetPrediction(new UICodePrediction([]));
        Assert.False(editor.HasPrediction);
    }

    [Fact]
    public void Painting_ghost_text_and_a_diff_settles_into_the_list_before()
    {
        var panel = new ScrollPanel();
        var editor = panel.Add(new CodeEditor(string.Join('\n', Enumerable.Range(0, 20).Select(i => $"int value{i} = old{i};"))) { Size = new Vector2(560.0f, 600.0f) });
        editor.Document.MoveTo(new UITextPosition(2, 5));
        editor.SetPrediction(Ghost(new UITextPosition(2, 5), "eger\n// more\n// and more"));
        var a = new UIDrawList();
        var b = new UIDrawList();
        Paint(a, null, panel, 1);
        Paint(b, a, panel, 2);
        Paint(a, b, panel, 3);
        Assert.True(a.Reused > 0, "A settled editor with ghost text is taken out of the list before.");

        editor.SetPrediction(new UICodePrediction(
        [
            new UITextEdit(new UITextPosition(4, 13), new UITextPosition(4, 17), "fresh4"),
            new UITextEdit(new UITextPosition(6, 0), new UITextPosition(8, 0), "int both = 0;\nint more = 1;\n"),
        ]));
        Paint(b, a, panel, 4);
        Paint(a, b, panel, 5);
        Paint(b, a, panel, 6);
        Assert.True(b.Reused > 0, "A settled editor with a diff is taken out of the list before.");
        Assert.Equal(22, editor.RowCount);
    }

    [Fact]
    public void A_secret_field_is_a_box_that_keeps_its_secret()
    {
        var form = new FormPanel { Fields = [new UIFormField("key", "key", UIFormKind.Secret), new UIFormField("name", "name", UIFormKind.Text)] };
        form.UpdateTree(0.01f);
        TextBox[] boxes = [.. Inside(form).OfType<TextBox>()];
        Assert.Equal(2, boxes.Length);
        Assert.Single(boxes, box => box.Secret);

        var row = new SettingRow(new SettingsPage(), new UISetting("api_key", "API key", UIFormKind.Secret, string.Empty), 320);
        Assert.True(Assert.Single(Inside(row).OfType<TextBox>()).Secret);
    }

    private static IEnumerable<UIComponent> Inside(UIComponent of)
    {
        foreach (UIComponent child in of.Children)
        {
            yield return child;
            foreach (UIComponent deeper in Inside(child))
                yield return deeper;
        }
    }
}

internal static class PredictionTestText
{
    /// <summary>What's left of ghost text, its one edit's text.</summary>
    public static string? PredictionText(this CodeEditor editor) => editor.Prediction is { } shown ? shown.Edits[0].Text : null;
}
