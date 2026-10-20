using System.Numerics;

using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// The two selectors an options screen wants for how the UI looks, which skin and which of its themes, wired to
/// the game's setting (<see cref="UISkins"/>) both ways. Stepping one changes the skin of every UI that follows the
/// setting there and then, and when somebody else changes it (a key, another screen) the selectors show that.
/// <code>
/// options.Add(new Label("Looks"));
/// options.Add(new SkinPicker { OnPicked = (skin, theme) => Settings.Save(skin.Name, theme) });
/// </code>
/// The themes are those of the skin that is picked, a skin that is one look has a selector with nothing to step.
/// </summary>
public sealed class SkinPicker : StackPanel
{
    private const string NO_THEMES = "one look";

    private readonly Selector skins, themes;
    private int seen = -1;

    // Set while the selectors are being made to show the setting, so that doesn't count as somebody picking
    private bool showing;

    /// <summary>Called when the player picked something here, with the skin and its theme (null for a skin without any).</summary>
    public Action<UISkinChoice, string?>? OnPicked { get; set; }

    /// <summary>The selector of the skins, to size or to walk to with a gamepad.</summary>
    public Selector Skins => skins;

    /// <summary>The selector of the themes of the skin that is picked.</summary>
    public Selector Themes => themes;

    /// <summary>The scale of the text in both, 0 for the skin's.</summary>
    public float TextScale
    {
        get => skins.TextScale;
        set => skins.TextScale = themes.TextScale = value;
    }

    public SkinPicker()
    {
        Direction = UIDirection.Horizontal;
        Spacing = 10.0f;

        skins = Add(new Selector { Size = new Vector2(300.0f, 0.0f), Tooltip = "The skin every UI of the game is drawn in" });
        themes = Add(new Selector { Size = new Vector2(220.0f, 0.0f), Tooltip = "The colour of it" });

        skins.OnChanged = _ => Pick(skinChanged: true);
        themes.OnChanged = _ => Pick(skinChanged: false);

        Show();
    }

    /// <summary>Helper method to put what was picked here in force.</summary>
    private void Pick(bool skinChanged)
    {
        if (showing)
            return;

        var all = UISkins.Available;
        if ((uint)skins.Index >= (uint)all.Count)
            return;

        UISkinChoice skin = all[skins.Index];

        // Another skin comes up in its usual theme, the one that was showing was the old skin's
        string? theme = skinChanged || skin.Themes.Count == 0 ? null : skin.Themes[Math.Clamp(themes.Index, 0, skin.Themes.Count - 1)];
        UISkins.Use(skin, theme);
        Show();

        OnPicked?.Invoke(skin, UISkins.Theme ?? skin.UsualTheme);
    }

    /// <summary>Helper method to have the selectors say what the setting is.</summary>
    private void Show()
    {
        showing = true;
        seen = UISkins.Version;

        var all = UISkins.Available;
        UISkinChoice current = UISkins.Current;

        var titles = new string[all.Count];
        int at = 0;
        for (int i = 0; i < all.Count; i++)
        {
            titles[i] = all[i].Title;
            if (all[i].Name == current.Name) at = i;
        }

        skins.Options = titles;
        skins.Index = at;

        if (current.Themes.Count == 0)
        {
            themes.Options = [NO_THEMES];
            themes.Index = 0;
            themes.Enabled = false;
        }
        else
        {
            themes.Options = [.. current.Themes];
            themes.Enabled = true;

            string? theme = UISkins.Theme ?? current.UsualTheme;
            int index = 0;
            for (int i = 0; i < current.Themes.Count; i++)
            {
                if (current.Themes[i] == theme) index = i;
            }

            themes.Index = index;
        }

        showing = false;
    }

    protected override void Update(float dt)
    {
        base.Update(dt);

        // Somebody else changed it
        if (seen != UISkins.Version)
            Show();
    }
}
