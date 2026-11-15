using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>
/// A section of a program's settings as a settings page shows it, with each key's description, current value and what a
/// change does. In Hex the terminal, editor and extensions sections are each one of these. The program that owns a
/// section applies a change at once, and the other program writes it to the file for the owner to reload.
/// </summary>
public interface ISettingsSource
{
    /// <summary>Which section of the file it is (SettingsSections.TERMINAL and so on).</summary>
    string Section { get; }

    /// <summary>Its settings, their keys as the section writes them.</summary>
    IReadOnlyList<UISetting> Settings { get; }

    IRuntimeValue? Get(string key);

    /// <summary>Whether a setting differs from its default value.</summary>
    bool IsModified(string key);

    void Set(string key, IRuntimeValue value);

    /// <summary>Resets a setting to its default value.</summary>
    void Reset(string key);

    /// <summary>A setting's default value, which its reset restores and names, null for a section that has none.</summary>
    IRuntimeValue? Default(string key) => null;

    /// <summary>One of a setting's buttons pressed (the picture's Choose), by which, nothing unless a section has buttons.</summary>
    void Press(string key, int which)
    {
    }
}

/// <summary>Whether two values are the same, by how they are written.</summary>
public static class SettingValues
{
    public static bool Same(IRuntimeValue? a, IRuntimeValue? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        try
        {
            return HIDLWriter.Write(a) == HIDLWriter.Write(b);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
