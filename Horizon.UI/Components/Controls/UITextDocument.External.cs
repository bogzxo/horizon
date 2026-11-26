namespace Horizon.UI.Components;

public sealed partial class UITextDocument
{
    /// <summary>Apply another writer's revision. Retain each undo or redo step that can still be applied independently.</summary>
    public void ApplyExternal(string text, Func<string, string, string, string?> rebase) =>
        ApplyExternal(text, rebase, null);

    /// <summary>Apply shared text and move selections using the replica's exact position map.</summary>
    public void ApplyExternal(string text, Func<string, string, string, string?> rebase,
        Func<UITextPosition, UITextPosition>? moveSelection, IReadOnlyList<UITextEdit>? changes = null)
    {
        string before = Text;
        if (before == text)
            return;
        RebaseHistory(undo);
        RebaseHistory(redo);
        moveSelection ??= new ExternalPositionMap(before, text).Move;
        UITextPosition movedCaret = moveSelection(Caret), movedAnchor = moveSelection(Anchor);
        for (int i = 0; i < extras.Count; i++)
            extras[i] = new(moveSelection(extras[i].Caret), moveSelection(extras[i].Anchor));
        if (changes is null)
            SetLines(text);
        else
        {
            for (int i = changes.Count - 1; i >= 0; i--)
            {
                UITextEdit edit = changes[i];
                if (edit.Start != edit.End)
                    DeleteRaw(edit.Start, edit.End);
                if (edit.Text.Length != 0)
                    InsertRaw(edit.Start, edit.Text);
            }
        }
        Caret = Clamp(movedCaret);
        Anchor = Clamp(movedAnchor);
        preferredColumn = Caret.Column;
        lastKind = EditKind.Other;
        Touched();

        void RebaseHistory(List<Snapshot> history)
        {
            string newer = before, revised = text;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                Snapshot step = history[i];
                string older = string.Join('\n', step.Lines);
                string? next = rebase(newer, older, revised);
                if (next is null)
                {
                    history.RemoveRange(0, i + 1);
                    break;
                }
                var positions = new ExternalPositionMap(older, next);
                string[] updated = next.Split('\n');
                for (int line = 0; line < Math.Min(updated.Length, step.Lines.Length); line++)
                    if (updated[line] == step.Lines[line])
                        updated[line] = step.Lines[line];
                UITextSelection[]? movedExtras = null;
                if (step.ExtraCarets is { Length: > 0 } savedExtras)
                {
                    movedExtras = new UITextSelection[savedExtras.Length];
                    for (int j = 0; j < savedExtras.Length; j++)
                        movedExtras[j] = new(positions.Move(savedExtras[j].Caret), positions.Move(savedExtras[j].Anchor));
                }
                history[i] = new(updated, positions.Move(step.Caret), positions.Move(step.Anchor),
                    [.. step.Movements.Select(m => m with { At = positions.Move(m.At), ReverseAt = positions.Move(m.ReverseAt) })],
                    movedExtras);
                newer = older;
                revised = next;
            }
        }
    }

    private sealed class ExternalPositionMap
    {
        private readonly string before, after;
        private readonly int start, suffix;

        public ExternalPositionMap(string before, string after)
        {
            this.before = before;
            this.after = after;
            int common = Math.Min(before.Length, after.Length);
            while (start < common && before[start] == after[start]) start++;
            while (suffix < common - start && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        }

        public UITextPosition Move(UITextPosition at)
        {
            int offset = 0;
            for (int line = 0; line < at.Line; line++)
            {
                int end = before.IndexOf('\n', offset);
                if (end < 0) { offset = before.Length; break; }
                offset = end + 1;
            }
            offset = Math.Min(before.Length, offset + at.Column);
            int target = offset < start ? offset : offset >= before.Length - suffix
                ? offset + after.Length - before.Length : start;
            int last = -1, lines = 0;
            for (int i = 0; i < target; i++)
                if (after[i] == '\n') { lines++; last = i; }
            return new(lines, target - last - 1);
        }
    }
}
