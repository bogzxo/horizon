using System.Reflection;

using DotTiled.Serialization;
using Horizon.Content;
using Horizon.Graphics;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Text;
using Horizon.Rendering.Tiling;
using Horizon.UI;
using Horizon.UI.Skinning;

namespace Horizon.Tests.Content;

/// <summary>
/// Every file Horizon.Testing ships (the engine's Assets and shaders and its own, laid out the way they land next to
/// its exe) put in a package in memory, nothing on the disk to fall back on, and read through it by the readers that
/// read that kind of file. What a phone build would get, without a phone.
/// </summary>
[Collection(ContentSourceCollection.NAME)]
public class PackagedAssetsTests
{
    // Where each folder of the repo lands in the output, the order the build copies them in
    private static readonly (string From, string To)[] Shipped =
    [
        ("Horizon/Assets", "Assets"),
        ("Horizon/shaders", "shaders"),
        ("Horizon.Testing/Assets", "Assets"),
        ("Horizon.Testing/shaders", "shaders"),
    ];

    internal static Dictionary<string, byte[]> Package()
    {
        string root = ContentDecodeTests.Repo();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (from, to) in Shipped)
        {
            string folder = Path.Combine(root, from);
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                files[to + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        }

        return files;
    }

    [Fact]
    public void EveryShippedFileReadsOutOfAPackageInMemory()
    {
        Dictionary<string, byte[]> files = Package();
        var package = new MemoryContent(files);
        using var installed = new InstalledContent(package);
        var read = new Dictionary<string, int>();
        var failed = new List<string>();

        AsepriteDocument.Forget();
        SpriteImages.Forget();
        foreach (var (path, bytes) in files)
        {
            string kind = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                Assert.Equal(bytes, ContentFiles.ReadAllBytes(path));
                Read(path, kind);
                read[kind] = read.GetValueOrDefault(kind) + 1;
            }
            catch (Exception e)
            {
                failed.Add($"{path}: {e.GetType().Name} {e.Message}");
            }
        }

        AsepriteDocument.Forget();
        SpriteImages.Forget();
        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed));

        // Every kind the examples lean on was read by its reader, not only copied
        foreach (string kind in new[] { ".png", ".aseprite", ".tmx", ".tsx", ".tx", ".fnt", ".slang", ".hor", ".ttf" })
            Assert.True(read.GetValueOrDefault(kind) > 0, $"nothing {kind} was read");
        Assert.True(package.Opened > files.Count);
    }

    private static void Read(string path, string kind)
    {
        switch (kind)
        {
            case ".png":
                Assert.True(ImageFile.Load(path).Width > 0);
                break;
            case ".aseprite" or ".ase":
                var document = AsepriteDocument.Open(path);
                Assert.NotEmpty(document.ReadFrame(0));
                document.DropRendered();
                break;
            case ".tmx" when path.EndsWith("/slanted.tmx", StringComparison.Ordinal):
                // The map the tile map checks have to see turned away, it's isometric
                Assert.Throws<NotSupportedException>(() => TileMap.Load(path));
                break;
            case ".tmx":
                TileMap.Load(path);
                break;
            case ".tsx":
                Loader.DefaultWith(resourceReader: new TileMapContentReader(ContentFiles.Source)).LoadTileset(path);
                break;
            case ".tx":
                Assert.Contains("<template", ContentFiles.ReadAllText(path));
                break;
            case ".fnt":
                var font = new BMFontImporter(Path.GetDirectoryName(path)!, Path.GetFileName(path));
                Assert.True(ContentFiles.Exists(font.ImagePath), font.ImagePath);
                break;
            case ".slang":
                using (var preprocessor = new ShaderPreprocessor())
                    Assert.NotEmpty(preprocessor.ProcessFile(path));
                break;
            case ".hor":
                // Data and skins are evaluated by whatever reads them, here it's enough that each one parses
                new Horizon.HIDL.Parsing.Parser().ProduceSyntaxTree(Horizon.HIDL.Lexing.Lexer.Tokenize(ContentFiles.ReadAllText(path)));
                break;
            default:
                Assert.NotNull(ContentFiles.ReadAllBytes(path));
                break;
        }
    }

    [Fact]
    public void TheSkinsAreFoundListedAndDescribedOutOfThePackage()
    {
        string[] onDisk = [.. Directory.GetDirectories(Path.Combine(ContentDecodeTests.Repo(), "Horizon/Assets/uix"))
            .Where(folder => File.Exists(Path.Combine(folder, UISkins.FILE))).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];

        try
        {
            using (new InstalledContent(new MemoryContent(Package())))
            {
                UISkins.Refresh();
                Assert.Equal(onDisk, UISkins.Available.Select(skin => skin.Name));
                foreach (UISkinChoice skin in UISkins.Available)
                {
                    var (_, themes, _) = UISkin.Describe(skin.Directory, skin.File);
                    Assert.Equal(themes, skin.Themes);
                }

                Assert.NotEmpty(UISkins.Find("flat")!.Themes);
            }
        }
        finally
        {
            UISkins.Refresh();
        }
    }

    [Fact]
    public void ALayoutIsCompiledOutOfItsOwnSourceEvenUnderTheSameName()
    {
        MethodInfo compile = typeof(UILayout).GetMethod("Compile", BindingFlags.NonPublic | BindingFlags.Static)!;
        object first, second, again;

        var one = MemoryContent.Of(("Assets/ui/menu.hor", "let a = 1\n"));
        var two = MemoryContent.Of(("Assets/ui/menu.hor", "let a = 1\nlet b = 2\nlet c = 3\n"));
        using (new InstalledContent(one))
            first = compile.Invoke(null, ["Assets/ui/menu.hor"])!;
        using (new InstalledContent(two))
            second = compile.Invoke(null, ["Assets/ui/menu.hor"])!;
        using (new InstalledContent(one))
            again = compile.Invoke(null, ["Assets/ui/menu.hor"])!;

        Assert.NotSame(first, second);
        Assert.Single(((Horizon.HIDL.Parsing.ProgramStatement)first).Body);
        Assert.Equal(3, ((Horizon.HIDL.Parsing.ProgramStatement)second).Body.Count);

        // Installing a source again is a new identity too, its files could have changed in between
        Assert.NotSame(first, again);
        UILayout.Forget("Assets/ui/menu.hor");
    }
}
