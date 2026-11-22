using System.Numerics;

namespace Horizon.Core;

/// <summary>What files dragged from outside the program are doing to its window.</summary>
public enum FileDropKind
{
    /// <summary>Dragged into the window, not let go of yet.</summary>
    Enter,

    /// <summary>Moved about over the window, still held.</summary>
    Over,

    /// <summary>Dragged back out of the window, or the drag was called off.</summary>
    Leave,

    /// <summary>Let go of over the window.</summary>
    Drop
}

/// <summary>
/// Files and folders dragged from outside the program (a file manager) over its window or dropped on it, see
/// <see cref="WindowManager.FileDrag"/> and <see cref="WindowManager.TryTakeFileDrop"/>. The paths are whole ones, empty
/// for a <see cref="FileDropKind.Leave"/> and where the platform can't say what is held until it is let go of. Where is
/// in pixels of what is drawn, from the top left of the window, when <see cref="HasPosition"/> says the platform knew.
/// </summary>
public sealed record FileDrop(FileDropKind Kind, IReadOnlyList<string> Paths, Vector2 Position, bool HasPosition = true)
{
    /// <summary>A drag that left the window, or was called off.</summary>
    public static FileDrop Left { get; } = new(FileDropKind.Leave, [], Vector2.Zero, HasPosition: false);
}
