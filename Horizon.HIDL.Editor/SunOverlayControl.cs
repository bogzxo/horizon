using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Horizon.HIDL.Editor
{
    public class SunOverlayControl : Control
    {
        public SunOverlayControl()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Opaque, false);
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

            BackColor = Color.Transparent;
            Dock = DockStyle.Fill;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTTRANSPARENT = -1;
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            float containerWidth = Width;
            float containerHeight = Height;

            if (containerWidth <= 0 || containerHeight <= 0) return;

            // Origin at bottom right corner of editor canvas
            float cx = containerWidth;
            float cy = containerHeight;

            // Gradient brush to paint a nice soft glow, probably expensive
            float glowRadius = 350f;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(cx - glowRadius, cy - glowRadius, glowRadius * 2, glowRadius * 2);

                using (PathGradientBrush glowBrush = new PathGradientBrush(path))
                {
                    glowBrush.CenterPoint = new PointF(cx, cy);
                    glowBrush.CenterColor = Color.FromArgb(90, 255, 120, 30);
                    glowBrush.SurroundColors = new Color[] { Color.FromArgb(0, 255, 120, 30) };
                    e.Graphics.FillPath(glowBrush, path);
                }
            }

            // Outer core
            float sunRadius = 140f;
            using (SolidBrush sunBrush = new SolidBrush(Color.FromArgb(210, 235, 110, 45)))
            {
                e.Graphics.FillEllipse(sunBrush, cx - sunRadius, cy - sunRadius, sunRadius * 2, sunRadius * 2);
            }

            // Inner core
            float coreRadius = 85f;
            using (SolidBrush coreBrush = new SolidBrush(Color.FromArgb(240, 255, 160, 60)))
            {
                e.Graphics.FillEllipse(coreBrush, cx - coreRadius, cy - coreRadius, coreRadius * 2, coreRadius * 2);
            }
            
            // Thank u gemini
            // Sun rays radiating from bottom right origin
            using (Pen rayPen = new Pen(Color.FromArgb(180, 240, 130, 50), 3.5f))
            {
                double[] rayAngles = new double[] { 190, 205, 220, 235, 250, 265 };
                float rayInner = sunRadius + 12f;
                float rayOuter = sunRadius + 45f;

                foreach (double angleDeg in rayAngles)
                {
                    double rad = angleDeg * Math.PI / 180.0;
                    float x1 = cx + (float)(Math.Cos(rad) * rayInner);
                    float y1 = cy + (float)(Math.Sin(rad) * rayInner);
                    float x2 = cx + (float)(Math.Cos(rad) * rayOuter);
                    float y2 = cy + (float)(Math.Sin(rad) * rayOuter);

                    e.Graphics.DrawLine(rayPen, x1, y1, x2, y2);
                }
            }
        }
    }
}