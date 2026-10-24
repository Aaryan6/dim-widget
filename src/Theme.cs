using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DimWidget
{
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(19, 19, 22);
        public static readonly Color Surface = Color.FromArgb(33, 33, 39);
        public static readonly Color SurfaceHover = Color.FromArgb(46, 46, 54);
        public static readonly Color Border = Color.FromArgb(52, 52, 61);
        public static readonly Color Track = Color.FromArgb(46, 46, 55);
        public static readonly Color Text = Color.FromArgb(242, 242, 245);
        public static readonly Color Muted = Color.FromArgb(138, 138, 149);
        public static readonly Color Accent = Color.FromArgb(255, 209, 102);
        public static readonly Color OnAccent = Color.FromArgb(23, 23, 26);

        private static FontFamily _family;

        public static Font Ui(float size, FontStyle style = FontStyle.Regular)
        {
            if (_family == null)
            {
                string[] candidates = { "Segoe UI Variable Text", "Segoe UI", "Tahoma" };
                foreach (string name in candidates)
                {
                    try
                    {
                        _family = new FontFamily(name);
                        break;
                    }
                    catch (ArgumentException)
                    {
                    }
                }

                if (_family == null)
                {
                    _family = SystemFonts.MessageBoxFont.FontFamily;
                }
            }

            return new Font(_family, size, style, GraphicsUnit.Point);
        }

        public static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Max(0f, Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height)));

            if (d <= 0.01f)
            {
                path.AddRectangle(rect);
                return path;
            }

            path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        public static Icon CreateTrayIcon()
        {
            const int size = 64;
            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    using (SolidBrush moon = new SolidBrush(Accent))
                    {
                        g.FillEllipse(moon, 4f, 4f, size - 8f, size - 8f);
                    }

                    g.CompositingMode = CompositingMode.SourceCopy;
                    using (SolidBrush cut = new SolidBrush(Color.FromArgb(0, 0, 0, 0)))
                    {
                        g.FillEllipse(cut, size * 0.32f, -size * 0.04f, size - 4f, size - 4f);
                    }
                    g.CompositingMode = CompositingMode.SourceOver;
                }

                IntPtr handle = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    NativeMethods.DestroyIcon(handle);
                }
            }
        }

        public static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }
    }
}
