using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

using Horizon.Platform;

namespace Horizon.Platform.Desktop;

/// <summary>
/// The Dock icon of a Mac: it bounces for what needs the person (<c>requestUserAttention:</c>) and carries a badge
/// (<c>[[NSApp dockTile] setBadgeLabel:]</c>). AppKit is the main thread's, so every call is handed to <see cref="MacMain"/>.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacDock : IPlatformAttention
{
    // NSRequestUserAttentionType
    internal const int CRITICAL = 0, INFORMATIONAL = 10;

    /// <summary>Installs the Mac's Dock as the platform's, once this assembly loads.</summary>
    [ModuleInitializer]
    internal static void Install()
    {
        if (OperatingSystem.IsMacOS())
            PlatformAttention.Current ??= new MacDock();
    }

    /// <inheritdoc />
    public void RequestAttention(bool critical) => MacMain.Run(() =>
    {
        nint app = MacObjC.SharedApplication;
        if (app == 0 || MacObjC.SendBool(app, MacObjC.Sel("isActive")))
            return;

        MacObjC.Send(app, MacObjC.Sel("requestUserAttention:"), (nuint)(critical ? CRITICAL : INFORMATIONAL));
    });

    /// <inheritdoc />
    public void SetBadge(string? label) => MacMain.Run(() =>
    {
        nint app = MacObjC.SharedApplication;
        nint tile = app == 0 ? 0 : MacObjC.Send(app, MacObjC.Sel("dockTile"));
        if (tile != 0)
            MacObjC.SendVoid(tile, MacObjC.Sel("setBadgeLabel:"), string.IsNullOrEmpty(label) ? 0 : MacObjC.NSString(label));
    });
}
