using System;
using System.Numerics;
using Horizon.Engine;
using Horizon.Input;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;
using Horizon.HIDL.Runtime;
using Silk.NET.Input;
using Button = Horizon.Rendering.UIX.Components.Button;

namespace Horizon.Testing.Scenes;

public class UITestScene : Scene
{
    public override Camera ActiveCamera { get; protected set; }
    private UICompositor _compositor;
    private ProgressBar _progressBar;

    public UITestScene()
    {
        // 1. Setup Camera and Compositor
        ActiveCamera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        _compositor = AddComponent<UICompositor>();

        // Register a print function in HIDL for testing
        _compositor.Runtime.GlobalScope.DeclareSystem("print", new NativeFunctionValue((args, env) =>
        {
            Console.WriteLine(string.Join(" ", args.Select(a => a.ToString())));
            return new NullValue();
        }));

        //// 2. Test C# Component Creation
        //var btn = new Button("Click Me (C#)");
        //btn.OnPressed = () => 
        //{
        //    Console.WriteLine("C# Button Pressed!");
        //    _progressBar.Progress += 0.1f;
        //    if (_progressBar.Progress > 1.0f) 
        //        _progressBar.Progress = 0f;
        //};
        //_compositor.AddComponent(btn);

        //_progressBar = new ProgressBar();
        //_progressBar.Progress = 0.5f;
        //_compositor.AddComponent(_progressBar);

        // 3. Test HIDL Integration
    }

    public override void PostInit()
    {
        base.PostInit();

        var (success, result) = _compositor.Runtime.Evaluate(@"
            let btn = compositor.button({
                label: ""Test"",
                spr_scale: 1.0,
                lbl_scale: 0.4,
                pos: vec(0, 0)
            });
            

            btn.on_pressed = func() {
                print(""HIDL Button Pressed! Progress reset!"");
            };
            

            let pb = compositor.progress_bar();
            pb.progress = ""0.25;""

        ");

        if (!success)
        {
            Console.WriteLine($"HIDL Error: {result}");
        }

        Console.WriteLine("UITestScene Initialized. Press SPACE to simulate pressing all buttons.");
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        // Simulating button presses for testing
        if (Engine.InputManager.KeyboardManager.IsKeyPressed(Key.Space))
        {
            foreach (var comp in _compositor.Components)
            {
                if (comp is Button b)
                {
                    b.OnPressed?.Invoke();
                }
            }
        }
    }
}
