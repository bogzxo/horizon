﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Horizon.Core.Primitives;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL.Assets;

public class Texture : IGLObject
{
    public uint Width { get; init; }
    public uint Height { get; init; }

    public uint Handle { get; init; }

    public static Texture Invalid { get; } =
        new Texture
        {
            Handle = 0,
            Width = 0,
            Height = 0
        };

    public void Bind(uint bindingPoint)
    {
        ObjectManager
            .GL
            .BindTextureUnit(bindingPoint, Handle);
    }
}
