namespace Horizon.Platform.Desktop;

/// <summary>What a double click on a window's caption does on a Mac, the person's choice in System Settings (Desktop &amp; Dock).</summary>
public enum MacCaptionAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary><c>performZoom:</c>, which fills the screen or goes back (the Dock setting's "Zoom" or "Fill").</summary>
    Zoom,

    /// <summary><c>performMiniaturize:</c>, which puts the window in the Dock.</summary>
    Miniaturize
}

/// <summary>The pure half of a Mac's caption double click, so it is tested wherever the tests run.</summary>
public static class MacCaptionActions
{
    /// <summary>The <c>NSUserDefaults</c> key the system keeps the choice under.</summary>
    public const string DefaultsKey = "AppleActionOnDoubleClick";

    /// <summary>
    /// What a double click does for the value of <c>AppleActionOnDoubleClick</c>: "Maximize" (and "Fill"), "Minimize", "None".
    /// The key is not there until the person changes the setting, and then the system zooms, so does anything unknown.
    /// </summary>
    public static MacCaptionAction FromDefault(string? value)
    {
        string text = value?.Trim() ?? string.Empty;
        if (text.Equals("Minimize", StringComparison.OrdinalIgnoreCase))
            return MacCaptionAction.Miniaturize;
        if (text.Equals("None", StringComparison.OrdinalIgnoreCase))
            return MacCaptionAction.None;
        return MacCaptionAction.Zoom;
    }
}
