using System;
using System.Drawing;
using System.Windows.Forms;

namespace DimWidget
{
    internal sealed class OverlayForm : Form
    {
        public OverlayForm(Rectangle bounds)
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.Opaque |
                ControlStyles.UserPaint,
                true);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            MinimizeBox = false;
            MaximizeBox = false;
            BackColor = Color.Black;
            Bounds = bounds;
            TopMost = true;
            Opacity = 0d;
            TabStop = false;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_LAYERED
                              | NativeMethods.WS_EX_TRANSPARENT
                              | NativeMethods.WS_EX_TOOLWINDOW
                              | NativeMethods.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        public void ApplyAlpha(double alpha)
        {
            if (alpha <= 0.001d)
            {
                if (Visible)
                {
                    Hide();
                }

                return;
            }

            if (alpha > 1d)
            {
                alpha = 1d;
            }

            Opacity = alpha;

            if (!Visible)
            {
                Show();
            }
        }

        public void Promote()
        {
            if (!Visible)
            {
                return;
            }

            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }
}
