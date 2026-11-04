using System.Numerics;

using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Horizon.Tests.UI;

[CollectionDefinition("UI focus restoration", DisableParallelization = true)]
public sealed class UIFocusRestoreCollection { }

[Collection("UI focus restoration")]
public sealed class UIFocusRestoreTests
{
    [Fact]
    public void A_dialog_does_not_restore_focus_to_a_hidden_control()
    {
        var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        UIModule module = compositor.CreateModule();
        var previous = module.AddComponent(new Button("Previous"));
        previous.Focus();
        UIDialog dialog = UIDialog.Show(module, "Question", "", new DialogChoice("OK"));
        previous.Visible = false;

        bool motion = UIDrawList.Motion;
        try
        {
            UIDrawList.Motion = false;
            dialog.Close();
            Assert.Null(compositor.Focus);
        }
        finally
        {
            UIDrawList.Motion = motion;
            compositor.RemoveModule(module);
        }

    }
}
