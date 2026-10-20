using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>
/// A section of a program's settings as a settings page shows it, what is in it described, what each is now, and what
/// changing one does. In Hex the terminal's, the editor's and the extensions' are each one of these, the mode that owns a
/// section putting a change in force there and then, the other mode writing it to the file for its owner to take in.
/// </summary>
public interface ISettingsSource
{
    /// <summary>Which section of the file it is (SettingsSections.TERMINAL and so on).</summary>
    string Section { get; }

    /// <summary>Its settings, their keys as the section writes them.</summary>
    IReadOnlyList<UISetting> Settings { get; }

    IRuntimeValue? Get(string key);

    /// <summary>Whether a setting is not what it comes as.</summary>
    bool IsModified(string key);

    void Set(string key, IRuntimeValue value);

    /// <summary>A setting back to what it comes as.</summary>
    void Reset(string key);

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
