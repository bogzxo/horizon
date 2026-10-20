using System.Runtime.CompilerServices;

namespace Horizon.UI.Components;

/// <summary>
/// The terminal's components made known to scripts and layouts, terminal and terminal_panel, as the engine's own are,
/// and what its settings are to the settings file.
/// It happens by itself as the assembly loads, and <see cref="Register"/> says it outright for a program that makes a
/// module before it touches anything of the terminal's.
/// </summary>
public static class TerminalKinds
{
    private static int registered;

    [ModuleInitializer]
    internal static void Loaded() => Register();

    /// <summary>The terminal's components made known to every module made from now on. Once.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 1)
            return;

        UIModule.AddKind<TerminalView>("terminal");
        UIModule.AddKind<TerminalPanel>("terminal_panel");
        // and the help over the terminal's settings, as the settings file is written
        SettingsSections.Help[SettingsSections.TERMINAL] = key => Array.Find(TerminalSettings.Described, setting => setting.Key == key)?.Help;
    }
}
