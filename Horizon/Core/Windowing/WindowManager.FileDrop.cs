using System.Collections.Concurrent;

namespace Horizon.Core;

// Files dragged onto the window from outside the program. The platform says what is held over the window as it moves and
// what was let go of, the program looks at the first whenever it draws (an overlay saying what a drop would do) and takes
// the drops one at a time on whatever thread it updates on. A desktop knows what is held while it is dragged on Windows,
// a Mac and Linux only say once it was dropped; a phone never says anything.
public partial class WindowManager
{
    // What was dropped and not taken yet, oldest first
    private readonly ConcurrentQueue<FileDrop> fileDrops = new();

    // What is held over the window right now, null when nothing is
    private volatile FileDrop? fileDrag;

    /// <summary>
    /// Files held over the window right now (Enter or Over, where it is the last the platform said), null when nothing is
    /// or the platform can't tell until they are dropped. From any thread.
    /// </summary>
    public FileDrop? FileDrag => fileDrag;

    /// <summary>
    /// The oldest drop of files on the window not taken yet, once each. From any thread, the program's usual one being
    /// whichever it updates on.
    /// </summary>
    public bool TryTakeFileDrop(out FileDrop drop)
    {
        if (fileDrops.TryDequeue(out FileDrop? taken))
        {
            drop = taken;
            return true;
        }

        drop = null!;
        return false;
    }

    /// <summary>
    /// What files dragged from outside the program are doing to the window, as the platform says it, or a test standing in
    /// for one. A drop is kept for <see cref="TryTakeFileDrop"/> and ends whatever was held, anything else is what
    /// <see cref="FileDrag"/> says from now on. Wakes an application that went quiet. From any thread.
    /// </summary>
    public void PostFileDrop(FileDrop drop)
    {
        ArgumentNullException.ThrowIfNull(drop);
        switch (drop.Kind)
        {
            case FileDropKind.Drop:
                fileDrag = null;
                if (drop.Paths.Count > 0)
                    fileDrops.Enqueue(drop);
                break;
            case FileDropKind.Leave:
                fileDrag = null;
                break;
            default:
                // Moving over says where only on some platforms, the files are the ones it came in with
                fileDrag = drop.Kind == FileDropKind.Over && drop.Paths.Count == 0 && fileDrag is { } held
                    ? held with { Kind = FileDropKind.Over, Position = drop.Position, HasPosition = drop.HasPosition }
                    : drop;
                break;
        }

        NoteInput();
        RequestFrames();
    }
}
