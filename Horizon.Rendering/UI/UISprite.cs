using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Horizon.Rendering.Spriting;

namespace Horizon.Rendering.UI;

public class UISprite : Sprite
{
    public Vector2 Margin { get; set; }


    public UISprite(in Vector2 size) : base(in size)
    {

    }
}

