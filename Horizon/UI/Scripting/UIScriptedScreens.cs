using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Horizon.UI.Scripting;

/// <summary>
/// Everything a program's scripts put on screen out of their layouts, kept together: the views of a file (one of each
/// kind for every file, made the first time it's shown and kept until the file goes), the pages of a tab panel and the
/// little windows over everything. They're ticked here, closed here when their owner is read again or a file goes, and
/// what goes wrong in them is said through the two callbacks it's made with. Hex's extensions are what it was made for,
/// <typeparamref name="TFile"/> being Hex's documents.
/// </summary>
public sealed class UIScriptedScreens<TFile>(UIModule module, Action<string> problem, Action<IUIScriptOwner> failed) where TFile : class
{
    private readonly Dictionary<(TFile File, string Id), UIScriptedLayout> views = [];
    private readonly List<UIScriptedLayout> pages = [], windows = [];
    private readonly Dictionary<UIScriptedLayout, string> pageTitles = [];

    /// <summary>The module everything is made in.</summary>
    public UIModule Module => module;

    /// <summary>Whether there's anything at all, nothing ticks while there isn't.</summary>
    public bool Any => views.Count > 0 || pages.Count > 0 || windows.Count > 0;

    /// <summary>The views made so far, of every file.</summary>
    public IEnumerable<UIScriptedLayout> Views => views.Values;

    /// <summary>The pages made, in the order they were.</summary>
    public IReadOnlyList<UIScriptedLayout> Pages => pages;

    /// <summary>The windows that are up.</summary>
    public IReadOnlyList<UIScriptedLayout> Windows => windows;

    /// <summary>What a page says on its tab.</summary>
    public string TitleOf(UIScriptedLayout page) => pageTitles.TryGetValue(page, out string? title) ? title : string.Empty;

    /// <summary>The view of a kind made for a file, null when there isn't one yet.</summary>
    public UIScriptedLayout? View(TFile of, string id) => views.TryGetValue((of, id), out UIScriptedLayout? made) ? made : null;

    /// <summary>
    /// A layout of a script made inside of something, its handlers the owner's to call. Null, said, if the layout doesn't
    /// build. Nothing keeps it, it's for what has a place of its own (a page of the settings).
    /// </summary>
    public UIScriptedLayout? Make(IUIScriptOwner owner, string kind, string layoutPath, UIComponent into, string? file, Action? close, Func<Vector2>? room = null) =>
        UIScriptedLayout.Make(module, owner, kind, layoutPath, into, file, close, problem, failed, room);

    /// <summary>
    /// A view of a kind made for a file, in a holder of its own inside <paramref name="views"/>, which is the room it has.
    /// Null, said, when its layout doesn't build.
    /// </summary>
    public UIScriptedLayout? MakeView(IUIScriptOwner owner, string id, TFile of, string layoutPath, Panel views, string? file)
    {
        var holder = views.Add(new Panel { Fill = UIFill.Both });
        // What it has room for is the panel the views go in, the holder is only as big as what's in it
        UIScriptedLayout? made = Make(owner, "view", layoutPath, holder, file, null, () => views.Bounds.Size);
        if (made is null)
        {
            views.Remove(holder);
            return null;
        }

        this.views[(of, id)] = made;
        return made;
    }

    /// <summary>
    /// A page of a tab panel out of a layout, its tab saying <paramref name="title"/>. <paramref name="changed"/> once
    /// the pages are different, it was made or it closed itself.
    /// </summary>
    public UIScriptedLayout? MakePage(IUIScriptOwner owner, string title, string layoutPath, TabPanel into, Action changed)
    {
        var page = into.Add(new Panel { Fill = UIFill.Both });
        UIScriptedLayout? made = null;
        made = Make(owner, "panel page", layoutPath, page, null, () =>
        {
            into.Remove(page);
            if (made is not null)
            {
                made.Closed = true;
                pages.Remove(made);
                pageTitles.Remove(made);
            }
            changed();
        });

        if (made is null)
        {
            into.Remove(page);
            return null;
        }

        pageTitles[made] = title;
        pages.Add(made);
        changed();
        return made;
    }

    /// <summary>A little window over everything out of a layout, kept while it's up.</summary>
    public UIScriptedLayout? MakeWindow(IUIScriptOwner owner, string title, string layoutPath, Vector2 size)
    {
        UIScriptedLayout? made = UIScriptedLayout.MakeWindow(module, owner, title, layoutPath, size, problem, failed, closed => windows.Remove(closed));
        if (made is not null)
            windows.Add(made);
        return made;
    }

    /// <summary>
    /// What an owner has on screen taken down as it's read again, its views and its windows (its pages close themselves),
    /// handing back the files and kinds of the views that were taken, for them to be put back on once it's started anew.
    /// </summary>
    public List<(TFile File, string Id)> Forget(IUIScriptOwner owner)
    {
        var taken = new List<(TFile, string)>();
        foreach (var (key, made) in views.ToList())
        {
            if (made.Owner != owner)
                continue;

            made.Closed = true;
            made.Root.Parent?.Remove(made.Root);
            views.Remove(key);
            taken.Add(key);
        }

        for (int i = windows.Count - 1; i >= 0; i--)
        {
            if (windows[i].Owner == owner)
            {
                windows[i].Closed = true;
                windows[i].Root.Parent?.Remove(windows[i].Root);
                windows.RemoveAt(i);
            }
        }

        return taken;
    }

    /// <summary>The views a file had made for it taken away, as it closes.</summary>
    public void Forget(TFile closing)
    {
        foreach (var key in views.Keys.Where(key => key.File == closing).ToList())
        {
            UIScriptedLayout made = views[key];
            made.Closed = true;
            made.Root.Parent?.Remove(made.Root);
            views.Remove(key);
        }
    }

    /// <summary>
    /// Every tick, what's on screen told how long it was (on_tick): the view <paramref name="shown"/>, the page the panel
    /// is on and the windows that are up.
    /// </summary>
    public void Update(float dt, UIScriptedLayout? shown, TabPanel? pagesIn)
    {
        if (!Any)
            return;

        // Made only when something ticks, a view with nothing going on costs nothing a frame
        NumberValue? number = null;
        shown?.Update(dt, ref number, failed);

        for (int i = pages.Count - 1; i >= 0; i--)
        {
            if (pages[i] is { } page && page.Root.Visible && pagesIn is { Visible: true } && pagesIn.SelectedPage == page.Root)
                page.Update(dt, ref number, failed);
        }

        for (int i = windows.Count - 1; i >= 0; i--)
        {
            if (windows[i] is { } window && window.Root.Visible)
                window.Update(dt, ref number, failed);
        }
    }

    /// <summary>A key handed to a view, true if it took it.</summary>
    public bool Press(UIScriptedLayout? view, string name) => view is not null && view.Press(name, failed);

    /// <summary>A key handed to a view with what's held, "shift+left" first, then "left".</summary>
    public void Press(UIScriptedLayout view, string name, bool shift, bool alt) => view.Press(name, shift, alt, failed);
}
