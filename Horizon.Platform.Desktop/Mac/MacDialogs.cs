using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horizon.Platform.Desktop;

/// <summary>
/// Native macOS Finder open, save and folder panels (<c>NSOpenPanel</c> / <c>NSSavePanel</c>), run on the AppKit main
/// thread through <see cref="MacMain"/> and <see cref="MacObjC"/> with zero reflection for NativeAOT compatibility.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacDialogs : IPlatformDialogs
{
    private const nint NSModalResponseOK = 1;
    private const string UniformTypeIdentifiers = "/System/Library/Frameworks/UniformTypeIdentifiers.framework/UniformTypeIdentifiers";

    private static int utiLoaded;

    public Task<string?> OpenFile(string title, string directory, IReadOnlyList<(string Name, string Pattern)> filter, string picked = "") =>
        RunOnMain(() =>
        {
            nint panel = MacObjC.Send(MacObjC.Class("NSOpenPanel"), MacObjC.Sel("openPanel"));
            if (panel == 0)
                return null;

            ConfigureCommon(panel, title, directory, picked, filter);
            MacObjC.SendVoid(panel, MacObjC.Sel("setCanChooseFiles:"), true);
            MacObjC.SendVoid(panel, MacObjC.Sel("setCanChooseDirectories:"), false);
            MacObjC.SendVoid(panel, MacObjC.Sel("setAllowsMultipleSelection:"), false);
            return RunModal(panel);
        });

    public Task<string?> OpenFolder(string title, string directory) =>
        RunOnMain(() =>
        {
            nint panel = MacObjC.Send(MacObjC.Class("NSOpenPanel"), MacObjC.Sel("openPanel"));
            if (panel == 0)
                return null;

            ConfigureCommon(panel, title, directory, string.Empty, null);
            MacObjC.SendVoid(panel, MacObjC.Sel("setCanChooseFiles:"), false);
            MacObjC.SendVoid(panel, MacObjC.Sel("setCanChooseDirectories:"), true);
            MacObjC.SendVoid(panel, MacObjC.Sel("setAllowsMultipleSelection:"), false);
            return RunModal(panel);
        });

    public Task<string?> SaveFile(string title, string directory, string name, IReadOnlyList<(string Name, string Pattern)> filter) =>
        RunOnMain(() =>
        {
            nint panel = MacObjC.Send(MacObjC.Class("NSSavePanel"), MacObjC.Sel("savePanel"));
            if (panel == 0)
                return null;

            ConfigureCommon(panel, title, directory, name, filter);
            return RunModal(panel);
        });

    private static Task<string?> RunOnMain(Func<string?> show)
    {
        if (!OperatingSystem.IsMacOS())
            return Task.FromResult<string?>(null);

        if (!MacMain.IsMainThread && MacMain.Poster is null)
            return Task.FromResult<string?>(null);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        MacMain.Run(() =>
        {
            try
            {
                tcs.TrySetResult(show());
            }
            catch (Exception)
            {
                tcs.TrySetResult(null);
            }
        });
        return tcs.Task;
    }

    private static void ConfigureCommon(nint panel, string title, string directory, string nameField, IReadOnlyList<(string Name, string Pattern)>? filter)
    {
        if (!string.IsNullOrEmpty(title))
            MacObjC.SendVoid(panel, MacObjC.Sel("setTitle:"), MacObjC.NSString(title));

        MacObjC.SendVoid(panel, MacObjC.Sel("setCanCreateDirectories:"), true);

        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            nint url = MacObjC.Send(MacObjC.Class("NSURL"), MacObjC.Sel("fileURLWithPath:"), MacObjC.NSString(directory));
            if (url != 0)
                MacObjC.SendVoid(panel, MacObjC.Sel("setDirectoryURL:"), url);
        }

        if (!string.IsNullOrEmpty(nameField))
            MacObjC.SendVoid(panel, MacObjC.Sel("setNameFieldStringValue:"), MacObjC.NSString(nameField));

        ApplyFilters(panel, filter);
    }

    private static void ApplyFilters(nint panel, IReadOnlyList<(string Name, string Pattern)>? filter)
    {
        string[] extensions = PlatformDialogFilters.ExtensionsOf(filter);
        if (extensions.Length == 0)
            return;

        nint arrayClass = MacObjC.Class("NSMutableArray");
        if (arrayClass == 0)
            return;

        nint utTypeClass = FindUTTypeClass();
        nint setAllowedContentTypes = MacObjC.Sel("setAllowedContentTypes:");
        if (utTypeClass != 0 && MacObjC.RespondsTo(panel, setAllowedContentTypes))
        {
            nint types = MacObjC.Send(arrayClass, MacObjC.Sel("array"));
            nint typeWithExtension = MacObjC.Sel("typeWithFilenameExtension:");
            nint addObject = MacObjC.Sel("addObject:");
            bool allResolved = true;

            for (int i = 0; i < extensions.Length; i++)
            {
                nint ut = MacObjC.Send(utTypeClass, typeWithExtension, MacObjC.NSString(extensions[i]));
                if (ut != 0)
                    MacObjC.SendVoid(types, addObject, ut);
                else
                    allResolved = false;
            }

            if (allResolved && MacObjC.SendUInt(types, MacObjC.Sel("count")) > 0)
            {
                MacObjC.SendVoid(panel, setAllowedContentTypes, types);
                return;
            }
        }

        nint setAllowedFileTypes = MacObjC.Sel("setAllowedFileTypes:");
        if (MacObjC.RespondsTo(panel, setAllowedFileTypes))
        {
            nint names = MacObjC.Send(arrayClass, MacObjC.Sel("array"));
            nint addObject = MacObjC.Sel("addObject:");
            for (int i = 0; i < extensions.Length; i++)
                MacObjC.SendVoid(names, addObject, MacObjC.NSString(extensions[i]));

            MacObjC.SendVoid(panel, setAllowedFileTypes, names);
        }
    }

    private static nint FindUTTypeClass()
    {
        nint utType = MacObjC.Class("UTType");
        if (utType == 0 && Interlocked.Exchange(ref utiLoaded, 1) == 0)
        {
            _ = NativeLibrary.TryLoad(UniformTypeIdentifiers, out _);
            utType = MacObjC.Class("UTType");
        }
        return utType;
    }

    private static string? RunModal(nint panel)
    {
        if (MacObjC.SendInt(panel, MacObjC.Sel("runModal")) != NSModalResponseOK)
            return null;

        nint url = MacObjC.Send(panel, MacObjC.Sel("URL"));
        if (url == 0)
            return null;

        nint path = MacObjC.Send(url, MacObjC.Sel("path"));
        return MacObjC.ToManaged(path);
    }
}
