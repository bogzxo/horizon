# Edit prediction in the engine's code editor, where it stands

Branch `feat/edit-prediction-ghost`, based on engine development 68b7e11. The work is commit 2ba2ac4.

## Done

- `Horizon.UI/Components/Controls/CodeEditor.Prediction.cs`: the whole API of plan section 5 (UICodePrediction, SetPrediction,
  ClearPrediction, HasPrediction, PredictionIsGhost, PredictionLine, PredictionInSight, AcceptPrediction, AcceptPredictionWord,
  OnPredictionEnded, OnPredictionTypedThrough, PredictionTakesTab). Ghost text, virtual rows for more lines, diffs struck through
  with the addition after, Tab / Ctrl+Right / Escape / typing through, the completion list keeping Tab.
- `UIFormKind.Secret`: FormPanel and SettingRow make a TextBox with Secret = true; the settings menu doesn't copy a secret.
- `Horizon.Tests/UI/CodeEditorPredictionTests.cs`: 15 tests. Whole Horizon.Tests: 2040 passed, 0 failed.

## Left (known gaps)

- Word wrap doesn't count ghost width when cutting rows.
- Selection, squiggles, brackets and line-end hints after the caret on the ghost's line aren't moved along by the ghost.
- A multi-line ghost moves the rest of the caret's line along by its first line, not after its last.
- Predicted rows stay (blank) while the completion list is open.
- Typing through gives up on auto-closed or stepped-over brackets.

## Next step

Hex's glue codes against the API (chat #110). Merge this branch into engine development when Hex's side lands.

## Build and test

From this folder (W is its Windows path):

    dotnet build Horizon.UI/Horizon.UI.csproj -v q -nologo -p:HorizonRoot=$W/ -p:HidlRoot=$W/Horizon.HIDL/
    dotnet test Horizon.Tests/Horizon.Tests.csproj -p:HorizonRoot=$W/ -p:HidlRoot=$W/Horizon.HIDL/ --filter "FullyQualifiedName~CodeEditorPrediction"
