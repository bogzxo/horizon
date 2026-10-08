using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// Making, opening, saving and closing layouts. Windows asks where, the editor testing itself (which can't click in a dialog of Windows) uses the list on the left to open and its workspace to save.
internal sealed partial class HexScene
{
    /* Documents */

    private HexDocument NewDocument()
    {
        string name = "layout";
        for (int number = 2; documents.Exists(other => other.Name == name); number++)
            name = $"layout{number}";

        var created = new HexDocument(name, stage.CreateModule()) { FallbackDirectory = browseDirectory };

        // Laid out as if the canvas were the screen it is made for, and kept inside of it
        created.Module.Viewport = DesignScreen;
        created.Module.Clip = DesignScreen;

        // An empty layout that nobody has touched has nothing to save
        created.SavedCode = created.GenerateCode();

        documents.Add(created);
        return created;
    }

    private void Show(HexDocument shown)
    {
        // Whatever was being done to the layout that is put away is a step back in it, not in the next one
        if (recordPending && document is not null)
            document.Record();
        recordPending = false;

        document = shown;

        // The editor's own layouts are made for the skin of the editor, everything else for the one games have
        bool plain = shown.Path is { } path && SameDirectory(path, EDITOR_LAYOUT);
        if (plain != stagePlain)
        {
            stagePlain = plain;

            if (plain)
                stage.SetSkin(TOOL_SKIN_DIRECTORY, TOOL_SKIN);
            else
                stage.SetSkin(UICompositor.DEFAULT_SKIN_DIRECTORY, UICompositor.DEFAULT_SKIN_FILE);
        }

        // Only the layout that is being worked on is on screen
        foreach (var other in documents)
            other.Module.Enabled = other == shown;

        nameBox.Text = shown.Name;
        MarkSelection();
        browsing = false;
        tabsDirty = treeDirty = inspectorDirty = codeDirty = true;
    }

    /// <summary>
    /// Opens a layout file in a tab of its own, and checks that writing it back out and reading that gives the
    /// same layout again: if it doesn't, saving would change it.
    /// </summary>
    /// <returns>Whether it opened and writes back the same.</returns>
    public bool Open(string path)
    {
        if (!File.Exists(path))
        {
            Say($"there is no '{path}'", error: true);
            return false;
        }

        // Somebody who opens the same file twice wants to see it, not have it twice
        string full = Path.GetFullPath(path);
        if (documents.Find(other => other.Path is not null && Path.GetFullPath(other.Path) == full) is { } already)
        {
            Show(already);
            Say($"{already.Name} is open already");
            return true;
        }

        // Reusing the empty layout the editor starts with saves a tab
        HexDocument target = document.Path is null && !document.Module.Root.Children.Any() ? document : NewDocument();
        target.Name = Path.GetFileNameWithoutExtension(path);
        target.Path = path;

        var (success, message) = target.Load(File.ReadAllText(path), path);
        Show(target);

        if (!success)
        {
            target.Path = null;
            Console.WriteLine($"[Hex] '{path}' didn't open: {message}");
            Say(message, error: true);
            return false;
        }

        Remember(path);
        target.SavedCode = target.GenerateCode();

        bool faithful = CheckRoundTrip(target);
        int count = target.Walk().Count();

        Console.WriteLine($"[Hex] Opened '{path}': {count} components, {target.PreviewCount} stand-in items, round trip {(faithful ? "ok" : "DIFFERS")}.");

        if (!faithful)
            Say($"opened {target.Name}, but it doesn't write back the same", error: true);
        else if (message.Length > 0)
            Say(message, error: true);
        else
            Say($"opened {target.Name}: {count} components, writes back the same");

        return faithful;
    }

