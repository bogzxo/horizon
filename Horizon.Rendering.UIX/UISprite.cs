using System.Numerics;
using Horizon.Rendering.Spriting;

namespace Horizon.Rendering.UIX;

public class UISprite : Sprite
{
    public Vector2 Margin { get; set; }


    public UISprite(in UICompositor compositor) : base(compositor.SharedSheet.SpriteSize)
    {
        Spritesheet = compositor.SharedSheet;
        AnimationManager = compositor.AnimationManager;
    }
}