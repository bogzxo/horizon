using Horizon.Rendering.Spriting;
using Horizon.UI.Skinning;

namespace Horizon.Tests;

/// <summary>
/// Every skin under Assets/uix, read off the disk the way the engine reads it and without a window. Whether its
/// files say what they have to, whether its sheet has (or falls back on) every piece of art the components ask
/// for, in every theme, and whether every sprite it names lies inside of the picture it is cut out of. The same
/// things with something on screen are the check-ui-skins scene of the examples, this is the part that is over
/// in a second and runs with the rest of the tests. A skin somebody adds is tested the day its folder is there.
/// </summary>
public class UISkinPackTests
{
    private const string ROOT = "Horizon/Assets/uix";

    // What the components and the layouts ask a skin with art for by name
    private static readonly string[] Wanted =
    [
        "button", "button_hover", "button_pressed", "button_selected", "button_flat", "button_flat_hover", "button_outline",
        "panel", "panel_window", "frame", "header", "slot", "check_on", "check_off", "radio_on", "radio_off",
        "slider_track", "slider_knob", "bar_frame", "bar_fill", "key", "selector", "arrow_up", "arrow_down", "arrow_left",
        "arrow_right", "heart", "star_flat", "round_check", "round_cross", "xbox_a", "xbox_dpad", "ps_cross", "key_w",
        "key_space", "mouse_left", "icon_lock", "icon_sword", "icon_shield"
    ];

    private static string Repo()
    {
        for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
        {
            if (File.Exists(Path.Combine(at, "Horizon.sln")))
                return at;
        }

        throw new DirectoryNotFoundException("The tests have to run somewhere under the repo.");
    }

    /// <summary>Every folder that is a skin, by its name.</summary>
    public static TheoryData<string> Skins()
    {
        var skins = new TheoryData<string>();
        foreach (string name in Names())
            skins.Add(name);

        return skins;
    }

    private static string[] Names() =>
    [
        .. Directory.GetDirectories(Path.Combine(Repo(), ROOT)).Order()
            .Where(folder => File.Exists(Path.Combine(folder, UISkins.FILE)))
            .Select(folder => Path.GetFileName(folder)!)
    ];

    private static string Folder(string skin) => Path.Combine(Repo(), ROOT, skin) + Path.DirectorySeparatorChar;

    private static SpriteSheetDefinition? Sheet(string skin)
    {
        string file = Path.Combine(Folder(skin), "sprites.hor");
        return File.Exists(file) ? SpriteSheetDefinition.Load(Folder(skin), "sprites.hor") : null;
    }

    [Fact]
    public void There_are_the_skins_that_were_asked_for()
    {
        string[] skins = Names();

        Assert.Contains("dead_revolver", skins);
        Assert.Contains("flat", skins);
        Assert.Contains("retro_windows", skins);
        Assert.Contains("kenney", skins);
        Assert.Contains("adventure", skins);
        Assert.Contains("dragon", skins);
        Assert.Contains("scifi", skins);
    }

    [Theory]
    [MemberData(nameof(Skins))]
    public void A_skin_says_what_it_is_called_and_which_theme_is_its_usual_one(string skin)
    {
        var (title, themes, usual) = UISkin.Describe(Folder(skin), UISkins.FILE);

        Assert.False(string.IsNullOrWhiteSpace(title), $"{skin} has no title, an options screen would have to call it by its folder");

        // No two alike, and the one it comes up in is one of them
        Assert.Equal(themes.Length, themes.Distinct().Count());
        if (themes.Length > 0)
            Assert.Contains(usual, themes);
    }

    [Theory]
    [MemberData(nameof(Skins))]
    public void A_skin_has_its_credits_next_to_it(string skin)
    {
        // The two the engine always had are its own. Whatever was got from somebody says who and under what
        if (skin is "dead_revolver" or "flat")
            return;

        string credits = Path.Combine(Folder(skin), "README.txt");
        Assert.True(File.Exists(credits), $"{skin} has art somebody made and no README.txt saying who");

        string text = File.ReadAllText(credits);
        Assert.Contains("http", text);
    }

