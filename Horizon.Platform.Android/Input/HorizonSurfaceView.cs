using Android.Content;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;

using Horizon.Input;

using InputEvent = Horizon.Input.InputEvent;

namespace Horizon.Platform.Android;

/// <summary>
/// The SurfaceView the game is drawn into, which is also what the soft keyboard types into. Android only gives an IME
/// to a view that says it's a text editor, so this one says so while Horizon.UI has a box a finger focused (see
/// <see cref="IPlatformInput.SetTextInputActive"/>) and hands what the IME commits to the engine as text, and what it
/// deletes as Backspace. A hardware keyboard never comes this way, the activity hears its keys first.
/// </summary>
internal sealed class HorizonSurfaceView : SurfaceView
{
    private readonly Func<AndroidInput?> input;

    /// <summary>The view of the activity that's up, the one the soft keyboard is asked for on. Main thread.</summary>
    internal static HorizonSurfaceView? Current { get; private set; }

    /// <summary>Whether something takes text right now, which is when an IME may connect.</summary>
    internal bool TextActive { get; private set; }

    public HorizonSurfaceView(Context context, Func<AndroidInput?> input) : base(context)
    {
        this.input = input;
        Focusable = true;
        FocusableInTouchMode = true;
        Current = this;
    }

    public override bool OnCheckIsTextEditor() => TextActive;

    public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
    {
        if (!TextActive) return null;
        if (outAttrs is not null)
        {
            // Plain text with no suggestions and no fullscreen extract box. Suggestions keep words composing, which a
            // game's text box doesn't draw underlined, and the extract box covers the whole game in landscape
            outAttrs.InputType = InputTypes.ClassText | InputTypes.TextVariationVisiblePassword | InputTypes.TextFlagNoSuggestions;
            outAttrs.ImeOptions = (ImeFlags)((int)ImeFlags.NoExtractUi | (int)ImeFlags.NoFullscreen | (int)ImeAction.Done);
        }

        return new Connection(this);
    }

    /// <summary>Puts the soft keyboard up or away. Main thread.</summary>
    internal void SetTextActive(bool active)
    {
        if (TextActive == active) return;
        TextActive = active;
        if (Context?.GetSystemService(Context.InputMethodService) is not InputMethodManager manager) return;

        if (active)
        {
            RequestFocus();
            manager.RestartInput(this);
            manager.ShowSoftInput(this, ShowFlags.Implicit);
        }
        else
        {
            manager.HideSoftInputFromWindow(WindowToken, HideSoftInputFlags.None);
            manager.RestartInput(this);
        }
    }

    protected override void OnDetachedFromWindow()
    {
        if (Current == this) Current = null;
        base.OnDetachedFromWindow();
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        Current = this;
    }

    // What the IME says, turned into the engine's text and keys. Text being composed is typed as it changes, taken back
    // with Backspace and typed again, which is all a box that draws no underline can do with it
    private sealed class Connection(HorizonSurfaceView view) : BaseInputConnection(view, false)
    {
        private string composing = string.Empty;

        public override bool CommitText(Java.Lang.ICharSequence? text, int newCursorPosition)
        {
            Replace(text?.ToString() ?? string.Empty);
            composing = string.Empty;
            return true;
        }

        public override bool SetComposingText(Java.Lang.ICharSequence? text, int newCursorPosition)
        {
            string now = text?.ToString() ?? string.Empty;
            Replace(now);
            composing = now;
            return true;
        }

        public override bool FinishComposingText()
        {
            composing = string.Empty;
            return true;
        }

        public override bool DeleteSurroundingText(int beforeLength, int afterLength)
        {
            for (int i = 0; i < beforeLength; i++) view.input()?.TapFromIme(Key.Backspace);
            for (int i = 0; i < afterLength; i++) view.input()?.TapFromIme(Key.Delete);
            return true;
        }

        public override bool PerformEditorAction(ImeAction actionCode)
        {
            view.input()?.TapFromIme(Key.Enter);
            return true;
        }

        private void Replace(string text)
        {
            if (view.input() is not { } target) return;

            // Only what changed, a word growing a letter at a time is one letter typed, not the word again
            int same = 0;
            while (same < composing.Length && same < text.Length && composing[same] == text[same]) same++;
            for (int i = same; i < composing.Length; i++) target.TapFromIme(Key.Backspace);
            if (same < text.Length) target.TypeFromIme(text[same..]);
        }
    }
}
