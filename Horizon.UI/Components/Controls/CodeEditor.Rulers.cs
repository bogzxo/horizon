using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// The rulers, a faint line straight down the code at every column somebody wants to keep under, VS Code's editor.rulers.
// They go by columns of the font's digit from the left of the code, so they stay put as lines wrap round them, and are
// drawn under the text with the guides.
public partial class CodeEditor
{
    /// <summary>The columns a line is drawn down the code at, none unless the program sets some.</summary>
    public int[] Rulers { get; set; } = [];

    /// <summary>Helper method to draw a line a pixel wide down the code at every ruler that's in sight.</summary>
    private void PaintRulers(UIDrawList list, UISkin skin, UIRect area, float left, float digit)
    {
        int[] rulers = Rulers;
        if (rulers.Length == 0)
            return;

        Vector4 colour = skin.TextColor with { W = 0.12f };
        for (int i = 0; i < rulers.Length; i++)
        {
            float x = MathF.Round(left + rulers[i] * digit);
            if (x < area.Min.X || x > area.Max.X)
                continue;

            list.Rect(new UIRect(new Vector2(x, area.Min.Y), new Vector2(x + 1.0f, area.Max.Y)), colour);
        }
    }
}
