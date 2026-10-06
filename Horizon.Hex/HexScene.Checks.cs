using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

namespace Horizon.Hex;

// Testing layouts rather than editing them, for whoever runs the editor with --check.
internal sealed partial class HexScene
{
    /* Testing layouts rather than editing them */

    /// <summary>
    /// Opens every layout that was asked to be checked, says how each of them did and (if asked to) closes the
    /// editor with the number that didn't make it as its exit code.
    /// </summary>
    private void RunChecks()
    {
        checksDone = true;

        var files = new List<string>();
        foreach (string path in options.Check)
        {
            if (Directory.Exists(path))
                files.AddRange(Directory.GetFiles(path, "*.hor", SearchOption.AllDirectories).Order());
            else
                files.Add(path);
        }

        int failed = 0;
        foreach (string file in files)
        {
            if (!Open(file) || (options.Exercise && !Exercise(file)))
            {
                Console.WriteLine($"[Hex check] FAIL: {file}");
                failed++;
            }
            else
            {
                Console.WriteLine($"[Hex check] pass: {file}");
            }
        }

        Console.WriteLine(options.Exercise
            ? $"[Hex check] {files.Count - failed} of {files.Count} layouts open, write back the same and come through being edited."
            : $"[Hex check] {files.Count - failed} of {files.Count} layouts open and write back the same.");

        if (options.Exit && !options.SelfTest)
            Quit(failed);
    }

    /// <summary>
    /// Puts the layout that was just opened through what somebody editing it would. Something in it is moved,
    /// something is added and named, it is saved and opened again, all of that is taken back by hand and it is
    /// saved and opened once more, and then the same change is undone and redone. Every name the layout had has
    /// to still be there after each step (a program finds its parts by them), and at the end the file has to be
    /// what the editor wrote for the layout nobody had touched. The file is written over, so this is for copies.
    /// </summary>
    private bool Exercise(string file)
    {
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            if (!holds) problems.Add(what);
        }

        string untouched = document.GenerateCode();
        List<string> names = [.. document.Walk().Select(entry => document.NameOf(entry.Component))];
        string firstName = names[0];
        bool AllNamed() => names.TrueForAll(name => document.Find(name) is not null);

        // Edited. Moved, and with a label of its own in the first thing that takes one
        UIComponent first = document.Find(firstName)!;
        Vector2 was = first.Position;
        Select(first);
        Set(first, "pos", new Vector2Value(was + new Vector2(12, -7)));

        UIComponent? added = document.Add("label", document.Walk().Select(entry => entry.Component).OfType<Panel>().FirstOrDefault());
        Expect(added is not null && document.Rename(added, "hex_exercise"), "a label couldn't be added and named");
        document.Record();

        string edited = document.GenerateCode();
        SaveTo(file);
        Close();

        Expect(Open(file), "the edited layout didn't open, or doesn't write back the same");
        Expect(document.GenerateCode() == edited, "the edited layout came back different from how it was saved");
        Expect(AllNamed() && document.Find("hex_exercise") is Label, "a name went missing in the edited layout");
        Expect(document.Find(firstName)?.Position == was + new Vector2(12, -7), "the move didn't make it into the file");

        // Put back by hand
        if (document.Find("hex_exercise") is { } label)
            document.Remove(label);
        if (document.Find(firstName) is { } moved)
            Set(moved, "pos", new Vector2Value(was));

        SaveTo(file);
        Close();

        Expect(Open(file), "the layout that was put back didn't open");
        Expect(document.GenerateCode() == untouched, "putting everything back didn't give the layout it started as");
        Expect(AllNamed(), "a name went missing putting everything back");

        // And once more with undo and redo, which build the layout again from what it was
        Set(document.Find(firstName)!, "pos", new Vector2Value(was + new Vector2(30, 30)));
        document.Record();
        string changed = document.GenerateCode();

        Expect(document.Undo() && document.GenerateCode() == untouched && AllNamed(), "undo didn't give the layout back");
        Expect(document.Redo() && document.GenerateCode() == changed && AllNamed(), "redo didn't make the change again");
        Expect(document.Undo() && document.GenerateCode() == untouched, "undo didn't work a second time");
        Expect(System.IO.File.ReadAllText(file) == untouched, "the file isn't the layout it started as");

        Restored("exercised");

        foreach (string problem in problems)
            Console.WriteLine($"[Hex check] {Path.GetFileName(file)}: {problem}");

        return problems.Count == 0;
    }
}
