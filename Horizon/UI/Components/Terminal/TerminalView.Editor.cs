using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

using Silk.NET.Input;

namespace Horizon.UI.Components;

// A file edited in place, the way nano takes over the terminal it's run in. The dawn command of the shells' integration
// asks for it (dawn file.cs), the host calls EditInline, and the pane is Dawn's code editor (the engine's CodeEditor,
// coloured by the file's grammar) until it's put away with Ctrl+Q, Ctrl+S saving it. The shell carries on underneath
// the whole time, it said its prompt again the moment the command was done, so there's nothing to wait for or resume.
public partial class TerminalView
{
    // How tall the strip along the bottom is, what's being edited and the keys, as a share of a line of the terminal
    private const float INLINE_BAR_LINES = 1.6f;

    private CodeEditor? inline;
    private string? inlinePath;
    private int inlineSavedVersion;

    // What the strip along the bottom says, made again only when what it says changes, the pane is painted every tick
    private string inlineLabel = string.Empty;
    private (bool Changed, string Status, string? Path) inlineLabelOf;

    // Ctrl+Q with changes that aren't saved says so once, the second one goes anyway
    private bool inlineWarned;
    private string inlineStatus = string.Empty;

    /// <summary>The editor the pane is while a file is edited in place, null while it's the terminal.</summary>
    public CodeEditor? InlineEditor => inline;

    /// <summary>The file being edited in place, null for none.</summary>
    public string? InlinePath => inlinePath;

    /// <summary>Whether what's in the editor isn't what was last saved.</summary>
    public bool InlineChanged => inline is not null && inline.Document.Version != inlineSavedVersion;

    /// <summary>Called when the file edited in place was saved, with its path, for whoever watches files.</summary>
    public Action<TerminalView, string>? OnInlineSaved { get; set; }

    /// <summary>Called when the pane went back to being the terminal.</summary>
    public Action<TerminalView>? OnInlineClosed { get; set; }

    /// <summary>
    /// Edits a file in place of the terminal, a new one if there's nothing at the path yet (made when it's first
    /// saved). Another file being edited is put away first, unsaved changes and all, so ask <see cref="InlineChanged"/>.
    /// </summary>
    /// <returns>False when the path is a folder or the file can't be read.</returns>
    public bool EditInline(string path)
    {
        if (Directory.Exists(path))
            return false;

        string text;
        try
        {
            text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception)
        {
            return false;
        }

        CloseInline(force: true);

        inlinePath = Path.GetFullPath(path);
        inlineWarned = false;
        inlineStatus = File.Exists(path) ? string.Empty : "new file";
        inline = Add(new CodeEditor
        {
            Text = text,
            Highlighter = CodeLanguages.ForFile(path),
            Font = Font,
            TextScale = TextScale
        });
        inlineSavedVersion = inline.Document.Version;
        inline.Focus();
        return true;
    }

    /// <summary>Writes what's in the editor to its file, false when it couldn't be (said in the strip along the bottom).</summary>
    public bool SaveInline()
    {
        if (inline is null || inlinePath is null)
            return false;

        try
        {
            if (Path.GetDirectoryName(inlinePath) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);

            File.WriteAllText(inlinePath, inline.Text);
            inlineSavedVersion = inline.Document.Version;
            inlineWarned = false;
            inlineStatus = "saved";
            OnInlineSaved?.Invoke(this, inlinePath);
            return true;
        }
        catch (Exception e)
        {
            inlineStatus = $"couldn't save, {e.Message}";
            return false;
        }
    }

    /// <summary>Back to the terminal. Changes that aren't saved stop it once unless forced, a second go throws them away.</summary>
    /// <returns>Whether it went back.</returns>
    public bool CloseInline(bool force = false)
    {
        if (inline is null)
            return true;

        if (!force && InlineChanged && !inlineWarned)
        {
            inlineWarned = true;
            inlineStatus = "not saved, Ctrl+Q again throws the changes away";
            return false;
        }

        Remove(inline);
        inline = null;
        inlinePath = null;
        inlineStatus = string.Empty;
        Focus();
        OnInlineClosed?.Invoke(this);
        return true;
    }

    /// <summary>Every update, the keys of the editor that are the pane's (save and close), and the focus kept in it.</summary>
    private void UpdateInline()
    {
        if (inline is null)
            return;

        // The pane being picked (a click on its tab, the panel's focus moving) gives the focus to the view, it's the editor's
        if (IsFocused)
            inline.Focus();

        if (!inline.IsFocused || GameEngine.Instance?.Input?.Keyboard is not { Control: true } keyboard)
            return;

        if (keyboard.WasPressed(Key.S))
            SaveInline();
        else if (keyboard.WasPressed(Key.Q))
            CloseInline();
    }

    protected override void Arrange(UIRect content)
    {
        if (inline is null)
        {
            base.Arrange(content);
            return;
        }

        // The editor over all of the pane but the strip along the bottom
        float bar = InlineBarHeight();
        inline.ArrangeTree(new UIRect(new Vector2(Bounds.Min.X, Bounds.Min.Y + bar), Bounds.Max));
    }

    private const string INLINE_KEYS = "[color:dim]Ctrl+S save   Ctrl+Q back to the shell[/color]";

    // A method of its own, its strings are only made when the strip says something new
    private string MakeInlineLabel(bool changed)
    {
        inlineLabelOf = (changed, inlineStatus, inlinePath);
        string name = inlinePath is null ? string.Empty : Path.GetFileName(inlinePath);
        string said = changed ? $"$(circle-filled) {name}" : $"$(file) {name}";
        return inlineStatus.Length > 0 ? $"{said}   [color:dim]{inlineStatus}[/color]" : said;
    }

    private float InlineBarHeight() => MathF.Round(cellHeight > 0.0f ? cellHeight * INLINE_BAR_LINES : 26.0f);

    /// <summary>Helper method to paint the pane while it's the editor, its background and the strip along the bottom.</summary>
    private void PaintInline(UIDrawList list)
    {
        UISkin skin = list.Skin;
        list.Rect(Bounds, palette.Background);

        float height = InlineBarHeight();
        var bar = new UIRect(Bounds.Min, new Vector2(Bounds.Max.X, Bounds.Min.Y + height));
        list.Rect(bar, skin.PanelColor with { W = 1.0f });
        list.Rect(new UIRect(new Vector2(bar.Min.X, bar.Max.Y - 1.0f), bar.Max), skin.BorderColor);

        float scale = ScaleOf(skin) * 0.9f;
        bool changed = InlineChanged;
        if (inlineLabelOf != (changed, inlineStatus, inlinePath))
            inlineLabel = MakeInlineLabel(changed);

        UIRect inside = bar.Shrink(new UIEdges(PADDING * 2.0f, 0.0f));
        list.Text(inlineLabel, inside, Origin.Left, scale, skin.TextColor);
        list.Text(INLINE_KEYS, inside, Origin.Right, scale, skin.TextColor);
    }
}
