namespace Horizon.UI.Scripting;

public sealed partial class DebugSession
{
    private sealed record RunTarget(string File, int Line, DebugBreakpoint[] Original);
    private RunTarget? runTarget;
    private volatile bool preparingRunTarget;

    public async Task RunTo(string file, int line)
    {
        if (State != DebugState.Stopped || preparingRunTarget || line < 0) return;
        preparingRunTarget = true;
        try
        {
            DebugBreakpoint[] original;
            lock (breakpoints)
            {
                original = breakpoints.TryGetValue(file, out var placed) ? placed.Select(item => item.Wanted).ToArray() : [];
                runTarget = new(file, line, original);
            }
            await SetBreakpointsNow(file, original).ConfigureAwait(false);
            lock (breakpoints)
            {
                if (!breakpoints.TryGetValue(file, out var placed) || !placed.Any(item => item.Wanted.Line == line && item.Verified))
                    throw new InvalidOperationException("The debugger could not place a breakpoint at the cursor.");
            }
            if (State == DebugState.Stopped) Go("continue", runTo: true);
            else await ClearRunTarget().ConfigureAwait(false);
        }
        catch
        {
            await ClearRunTarget().ConfigureAwait(false);
            throw;
        }
        finally { preparingRunTarget = false; }
    }

    private async Task ClearRunTarget()
    {
        RunTarget? target;
        lock (breakpoints) { target = runTarget; runTarget = null; }
        if (target is not null && State != DebugState.Ended)
            await SetBreakpointsNow(target.File, target.Original).ConfigureAwait(false);
    }
}
