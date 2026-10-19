using System.Diagnostics;
using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>What a program with a screen of its own may ask of whoever runs it, each null for nothing.</summary>
public sealed class TerminalTuiServices
{
    /// <summary>A file opened to edit at a line, in Hex or whatever the program is.</summary>
    public Action<string, int>? Open { get; init; }

    /// <summary>Text typed into the shell the program was started from (a path picked, put on its prompt).</summary>
    public Action<string>? Type { get; init; }

    /// <summary>A line said in the status bar.</summary>
    public Action<string>? Say { get; init; }

    /// <summary>The settings as a form describes them and what they are, and one of them set.</summary>
    public Func<IRuntimeValue>? Settings { get; init; }
    public Func<string, IRuntimeValue, bool>? SetSetting { get; init; }

    /// <summary>Somewhere HIDL is run for a REPL, what a line comes to (or what went wrong), and the names that start with a word.</summary>
    public Func<string, (bool Success, string Result)>? Evaluate { get; init; }
    public Func<string, IEnumerable<string>>? Complete { get; init; }

    /// <summary>The folder it starts in.</summary>
    public string? Folder { get; init; }
}

/// <summary>
/// A program with a screen of its own written in HIDL, run inside a terminal the way kitty's kittens run in kitty. The
/// file is run with a <c>tui</c> object, the screen to write into (a cell at a time or a text, in the palette's colours,
/// boxes, the cursor), its size, a timer, and what the program may want of the machine (the files of a folder, a program
/// run and what it said, the processes) and of the terminal (a file opened, a line typed into the shell it came from).
/// Its functions are called as things happen, <c>on_start(tui)</c>, <c>on_key(tui, key)</c> with a key as
/// <c>{ name, text, ctrl, alt, shift }</c>, <c>on_resize(tui)</c>, <c>on_tick(tui)</c> as often as <c>tui.timer</c> says,
/// and <c>draw(tui)</c> after every one of them. The screen is the alternate one, drawn whole into cells and handed to
/// the view as one synchronised frame, so a TUI over SSH is no different. Assets/terminal/tui has the widgets
/// (widgets.hor, a list, a table, a text field, a panel, tabs, a menu, a progress bar, rows and columns) and the programs
/// that come with it.
/// </summary>
public sealed class TerminalTui : ITerminalProgram
{
    private readonly string file;
    private readonly TerminalTuiServices services;
    private TerminalView? view;
    private HIDLRuntime? runtime;
    private bool running;
    private float tickEvery, tickIn;
    private int columns = 80, rows = 24;
    private Cell[] cells = [];
    private bool showCursor;
    private int cursorX, cursorY;
    private string? failed;

    private struct Cell
    {
        public char Char;
        public byte Fg, Bg, Flags;
    }

    private const byte BOLD = 1, DIM = 2, UNDERLINE = 4, INVERSE = 8, ITALIC = 16;
    private const byte DEFAULT = 255;

    public TerminalTui(string file, TerminalTuiServices? services = null)
    {
        this.file = Path.GetFullPath(file);
        this.services = services ?? new TerminalTuiServices();
        Name = Path.GetFileNameWithoutExtension(file);
    }

    public string Name { get; private set; }

    public bool IsRunning => running;

    /// <summary>What went wrong, null while nothing has.</summary>
    public string? Failed => failed;

    /// <summary>The runtime it runs in, for a test.</summary>
    public HIDLRuntime? Runtime => runtime;

    /// <summary>The screen as text a row a line, for a test.</summary>
    public string ScreenText()
    {
        var text = new StringBuilder();
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
                text.Append(cells[y * columns + x].Char);
            text.Append('\n');
        }