    [Theory]
    [MemberData(nameof(Skins))]
    public void The_themes_of_a_skin_are_the_themes_of_its_art(string skin)
    {
        if (Sheet(skin) is not { } sheet)
            return;

        var (_, themes, _) = UISkin.Describe(Folder(skin), UISkins.FILE);

        // A theme the skin offers and the art hasn't got is a skin that fails to load the day somebody picks it
        foreach (string theme in themes)
            Assert.Contains(theme, sheet.Themes);
    }

    [Theory]
    [MemberData(nameof(Skins))]
    public void A_skin_with_art_has_everything_the_components_ask_for_in_every_theme(string skin)
    {
        if (Sheet(skin) is not { } sheet)
            return;

        var (_, themes, _) = UISkin.Describe(Folder(skin), UISkins.FILE);

        foreach (string? theme in themes.Length > 0 ? themes.Cast<string?>() : [null])
        {
            foreach (string name in Wanted)
            {
                Assert.True(sheet.TryGetSprite(name, theme, out var sprite), $"{skin} has no '{name}' in {theme ?? "its only look"}, its own or borrowed");
                Assert.True(File.Exists(sprite.Path), $"{skin}'s '{name}' is cut out of '{sprite.Path}', which isn't there");
                Assert.True(sprite.Width > 0 && sprite.Height > 0, $"{skin}'s '{name}' is no size at all");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Skins))]
    public void Every_sprite_of_a_skin_lies_inside_of_its_picture_with_borders_that_fit(string skin)
    {
        if (Sheet(skin) is not { } sheet)
            return;

        var sizes = new Dictionary<string, (int Width, int Height)>();

        foreach (string name in sheet.Sprites)
        {
            Assert.True(sheet.TryGetSprite(name, null, out var sprite), name);

            if (!sizes.TryGetValue(sprite.Path, out var size))
            {
                var document = AsepriteDocument.Open(sprite.Path);
                sizes[sprite.Path] = size = (document.Width, document.Height);
            }

            for (int frame = 0; frame < sprite.Frames; frame++)
            {
                (int x, int y) = sprite.FrameAt(frame);
                Assert.True(x >= 0 && y >= 0 && x + sprite.Width <= size.Width && y + sprite.Height <= size.Height,
                    $"{skin}'s '{name}' frame {frame} is at {x}, {y} and {sprite.Width} by {sprite.Height}, outside of a picture of {size.Width} by {size.Height}");
            }

            // What keeps its size along the edges can't be more than there is of the sprite
            Assert.True(sprite.Border.X + sprite.Border.Z <= sprite.Width && sprite.Border.Y + sprite.Border.W <= sprite.Height,
                $"{skin}'s '{name}' has a border wider than itself");
        }
    }

    [Theory]
    [InlineData("retro_windows")]
    [InlineData("kenney")]
    [InlineData("adventure")]
    [InlineData("dragon")]
    public void A_pack_borrows_what_it_hasnt_got_at_the_size_that_art_was_drawn_for(string skin)
    {
        var sheet = Sheet(skin)!;

        // Its own button is its own picture, drawn at whatever the skin draws its art at
        Assert.True(sheet.TryGetSprite("button", null, out var own));
        Assert.Contains(skin, own.Path);
        Assert.Equal(0.0f, own.Scale);

        // A gamepad button it never had is Dead Revolver's, at the three times its size that was made for
        Assert.True(sheet.TryGetSprite("xbox_a", null, out var borrowed));
        Assert.Contains("dead_revolver", borrowed.Path);
        Assert.Equal(3.0f, borrowed.Scale);

        // And only the sprites are borrowed, the themes of that pack are not this one's
        Assert.DoesNotContain("purple", sheet.Themes);
    }

    [Fact]
    public void The_high_resolution_pack_brings_icons_of_its_own()
    {
        var sheet = Sheet("scifi")!;

        // Pixel art blown up next to art this smooth looked like trash, so none of what a text draws is borrowed
        foreach (string name in (string[])["xbox_a", "xbox_dpad", "ps_cross", "key_w", "key_space", "mouse_left", "heart", "star_flat", "icon_lock", "icon_sword", "icon_shield", "arrow_up", "round_check"])
        {
            Assert.True(sheet.TryGetSprite(name, null, out var sprite), name);
            Assert.Contains("scifi", sprite.Path);
            Assert.Equal(0.0f, sprite.Scale);
        }

        var (_, _, _) = UISkin.Describe(Folder("scifi"), UISkins.FILE);
        Assert.Contains("smooth: true", File.ReadAllText(Path.Combine(Folder("scifi"), UISkins.FILE)));
    }

