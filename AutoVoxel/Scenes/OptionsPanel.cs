using System.Numerics;

using AutoVoxel.Gameplay;

using Horizon.Rendering;
using Horizon.Engine;
using Horizon.Rendering.Voxels;
using Horizon.UI;
using Horizon.UI.Components;

namespace AutoVoxel.Scenes;

/// <summary>
/// The options, the same panel on the main menu and on the pause menu. Every change is applied to the renderer that
/// is drawing at once (so what it does is seen behind the menu) and saved.
/// </summary>
public static class OptionsPanel
{
    public static readonly Vector4 PanelColor = new(0.06f, 0.07f, 0.1f, 0.82f);

    public static StackPanel Build(UIModule module, Func<VoxelRenderer?> renderer, Action back)
    {
        GameSettings s = GameSettings.Current;
        var panel = module.AddComponent(new StackPanel { Color = PanelColor, Radius = 12, Padding = new UIEdges(28), Spacing = 12, Stretch = true });

        panel.Add(new Label("Options") { TextScale = 0.5f });

        Row(panel, "Render scale", new Selector([.. GameSettings.Scales.Select(x => $"{x * 100:0}%")])
        {
            Index = Math.Max(0, Array.IndexOf(GameSettings.Scales, s.RenderScale)),
            OnChanged = _ => { }
        }, selector => s.RenderScale = GameSettings.Scales[selector.Index]);

        Row(panel, "Detail far off", new Selector(GameSettings.DetailNames) { Index = s.Detail }, selector => s.Detail = selector.Index);
        Row(panel, "Bounce light", new Selector("Off", "On") { Index = s.BounceLight ? 1 : 0 }, selector => s.BounceLight = selector.Index == 1);
        Row(panel, "Block textures", new Selector("Off", "On") { Index = s.Textures ? 1 : 0 }, selector => s.Textures = selector.Index == 1);
        Row(panel, "Bloom", new Selector("Off", "On") { Index = s.Bloom ? 1 : 0 }, selector => s.Bloom = selector.Index == 1);
        Row(panel, "Auto jump", new Selector("Off", "On") { Index = s.AutoJump ? 1 : 0 }, selector => s.AutoJump = selector.Index == 1);

        SliderRow(panel, "Field of view", 60, 110, 5, s.FieldOfView, value => s.FieldOfView = value);
        SliderRow(panel, "Mouse speed", 0.2f, 3.0f, 0.1f, s.MouseSensitivity, value => s.MouseSensitivity = value);
        SliderRow(panel, "Day length (min)", 2, 60, 1, s.DayMinutes, value => s.DayMinutes = value);

        panel.Add(new Button("Back") { OnPressed = back });
        return panel;

        void Row(StackPanel into, string name, Selector selector, Action<Selector> set)
        {
            var row = into.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 16 });
            row.Add(new Label(name) { Size = new Vector2(220, 0), TextScale = 0.26f, Align = Origin.Left });
            selector.Size = new Vector2(240, 44);
            selector.TextScale = 0.24f;
            selector.OnChanged = _ =>
            {
                set(selector);
                Changed();
            };
            row.Add(selector);
        }

        void SliderRow(StackPanel into, string name, float min, float max, float step, float value, Action<float> set)
        {
            var row = into.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 16 });
            row.Add(new Label(name) { Size = new Vector2(220, 0), TextScale = 0.26f, Align = Origin.Left });
            var slider = row.Add(new Slider { Min = min, Max = max, Step = step, Size = new Vector2(240, 28) });
            slider.Value = value;
            slider.OnChanged = v =>
            {
                set(v);
                Changed();
            };
        }

        void Changed()
        {
            if (renderer() is { } r) GameSettings.Current.Apply(r);
            GameSettings.Save();
        }
    }
}
