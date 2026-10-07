namespace Horizon.Hex;

/// <summary>
/// What the editor was started with, see <see cref="Program"/> for how each of them is asked for.
/// </summary>
/// <param name="Open">A layout to start with.</param>
/// <param name="Workspace">The folder layouts are opened from and saved to until another one is browsed to.</param>
/// <param name="SelfTest">Whether the editor works through itself with a scripted pointer and prints how that went.</param>
/// <param name="Wheel">Whether the self-test waits for somebody to turn the real mouse wheel.</param>
/// <param name="Check">Layouts (or folders of them) to open, test and report on rather than edit.</param>
/// <param name="Exit">Whether the editor closes once its self-test or its checks are done.</param>
/// <param name="Exercise">Whether the layouts that are checked are changed, saved, opened again and put back as well.</param>
/// <param name="Scale">How big the editor draws itself, null for what it was left at the last time.</param>
/// <param name="Browse">Whether the editor starts by asking which layout to open.</param>
internal sealed record HexOptions(string? Open, string Workspace, bool SelfTest, bool Wheel, string[] Check, bool Exit, bool Exercise = false, float? Scale = null, bool Browse = false);
