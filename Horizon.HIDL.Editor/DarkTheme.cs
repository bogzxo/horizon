using System.Drawing;
using System.Windows.Forms;

namespace Horizon.HIDL.Editor
{
    public class DarkThemeRenderer : ToolStripProfessionalRenderer
    {
        public DarkThemeRenderer() : base(new DarkThemeColors()) { }
    }

    /// <summary>
    /// Theme colours mimicking visual studio, thank u Gemini
    /// </summary>
    public class DarkThemeColors : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => Color.FromArgb(45, 45, 48);
        public override Color MenuStripGradientEnd => Color.FromArgb(45, 45, 48);
        public override Color MenuItemSelected => Color.FromArgb(62, 62, 66);
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuBorder => Color.FromArgb(45, 45, 48);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(62, 62, 66);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(62, 62, 66);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(235, 110, 45);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(235, 110, 45);
        public override Color ToolStripDropDownBackground => Color.FromArgb(37, 37, 38);
        public override Color ImageMarginGradientBegin => Color.FromArgb(37, 37, 38);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(37, 37, 38);
        public override Color ImageMarginGradientEnd => Color.FromArgb(37, 37, 38);
        public override Color StatusStripGradientBegin => Color.FromArgb(235, 110, 45);
        public override Color StatusStripGradientEnd => Color.FromArgb(215, 85, 25);
        public override Color ToolStripGradientBegin => Color.FromArgb(37, 37, 38);
        public override Color ToolStripGradientMiddle => Color.FromArgb(37, 37, 38);
        public override Color ToolStripGradientEnd => Color.FromArgb(37, 37, 38);
        public override Color SeparatorDark => Color.FromArgb(60, 60, 60);
    }
}