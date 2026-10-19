using System.Numerics;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.Tests.Scripting;

/// <summary>Whole .hor files written with HIDLDocument and C# objects kept in them with HIDLSerializer, read back the same.</summary>
public class HIDLSerializerTests
{
    private enum Mood { Calm, VeryAngry }

    private sealed class Options
    {
        [HidlComment("How big the text is.")]
        public float TextScale { get; set; } = 0.14f;

        public string Theme = "slate";
        public bool Animations { get; set; } = true;
        public Mood Feeling { get; set; } = Mood.Calm;
        public Vector2 Size { get; set; } = new(1600, 900);
        public List<string> Recent { get; set; } = [];
        public HashSet<string> Off { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, float> Zoom { get; set; } = [];
        public Inner Window { get; set; } = new();

        [HidlName("dark")]
        public bool IsDark { get; set; }

        [HidlIgnore]
        public string Secret { get; set; } = "never written";

        public Action? Callback { get; set; }
    }

    private sealed class Inner
    {
        public int Width { get; set; } = 10;
    }

    private static IRuntimeValue Evaluate(string text, string name)
    {
        var runtime = new HIDLRuntime { Output = null };
        var (success, message) = runtime.Evaluate(text);
        Assert.True(success, message);
        return runtime.UserScope.Lookup(name);
    }

    [Fact]
    public void A_document_writes_its_comments_defines_and_commented_keys_and_reads_back()
    {
        string text = new HIDLDocument()
            .Comment("The first line.\nThe second.")
            .Define("dusk")
            .Define("version", "2")
            .Let("settings", new HIDLObject()
                .Add("theme", "horizon", "Which theme.")
                .Add("inner", new HIDLObject().Add("x", 1f, "A number."))
                .Add("rows", [new HIDLObject().Add("a", true), new HIDLObject().Add("b", false)])
                .Note("example: 1,"))
            .Write();

        Assert.StartsWith("// The first line.\n// The second.\n#define dusk\n#define version 2\n\nlet settings = {\n    // Which theme.\n    theme: \"horizon\",", text);
        Assert.Contains("        // A number.\n        x: 1\n", text);
        Assert.Contains("    // example: 1,\n}", text);

        var read = Assert.IsType<ObjectValue>(Evaluate(text, "settings"));
        Assert.Equal("horizon", Assert.IsType<StringValue>(read.Properties["theme"]).Value);
        Assert.Equal(2, Assert.IsType<ListValue>(read.Properties["rows"]).Count);
        Assert.False(read.Properties.ContainsKey("example"));
    }

    [Fact]
    public void An_object_goes_out_as_snake_case_with_its_comments_and_comes_back_the_same()
    {
        var options = new Options { TextScale = 0.2f, Theme = "dark", Feeling = Mood.VeryAngry, Size = new Vector2(800, 600), IsDark = true };
        options.Recent.AddRange(["a.hor", "b.hor"]);
        options.Off.Add("rules");
        options.Zoom["code"] = 1.5f;
        options.Window.Width = 42;

        string text = HIDLSerializer.Document("options", options).Comment("Options.").Write();
        Assert.Contains("    // How big the text is.\n    text_scale: 0.2,", text);
        Assert.Contains("feeling: \"very_angry\"", text);
        Assert.Contains("dark: true", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("callback", text);

        var back = new Options();
        HIDLSerializer.Populate(back, Evaluate(text, "options"));
        Assert.Equal(0.2f, back.TextScale, 3);
        Assert.Equal("dark", back.Theme);
        Assert.Equal(Mood.VeryAngry, back.Feeling);
        Assert.Equal(new Vector2(800, 600), back.Size);
        Assert.Equal(["a.hor", "b.hor"], back.Recent);
        Assert.Contains("rules", back.Off);
        Assert.Equal(1.5f, back.Zoom["code"], 3);
        Assert.Equal(42, back.Window.Width);
        Assert.True(back.IsDark);
        Assert.Equal("never written", back.Secret);
    }

    [Fact]
    public void What_a_file_doesnt_say_is_left_as_it_was()
    {
        var options = new Options { Theme = "kept" };
        HIDLSerializer.Populate(options, Evaluate("let options = { text_scale: 0.3 };", "options"));
        Assert.Equal(0.3f, options.TextScale, 3);
        Assert.Equal("kept", options.Theme);
    }

    [Theory]
    [InlineData("TextScale", "text_scale")]
    [InlineData("HTMLParser", "html_parser")]
    [InlineData("Size2D", "size2_d")]
    [InlineData("theme", "theme")]
    public void Names_go_into_snake_case(string name, string snake) => Assert.Equal(snake, HIDLSerializer.SnakeCase(name));
}
