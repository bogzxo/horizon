using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using Horizon.Engine;
using Horizon.Rendering.UIX;

namespace Horizon.Testing;
internal class TestSelectorScene : Scene
{
    public override Camera ActiveCamera { get; protected set; }
    private UICompositor _compositor;

    public TestSelectorScene()
    {
        // Create a 2d scene camera
        ActiveCamera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        _compositor = AddComponent<UICompositor>();
        _compositor.Load("Data/example/example.hor");
        Console.WriteLine();
    }

    public override void Initialize()
    {
        base.Initialize();
    }

    public override void Render(float dt, object? obj = null)
    {
        Engine.GL.ClearColor(Color.CornflowerBlue);
        base.Render(dt, obj);
    }
}