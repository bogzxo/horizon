using System.Numerics;

using Horizon.Rendering.Spriting;

namespace Horizon.UI.Drawing;

public sealed partial class UIDrawList
{
    /// <summary>
    /// A wavy line from one place to another along a middle, what a code editor draws under what's wrong. It's half rings
    /// one after the other, over the middle and then under it, each the quarters of a disc a rounded box has for its
    /// corners, so it's smooth at any size and worked out per pixel like they are. As many half waves as fit the length
    /// near enough, the last ends where the line does. Two quads a half wave and nothing allocated.
    /// </summary>
    /// <param name="height">How tall it is from the top of a wave to the bottom of the next.</param>
    /// <param name="thickness">How thick the line is.</param>
    public void Squiggle(float from, float to, float middle, float height, float thickness, Vector4 color)
    {
        if (to <= from || height <= 0.0f || thickness <= 0.0f || color.W <= 0.0f)
            return;

        // Where the stroke's middle goes round, the half waves' middles are two of these apart and meet end to end
        float bend = MathF.Max(0.5f, height * 0.5f - thickness * 0.5f);
        int halves = Math.Max(2, (int)MathF.Round((to - from) / (bend * 2.0f)));
        bend = (to - from) / (halves * 2.0f);
        thickness = MathF.Min(thickness, bend * 2.0f);
        float radius = bend + thickness * 0.5f;
        float ring = thickness / radius;

        uint packed = SpriteItem.PackColor(color);
        uint flags = SpriteItem.NoTexture | SpriteItem.CornerFlag;
        for (int i = 0; i < halves; i++)
        {
            float centre = from + bend * (i * 2 + 1);
            if (i % 2 == 0)
            {
                // Over the middle, the top left and the top right quarters of the disc
                Quad(new UIRect(new Vector2(centre - radius, middle), new Vector2(centre, middle + radius)), Vector2.One, Vector2.Zero, packed, flags, ring);
                Quad(new UIRect(new Vector2(centre, middle), new Vector2(centre + radius, middle + radius)), Vector2.UnitY, Vector2.UnitX, packed, flags, ring);
            }
            else
            {
                // Under it, the bottom left and the bottom right
                Quad(new UIRect(new Vector2(centre - radius, middle - radius), new Vector2(centre, middle)), Vector2.UnitX, Vector2.UnitY, packed, flags, ring);
                Quad(new UIRect(new Vector2(centre, middle - radius), new Vector2(centre + radius, middle)), Vector2.Zero, Vector2.One, packed, flags, ring);
            }
        }
    }
}
