namespace Horizon.UI.Components;

/// <summary>A replacement in the document's original coordinates.</summary>
public readonly record struct UITextEdit(UITextPosition Start, UITextPosition End, string Text);

public sealed partial class UITextDocument
{
    /// <summary>Accepts a completion and its imports as one undo step. Invalid or overlapping edits change nothing.</summary>
    public bool ApplyCompletion(UITextEdit completion, IReadOnlyList<UITextEdit> additional, int? selectionStart = null, int selectionLength = 0)
    {
        if (selectionStart is {} selected && (selected < 0 || selected > completion.Text.Length || selectionLength < 0 || selectionLength > completion.Text.Length - selected)) return false;
        if (PrepareEdits(additional.Append(completion)) is not { } edits)
            return false;

        int caret = OffsetOf(completion.Start) + completion.Text.ReplaceLineEndings("\n").Length;
        foreach (UITextEdit edit in edits)
            if (edit.Start < completion.Start
                || (edit.Start == completion.Start && edit.End == edit.Start && completion.End > completion.Start))
                caret += edit.Text.Length - (OffsetOf(edit.End) - OffsetOf(edit.Start));

        ApplyPreparedEdits(edits);
        MoveTo(PositionOf(caret));
        if (selectionStart is {} start)
        {
            int insertedAt = caret - completion.Text.ReplaceLineEndings("\n").Length;
            Select(PositionOf(insertedAt + completion.Text[..start].ReplaceLineEndings("\n").Length),
                PositionOf(insertedAt + completion.Text[..(start + selectionLength)].ReplaceLineEndings("\n").Length));
        }
        Touched();
        return true;
    }

    /// <summary>Applies non-overlapping edits as one undo step, preserving the selection and tracking line movements.</summary>
    public bool ApplyEdits(IReadOnlyList<UITextEdit> changes)
    {
        if (changes.Count == 0) return true;
        if (PrepareEdits(changes) is not { } edits) return false;
        int caret = OffsetOf(Caret), anchor = OffsetOf(Anchor);
        for (int i = edits.Length - 1; i >= 0; i--)
        {
            int start = OffsetOf(edits[i].Start), end = OffsetOf(edits[i].End), length = edits[i].Text.Length;
            caret = MapOffset(caret, start, end, length);
            anchor = MapOffset(anchor, start, end, length);
        }
        ApplyPreparedEdits(edits);
        MoveTo(PositionOf(anchor));
        MoveTo(PositionOf(caret), selecting: true);
        Touched();
        return true;
    }

    private static int MapOffset(int offset, int start, int end, int length) =>
        offset >= end ? offset + length - (end - start) : offset > start ? start + length : offset;

    /// <summary>Checks every range before a caller applies edits across several documents.</summary>
    public bool CanApplyEdits(IReadOnlyList<UITextEdit> changes) => PrepareEdits(changes) is not null;

    private UITextEdit[]? PrepareEdits(IEnumerable<UITextEdit> changes)
    {
        UITextEdit[] edits = changes
            .Select(edit => edit with { Text = edit.Text.ReplaceLineEndings("\n") })
            // Applying in reverse puts the replacement in first, then an import inserted at its start.
            .OrderBy(edit => edit.Start).ThenBy(edit => edit.End).ToArray();
        for (int i = 0; i < edits.Length; i++)
        {
            UITextEdit edit = edits[i];
            if (Clamp(edit.Start) != edit.Start || Clamp(edit.End) != edit.End || edit.End < edit.Start
                || (i > 0 && (edits[i - 1].End > edit.Start
                    || (edits[i - 1].Start == edit.Start && edits[i - 1].End == edit.End))))
                return null;
        }
        return edits;
    }

    private void ApplyPreparedEdits(UITextEdit[] edits)
    {
        Record(EditKind.Other);
        for (int i = edits.Length - 1; i >= 0; i--)
        {
            DeleteRaw(edits[i].Start, edits[i].End);
            InsertRaw(edits[i].Start, edits[i].Text);
        }
    }
}
