using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.Text;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

public sealed class UIFileKindIconTests
{
    [Theory]
    [InlineData("A.cs", "filetype_csharp")]
    [InlineData("main.cpp", "filetype_cpp")]
    [InlineData("main.py", "filetype_python")]
    [InlineData("main.js", "filetype_javascript")]
    [InlineData("main.ts", "filetype_typescript")]
    [InlineData("view.tsx", "filetype_react")]
    [InlineData("types.d.ts", "filetype_typescript")]
    [InlineData("main.go", "filetype_go")]
    [InlineData("main.rs", "filetype_rust")]
    [InlineData("view.vue", "filetype_vue")]
    [InlineData("app.scss", "filetype_sass")]
    [InlineData("settings.yaml", "filetype_yaml")]
    public void Language_and_config_extensions_get_distinct_icons(string file, string icon) =>
        Assert.Equal(icon, UIFileKinds.IconOf(file));

    [Theory]
    [InlineData("package.json", "filetype_nodejs")]
    [InlineData("Cargo.lock", "filetype_rust")]
    [InlineData("GO.MOD", "filetype_go")]
    [InlineData("requirements.txt", "filetype_python")]
    [InlineData("compose.prod.yaml", "filetype_docker")]
    [InlineData("Dockerfile.dev", "filetype_docker")]
    public void Well_known_manifests_override_their_generic_extensions(string file, string icon) =>
        Assert.Equal(icon, UIFileKinds.IconOf(file));

    [Theory]
    [InlineData("main.pythn", "filetype_python")]
    [InlineData("main.typecript", "filetype_typescript")]
    [InlineData("main.python", "filetype_python")]
    [InlineData("main.csharp", "filetype_csharp")]
    public void Long_extension_aliases_allow_one_edit_matches(string file, string icon) =>
        Assert.Equal(icon, UIFileKinds.IconOf(file));

    [Fact]
    public void Short_and_unrelated_extensions_are_not_fuzzily_mislabeled()
    {
        Assert.Equal("file", UIFileKinds.IconOf("main.pthn"));
        Assert.Equal("file", UIFileKinds.IconOf("main.unknownx"));
        Assert.Equal("filetype_csharp", UIFileKinds.IconOf("Main.CS"));
    }

    [Fact]
    public void Text_detection_does_not_depend_on_icon_names()
    {
        Assert.True(UIFileKinds.IsText("source.unknown"));
        Assert.True(UIFileKinds.IsText("README"));
        Assert.True(UIFileKinds.IsText("vector.svg"));
        Assert.False(UIFileKinds.IsText("image.png"));
        Assert.False(UIFileKinds.IsText("library.dll"));
        Assert.False(UIFileKinds.IsText("font.woff2"));
    }

    [Fact]
    public void Devicon_asset_map_has_glyph_codes_for_the_resolver_icons()
    {
        string root = FindRepositoryRoot();
        string mapPath = Path.Combine(root, "Horizon", "Assets", "fonts", "devicon", "devicons.hor");
        string fontPath = Path.Combine(root, "Horizon", "Assets", "fonts", "devicon", "devicon.ttf");
        var runtime = new HIDLRuntime();
        var (success, message) = runtime.Evaluate(File.ReadAllText(mapPath));
        Assert.True(success, message);
        Assert.True(File.Exists(fontPath));
        Assert.IsType<ObjectValue>(runtime.UserScope.Lookup("icons"));
        var icons = ((ObjectValue)runtime.UserScope.Lookup("icons")!).Properties;

        Assert.Equal(60316, (int)Assert.IsType<NumberValue>(icons["python"]).Value);
        Assert.Equal(60033, (int)Assert.IsType<NumberValue>(icons["javascript"]).Value);
        Assert.Equal(60515, (int)Assert.IsType<NumberValue>(icons["typescript"]).Value);
        Assert.Equal(60390, (int)Assert.IsType<NumberValue>(icons["rust"]).Value);
        Assert.Equal(60597, (int)Assert.IsType<NumberValue>(icons["yaml"]).Value);

        // Building these outlines with stb's direct SDF solver overflowed the test host's stack. Exercise every
        // bundled glyph through the coverage path; the headless host needs only the resulting CPU metrics.
        var ranges = icons.Values.Cast<NumberValue>().Select(value => ((int)value.Value, (int)value.Value)).ToArray();
        var font = DistanceFieldFont.Load(Path.GetDirectoryName(fontPath)!, Path.GetFileName(fontPath), DistanceFieldFont.DEFAULT_EM,
            characters: ranges, rasterizeTrueTypeForDistanceField: true);
        Assert.All(icons.Values.Cast<NumberValue>(), value =>
            Assert.True(font.Glyphs.TryGetValue((char)(int)value.Value, out var glyph) && glyph.Size.X > 0));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
            if (Directory.Exists(Path.Combine(at.FullName, "Horizon", "Assets", "fonts", "devicon")))
                return at.FullName;
        for (DirectoryInfo? at = new(Directory.GetCurrentDirectory()); at is not null; at = at.Parent)
            if (Directory.Exists(Path.Combine(at.FullName, "Horizon", "Assets", "fonts", "devicon")))
                return at.FullName;
        throw new DirectoryNotFoundException("Could not find the Horizon repository asset folder.");
    }
}
