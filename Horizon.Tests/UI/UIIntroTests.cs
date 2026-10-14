using Horizon.UI.Components;

namespace Horizon.Tests;

public class UIIntroTests
{
    /// <summary>Helper method to move a component's entrance along, the way its updates would.</summary>
    private static void Tick(UIComponent component, float seconds)
    {
        const float step = 1.0f / 120.0f;
        for (float t = 0.0f; t < seconds; t += step)
            component.Tweens.Tick(step);
    }

    [Fact]
    public void What_is_inside_waits_as_long_as_what_it_is_in()
    {
        var panel = new StackPanel { Intro = UIIntro.Fade, IntroDelay = 0.5f, IntroTime = 0.2f };
        var child = panel.Add(new Label("hello") { Intro = UIIntro.Fade, IntroTime = 0.2f });
        var late = panel.Add(new Label("later") { Intro = UIIntro.Fade, IntroDelay = 0.3f, IntroTime = 0.2f });

        panel.PlayIntro();

        // Not a thing of the child to see while the panel is still waiting, it used to be all the way in by now
        Tick(child, 0.4f);
        Tick(late, 0.4f);
        Assert.Equal(0.0f, child.Opacity, 3);
        Assert.Equal(0.0f, late.Opacity, 3);

        // It comes in with the panel, and the one with a delay of its own that much after
        Tick(child, 0.4f);
        Tick(late, 0.4f);
        Assert.Equal(1.0f, child.Opacity, 3);
        Assert.InRange(late.Opacity, 0.0f, 0.5f);

        Tick(late, 0.4f);
        Assert.Equal(1.0f, late.Opacity, 3);
    }

    [Fact]
    public void A_stagger_goes_on_top_of_the_delay_of_the_container()
    {
        var panel = new StackPanel { IntroDelay = 0.4f, Stagger = 0.2f };
        var first = panel.Add(new Label("one") { Intro = UIIntro.Fade, IntroTime = 0.1f });
        var second = panel.Add(new Label("two") { Intro = UIIntro.Fade, IntroTime = 0.1f });

        panel.PlayIntro();

        Tick(first, 0.55f);
        Tick(second, 0.55f);
        Assert.Equal(1.0f, first.Opacity, 3);
        Assert.Equal(0.0f, second.Opacity, 3);

        Tick(second, 0.2f);
        Assert.Equal(1.0f, second.Opacity, 3);
    }
}
