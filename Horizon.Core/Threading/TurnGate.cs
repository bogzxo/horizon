using System.Diagnostics;

namespace Horizon.Core.Threading;

/// <summary>
/// The gate the loops of the engine and its drawing take turns at. It is locked like any other object, what it adds is manners.
/// <para>
/// A lock doesn't care who has been waiting the longest. Whoever lets go of it and asks for it again straight away gets it
/// again, which is exactly what drawing as many frames as there is time for does. The loops that update the game were left
/// standing at the door, and the game ran slow the faster it was drawn. So the loops say when they are waiting, and the
/// drawing lets them go first.
/// </para>
/// </summary>
public sealed class TurnGate
{
    // The longest (in seconds) the drawing holds back for a loop that says it is waiting. Getting to the lock takes a loop
    // a few microseconds, anything that takes longer than this is not coming
    private const double LONGEST_COURTESY = 0.0005;

    private int waiting;

    /// <summary>
    /// Whether a loop is waiting for its turn right now.
    /// </summary>
    public bool HasWaiters => Volatile.Read(ref waiting) > 0;

    /// <summary>
    /// Called by a loop right before it asks for the lock, and again (with false) once it has it.
    /// </summary>
    internal void NoteWaiting(bool isWaiting)
    {
        if (isWaiting) Interlocked.Increment(ref waiting);
        else Interlocked.Decrement(ref waiting);
    }

    /// <summary>
    /// Called by the drawing before it asks for the lock. Returns once nobody is waiting any more, which is nearly always right away.
    /// </summary>
    public void LetWaitersGoFirst()
    {
        if (!HasWaiters) return;

        long until = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * LONGEST_COURTESY);
        while (HasWaiters && Stopwatch.GetTimestamp() < until)
            Thread.Yield();
    }
}
