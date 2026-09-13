using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Box2D.NetStandard.Dynamics.World;
using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.HIDL;
using Horizon.Input;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.UIX.Components;
using Horizon.Rendering.Text;
using Silk.NET.OpenGL;

namespace Horizon.Rendering.UIX;

public partial class UICompositor : IGameComponent
{
    private readonly Camera2D viewportCamera;
    private readonly HIDLRuntime _runtime;
    private readonly SpriteBatch _spriteBatch;
    private readonly GlyphRenderer _glyphRenderer;
    private SpriteSheet _sharedSheet;
    private SpriteSheetAnimationManager _manager;
    public List<UIComponent> Components { get; init; } = [];
    private Queue<UIComponent> _toInitialize = [];

    public UICompositor(in Camera2D viewportCamera)
    {
        this.viewportCamera = viewportCamera;

        _glyphRenderer = new GlyphRenderer();
        _spriteBatch = new SpriteBatch();
        _spriteBatch.CustomCamera = viewportCamera;
        _runtime = new HIDLRuntime();


        SetupRuntime();
    }

    public UIComponent AddComponent(in UIComponent component)
    {
        this.Components.Add(component);
        _toInitialize.Enqueue(component);
        return component;
    }

    public void Render(float dt, object? obj = null)
    {
        while (_toInitialize.Count > 0)
        {
            _toInitialize.Dequeue().Initialize(this);
        }

        _spriteBatch.Render(dt, obj);
        _glyphRenderer.MarkDirty();
        _glyphRenderer.Render(dt, obj);
    }

    private bool prevMouseClicked = false;
    public void UpdateState(float dt)
    {
        var mouseData = GameEngine.Instance.InputManager.MouseManager.GetData();
        bool mouseClicked = (mouseData.Actions & VirtualAction.PrimaryAction) != 0;
        var mousePos = mouseData.Position;
        mousePos = viewportCamera.ScreenToWorld(mousePos);

        foreach (var component in Components)
        {
            component.UpdateState(dt, this, mousePos, mouseClicked & !prevMouseClicked);
        }
        _spriteBatch.UpdateState(dt);
        _glyphRenderer.UpdateState(dt);
        prevMouseClicked = mouseClicked;
    }

    public void UpdatePhysics(float dt)
    {
        _spriteBatch.UpdatePhysics(dt);
        _glyphRenderer.UpdatePhysics(dt);
    }

    public void Initialize()
    {
        var (success, result, manager) = SpriteSheet.LoadSpriteSheetFromDirectory("Assets/uix/", "example_definition.hor");
       
        if (success)
        {
            _sharedSheet = result;
            _manager = manager;
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Failed to load UI sprite sheet");
        }

        _spriteBatch.Initialize();
        _glyphRenderer.Initialize();
    }

    public Vector2 Position
    {
        get => SpriteBatch.Transform.Position;
        set => SpriteBatch.Transform.Position = GlyphRenderer.Transform.Position = value;
    }

    public Vector2 Scale
    {
        get => SpriteBatch.Transform.Size;
        set => SpriteBatch.Transform.Size = GlyphRenderer.Transform.Size = value;
    }

    public SpriteBatch SpriteBatch => _spriteBatch;
    public Horizon.Rendering.Text.GlyphRenderer GlyphRenderer => _glyphRenderer;
    public SpriteSheet SharedSheet => _sharedSheet;
    public SpriteSheetAnimationManager AnimationManager => _manager;
    public HIDLRuntime Runtime => _runtime;

    public bool Enabled { get; set; }
    public string Name { get; set; } = "UI Compositor";
    public Entity Parent { get; set; }
}