        return text.ToString();
    }

    public void Attach(TerminalView attached)
    {
        view = attached;
        columns = Math.Max(1, attached.Columns);
        rows = Math.Max(1, attached.Rows);
        cells = new Cell[columns * rows];
        Clear();
        running = true;

        // the alternate screen, the cursor hidden, the program's own until it ends
        attached.Feed("\u001b[?1049h\u001b[?25l");

        runtime = new HIDLRuntime { BaseDirectory = Path.GetDirectoryName(file), Output = text => services.Say?.Invoke(text) };
        tuiObject = Api();
        runtime.UserScope.DeclareSystem("tui", tuiObject);
        if (!Call(() => runtime.RunFile(file)))
            return;

        if (runtime.UserScope.Variables.TryGetValue("program", out IRuntimeValue? about) && about is ObjectValue said && said.Properties.TryGetValue("name", out var name) && name is StringValue text)
            Name = text.Value;

        Raise("on_start");
    }

    public void Input(string bytes)
    {
        if (!running)
            return;

        // gone wrong, any key leaves
        if (failed is not null)
        {
            Exit(1);
            return;
        }

        foreach (ObjectValue key in TerminalTuiKeys.Read(bytes))
        {
            Raise("on_key", key);
            if (!running)
                return;
        }
    }

    public void Resize(int newColumns, int newRows)
    {
        if (newColumns == columns && newRows == rows)
            return;

        columns = Math.Max(1, newColumns);
        rows = Math.Max(1, newRows);
        cells = new Cell[columns * rows];
        Clear();
        if (running)
            Raise("on_resize");
    }

    public void Update(float dt)
    {
        if (!running || tickEvery <= 0.0f || failed is not null)
            return;

        tickIn -= dt;
        if (tickIn > 0.0f)
            return;
        tickIn = tickEvery;
        Raise("on_tick");
    }

    public void Dispose()
    {
        running = false;
        runtime = null;
    }

    /// <summary>One of the program's functions called if it has it, and then its draw, and the frame shown.</summary>
    private void Raise(string name, params IRuntimeValue[] arguments)
    {
        if (runtime is null || failed is not null)
            return;

        if (runtime.UserScope.Variables.TryGetValue(name, out IRuntimeValue? function) && Values.IsFunction(function))
        {
            IRuntimeValue[] given = [Tui, .. arguments];
            if (!Call(() => runtime.Invoke(function, given)) || !running)
                return;
        }

        if (runtime.UserScope.Variables.TryGetValue("draw", out IRuntimeValue? draw) && Values.IsFunction(draw))
        {
            if (!Call(() => runtime.Invoke(draw, [Tui])))
                return;
        }

        Present();
    }

    private ObjectValue tuiObject;
    private ObjectValue Tui => tuiObject;

    private bool Call(Action run)
    {
        using var cancel = new CancellationTokenSource(2000);
        runtime!.Interpreter.Cancellation = cancel.Token;
        try
        {
            run();
            return true;
        }
        catch (Exception e)
        {
            failed = cancel.IsCancellationRequested ? "it went on for more than two seconds and was stopped" : e.Message;
            ShowFailure();
            return false;
        }
        finally
        {
            if (runtime is not null)
                runtime.Interpreter.Cancellation = CancellationToken.None;
        }
    }

    private void ShowFailure()
    {
        Clear();
        Write(0, 0, $"{Name} went wrong", Parse("red bold"));
        int y = 2;
        foreach (string line in (failed ?? string.Empty).Split('\n'))
        {
            for (int at = 0; at < line.Length && y < rows - 2; at += columns, y++)
                Write(0, y, line.Substring(at, Math.Min(columns, line.Length - at)), Parse("default"));
        }

        Write(0, Math.Min(rows - 1, y + 1), "any key to leave", Parse("dim"));
        Present();
    }

    /// <summary>Leaves, the screen the shell had put back, the code said to the view.</summary>
    private void Exit(int code)
    {
        if (!running)
            return;
        running = false;
        view?.Feed("\u001b[?2026l\u001b[0m\u001b[?1049l\u001b[?25h");
        view?.End(code);
    }

    /* The screen */

    private void Clear()
    {
        for (int i = 0; i < cells.Length; i++)
            cells[i] = new Cell { Char = ' ', Fg = DEFAULT, Bg = DEFAULT };
    }

    private int Write(int x, int y, string text, Cell style)
    {
        if (y < 0 || y >= rows)
            return x + text.Length;
        foreach (char c in text)
        {
            if (x >= 0 && x < columns)
            {
                style.Char = c < ' ' ? ' ' : c;
                cells[y * columns + x] = style;
            }

            x++;
        }

        return x;
    }

    private void Fill(int x, int y, int width, int height, char c, Cell style)
    {
        style.Char = c;
        for (int row = Math.Max(0, y); row < Math.Min(rows, y + height); row++)
        {
            for (int column = Math.Max(0, x); column < Math.Min(columns, x + width); column++)
                cells[row * columns + column] = style;
        }
    }

    private void Box(int x, int y, int width, int height, string title, Cell style, bool heavy)
    {
        if (width < 2 || height < 2)
            return;
        string corners = heavy ? "┏┓┗┛━┃" : "╭╮╰╯─│";
        Write(x, y, corners[0] + new string(corners[4], width - 2) + corners[1], style);
        Write(x, y + height - 1, corners[2] + new string(corners[4], width - 2) + corners[3], style);
        for (int row = y + 1; row < y + height - 1; row++)
        {
            Write(x, row, corners[5].ToString(), style);
            Write(x + width - 1, row, corners[5].ToString(), style);
        }

        if (title.Length > 0 && width > 4)
        {
            string shown = title.Length > width - 4 ? title[..(width - 4)] : title;
            Cell bold = style;
            bold.Flags |= BOLD;
            Write(x + 1, y, " " + shown + " ", bold);
        }
    }

    /// <summary>The cells as one frame of text with its colours, synchronised so the view never shows half of one.</summary>
    private void Present()
    {
        if (view is null || !running)
            return;

        var frame = new StringBuilder(columns * rows + 256);
        frame.Append("\u001b[?2026h");
        for (int y = 0; y < rows; y++)
        {
            frame.Append("\u001b[").Append(y + 1).Append(";1H");
            Cell last = new() { Fg = 254, Bg = 254, Flags = 255 };
            // the last cell of the last row would scroll the screen, it is left out
            int end = y == rows - 1 ? columns - 1 : columns;
            for (int x = 0; x < end; x++)
            {
                Cell cell = cells[y * columns + x];
                if (cell.Fg != last.Fg || cell.Bg != last.Bg || cell.Flags != last.Flags)
                {
                    AppendStyle(frame, cell);
                    last = cell;
                }

                frame.Append(cell.Char);
            }
        }

        frame.Append("\u001b[0m");
        if (showCursor)
            frame.Append("\u001b[").Append(cursorY + 1).Append(';').Append(cursorX + 1).Append("H\u001b[?25h");
        else
            frame.Append("\u001b[?25l");
        frame.Append("\u001b[?2026l");
        view.Feed(frame.ToString());
    }

    private static void AppendStyle(StringBuilder frame, Cell cell)
    {
        frame.Append("\u001b[0");
        if ((cell.Flags & BOLD) != 0) frame.Append(";1");
        if ((cell.Flags & DIM) != 0) frame.Append(";2");
        if ((cell.Flags & ITALIC) != 0) frame.Append(";3");
        if ((cell.Flags & UNDERLINE) != 0) frame.Append(";4");
        if ((cell.Flags & INVERSE) != 0) frame.Append(";7");
        if (cell.Fg != DEFAULT) frame.Append(cell.Fg < 8 ? ";3" : ";9").Append(cell.Fg % 8);
        if (cell.Bg != DEFAULT) frame.Append(cell.Bg < 8 ? ";4" : ";10").Append(cell.Bg % 8);
        frame.Append('m');
    }

    /* Styles, "bold red on blue", or an object { fg, bg, bold, dim, italic, underline, inverse } */

    private static readonly string[] ColourNames = ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white"];

    private static Cell Parse(IRuntimeValue? style) => style switch
    {
        StringValue text => Parse(text.Value),
        ObjectValue said => FromObject(said),
        _ => new Cell { Fg = DEFAULT, Bg = DEFAULT }
    };

    private static Cell Parse(string style)
    {
        var cell = new Cell { Fg = DEFAULT, Bg = DEFAULT };
        bool background = false;
        foreach (string word in style.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (word.ToLowerInvariant())
            {
                case "on": background = true; continue;
                case "bold": cell.Flags |= BOLD; continue;
                case "dim": cell.Flags |= DIM; continue;
                case "italic": cell.Flags |= ITALIC; continue;
                case "underline": cell.Flags |= UNDERLINE; continue;
                case "inverse" or "selected": cell.Flags |= INVERSE; continue;
                case "default" or "plain": continue;
            }

            byte colour = ColourOf(word);
            if (colour == DEFAULT)
                continue;
            if (background) cell.Bg = colour;
            else cell.Fg = colour;
        }

        return cell;
    }

    private static Cell FromObject(ObjectValue said)
    {
        var cell = new Cell { Fg = DEFAULT, Bg = DEFAULT };
        var p = said.Properties;
        if (p.TryGetValue("fg", out var fg) && fg is StringValue f) cell.Fg = ColourOf(f.Value);
        if (p.TryGetValue("bg", out var bg) && bg is StringValue b) cell.Bg = ColourOf(b.Value);
        if (p.TryGetValue("bold", out var bold) && bold is BooleanValue { Value: true }) cell.Flags |= BOLD;
        if (p.TryGetValue("dim", out var dim) && dim is BooleanValue { Value: true }) cell.Flags |= DIM;
        if (p.TryGetValue("italic", out var italic) && italic is BooleanValue { Value: true }) cell.Flags |= ITALIC;
        if (p.TryGetValue("underline", out var underline) && underline is BooleanValue { Value: true }) cell.Flags |= UNDERLINE;
        if (p.TryGetValue("inverse", out var inverse) && inverse is BooleanValue { Value: true }) cell.Flags |= INVERSE;
        return cell;
    }

    // a colour of the sixteen by its name, bright_red for the bright ones, accent is cyan and error red
    private static byte ColourOf(string name)
    {
        string lower = name.ToLowerInvariant();
        bool bright = lower.StartsWith("bright_", StringComparison.Ordinal);
        if (bright) lower = lower[7..];
        lower = lower switch { "accent" => "cyan", "error" => "red", "warning" => "yellow", "success" => "green", "link" => "blue", "grey" or "gray" => "black", _ => lower };
        int index = Array.IndexOf(ColourNames, lower);
        if (index < 0)
            return DEFAULT;
        return (byte)(index + (bright || name is "grey" or "gray" ? 8 : 0));
    }

    /* The tui object */

    private ObjectValue Api() => Natives.Object(
        ("columns", Natives.Function("columns", _ => new NumberValue(columns))),
        ("rows", Natives.Function("rows", _ => new NumberValue(rows))),
        ("clear", Natives.Action("clear", _ => Clear())),
        ("write", Natives.Function("write", args => new NumberValue(Write(args.Integer(0), args.Integer(1), Values.Text(args[2]), Parse(args.Has(3) ? args[3] : null))))),
        ("fill", Natives.Action("fill", args => Fill(args.Integer(0), args.Integer(1), args.Integer(2), args.Integer(3),
            args.Has(4) && args.Text(4) is { Length: > 0 } c ? c[0] : ' ', Parse(args.Has(5) ? args[5] : null)))),
        ("box", Natives.Action("box", args => Box(args.Integer(0), args.Integer(1), args.Integer(2), args.Integer(3),
            args.Has(4) ? args.Text(4) : string.Empty, Parse(args.Has(5) ? args[5] : null), args.Bool(6, false)))),
        ("cursor", Natives.Action("cursor", args =>
        {
            showCursor = args.Has(0);
            if (showCursor)
            {
                cursorX = Math.Clamp(args.Integer(0), 0, columns - 1);
                cursorY = Math.Clamp(args.Integer(1), 0, rows - 1);
            }
        })),
        ("timer", Natives.Action("timer", args =>
        {
            tickEvery = Math.Max(0.0f, args.Number(0));
            tickIn = tickEvery;
        })),
        ("exit", Natives.Action("exit", args => Exit(args.Integer(0, 0)))),
        ("say", Natives.Action("say", args => services.Say?.Invoke(Values.Text(args[0])))),
        ("folder", Natives.Function("folder", _ => new StringValue(services.Folder ?? view?.WorkingDirectory ?? System.Environment.CurrentDirectory))),
        ("files", Natives.Function("files", args => Files(args.Has(0) ? args.Text(0) : services.Folder ?? System.Environment.CurrentDirectory))),
        // a text in its lines as they are, unlike a text's split, which trims them and drops the empty ones
        ("lines", Natives.Function("lines", args => Natives.List(args.Text(0).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n')))),
        ("parent", Natives.Function("parent", args => Path.GetDirectoryName(args.Text(0).TrimEnd('\\', '/')) is { } up ? new StringValue(up) : Values.Null)),
        ("join", Natives.Function("join", args => new StringValue(Path.Combine(args.Text(0), args.Text(1))))),
        ("run", Natives.Function("run", args => Run(args.Text(0), args.Has(1) ? args.List(1).Items.Select(value => Values.Text(value)).ToArray() : [], args.Has(2) ? args.Text(2) : null))),
        ("processes", Natives.Function("processes", _ => Processes())),
        ("kill", Natives.Function("kill", args => Values.Bool(Kill(args.Integer(0))))),
        ("open", Natives.Action("open", args => services.Open?.Invoke(args.Text(0), args.Integer(1, 0)))),
        ("type", Natives.Action("type", args => services.Type?.Invoke(args.Text(0)))),
        ("settings", Natives.Function("settings", _ => services.Settings?.Invoke() ?? Values.Null)),
        ("set_setting", Natives.Function("set_setting", args => Values.Bool(services.SetSetting?.Invoke(args.Text(0), args[1]) ?? false))),
        ("evaluate", Natives.Function("evaluate", args =>
        {
            var (success, result) = services.Evaluate?.Invoke(args.Text(0)) ?? (false, "there is nowhere to run HIDL here");
            return Natives.Object(("ok", Values.Bool(success)), ("text", new StringValue(result)));
        })),
        ("complete", Natives.Function("complete", args => Natives.List(services.Complete?.Invoke(args.Text(0)).Take(50) ?? [])))
    );

    private static IRuntimeValue Files(string folder)
    {
        var listed = new List<IRuntimeValue>();
        try
        {
            var info = new DirectoryInfo(folder);
            foreach (FileSystemInfo entry in info.EnumerateFileSystemInfos().OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(5000))
            {
                bool isFolder = entry is DirectoryInfo;
                listed.Add(Natives.Object(
                    ("name", new StringValue(entry.Name)),
                    ("path", new StringValue(entry.FullName)),
                    ("folder", Values.Bool(isFolder)),
                    ("size", new NumberValue(entry is FileInfo file ? file.Length : 0)),
                    ("modified", new StringValue(entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm"))),
                    ("hidden", Values.Bool(entry.Name.StartsWith('.') || entry.Attributes.HasFlag(FileAttributes.Hidden)))));
            }
        }
        catch (Exception)
        {
        }

        return new ListValue(listed);
    }

    // A program run to its end (five seconds at most), what it wrote and how it ended
    private static IRuntimeValue Run(string program, string[] arguments, string? folder)
    {
        try
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            if (folder is not null)
                start.WorkingDirectory = folder;

            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return Natives.Object(("code", new NumberValue(-1)), ("output", new StringValue(string.Empty)), ("errors", new StringValue("it took longer than five seconds")));
            }

            return Natives.Object(("code", new NumberValue(process.ExitCode)), ("output", new StringValue(output.Result.ReplaceLineEndings("\n"))), ("errors", new StringValue(errors.Result.ReplaceLineEndings("\n"))));
        }
        catch (Exception e)
        {
            return Natives.Object(("code", new NumberValue(-1)), ("output", new StringValue(string.Empty)), ("errors", new StringValue(e.Message)));
        }
    }

    private static IRuntimeValue Processes()
    {
        var listed = new List<IRuntimeValue>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    listed.Add(Natives.Object(
                        ("pid", new NumberValue(process.Id)),
                        ("name", new StringValue(process.ProcessName)),
                        ("memory", new NumberValue(process.WorkingSet64 / (1024f * 1024f))),
                        ("threads", new NumberValue(process.Threads.Count))));
                }
                catch (Exception)
                {
                }
            }
        }

        return new ListValue(listed);
    }

    private static bool Kill(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
