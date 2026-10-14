using Horizon.Content;

namespace Horizon.Tests;

// One test class, the texts are the engine's one setting and tests running side by side would trip over each other
[Collection(nameof(LocalisationTests))]
public class LocalisationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "horizon_localisation_" + Guid.NewGuid().ToString("N"));

    public LocalisationTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "en-gb.hor"), """
            let strings = {
                main_menu: {
                    quit: "Quit",
                    welcome: "Hello {name}, {count} wins"
                },
                pause: { title: "PAUSED" },
                quoted: "a \"quote\""
            };
            """);
        File.WriteAllText(Path.Combine(folder, "fr-fr.hor"), """let strings = { main_menu: { quit: "Quitter" } };""");
    }

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void A_language_is_read_by_keys_with_dots_in_them()
    {
        Assert.True(Localisation.Load(folder, "en-gb"));

        Assert.Equal("Quit", Localisation.Get("main_menu.quit"));
        Assert.Equal("PAUSED", Localisation.Get("pause.title"));
        Assert.Equal("a \"quote\"", Localisation.Get("quoted"));
        Assert.Equal("en-gb", Localisation.Language);
        Assert.Equal(["en-gb", "fr-fr"], Localisation.Available(folder));
    }

    [Fact]
    public void A_missing_text_is_its_key_and_never_a_crash()
    {
        Localisation.Load(folder, "en-gb");

        Assert.Equal("main_menu.nothing", Localisation.Get("main_menu.nothing"));
        Assert.Equal("@main_menu.nothing", Localisation.Resolve("@main_menu.nothing"));
        Assert.False(Localisation.Load(folder, "xx-xx"));
        Assert.Equal("Quit", Localisation.Get("main_menu.quit"));
    }

    [Fact]
    public void Names_in_braces_are_filled_in()
    {
        Localisation.Load(folder, "en-gb");

        Assert.Equal("Hello Ryu, 3 wins", Localisation.Format("main_menu.welcome", ("name", "Ryu"), ("count", 3)));
        Assert.Equal("Hello {name}, 3 wins", Localisation.Format("main_menu.welcome", ("count", 3)));
    }

    [Fact]
    public void A_layout_names_a_text_with_an_at_and_two_of_them_is_one_written_on_purpose()
    {
        Localisation.Load(folder, "en-gb");

        Assert.Equal("Quit", Localisation.Resolve("@main_menu.quit"));
        Assert.Equal("just words", Localisation.Resolve("just words"));
        Assert.Equal("@home", Localisation.Resolve("@@home"));
        Assert.True(Localisation.IsKey("@pause.title"));
        Assert.False(Localisation.IsKey("@@pause.title"));

        Localisation.Load(folder, "fr-fr");
        Assert.Equal("Quitter", Localisation.Resolve("@main_menu.quit"));
    }

    [Fact]
    public void The_folder_is_found_next_to_the_layouts()
    {
        string layouts = Path.Combine(folder, "ui", "layouts");
        string languages = Path.Combine(folder, "ui", "localisation");
        Directory.CreateDirectory(layouts);
        Directory.CreateDirectory(languages);

        Assert.Equal(Path.GetFullPath(languages), Localisation.FindNear(Path.Combine(layouts, "main_menu.hor")));
    }
}
