using Horizon.Content;

namespace Horizon.Tests.Content;

/// <summary>
/// The content sources on their own, paths in a package, the overlay of loose files over packed ones, listings, and
/// the keys caches tell sources apart by.
/// </summary>
[Collection(ContentSourceCollection.NAME)]
public class ContentSourceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "content-tests-" + Guid.NewGuid().ToString("N"));

    public ContentSourceTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch { }
    }

    private static MemoryContent Package(string? diskRoot = null) => new(new Dictionary<string, byte[]>
    {
        ["Assets/data/moves.hor"] = "packed moves"u8.ToArray(),
        ["Assets/data/rules.hor"] = "packed rules"u8.ToArray(),
        ["Assets/data/deep/inner.hor"] = "inner"u8.ToArray(),
        ["Assets/readme"] = "no extension"u8.ToArray(),
        ["shaders/a.slang"] = "a"u8.ToArray(),
    }, diskRoot);

    [Fact]
    public void PackagePathsAreNormalisedWhicheverSlashAndDotsTheyUse()
    {
        var package = Package();

        Assert.Equal("Assets/data/moves.hor", package.ResolvePath("Assets\\data\\moves.hor"));
        Assert.Equal("Assets/data/moves.hor", package.ResolvePath("./Assets//data/./deep/../moves.hor"));
        Assert.True(package.Exists("Assets/maps/../data/moves.hor"));
        Assert.Equal("packed moves", ContentFiles.ReadAllText(package, "Assets\\data\\moves.hor"));
        Assert.Equal(string.Empty, package.ResolvePath(""));
    }

    [Fact]
    public void APackagePathCantLeaveThePackage()
    {
        var package = Package();

        Assert.Throws<ArgumentException>(() => package.ResolvePath("../outside.hor"));
        Assert.Throws<ArgumentException>(() => package.ResolvePath("Assets/../../outside.hor"));
        Assert.Throws<ArgumentException>(() => package.ResolvePath(Path.Combine(Path.GetTempPath(), "x.hor")));
        Assert.False(package.Exists("../Assets/data/moves.hor"));
        Assert.False(package.DirectoryExists(".."));
        Assert.Empty(package.EnumerateFiles(".."));
        Assert.Throws<ArgumentException>(() => package.OpenRead("../outside.hor"));
    }

    [Fact]
    public void PackagePathsAreCaseSensitiveEverywhere()
    {
        var package = Package();
        Assert.False(package.Exists("assets/data/moves.hor"));
        Assert.Throws<FileNotFoundException>(() => package.OpenRead("assets/data/moves.hor"));
    }

    [Fact]
    public void MissingFilesAndFoldersAreMissing()
    {
        var package = Package();

        Assert.False(package.Exists("Assets/data/nope.hor"));
        Assert.False(package.Exists("Assets/data"));
        Assert.False(package.DirectoryExists("Assets/data/moves.hor"));
        Assert.False(package.DirectoryExists("Assets/nope"));
        Assert.Empty(package.EnumerateFiles("Assets/nope"));
        Assert.Empty(package.EnumerateDirectories("Assets/nope"));
        Assert.Throws<FileNotFoundException>(() => package.OpenRead("Assets/data/nope.hor"));
        Assert.Equal(DateTime.MinValue, ((IContentFiles)package).GetLastWriteTimeUtc("Assets/data/moves.hor"));
    }

    [Fact]
    public void ListingsGoByFolderAndPatternLikeTheDisksDo()
    {
        var package = Package();

        Assert.Equal(["Assets/data/moves.hor", "Assets/data/rules.hor"], package.EnumerateFiles("Assets/data", "*.hor"));
        Assert.Equal(["Assets/data/deep/inner.hor", "Assets/data/moves.hor", "Assets/data/rules.hor"],
            package.EnumerateFiles("Assets/data", "*.hor", SearchOption.AllDirectories));
        Assert.Equal(["Assets/data/moves.hor"], package.EnumerateFiles("Assets\\data", "moves.*"));
        Assert.Equal(["Assets/readme"], package.EnumerateFiles("Assets", "*.*"));
        Assert.Equal(["Assets", "shaders"], package.EnumerateDirectories(""));
        Assert.Equal(["Assets/data"], package.EnumerateDirectories("Assets"));
        Assert.Equal(["Assets/data/deep"], package.EnumerateDirectories("Assets/data/"));
        Assert.True(package.DirectoryExists(""));
        Assert.True(package.DirectoryExists("Assets/data/deep"));
        Assert.Equal(5, package.EnumerateFiles("", "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public void ADiskRootLetsThePackageAnswerForPathsOnTheDisk()
    {
        var package = Package(folder);
        string moves = Path.Combine(folder, "Assets", "data", "moves.hor");

        Assert.Equal("Assets/data/moves.hor", package.ResolvePath(moves));
        Assert.True(package.Exists(moves));
        Assert.True(package.DirectoryExists(Path.Combine(folder, "Assets")));
        Assert.False(package.Exists(Path.Combine(Path.GetDirectoryName(folder)!, "Assets", "data", "moves.hor")));
        Assert.Equal([Path.Combine(folder, "Assets", "data", "moves.hor"), Path.Combine(folder, "Assets", "data", "rules.hor")],
            package.EnumerateFiles(Path.Combine(folder, "Assets", "data")));
    }

    [Fact]
    public void LooseFilesWinOverPackedOnesAndTheRestComeFromThePackage()
    {
        Directory.CreateDirectory(Path.Combine(folder, "Assets", "data"));
        File.WriteAllText(Path.Combine(folder, "Assets", "data", "moves.hor"), "loose moves");
        File.WriteAllText(Path.Combine(folder, "Assets", "data", "extra.hor"), "loose extra");
        var overlay = new OverlayContent(new DiskContent(folder), Package(folder));

        Assert.Equal("loose moves", ContentFiles.ReadAllText(overlay, "Assets/data/moves.hor"));
        Assert.Equal("packed rules", ContentFiles.ReadAllText(overlay, "Assets/data/rules.hor"));
        Assert.Equal("loose extra", ContentFiles.ReadAllText(overlay, "Assets/data/extra.hor"));
        Assert.True(overlay.Exists("shaders/a.slang"));
        Assert.False(overlay.Exists("Assets/data/nope.hor"));
        Assert.Throws<FileNotFoundException>(() => overlay.OpenRead("Assets/data/nope.hor"));

        // The loose one resolves to the disk, the packed one to the package, so they never share a cache key
        Assert.Equal(Path.Combine(folder, "Assets", "data", "moves.hor"), overlay.ResolvePath("Assets/data/moves.hor"));
        Assert.Equal("Assets/data/rules.hor", overlay.ResolvePath("Assets/data/rules.hor"));
        Assert.Equal(Path.Combine(folder, "shaders", "a.slang"), overlay.ResolvePath(Path.Combine(folder, "shaders", "a.slang")));
    }

    [Fact]
    public void AnOverlayListsAFileThatIsLooseAndPackedOnce()
    {
        Directory.CreateDirectory(Path.Combine(folder, "Assets", "data"));
        File.WriteAllText(Path.Combine(folder, "Assets", "data", "moves.hor"), "loose moves");
        File.WriteAllText(Path.Combine(folder, "Assets", "data", "extra.hor"), "loose extra");
        var overlay = new OverlayContent(new DiskContent(folder), Package(folder));

        static string[] Names(IEnumerable<string> paths) => [.. paths.Select(path => path.Replace('\\', '/')).Order(StringComparer.Ordinal)];

        Assert.Equal(["Assets/data/extra.hor", "Assets/data/moves.hor", "Assets/data/rules.hor"], Names(overlay.EnumerateFiles("Assets/data", "*.hor")));

        // Asked by where it is on the disk, both sources answer with paths on the disk and still only once each
        string data = Path.Combine(folder, "Assets", "data");
        string[] rooted = [.. overlay.EnumerateFiles(data, "*.hor")];
        Assert.Equal(3, rooted.Length);
        Assert.All(rooted, path => Assert.True(Path.IsPathRooted(path)));
        Assert.Equal(["Assets/data"], Names(overlay.EnumerateDirectories("Assets")));
        Assert.Equal(["Assets/data/deep"], Names(overlay.EnumerateDirectories("Assets/data")));
    }

    [Fact]
    public void DiskContentKeepsRelativePathsRelativeAndAbsoluteOnesAbsolute()
    {
        Directory.CreateDirectory(Path.Combine(folder, "Assets"));
        File.WriteAllText(Path.Combine(folder, "Assets", "a.hor"), "a");
        var disk = new DiskContent(folder);

        Assert.Equal([Path.Combine("Assets", "a.hor")], disk.EnumerateFiles("Assets"));
        Assert.Equal([Path.Combine(folder, "Assets", "a.hor")], disk.EnumerateFiles(Path.Combine(folder, "Assets")));
        Assert.Equal(Path.Combine(folder, "Assets", "a.hor"), disk.ResolvePath("Assets/a.hor"));
        Assert.Equal(folder, disk.ResolvePath(""));
        Assert.True(disk.GetLastWriteTimeUtc("Assets/a.hor") > DateTime.MinValue);
    }

    [Fact]
    public void ACacheKeyChangesWithTheSourceButThePathDoesnt()
    {
        var first = Package();
        var second = Package();
        string firstKey, secondKey;
        FoundContent found;

        using (new InstalledContent(first))
        {
            firstKey = ContentFiles.CacheKey("Assets/data/moves.hor");
            found = ContentFiles.Find("Assets\\data\\moves.hor");
            Assert.Equal(firstKey, found.Key);
        }

        using (new InstalledContent(second))
            secondKey = ContentFiles.CacheKey("Assets/data/moves.hor");

        Assert.NotEqual(firstKey, secondKey);
        Assert.Same(first, found.Source);
        Assert.Equal("Assets/data/moves.hor", found.Path);
        Assert.Equal("packed moves"u8.ToArray(), found.ReadAllBytes());
    }

    [Fact]
    public void ReadingHelpersCopeWithStreamsThatTrickle()
    {
        var package = MemoryContent.Of(("a.txt", "﻿first\r\nsecond\nthird"));
        using var installed = new InstalledContent(package);

        Assert.Equal(["first", "second", "third"], ContentFiles.ReadAllLines("a.txt"));
        using Stream seekable = ContentFiles.OpenSeekable("a.txt");
        Assert.True(seekable.CanSeek);
        Assert.Equal(seekable.Length, ContentFiles.ReadAllBytes("a.txt").Length);
    }
}