    [Fact]
    public void A_sprite_says_what_to_write_on_it_in_when_its_face_is_not_the_colour_of_the_rest()
    {
        var sheet = Sheet("kenney")!;

        // The solid buttons are written on in white, the white panel in whatever the skin writes in
        Assert.True(sheet.TryGetSprite("button", "blue", out var button));
        Assert.Equal(1.0f, button.TextColor.W);
        Assert.Equal(1.0f, button.TextColor.X);

        // The same goes for its other looks, they are the same button
        Assert.True(sheet.TryGetSprite("button_hover", "blue", out var hover));
        Assert.Equal(button.TextColor, hover.TextColor);

        Assert.True(sheet.TryGetSprite("panel", "blue", out var panel));
        Assert.Equal(0.0f, panel.TextColor.W);
    }

    [Fact]
    public void A_theme_is_a_layer_and_a_sprite_is_in_the_same_place_on_every_one()
    {
        var sheet = Sheet("kenney")!;

        Assert.True(sheet.TryGetSprite("button", "blue", out var blue));
        Assert.True(sheet.TryGetSprite("button", "red", out var red));
        Assert.Equal("blue", blue.Layer);
        Assert.Equal("red", red.Layer);
        Assert.Equal((blue.X, blue.Y, blue.Width, blue.Height), (red.X, red.Y, red.Width, red.Height));

        // And they are different art
        Assert.False(blue.ReadFrame(0)!.Value.Data.AsSpan().SequenceEqual(red.ReadFrame(0)!.Value.Data));
    }

    [Fact]
    public void The_sakura_theme_is_a_layer_of_every_themed_file_of_dead_revolver()
    {
        string folder = Folder("dead_revolver");

        foreach (string file in (string[])["buttons", "panels", "form_elements", "value_bars", "cursors"])
            Assert.Contains("sakura", AsepriteDocument.Open(Path.Combine(folder, file + ".aseprite")).TopLayers);

        // The same button in pink, and not the purple it was made out of
        var sheet = Sheet("dead_revolver")!;
        Assert.True(sheet.TryGetSprite("button", "sakura", out var pink));
        Assert.True(sheet.TryGetSprite("button", "purple", out var purple));
        Assert.Equal("sakura", pink.Layer);
        Assert.False(pink.ReadFrame(0)!.Value.Data.AsSpan().SequenceEqual(purple.ReadFrame(0)!.Value.Data));

        var (_, themes, _) = UISkin.Describe(folder, UISkins.FILE);
        Assert.Contains("sakura", themes);
    }

    [Fact]
    public void A_sheet_that_falls_back_on_itself_says_so()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"horizon_sheet_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        try
        {
            File.WriteAllText(Path.Combine(folder, "sprites.hor"), "let sheet = { images: {}, inherit: \"sprites.hor\" }");
            var thrown = Assert.ThrowsAny<Exception>(() => SpriteSheetDefinition.Load(folder, "sprites.hor"));
            Assert.Contains("inherits from itself", thrown.Message);

            File.WriteAllText(Path.Combine(folder, "sprites.hor"), "let sheet = { images: {}, inherit: 5 }");
            Assert.ThrowsAny<Exception>(() => SpriteSheetDefinition.Load(folder, "sprites.hor"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_overlay_and_the_editor_have_their_layouts()
    {
        // The performance overlay is put together out of these, a build that leaves them behind has no overlay
        string diagnostics = Path.Combine(Repo(), ROOT, "diagnostics");
        foreach (string layout in (string[])["performance_card.hor", "performance_pill.hor", "performance_pass.hor"])
            Assert.True(File.Exists(Path.Combine(diagnostics, layout)), layout);

        // And they are not a skin, nobody should be offered "diagnostics" on an options screen
        Assert.False(File.Exists(Path.Combine(diagnostics, UISkins.FILE)));
    }
}
