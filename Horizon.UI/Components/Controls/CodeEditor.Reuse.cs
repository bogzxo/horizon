using Horizon.UI.Scripting;

namespace Horizon.UI.Components;

// The code editor's PaintVersion, what decides whether what it painted the tick before is taken as it was rather
// than painted again (see UIComponent.PaintVersion). Everything it paints out of goes into it, and while anything of
// it is on its way somewhere (a scroll easing, the caret gliding, words flowing, a note fading in, the completions up)
// it's painted every tick, its paint is what moves those along
public partial class CodeEditor
{
    // Whether a word was still on its way somewhere the last time the code was painted, see Flow
    private bool flowMoving;

    protected override long PaintVersion
    {
        get
        {
            if (scrollGoal is not null || flowMoving || completions.Count > 0 || WaitingShown || draggingMinimap || draggingBar)
                return -1;
            if (caretSeen && CaretGlide > 0.0f && glideTime < CaretGlide)
                return -1;
            if (LineNote is not null && lineNoteAge < LineNoteDelay + LINE_NOTE_FADE)
                return -1;

            var hash = new HashCode();

            // The code and how it looks
            hash.Add(document.Version);
            hash.Add(coloured);
            hash.Add(spans);
            hash.Add(semanticList);
            hash.Add(highlighter);
            hash.Add(Font);
            hash.Add(textScale);
            hash.Add(wordWrap);
            hash.Add(tabSize);
            hash.Add(Padding);
            hash.Add(Color);
            hash.Add(ReadOnly);

            // The colours it's coloured in, a language can have its own rather than the theme's (C# in ReSharper's)
            if (paintedSkin is { } skin)
                hash.Add(CodeLanguages.ColorsFor(Highlighter, skin.Palette).Fingerprint());
            hash.Add(CodeLanguages.Version);

            // What the code wraps round, filled in where it is
            foreach (UIWrapArea area in WrapAreas)
            {
                hash.Add(area.Shape);
                hash.Add(area.Bounds);
                hash.Add(area.Margin);
            }
            hash.Add(BackdropShape);
            hash.Add(BackdropMargin);

            // Where it's scrolled to and what's picked
            hash.Add(scrollX);
            hash.Add(scrollY);
            hash.Add(document.Caret);
            hash.Add(document.SelectionStart);
            hash.Add(document.SelectionEnd);
            hash.Add(document.ExtraCarets.Count);
            for (int i = 0; i < document.ExtraCarets.Count; i++)
            {
                hash.Add(document.ExtraCarets[i].Start);
                hash.Add(document.ExtraCarets[i].End);
            }
            hash.Add(columnFrom);
            hash.Add(caretDrawn);
            hash.Add(IsFocused && blink < BLINK);
            hash.Add(CaretStyle);

            // What's drawn over and round the code
            // The lists it's handed and how long they are, a list filled again where it is would be the same list
            hash.Add(diagnostics);
            hash.Add(diagnostics.Count);
            hash.Add(moreErrors.Count);
            hash.Add(inlineHints);
            hash.Add(inlineHints.Count);
            hash.Add(lenses);
            hash.Add(lenses.Count);
            hash.Add(lensesAbove.Count);
            hash.Add(lineChanges.Count);
            hash.Add(lineHighlights.Count);
            hash.Add(rangeHighlights.Count);
            hash.Add(agentHints.Count);
            hash.Add(lensesAbove);
            hash.Add(lensRowsChanged);
            hash.Add(lensHeldLine);
            hash.Add(inlaysVersion);
            hash.Add(agentHints);
            hash.Add(agentHintsVersion);
            hash.Add(rangeHighlights);
            hash.Add(lineChanges);
            hash.Add(lineHighlights);
            hash.Add(highlightedLines.Count);
            hash.Add(foldsChanged);
            hash.Add(folded.Count);
            hash.Add(foldable.Count);
            hash.Add(foundVersion);
            hash.Add(found.Count);
            hash.Add(findQuery);
            hash.Add(occurrencesVersionSeen);
            hash.Add(occurrences.Count);
            hash.Add(occurrenceLength);
            hash.Add(lightOccurrences);
            hash.Add(LightOccurrences);
            hash.Add(bracket);
            hash.Add(bracketPair);
            hash.Add(swatches.Count);
            hash.Add(art.Count);
            hash.Add(hoveredArt);
            hash.Add(hoverText);
            hash.Add(signature);
            hash.Add(signatureAt);
            hash.Add(LineNote);
            hash.Add(lineNoteFor);
            hash.Add(ExecutionLine);
            hash.Add(Breakpoints);
            hash.Add(Breakpoints?.Count ?? 0);
            hash.Add(Rulers);
            hash.Add(FirstLineNumber);

            // What it's dressed in
            hash.Add(LineNumbers);
            hash.Add(Minimap);
            hash.Add(StickyScroll);
            hash.Add(RenderWhitespace);
            hash.Add(IndentGuides);
            hash.Add(HighlightTodos);
            hash.Add(ShowArt);
            hash.Add(FocusLine);
            hash.Add(Backdrop);
            hash.Add(BackdropOpacity);
            hash.Add(BackdropScale);
            hash.Add(BackdropOffset);
            hash.Add(BackdropStretch);
            hash.Add(AnsiColours);

            // Under the pointer the gutter shows what folds and the minimap lights its slider, so where the pointer is
            if (IsHovered && Module is { } module)
                hash.Add(System.Numerics.Vector2.Round(module.ToLocal(module.Compositor.Pointer.Position)));

            return hash.ToHashCode() & 0x7FFFFFFF;
        }
    }
}
