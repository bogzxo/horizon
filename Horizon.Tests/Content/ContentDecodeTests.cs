using System.Text;

using Horizon.Audio;
using Horizon.Content;
using Horizon.Graphics;
using Horizon.HIDL;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Text;
using Horizon.Rendering.Tiling;

namespace Horizon.Tests.Content;

/// <summary>
/// The readers of the engine reading out of a package in memory, with no disk under it and streams that trickle.
/// Pictures, Aseprite files, maps with their tile sets and templates in other folders, fonts, shaders, scripts and
/// sounds, and the caches of each not handing a source's file to another source with a file of the same name.
/// </summary>
[Collection(ContentSourceCollection.NAME)]
public class ContentDecodeTests
{
    internal static string Repo()
    {
        for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
        {
            if (File.Exists(Path.Combine(at, "Horizon.sln")))
                return at;
        }

        throw new DirectoryNotFoundException("The tests have to run somewhere under the repo.");
    }

    private static byte[] Png(byte r, byte g, byte b, int width = 3, int height = 2)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return ImageFile.EncodePng(pixels, width, height);
    }

    private static MemoryContent Files(params (string Path, byte[] Bytes)[] files) => new(files.ToDictionary(file => file.Path, file => file.Bytes));

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void APictureDecodesOutOfAStreamThatCantSeek()
    {
        using var installed = new InstalledContent(Files(("art/red.png", Png(255, 0, 0, 5, 4))));

        ImagePixels image = ImageFile.Load("art/red.png");

        Assert.Equal((5, 4), (image.Width, image.Height));
        Assert.Equal(255, image.Pixels[0]);
        Assert.Equal(0, image.Pixels[1]);
    }

    [Fact]
    public void TheSpriteImageCacheDoesntHandOneSourcesPictureToAnother()
    {
        SpriteImages.Forget();
        SpritePixels? red, blue;
        using (new InstalledContent(Files(("art/sheet.png", Png(255, 0, 0)))))
            red = SpriteImages.Load("art/sheet.png");
        using (new InstalledContent(Files(("art/sheet.png", Png(0, 0, 255)))))
            blue = SpriteImages.Load("art/sheet.png");

        Assert.Equal(255, red!.Value.Data[0]);
        Assert.Equal(0, blue!.Value.Data[0]);
        Assert.Equal(255, blue.Value.Data[2]);
        SpriteImages.Forget();
    }

    [Fact]
    public void AnAsepriteFileReadAgainAfterDroppingItsFramesReadsItsOwnSource()
    {
        string root = Repo();
        byte[] loading = File.ReadAllBytes(Path.Combine(root, "Horizon/Assets/global/loading.aseprite"));
        byte[] other = File.ReadAllBytes(Path.Combine(root, "Horizon/Assets/uix/adventure/art.aseprite"));
        AsepriteDocument.Forget();

        var first = Files(("art/thing.aseprite", loading));
        AsepriteDocument document;
        byte[] before;
        using (new InstalledContent(first))
        {
            document = AsepriteDocument.Open("art/thing.aseprite");
            before = document.ReadFrame(0);
        }

        // Another source with another file of the same name, and the frames let go of, so the next frame is read again
        var second = Files(("art/thing.aseprite", other));
        using (new InstalledContent(second))
        {
            document.DropRendered();
            Assert.Equal(before, document.ReadFrame(0));

            AsepriteDocument theOther = AsepriteDocument.Open("art/thing.aseprite");
            Assert.NotSame(document, theOther);
            Assert.NotEqual((document.Width, document.Height, document.FrameCount), (theOther.Width, theOther.Height, theOther.FrameCount));
        }

        AsepriteDocument.Forget();
    }

    [Fact]
    public void AMapFindsItsTileSetsTemplatesAndImagesInOtherFoldersOfThePackage()
    {
        const string tileset = """
            <?xml version="1.0" encoding="UTF-8"?>
            <tileset version="1.10" name="stone" tilewidth="16" tileheight="16" tilecount="6" columns="3">
             <image source="../images/stone.png" width="48" height="32"/>
            </tileset>
            """;
        const string template = """
            <?xml version="1.0" encoding="UTF-8"?>
            <template>
             <object type="light">
              <properties>
               <property name="light_radius" type="float" value="120"/>
              </properties>
              <point/>
             </object>
            </template>
            """;
        const string map = """
            <?xml version="1.0" encoding="UTF-8"?>
            <map version="1.10" orientation="orthogonal" renderorder="right-down" width="2" height="2" tilewidth="16" tileheight="16" infinite="0" nextlayerid="3" nextobjectid="2">
             <properties>
              <property name="next" type="file" value="../other/next.tmx"/>
             </properties>
             <tileset firstgid="1" source="../../shared/sets/stone.tsx"/>
             <layer id="1" name="ground" width="2" height="2">
              <data encoding="csv">
            1,2,
            3,4
            </data>
             </layer>
             <objectgroup id="2" name="things">
              <object id="1" template="../../shared/objects/lamp.tx" x="8" y="8"/>
             </objectgroup>
            </map>
            """;
        using var installed = new InstalledContent(Files(
            ("Assets/maps/town/town.tmx", Text(map)),
            ("Assets/shared/sets/stone.tsx", Text(tileset)),
            ("Assets/shared/objects/lamp.tx", Text(template)),
            ("Assets/shared/images/stone.png", Png(9, 9, 9, 48, 32))));

        TileMap loaded = TileMap.Load("Assets/maps/town/town.tmx");

        Assert.Equal("stone", Assert.Single(loaded.Data.Tilesets).Name);
        var lamp = Assert.Single(loaded.Objects);
        Assert.Equal("light", lamp.Class);
        Assert.Equal("Assets/maps/other/next.tmx", loaded.Properties.GetFile("next").Replace('\\', '/'));
    }

    [Fact]
    public void ABitmapFontIsReadWithThePictureBesideIt()
    {
        string root = Repo();
        string folder = Path.Combine(root, "Horizon/Assets/fonts/vcr_mono");
        var files = Directory.GetFiles(folder).Select(file => ($"Assets/fonts/vcr_mono/{Path.GetFileName(file)}", File.ReadAllBytes(file))).ToArray();
        using var installed = new InstalledContent(Files(files));

        var importer = new BMFontImporter("Assets/fonts/vcr_mono", "vcr_mono.fnt");

        Assert.NotEmpty(importer.Definitions);
        Assert.True(importer.LineHeight > 0);
        Assert.True(ContentFiles.Exists(importer.ImagePath), importer.ImagePath);
        Assert.True(ImageFile.Load(importer.ImagePath).Width > 0);
    }

    [Fact]
    public void ShaderIncludesAreFoundBesideTheFileAndAtTheRoot()
    {
        using var installed = new InstalledContent(Files(
            ("shaders/main.slang", Text("#include \"parts/near.slang\"\n#include <common/far.slang>\nmain body\n")),
            ("shaders/parts/near.slang", Text("#include \"../common/far.slang\"\nnear body\n")),
            ("shaders/common/far.slang", Text("far body\n"))));
        using var preprocessor = new ShaderPreprocessor();

        string processed = preprocessor.ProcessFile("shaders/main.slang");

        Assert.Contains("near body", processed);
        Assert.Contains("main body", processed);
        Assert.Single(processed.Split('\n'), line => line.Trim() == "far body");
    }

    [Fact]
    public void AShaderProgramIsFoundByItsNameInAFolderOfThePackage()
    {
        using var installed = new InstalledContent(Files(
            ("packed-shaders/ui/ui.slang", Text("ui\n")),
            ("packed-shaders/ui/ui.notes", Text("not a stage\n")),
            ("packed-shaders/ui/other.slang", Text("other\n"))));

        ShaderDescription found = ShaderDescription.FromPath("packed-shaders/ui", "ui");

        Assert.Equal("packed-shaders/ui/ui.slang", Assert.Single(found.Definitions).File);
        Assert.Empty(ShaderDescription.FromPath("shaders/nope", "ui").Definitions);
    }

    [Fact]
    public void ScriptsIncludeFilesNextToTheIncludedFileInsideThePackage()
    {
        using var installed = new InstalledContent(Files(
            ("Assets/data/main.hor", Text("let shared = include(\"parts/shared.hor\")\nlet answer = shared.value\n")),
            ("Assets/data/parts/shared.hor", Text("let deeper = include(\"../more/deeper.hor\")\nlet value = deeper.number + 1\n")),
            ("Assets/data/more/deeper.hor", Text("let number = 41\n"))));
        var runtime = new HIDLRuntime { Output = null }.ReadingContent();

        runtime.RunFile("Assets/data/main.hor");

        Assert.Equal(42.0f, Assert.IsType<Horizon.HIDL.Runtime.NumberValue>(runtime.UserScope.Lookup("answer")).Value);
        Assert.Throws<Horizon.HIDL.Runtime.HidlRuntimeException>(() => runtime.Run("include(\"Assets/data/nope.hor\")"));
    }

    [Fact]
    public void LanguagesAreListedAndReadOutOfThePackage()
    {
        using var installed = new InstalledContent(Files(
            ("Assets/lang/en-gb.hor", Text("let strings = { menu: { quit: \"Quit\" } }\n")),
            ("Assets/lang/fr-fr.hor", Text("let strings = { menu: { quit: \"Quitter\" } }\n")),
            ("Assets/lang/README", Text("not a language"))));

        Assert.Equal(["en-gb", "fr-fr"], Localisation.Available("Assets/lang"));
        Assert.True(Localisation.Load("Assets/lang", "en-gb"));
        Assert.Equal("Quit", Localisation.Get("menu.quit"));
        Assert.Equal("Quitter", Localisation.In("fr-fr", "menu.quit"));
    }

    [Fact]
    public void ASoundIsReadOutOfThePackageAndItsCacheKnowsWhichSource()
    {
        Sound.Forget();
        Sound? quiet, loud;
        using (new InstalledContent(Files(("sounds/hit.wav", Wave(100)))))
            quiet = Sound.Load("sounds/hit.wav");
        using (new InstalledContent(Files(("sounds/hit.wav", Wave(20000)))))
            loud = Sound.Load("sounds/hit.wav");

        Assert.Equal(100, quiet!.Samples[0]);
        Assert.Equal(20000, loud!.Samples[0]);
        Sound.Forget();
    }

    // A 16 bit stereo wave of a few frames, every sample the same
    private static byte[] Wave(short level)
    {
        const int frames = 8;
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 4);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(44100);
        writer.Write(44100 * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 4);
        for (int i = 0; i < frames * 2; i++) writer.Write(level);
        writer.Flush();
        return bytes.ToArray();
    }
}
