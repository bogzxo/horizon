using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.Tests.UI;

/// <summary>
/// The flat skin's sizes of text by what they are for, read out of its skin.hor, in order and never small print. Tools
/// are written in it (Hex), and a sentence somebody is meant to read is the body or bigger.
/// </summary>
public class UITextSizesTests
{
    private static ObjectValue FlatSkin()
    {
        string at = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(at, "Horizon.sln")))
            at = Path.GetDirectoryName(at) ?? throw new DirectoryNotFoundException("no Horizon.sln above the tests");

        var runtime = new HIDLRuntime { Output = null };
        var (success, message) = runtime.Evaluate(File.ReadAllText(Path.Combine(at, "Horizon", "Assets", "uix", "flat", "skin.hor")));
        Assert.True(success, message);
        return Assert.IsType<ObjectValue>(runtime.UserScope.Lookup("skin"));
    }

    [Fact]
    public void The_flat_skins_sizes_go_up_by_what_they_are_for_and_its_body_reads()
    {
        var skin = FlatSkin().Properties;
        float body = Assert.IsType<NumberValue>(skin["text_scale"]).Value;
        var sizes = Assert.IsType<ObjectValue>(skin["text_sizes"]).Properties;
        float caption = Assert.IsType<NumberValue>(sizes["caption"]).Value;
        float heading = Assert.IsType<NumberValue>(sizes["heading"]).Value;
        float title = Assert.IsType<NumberValue>(sizes["title"]).Value;

        Assert.True(body >= 0.15f, "a sentence at 1600 by 900 is 0.15 of the em at the least");
        Assert.True(caption >= 0.12f && caption < body);
        Assert.True(heading > body && title > heading);
        Assert.Contains("inter", Assert.IsType<ObjectValue>(skin["font"]).Properties["dir"].ToString(), StringComparison.Ordinal);
    }
}
