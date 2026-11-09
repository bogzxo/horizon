using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Skinning;

namespace Horizon.UI.Drawing;

/// <summary>
/// Keys drawn as keycaps, each part of a chord a cap of its own ("Ctrl+K Ctrl+S" is four), the one look a list of
/// commands and the table of the keys have them in. Menus write theirs as plain dim words the way VS Code's do. The
/// palette had caps, the keys table plain words and the menus plain grey, three looks of one thing.
/// </summary>
internal static class UIKeycaps
{
    // How far apart the caps are and how padded, and how much smaller their letters are than the row's
    public const float GAP = 4.0f, PAD = 6.0f, SCALE = 0.82f;

    /// <summary>Draws keys from the right of an area, giving back where the rest of the row has to stop.</summary>
    public static float PaintRight(UIDrawList list, string keys, UIRect area, float scale, Vector4 ink, bool chosen)
    {
        float right = area.Max.X;
        if (keys.Length == 0)
            return right;

        List<string> caps = Caps(keys);
        float height = Height(list.Skin, area, scale);
        for (int c = caps.Count - 1; c >= 0; c--)
        {
            float width = Width(list.Skin, caps[c], scale, height);
            Paint(list, caps[c], new UIRect(new Vector2(right - width, area.Center.Y - height * 0.5f), new Vector2(right, area.Center.Y + height * 0.5f)), scale, ink, chosen);
            right -= width + GAP;
        }

        return right - PAD * 2.0f;
    }

    /// <summary>Draws keys from the left of an area, cut off where the area ends, a column of a table.</summary>
    public static void PaintLeft(UIDrawList list, string keys, UIRect area, float scale, Vector4 ink, bool chosen)
    {
        if (keys.Length == 0)
            return;

        float left = area.Min.X;
        float height = Height(list.Skin, area, scale);
        list.PushClip(area);
        foreach (string cap in Caps(keys))
        {
            float width = Width(list.Skin, cap, scale, height);
            Paint(list, cap, new UIRect(new Vector2(left, area.Center.Y - height * 0.5f), new Vector2(left + width, area.Center.Y + height * 0.5f)), scale, ink, chosen);
            left += width + GAP;
        }
        list.PopClip();
    }

    private static float Height(UISkin skin, UIRect area, float scale) => MathF.Min(area.Height - 6.0f, skin.Font.LineHeight * scale * SCALE + 6.0f);

    private static float Width(UISkin skin, string cap, float scale, float height) =>
        MathF.Max(height, skin.Font.Measure(cap, scale * SCALE, markup: false).X + PAD * 2.0f);

    private static void Paint(UIDrawList list, string cap, UIRect box, float scale, Vector4 ink, bool chosen)
    {
        list.RoundRect(box, MathF.Min(list.Skin.CornerRadius, 5.0f), ink with { W = chosen ? 0.16f : 0.08f });
        list.Text(cap, box, Origin.Center, scale * SCALE, ink with { W = ink.W * 0.6f }, markup: false);
    }

    // The caps of some keys, a chord split at its spaces and each part at its pluses, a plus that is the key itself kept
    // ("Ctrl++" is Ctrl and +). It was split at every plus and Ctrl++ came out as Ctrl alone
    private static List<string> Caps(string keys)
    {
        var caps = new List<string>();
        foreach (string chord in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool plusKey = chord.Length > 1 && chord.EndsWith("++", StringComparison.Ordinal);
            string rest = plusKey ? chord[..^2] : chord;
            caps.AddRange(rest.Split('+', StringSplitOptions.RemoveEmptyEntries));
            if (plusKey || chord == "+")
                caps.Add("+");
        }

        return caps;
    }
}
