using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Horizon.Rendering.UIX.Components;

namespace Horizon.Rendering.UIX
{
    public partial class UIModule
    {
        public ProgressBar AddProgressBar(in Vector2 position)
        {
            return (ProgressBar)this.AddComponent(new ProgressBar(position));
        }
    }
}
