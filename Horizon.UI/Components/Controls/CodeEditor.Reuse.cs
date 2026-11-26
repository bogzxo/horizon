using System.Numerics;

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

    internal long CurrentPaintVersion => PaintVersion;

    protected override long PaintVersion
    {
        get
        {
            // A long line read a paint at a time is still short of what of it is in sight too (see LongColours)
            if (scrollGoal is not null || over.Active || flowMoving || completions.Count > 0 || WaitingShown || draggingMinimap || draggingBar || longReading || middleScroll.Active || scaleSetAt != 0)
                return -1;
            if (caretSeen && CaretGlide > 0.0f && glideTime < CaretGlide)
                return -1;
            if (LineNote is { } activeNote && activeNote.Line == document.Caret.Line && lineNoteAge < LineNoteDelay + LINE_NOTE_FADE)
                return -1;

            var hash = new HashCode();

            // The code and how it looks
            hash.Add(document.Version);
            hash.Add(coloured);
            hash.Add(spans);
            hash.Add(semanticList);
            hash.Add(Highlight);
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
            for (int i = 0; i < RemoteCarets.Count; i++)
                hash.Add(RemoteCarets[i]);
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
            hash.Add(CaretBand);

            // What's drawn over and round the code
            // The lists it's handed and how long they are, a list filled again where it is would be the same list
            hash.Add(SyntaxError);
            hash.Add(diagnostics);
            hash.Add(diagnostics.Count);
            hash.Add(moreErrors.Count);
            hash.Add(spellingVersion);
            hash.Add(spelling);
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
            hash.Add(blocksChanged);
            hash.Add(VisiblePredictionRowsVersion);
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
            hash.Add(signature);
            hash.Add(signatureAt);
            hash.Add(overloadStep);
            hash.Add(signatureHidden);
            hash.Add(LineNote);
            hash.Add(prediction is not null);
            hash.Add(lineNoteFor);
            hash.Add(ExecutionLine);
            hash.Add(OnActionPressed is null ? -1 : ActionLine);
            hash.Add(ActionPreferred);
            hash.Add(Breakpoints);
            hash.Add(Breakpoints?.Count ?? 0);
            hash.Add(BreakpointMarksVersion);
            hash.Add(Rulers);
            hash.Add(FirstLineNumber);
            hash.Add(MostGutterScale);
            hash.Add(diffNumbers);

            // What it's dressed in
            hash.Add(LineNumbers);
            hash.Add(MinimapShown);
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

            // Only the pointer regions that actually change painted quads invalidate reuse:
            // gutter fold chevrons / breakpoint hover dot, minimap slider highlight, scrollbar hover,
            // interactive code lenses, and remote caret labels. Moving the pointer over plain code reuses.
            if (IsHovered && Module is { } module)
            {
                Vector2 pointer = module.ToLocal(module.Compositor.Pointer.Position);
                if (paintedSkin is not { } hoveredSkin)
                {
                    hash.Add(Vector2.Round(pointer));
                }
                else
                {
                    UIRect area = TextArea(hoveredSkin, paintedScale);
                    float lineHeight = FontOf(hoveredSkin).LineHeight * paintedScale;

                    bool overGutter = LineNumbers && pointer.X < area.Min.X;
                    hash.Add(overGutter);
                    if (overGutter && OnGutterPressed is not null)
                        hash.Add((int)MathF.Floor((area.Max.Y + scrollY - pointer.Y) / MathF.Max(1.0f, lineHeight)));

                    bool overMinimap = false;
                    if (MinimapShown && rows.Count > 0)
                    {
                        UIRect minimap = MinimapArea(hoveredSkin, paintedScale);
                        overMinimap = new UIRect(OnPixel(minimap.Min), OnPixel(minimap.Max)).Contains(pointer);
                    }
                    hash.Add(overMinimap);

                    float trackMinX = area.Max.X + MinimapWidth;
                    bool overBar = rows.Count * lineHeight > area.Height
                        && pointer.X >= trackMinX && pointer.X <= Bounds.Max.X
                        && pointer.Y >= area.Min.Y && pointer.Y <= area.Max.Y;
                    hash.Add(overBar);

                    if (OnLensPressed is not null && lenses.Count > 0)
                    {
                        int hoveredLens = -1;
                        for (int i = 0; i < lensBoxes.Count; i++)
                        {
                            if (lensBoxes[i].Box.Contains(pointer))
                            {
                                hoveredLens = i;
                                break;
                            }
                        }
                        hash.Add(hoveredLens);
                    }

                    if (OnLensAbovePressed is not null && lensesAbove.Count > 0)
                    {
                        int hoveredLensAbove = -1;
                        for (int i = 0; i < lensAboveBoxes.Count; i++)
                        {
                            if (lensAboveBoxes[i].Box.Contains(pointer))
                            {
                                hoveredLensAbove = i;
                                break;
                            }
                        }
                        hash.Add(hoveredLensAbove);
                    }

                    if (RemoteCarets.Count > 0)
                        hash.Add(Vector2.Round(pointer));
                }
            }

            return hash.ToHashCode() & 0x7FFFFFFF;
        }
    }
}
