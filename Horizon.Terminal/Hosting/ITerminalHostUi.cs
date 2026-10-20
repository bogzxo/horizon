namespace Horizon.UI.Components;

/// <summary>
/// What a terminal host asks of the program round it, the few things every program has its own way of doing. Dusk
/// answers with its dialogs and Hex with its own, so the core plugin's commands (the palette, a tab renamed, a shell
/// picked) work the same in both.
/// </summary>
public interface ITerminalHostUi
{
    /// <summary>The program's command palette, every command there is.</summary>
    void Palette();

    /// <summary>Asks for one of a list, narrowed by what is typed, and says which was picked by its place.</summary>
    void Choose(string title, string message, IReadOnlyList<string> items, Action<int> picked);

    /// <summary>Asks for a line of text, starting from one, and says what was given. Nothing when it was cancelled.</summary>
    void AskText(string title, string message, string initial, Action<string> answered);

    /// <summary>Opens a file to edit at a line (1 for the first, 0 for wherever), false when there is nothing to open it in.</summary>
    bool OpenFile(string path, int line);

    /// <summary>
    /// A file or a folder for Dawn, what the shells' dawn command asks for (dawn -open path, dawn -window path). In the
    /// Dawn that's running, or a new window of it, false when there's no Dawn to be had. A program that knows no Dawn
    /// opens a file the way it opens one to edit and nothing else.
    /// </summary>
    bool OpenInDawn(string path, bool newWindow) => !newWindow && File.Exists(path) && OpenFile(path, 0);
}
