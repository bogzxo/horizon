namespace Horizon.UI.Components;

/// <summary>
/// A program that runs inside a <see cref="TerminalView"/> without a process behind it, the HIDL REPL, a TUI written in
/// HIDL. It is to the view what a shell behind a pty is, it gets the bytes a key sends (<see cref="Input"/>, the arrows
/// as ESC [ A and Return as a carriage return, the same as a shell would) and writes into the screen with
/// <see cref="TerminalView.Feed"/>, colours, cursor moves and all, so everything the view does with a shell's output
/// (selection, scrollback, the history, a theme's colours) it does with this. <see cref="TerminalView.Start(ITerminalProgram)"/>
/// runs one, a profile of the kind Embedded is how a panel gets one started.
/// </summary>
public interface ITerminalProgram : IDisposable
{
    /// <summary>The program is in a view now, at its size. Write the first of it here.</summary>
    void Attach(TerminalView view);

    /// <summary>What a key or a paste sent, on the UI's thread.</summary>
    void Input(string bytes);

    /// <summary>The view has so many cells now.</summary>
    void Resize(int columns, int rows);

    /// <summary>Every tick, for a program with something to do over time (a clock, a spinner).</summary>
    void Update(float dt);

    /// <summary>Whether it is still going. A program that ends calls <see cref="TerminalView.End"/> with its code.</summary>
    bool IsRunning { get; }

    /// <summary>What a tab running it is called.</summary>
    string Name { get; }
}
