namespace Horizon.UI.Components;

/// <summary>A line shift or permutation, with an opaque identity shared by its undo/redo replays.
/// Consumers retaining metadata about Identity should use weak keys so discarded history can be collected.</summary>
public readonly record struct UITextLineChange(object Identity, bool Undo, UITextPosition At, int By, bool MoveAt, int Last = -1)
{
    public int MapLine(int line) => Last >= 0
        ? line >= At.Line && line <= Last ? line + By
            : By < 0 && line == At.Line - 1 ? Last
            : By > 0 && line == Last + 1 ? At.Line : line
        : By > 0 ? line > At.Line || line == At.Line && MoveAt ? line + By : line
            : line > At.Line ? line + By : line > At.Line + By ? At.Line + By : line;
}
