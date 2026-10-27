namespace Horizon.UI.Components;

// A window that has to be done with before anything else is (a file to open, how F5 runs), over the soft dim every
// dialog has, which takes the pointer so nothing behind it is clicked or hovered. They were windows like any other, a
// tooltip came up through one and it could be left behind under the code and forgotten
public partial class WindowPanel
{
    /// <summary>Whether it's over the dim a dialog has (<see cref="UIScrim"/>) and nothing behind it takes the pointer.</summary>
    public bool Modal { get; set; }

    private UIScrim? scrim;

    // The dim kept right behind it in what it's in, put there the first update it's up and gone again with it (the dim
    // takes itself away once the window is closed, hidden or taken out)
    private void KeepScrim()
    {
        if (!Modal || !Visible || Parent is not { } parent)
        {
            scrim = null;
            return;
        }

        if (scrim is null || scrim.Parent != parent)
        {
            // Over everything else in what it's in whatever its z, the way a dialog is, or what's raised (the cards of an
            // editor) was drawn over the dim
            int highest = 0;
            foreach (UIComponent other in parent.Children)
            {
                if (other != this)
                    highest = Math.Max(highest, other.ZOffset);
            }
            ZOffset = Math.Max(ZOffset, highest + 1);

            scrim = new UIScrim(this);
            parent.Insert(IndexIn(parent), scrim);
            scrim.PlayIntro();
        }

        // Clicked to the front it takes its dim with it, right behind it
        scrim.ZOffset = ZOffset;
        int at = IndexIn(parent);
        if (at > 0 && parent.Children[at - 1] == scrim)
            return;

        int from = 0;
        while (from < parent.Children.Count && parent.Children[from] != scrim)
            from++;
        parent.MoveChild(scrim, from < at ? at - 1 : at);
    }

    private int IndexIn(UIComponent parent)
    {
        for (int i = 0; i < parent.Children.Count; i++)
        {
            if (parent.Children[i] == this)
                return i;
        }

        return parent.Children.Count;
    }
}
