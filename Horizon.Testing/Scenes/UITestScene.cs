using System;
using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Input;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Silk.NET.Input;
using Silk.NET.OpenGL;

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
        var cam = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = cam;
        _compositor = AddComponent(new UICompositor(cam));

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
            let btnAdd = compositor.button({
                label: ""Add 10%"",
                spr_scale: 1.0,
                lbl_scale: 0.4,
                pos: vec(64, -128)
            });

            let btnSub = compositor.button({
                label: ""Sub 10%"",
                spr_scale: 1.0,
                lbl_scale: 0.4,
                pos: vec(-64, -128)
            });

            btnAdd.on_pressed = func() {
                pb.progress = pb.progress + 0.1;
            };
            btnSub.on_pressed = func() {
                pb.progress = pb.progress - 0.1;
            };

            let pb = compositor.progress_bar();

        ");

        if (!success)
        {
            Console.WriteLine($"HIDL Error: {result}");
        }

        Console.WriteLine("UITestScene Initialized. Press SPACE to simulate pressing all buttons.");
        Engine.GL.ClearColor(1.0f, 1.0f, 1.0f, 1.0f);
    }


    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        // Simulating button presses for testing
        if (Engine.InputManager.KeyboardManager.IsKeyPressed(Key.Space))
        {
            _compositor.Scale += new Vector2(0.1f);
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
