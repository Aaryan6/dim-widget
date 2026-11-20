using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DimWidget
{
    // Live desktop integration check. Briefly shows a small color swatch.
    internal static class CaptureChecks
    {
        [STAThread]
        private static int Main()
        {
            if (Environment.OSVersion.Version < new Version(10, 0, 19041))
            {
                Console.WriteLine("SKIP: capture exclusion requires Windows 10 version 2004 or later.");
                return 0;
            }

            Application.EnableVisualStyles();
            try
            {
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                Rectangle bounds = new Rectangle(work.Right - 150, work.Bottom - 150, 100, 100);
                using (Form target = new CaptureTarget())
                using (OverlayForm overlay = new OverlayForm(bounds))
                {
                    target.Bounds = bounds;
                    target.Show();
                    Settle();
                    Color original = CaptureCenter(bounds);
                    Require(SameColor(original, target.BackColor), "test swatch is visible in capture");

                    overlay.ApplyAlpha(0.6d);
                    Settle();
                    RequireExcluded(overlay);
                    Require(SameColor(CaptureCenter(bounds), original), "dimming is excluded from screenshot");

                    // Negative control proves this capture path sees an ordinary overlay.
                    Require(NativeMethods.SetWindowDisplayAffinity(overlay.Handle, 0), "disable exclusion for control");
                    Settle();
                    Require(CaptureCenter(bounds).R < original.R * 0.6d, "ordinary overlay darkens screenshot");

                    // Recreating the native window must restore capture exclusion.
                    typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(overlay, null);
                    overlay.Promote();
                    Settle();
                    RequireExcluded(overlay);
                    Require(SameColor(CaptureCenter(bounds), original), "recreated overlay stays out of screenshot");

                    overlay.ApplyAlpha(0d);
                    overlay.ApplyAlpha(0.8d);
                    Settle();
                    Require(overlay.Visible && Math.Abs(overlay.Opacity - 0.8d) < 0.01d, "screen dimming remains active");
                    RequireExcluded(overlay);
                    Require(SameColor(CaptureCenter(bounds), original), "pause and brightness changes preserve clean capture");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void RequireExcluded(OverlayForm overlay)
        {
            uint affinity;
            Require(GetWindowDisplayAffinity(overlay.Handle, out affinity)
                && affinity == NativeMethods.WDA_EXCLUDEFROMCAPTURE, "overlay has capture exclusion");
        }

        private static void Settle()
        {
            Application.DoEvents();
            Thread.Sleep(100);
            DwmFlush();
        }

        private static Color CaptureCenter(Rectangle bounds)
        {
            using (Bitmap pixel = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(pixel))
            {
                IntPtr screen = GetDC(IntPtr.Zero);
                IntPtr destination = graphics.GetHdc();
                try
                {
                    // CAPTUREBLT includes layered windows in the negative control.
                    Require(BitBlt(destination, 0, 0, 1, 1, screen,
                        bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2,
                        0x40CC0020), "capture desktop pixel");
                }
                finally
                {
                    graphics.ReleaseHdc(destination);
                    ReleaseDC(IntPtr.Zero, screen);
                }
                return pixel.GetPixel(0, 0);
            }
        }

        private static bool SameColor(Color actual, Color expected)
        {
            return Math.Abs(actual.R - expected.R) <= 3 && Math.Abs(actual.G - expected.G) <= 3
                && Math.Abs(actual.B - expected.B) <= 3;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            Console.WriteLine("PASS: " + message);
        }

        private sealed class CaptureTarget : Form
        {
            public CaptureTarget()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                TopMost = true;
                BackColor = Color.FromArgb(220, 160, 80);
            }

            protected override bool ShowWithoutActivation { get { return true; } }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, uint operation);

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();
    }
}
