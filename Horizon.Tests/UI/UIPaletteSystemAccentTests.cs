using System.Numerics;
using Horizon.UI.Skinning;

namespace Horizon.Tests;

/// <summary>A theme that says <c>accent_color: "system"</c> takes the accent the program has been told the system has.</summary>
public class UIPaletteSystemAccentTests
{
    [Fact]
    public void A_theme_saying_system_takes_the_system_accent_and_one_with_a_colour_keeps_it()
    {
        string root = Path.Combine(Path.GetTempPath(), "uix-system-accent-" + Guid.NewGuid().ToString("N"));
        string themes = Path.Combine(root, "themes");
        Directory.CreateDirectory(themes);
        Vector4 before = UIPalette.SystemAccent;
        try
        {
            string source = FindFlat();
            File.Copy(Path.Combine(source, UISkins.FILE), Path.Combine(root, UISkins.FILE));
            File.WriteAllText(Path.Combine(themes, "tinted.hor"), "let theme = { title: \"Tinted\", accent_color: \"system\" };\n");
            File.WriteAllText(Path.Combine(themes, "plain.hor"), "let theme = { title: \"Plain\", accent_color: vec(1.0, 0.0, 0.0, 1.0) };\n");

            UIPalette.SystemAccent = new Vector4(0.1f, 0.8f, 0.3f, 1.0f);
            Assert.Equal(new Vector4(0.1f, 0.8f, 0.3f, 1.0f), UISkin.PaletteOf(root + Path.DirectorySeparatorChar, UISkins.FILE, "tinted").Accent);
            Assert.Equal(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), UISkin.PaletteOf(root + Path.DirectorySeparatorChar, UISkins.FILE, "plain").Accent);

            UIPalette.SystemAccent = new Vector4(0.9f, 0.2f, 0.6f, 1.0f);
            Assert.Equal(new Vector4(0.9f, 0.2f, 0.6f, 1.0f), UISkin.PaletteOf(root + Path.DirectorySeparatorChar, UISkins.FILE, "tinted").Accent);
        }
        finally
        {
            UIPalette.SystemAccent = before;
            Directory.Delete(root, true);
        }
    }

    private static string FindFlat([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Horizon", "Assets", "uix", "flat"));
}
