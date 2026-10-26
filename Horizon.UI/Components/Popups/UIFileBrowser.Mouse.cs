using Horizon.Engine;
using Silk.NET.Input;

namespace Horizon.UI.Components;

// The mouse's back and forward buttons, the ones on its side, going back and forward through the folders the way they do
// in a browser or the system's own file dialogs
public sealed partial class UIFileBrowser
{
    // How many file dialogs are up, counted rather than a flag so one coming down as another goes up never leaves it wrong
    private static int up;

    /// <summary>
    /// Whether a file dialog is up, for anything else that listens to the mouse's back and forward buttons (an editor going
    /// back through where it's been) to leave them to the dialog for as long as it is.
    /// </summary>
    public static bool AnyOpen => Volatile.Read(ref up) > 0;

    // Helper method for the dialog going up and coming down again, once each
    private void Opened() => Interlocked.Increment(ref up);

    private void Closed() => Interlocked.Decrement(ref up);

    // A part of the dialog that draws nothing and has no size, there to hear the side buttons every tick it's up
    private sealed class SideButtons(UIFileBrowser browser) : UIComponent
    {
        private bool backWas, forwardWas;

        protected override void Update(float dt)
        {
            if (GameEngine.Instance?.Input.Mouse is not { } mouse)
                return;

            bool back = mouse.IsDown(MouseButton.Button4), forward = mouse.IsDown(MouseButton.Button5);
            if (back && !backWas)
                browser.Back();
            if (forward && !forwardWas)
                browser.Forward();

            backWas = back;
            forwardWas = forward;
        }
    }
}
