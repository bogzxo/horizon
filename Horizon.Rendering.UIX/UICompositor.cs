using System;
using System.Collections.Generic;
using System.Text;
using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.HIDL;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.UIX.Components;
using Horizon.Rendering.Text;
using System.Numerics;

namespace Horizon.Rendering.UIX;

public partial class UICompositor : IGameComponent
{
    private readonly HIDLRuntime _runtime;
    private readonly SpriteBatch _spriteBatch;
    private readonly GlyphRenderer _glyphRenderer;
    private SpriteSheet _sharedSheet;
    public List<UIComponent> Components { get; init; } = [];

    public UICompositor()
    {
        _spriteBatch = new SpriteBatch();
        _glyphRenderer = new GlyphRenderer();
        _runtime = new HIDLRuntime();

        SetupRuntime();
    }

    public UIComponent AddComponent(in UIComponent component)
    {
        this.Components.Add(component);
        component.Initialize(this);
        return component;
    }

    public void Render(float dt, object? obj = null)
    {
        _spriteBatch.Render(dt, obj);
        _glyphRenderer.Render(dt, obj);
    }

    public void UpdateState(float dt)
    {
        foreach (var component in Components)
        {
            component.UpdateState(dt);
        }
        _spriteBatch.UpdateState(dt);
        _glyphRenderer.UpdateState(dt);
    }

    public void UpdatePhysics(float dt)
    {
        _spriteBatch.UpdatePhysics(dt);
        _glyphRenderer.UpdatePhysics(dt);
    }

    public void Initialize()
    {
        var dummy = new Sprite(new Vector2(128));
        if (dummy.LoadSpriteSheetFromDirectory("Assets/uix/", "example_definition.hor"))
        {
            _sharedSheet = dummy.Spritesheet;
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Failed to load UI sprite sheet");
        }

        _spriteBatch.Initialize();
        _glyphRenderer.Initialize();
    }

    public SpriteBatch SpriteBatch => _spriteBatch;
    public Horizon.Rendering.Text.GlyphRenderer GlyphRenderer => _glyphRenderer;
    public SpriteSheet SharedSheet => _sharedSheet;
    public HIDLRuntime Runtime => _runtime;

    public bool Enabled { get; set; }
    public string Name { get; set; } = "UI Compositor";
    public Entity Parent { get; set; }
}

