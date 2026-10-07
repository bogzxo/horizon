using AutocompleteMenuNS;
using ScintillaNET;

namespace Horizon.HIDL.Editor;

/// <summary>
/// Helper class to forward events for custom syntax highlighing integrations.
/// </summary>
internal class Scintilla5Wrapper : ITextBoxWrapper
{
    private readonly Scintilla _scintilla;

    public Scintilla5Wrapper(Scintilla scintilla)
    {
        _scintilla = scintilla;
        _scintilla.UpdateUI += (s, e) =>
        {
            Scroll?.Invoke(_scintilla, new ScrollEventArgs(ScrollEventType.EndScroll, 0));
        };
    }

    public Control TargetControl => _scintilla;
    public string Text => _scintilla.Text;
    public string SelectedText
    {
        get => _scintilla.SelectedText;
        set => _scintilla.ReplaceSelection(value);
    }
    public int SelectionLength
    {
        get => Math.Abs(_scintilla.SelectionEnd - _scintilla.SelectionStart);
        set => _scintilla.SelectionEnd = _scintilla.SelectionStart + value;
    }
    public int SelectionStart
    {
        get => _scintilla.SelectionStart;
        set => _scintilla.SelectionStart = value;
    }
    public bool Readonly => _scintilla.ReadOnly;

    public Point GetPositionFromCharIndex(int pos)
    {
        return new Point(_scintilla.PointXFromPosition(pos), _scintilla.PointYFromPosition(pos));
    }

    public event EventHandler? LostFocus
    {
        add => _scintilla.LostFocus += value;
        remove => _scintilla.LostFocus -= value;
    }

    public event ScrollEventHandler? Scroll;

    public event KeyEventHandler? KeyDown
    {
        add => _scintilla.KeyDown += value;
        remove => _scintilla.KeyDown -= value;
    }

    public event MouseEventHandler? MouseDown
    {
        add => _scintilla.MouseDown += value;
        remove => _scintilla.MouseDown -= value;
    }
}