    /// <summary>
    /// Helper to test that the code written for a layout builds that same layout. It is loaded into a module
    /// nobody sees and written out again, which has to give the same code.
    /// </summary>
    private bool CheckRoundTrip(HexDocument checkedDocument)
    {
        string written = checkedDocument.GenerateCode();

        var scratch = new HexDocument(checkedDocument.Name, stage.CreateModule());
        scratch.Module.Enabled = false;

        try
        {
            return scratch.Load(written).Success && scratch.GenerateCode() == written;
        }
        finally
        {
            stage.RemoveModule(scratch.Module);
        }
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(a)), Path.GetDirectoryName(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /* Opening and saving. Windows asks where, the editor testing itself (which can't click in a dialog of
       Windows) uses the list on the left to open and its workspace to save. */

    private nint WindowHandle => Engine.WindowManager.Window.Native?.Win32?.Hwnd ?? 0;

    private void OpenPressed()
    {
        if (options.SelfTest)
        {
            ToggleBrowsing();
            return;
        }

        Ask(HexDialogs.Open(browseDirectory, WindowHandle), path =>
        {
            // New layouts are saved to wherever was looked at last
            browseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? browseDirectory;
            Open(path);
        });
    }

    private void Save()
    {
        // Back to where it came from, unless it has been given another name since. That is saving a copy
        if (document.Path is { } known && Path.GetFileNameWithoutExtension(known) == document.Name)
            SaveTo(known);
        else if (document.Path is null && !options.SelfTest)
            SaveAs();
        else
            SaveTo(Path.Combine(document.Path is { } copyOf ? Path.GetDirectoryName(Path.GetFullPath(copyOf))! : browseDirectory, document.Name + ".hor"));
    }

    private void SaveAs()
    {
        if (options.SelfTest)
        {
            SaveTo(Path.Combine(browseDirectory, document.Name + ".hor"));
            return;
        }

        HexDocument saved = document;
        string directory = saved.Path is { } known ? Path.GetDirectoryName(Path.GetFullPath(known))! : browseDirectory;

        Ask(HexDialogs.Save(directory, saved.Name + ".hor", WindowHandle), path =>
        {
            // The dialog was open for a while, the layout it was about may not be the one on screen any more
            if (!documents.Contains(saved))
                return;

            Show(saved);

            // A layout goes by the name of its file
            saved.Name = Path.GetFileNameWithoutExtension(path);
            nameBox.Text = saved.Name;
            browseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? browseDirectory;

            SaveTo(path);
        });
    }

    /// <summary>Helper to show a dialog of Windows and do something with the file that is picked in it, one at a time.</summary>
    private void Ask(Task<string?> asked, Action<string> picked)
    {
        if (dialog is not null)
        {
            Say("there is a dialog open already", error: true);
            return;
        }

        dialog = asked;
        dialogPicked = picked;
    }

    private void UpdateDialog()
    {
        if (dialog is not { IsCompleted: true } answered)
            return;

        Action<string>? picked = dialogPicked;
        dialog = null;
        dialogPicked = null;

        if (answered.IsFaulted)
            Say($"the dialog didn't open: {answered.Exception?.InnerException?.Message}", error: true);
        else if (answered.Result is { } path)
            picked?.Invoke(path);
    }

    private void SaveTo(string path)
    {
        // Saving the editor over itself with something broken would be the end of it, that is only ever saved as a copy
        if (SameDirectory(path, EDITOR_LAYOUT))
            path = Path.Combine(Path.GetFullPath(options.Workspace), document.Name + "_edited.hor");

        try
        {
            string written = document.GenerateCode();

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, written);

            document.Path = path;
            document.SavedCode = written;
            document.Modified = false;

            // Its templates are looked for next to it, which may be somewhere else now
            document.RefreshPreviews();
            tabsDirty = treeDirty = true;
            Remember(path);

            Console.WriteLine($"[Hex] Saved '{path}'.");
            Say($"saved {path}");
        }
        catch (Exception e)
        {
            Say($"couldn't save {path}: {e.Message}", error: true);
        }
    }

    private void Close()
    {
        // The code on screen is the newest word on whether there is anything to lose
        if (document.Modified || document.GenerateCode() != document.SavedCode)
        {
            HexDocument closing = document;
            UIDialog.Show(chrome, "Unsaved changes", $"{closing.Name} has changes that aren't in its file.",
                new DialogChoice("Save", () =>
                {
                    Save();

                    // Saved on the spot it can go. Waiting on a dialog of Windows it stays until that is answered
                    if (document == closing && !document.Modified && document.GenerateCode() == document.SavedCode) CloseNow();
                }),
                new DialogChoice("Don't save", () => { if (document == closing) CloseNow(); }),
                new DialogChoice("Cancel"));
            return;
        }

        CloseNow();
    }

    private void CloseNow()
    {
        stage.Highlighted = null;
        stage.Highlights = [];

        stage.RemoveModule(document.Module);
        documents.Remove(document);
        recordPending = false;

        Show(documents.Count > 0 ? documents[^1] : NewDocument());
    }
}
