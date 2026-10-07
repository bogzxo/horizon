using System;
using System.Numerics;


using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Silk.NET.Input;

using Button = Horizon.Rendering.UIX.Components.Button;

namespace Horizon.Testing.Examples.UI;

public class OnScreenKeyboardExample : Scene, ITestControls
{
    public override Camera ActiveCamera { get; protected set; }
    public IReadOnlyList<TestControl> Controls { get; } = [];

    // Listed on screen by the test host
    private readonly UICompositor _compositor;
    private const string SkinDirectory = "Assets/uix/dead_revolver/";

    private UIModule _hud;
    private StackPanel _panel;
    private TextBox _textBox;

    public OnScreenKeyboardExample(bool selfTest = false)
    {
        // 1. Setup Camera and Compositor
        var cam = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = cam;
        
        _compositor = AddComponent(new UICompositor(cam));
    }

    public override void PostInit()
    {
        base.PostInit();

        BuildHud();

        Console.WriteLine("OnScreenKeyboardExample Initialized.");
        Engine.GL.ClearColor(0.22f, 0.27f, 0.36f, 1.0f);
    }


    private void BuildHud()
    {
        _hud = _compositor.CreateModule();

        // A panel to contain the controls
        _panel = _hud.AddComponent<StackPanel>(new()
        {
            Background = "panel",
            Padding = new UIEdges(40, 34),
            Spacing = 20,
            Direction = UIDirection.Vertical
        });

        // The textbox to preview entered text
        _textBox = _panel.Add<TextBox>(new TextBox { Size = new Vector2(900, 0) });

        // The real thing, drawn with the keys of the pack
        _panel.Add(new OnScreenKeyboard(OnScreenKeyboard.Full) { Target = _textBox });

        // A panel to contain the keyboards
        var keyboardPanel = _panel.Add<StackPanel>(new ()
        {
            Background = "panel",
            Padding = new UIEdges(40, 34),
            Spacing = 20,
            Direction = UIDirection.Horizontal
        });

        keyboardPanel.Add<OnScreenKeyboard>(new OnScreenKeyboard(OnScreenKeyboard.Alphanumeric) 
        { 
            Target = _textBox
        });
        keyboardPanel.Add<OnScreenKeyboard>(new OnScreenKeyboard(OnScreenKeyboard.Numeric)
        {
            Target = _textBox
        });
    }
}
