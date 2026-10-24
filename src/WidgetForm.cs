using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DimWidget
{
    internal sealed class WidgetForm : Form
    {
        // Geometry in 96-dpi units. The tab hangs from the top edge of the
        // screen: square top corners, rounded bottom corners, and it grows
        // sideways when open so it stays centred over the same spot.
        private const int IdleWidth = 56;
        private const int IdleHeight = 22;
        private const int OpenWidth = 184;
        private const int OpenHeight = 32;
        private const int HandleWidth = 30;
        private const int ValueWidth = 29;
        private const int RightPad = 8;
        private const int Gap = 8;
        private const int CloseWidth = 20;
        private const int CloseGap = 5;
        private const int IdleRadius = 10;
        private const int OpenRadius = 12;
        private const float IconSize = 11f;
        private const float KnobSize = 12f;
        private const float TrackSize = 5f;

        // Blur is sampled from the desktop behind the tab: 96-dpi radius of the
        // three-pass box blur, doubled for the sampling margin that keeps the
        // blurred pixels near the tab's edge from dragging in black.
        // Floor between two backdrop resamples, in milliseconds.
        private const int RefreshInterval = 300;

        // A flat black plate, no sampling and no translucency: the notch is the
        // same tone over every window, so nothing behind it can make it move.
        private static readonly Color Panel = Color.Black;

        private static readonly Color Ink = Color.FromArgb(231, 244, 251);
        private static readonly Color InkMuted = Color.FromArgb(152, 178, 195);
        private static readonly Color Accent = Color.FromArgb(150, 203, 227);
        private static readonly Color Track = Color.FromArgb(58, 226, 241, 249);
        private static readonly Color Border = Color.FromArgb(56, 255, 255, 255);

        private readonly DimController _dim;
        private readonly Settings _settings;
        private readonly float _scale;
        private readonly Font _fontValue;
        private readonly Button _quitButton;
        private readonly System.Windows.Forms.Timer _animate;
        private readonly System.Windows.Forms.Timer _closeDelay;

        private float _open;
        private float _target;
        private bool _expanded;
        private bool _pinned;
        private bool _hovering;
        private bool _sliderActive;
        private bool _dragArmed;
        private bool _dragMoved;
        private bool _suppressActivation;
        private int _anchorX;
        private int _anchorTop;
        private Point _dragStartCursor;
        private int _dragStartAnchor;
        private DateTime _lastToggleUtc = DateTime.MinValue;
        private DateTime _ignoreDeactivateUntil = DateTime.MinValue;

        public DateTime LastCollapsedUtc { get; private set; }

        public event EventHandler QuitRequested;

        public int AnchorX
        {
            get { return _anchorX; }
        }

        public int AnchorTop
        {
            get { return _anchorTop; }
        }

        public WidgetForm(DimController dim, Settings settings)
        {
            _dim = dim;
            _settings = settings;

            float dpi;
            using (Graphics screen = Graphics.FromHwnd(IntPtr.Zero))
            {
                dpi = screen.DpiX;
            }

            _scale = Math.Max(0.75f, dpi / 96f);

            SuspendLayout();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Panel;
            TopMost = true;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "Dim";
            ClientSize = new Size(Scale(IdleWidth), Scale(IdleHeight));

            _fontValue = Theme.Ui(7.5f, FontStyle.Bold);

            _quitButton = new RoundCloseButton();
            _quitButton.Paint += delegate(object sender, PaintEventArgs e)
            {
                float centerX = (_quitButton.ClientSize.Width - 1) / 2f;
                float centerY = (_quitButton.ClientSize.Height - 1) / 2f;
                float radius = ScaleF(4f);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen stroke = new Pen(InkMuted, ScaleF(1.2f)))
                {
                    stroke.StartCap = stroke.EndCap = LineCap.Round;
                    e.Graphics.DrawLine(stroke, centerX - radius, centerY - radius,
                        centerX + radius, centerY + radius);
                    e.Graphics.DrawLine(stroke, centerX + radius, centerY - radius,
                        centerX - radius, centerY + radius);
                }
            };
            _quitButton.AccessibleName = "Quit Dim";
            _quitButton.FlatStyle = FlatStyle.Flat;
            _quitButton.FlatAppearance.BorderSize = 0;
            _quitButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 45, 45);
            _quitButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(70, 35, 35);
            _quitButton.BackColor = Panel;
            _quitButton.ForeColor = InkMuted;
            _quitButton.Cursor = Cursors.Hand;
            _quitButton.Visible = false;
            _quitButton.Click += delegate
            {
                EventHandler handler = QuitRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            Controls.Add(_quitButton);

            _animate = new System.Windows.Forms.Timer();
            _animate.Interval = 16;
            _animate.Tick += OnAnimateTick;

            _closeDelay = new System.Windows.Forms.Timer();
            _closeDelay.Interval = 180;
            _closeDelay.Tick += OnCloseDelayTick;



            ResumeLayout(false);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // Keep the widget out of the shell's window switcher.
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return _suppressActivation; }
        }

        public bool Expanded
        {
            get { return _expanded; }
        }

        public void Start(bool forceCollapsed)
        {
            RestorePosition();

            _pinned = _settings.Expanded && !forceCollapsed;
            _open = _target = _pinned ? 1f : 0f;
            _expanded = _pinned;

            DebugLog.Write("widget.Start forceCollapsed=" + forceCollapsed
                + " pinned=" + _pinned + " anchor=" + _anchorX + "," + _anchorTop);

            ApplyLayout();

            _suppressActivation = true;
            Show();
            _suppressActivation = false;

            PromoteSelf();
        }

        public void ExpandAndFocus()
        {
            if (!Visible)
            {
                Show();
            }

            // Windows can refuse foreground activation when another app owns the
            // foreground, which would fire Deactivate straight away and collapse
            // the widget before the user ever sees it.
            _ignoreDeactivateUntil = DateTime.UtcNow.AddMilliseconds(700);

            _pinned = true;
            _settings.Expanded = true;
            _hovering = true;
            Open();
            Activate();
            BringToFront();
            PromoteSelf();
        }

        public void Collapse()
        {
            _pinned = false;
            _settings.Expanded = false;
            CollapseTab();
        }

        public void SyncFromController()
        {
            Invalidate();
        }

        public void UpdateFromSettings()
        {
            Invalidate();
        }

        public void PromoteSelf()
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
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        private void Open()
        {
            _expanded = true;
            _target = 1f;
            _animate.Start();
        }

        private void CollapseTab()
        {
            _expanded = false;
            _target = 0f;
            LastCollapsedUtc = DateTime.UtcNow;
            _animate.Start();
            SaveState();
        }

        private void OnAnimateTick(object sender, EventArgs e)
        {
            float delta = _target - _open;

            if (Math.Abs(delta) < 0.005f)
            {
                _open = _target;
                _animate.Stop();
            }
            else
            {
                _open += delta * 0.32f;
            }

            ApplyLayout();
        }

        private void OnCloseDelayTick(object sender, EventArgs e)
        {
            _closeDelay.Stop();

            // Resizing the window under a still cursor fires MouseLeave with no
            // matching MouseEnter, so the pointer position is the real answer.
            if (Bounds.Contains(Cursor.Position))
            {
                _hovering = true;
                return;
            }

            if (_hovering || _pinned || _sliderActive || _dragArmed)
            {
                return;
            }

            if (_expanded)
            {
                CollapseTab();
            }
        }

        private void ApplyLayout()
        {
            int width = Math.Max(1, (int)Math.Round(ScaleF(IdleWidth + (OpenWidth - IdleWidth) * _open)));
            int height = Math.Max(1, (int)Math.Round(ScaleF(IdleHeight + (OpenHeight - IdleHeight) * _open)));

            if (ClientSize.Width != width || ClientSize.Height != height)
            {
                ClientSize = new Size(width, height);
            }

            _anchorX = ClampAnchorX(_anchorX);
            Location = new Point(_anchorX - width / 2, _anchorTop);

            _quitButton.Bounds = new Rectangle(
                width - Scale(RightPad + CloseWidth), (height - Scale(CloseWidth)) / 2,
                Scale(CloseWidth), Scale(CloseWidth));
            _quitButton.Visible = _expanded && _open >= 0.95f;

            ApplyShape();
            Invalidate();
        }

        private void ApplyShape()
        {
            Region previous = Region;

            using (GraphicsPath path = BuildShape())
            {
                Region = new Region(path);
            }

            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private float Radius
        {
            get
            {
                float radius = ScaleF(IdleRadius + (OpenRadius - IdleRadius) * _open);
                return Math.Max(0f, Math.Min(radius, Math.Min(ClientSize.Width, ClientSize.Height) / 2f));
            }
        }

        private GraphicsPath BuildShape()
        {
            return BuildTabPath(new RectangleF(0f, 0f, ClientSize.Width, ClientSize.Height), Radius);
        }

        // Square top edge, rounded bottom corners: the tab is glued to the top
        // of the screen, so only the corners that touch the desktop are curved.
        private static GraphicsPath BuildTabPath(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();

            if (rect.Width <= 0f || rect.Height <= 0f)
            {
                return path;
            }

            float r = Math.Max(0f, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f));

            if (r < 0.5f)
            {
                path.AddRectangle(rect);
                return path;
            }

            path.StartFigure();
            path.AddLine(rect.Left, rect.Top, rect.Left, rect.Bottom - r);
            path.AddArc(rect.Left, rect.Bottom - r * 2f, r * 2f, r * 2f, 180f, -90f);
            path.AddLine(rect.Left + r, rect.Bottom, rect.Right - r, rect.Bottom);
            path.AddArc(rect.Right - r * 2f, rect.Bottom - r * 2f, r * 2f, r * 2f, 90f, -90f);
            path.AddLine(rect.Right, rect.Bottom - r, rect.Right, rect.Top);
            path.CloseFigure();

            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            int width = ClientSize.Width;
            int height = ClientSize.Height;

            if (width <= 0 || height <= 0)
            {
                return;
            }

            using (GraphicsPath plate = BuildShape())
            using (SolidBrush fill = new SolidBrush(Panel))
            {
                g.FillPath(fill, plate);
            }

            using (GraphicsPath shape = BuildTabPath(
                new RectangleF(0.5f, 0f, width - 1f, height - 0.5f), Math.Max(1f, Radius - 0.5f)))
            using (Pen pen = new Pen(Border, 1f))
            {
                Region previous = g.Clip;
                g.SetClip(new RectangleF(0f, 1f, width, height), CombineMode.Replace);
                g.DrawPath(pen, shape);
                g.Clip = previous;
                previous.Dispose();
            }

            DrawIcon(g);
            DrawPanel(g);
        }

        private void DrawIcon(Graphics g)
        {
            float size = ScaleF(IconSize);
            float centerX = IconCenterX();
            float centerY = ClientSize.Height / 2f;
            RectangleF circle = new RectangleF(centerX - size / 2f, centerY - size / 2f, size, size);

            using (SolidBrush disc = new SolidBrush(Color.FromArgb(236, 249, 255)))
            {
                g.FillEllipse(disc, circle);
            }

            // Left half in the theme ink: the little ice cube reads even at 14 px.
            using (GraphicsPath half = new GraphicsPath())
            {
                half.AddArc(circle, 90f, 180f);
                half.CloseFigure();

                Region previous = g.Clip;
                g.SetClip(half, CombineMode.Intersect);
                using (SolidBrush shade = new SolidBrush(Color.FromArgb(205, Accent)))
                {
                    g.FillEllipse(shade, circle);
                }
                g.Clip = previous;
                previous.Dispose();
            }

            using (Pen ring = new Pen(Color.FromArgb(_hovering ? 220 : 155, Ink), 1f))
            {
                g.DrawEllipse(ring, circle.X + 0.5f, circle.Y + 0.5f, circle.Width - 1f, circle.Height - 1f);
            }
        }

        private void DrawPanel(Graphics g)
        {
            float left;
            float right;
            float centerY;

            if (!TryGetSliderGeometry(out left, out right, out centerY))
            {
                return;
            }

            float fade = Math.Max(0f, Math.Min(1f, (_open - 0.4f) / 0.6f));
            if (fade <= 0.02f)
            {
                return;
            }

            float knobRadius = ScaleF(KnobSize) / 2f;
            float trackHeight = ScaleF(TrackSize);
            float usable = right - left - knobRadius * 2f;
            float ratio = Math.Max(0f, Math.Min(1f, _dim.Brightness / 100f));
            float knobX = left + knobRadius + usable * ratio;

            RectangleF track = new RectangleF(left, centerY - trackHeight / 2f, right - left, trackHeight);

            using (GraphicsPath path = Theme.RoundedRect(track, trackHeight / 2f))
            using (SolidBrush brush = new SolidBrush(Fade(Track, fade)))
            {
                g.FillPath(brush, path);
            }

            float filled = knobX - left;
            if (filled > 0.5f)
            {
                RectangleF fill = new RectangleF(
                    left,
                    centerY - trackHeight / 2f,
                    Math.Max(trackHeight, filled),
                    trackHeight);

                using (GraphicsPath path = Theme.RoundedRect(fill, trackHeight / 2f))
                using (SolidBrush brush = new SolidBrush(Fade(Accent, Math.Max(0.3f, fade))))
                {
                    g.FillPath(brush, path);
                }
            }

            using (SolidBrush ring = new SolidBrush(Fade(Color.White, Math.Max(0.35f, fade))))
            {
                g.FillEllipse(
                    ring,
                    knobX - knobRadius,
                    centerY - knobRadius,
                    knobRadius * 2f,
                    knobRadius * 2f);
            }

            float coreRadius = Math.Max(1f, knobRadius - ScaleF(2f));
            using (SolidBrush core = new SolidBrush(Fade(Accent, Math.Max(0.35f, fade))))
            {
                g.FillEllipse(
                    core,
                    knobX - coreRadius,
                    centerY - coreRadius,
                    coreRadius * 2f,
                    coreRadius * 2f);
            }

            float valueLeft = ClientSize.Width - ScaleF(RightPad + CloseWidth + CloseGap + ValueWidth);
            Rectangle box = new Rectangle(
                (int)Math.Round(valueLeft),
                0,
                (int)Math.Round(ScaleF(ValueWidth)),
                ClientSize.Height);

            Color ink = _dim.Suspended ? InkMuted : Ink;

            TextRenderer.DrawText(
                g,
                _dim.Brightness + "%",
                _fontValue,
                box,
                Fade(ink, Math.Max(0.3f, fade)),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        private bool TryGetSliderGeometry(out float left, out float right, out float centerY)
        {
            left = 0f;
            right = 0f;
            centerY = 0f;

            if (ClientSize.Width < Scale(118))
            {
                return false;
            }

            left = ScaleF(HandleWidth) + ScaleF(1f);
            right = ClientSize.Width - ScaleF(RightPad + CloseWidth + CloseGap + ValueWidth + Gap);
            centerY = ClientSize.Height / 2f;

            return right - left > ScaleF(14f);
        }

        private float IconCenterX()
        {
            float idle = ScaleF(IdleWidth) / 2f;
            float open = ScaleF(HandleWidth) / 2f;
            float t = SmoothStep(_open);
            return idle + (open - idle) * t;
        }

        private static float SmoothStep(float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return t * t * (3f - 2f * t);
        }

        private static Color Fade(Color color, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));

            return Color.FromArgb(
                (int)Math.Round(Panel.R + (color.R - Panel.R) * amount),
                (int)Math.Round(Panel.G + (color.G - Panel.G) * amount),
                (int)Math.Round(Panel.B + (color.B - Panel.B) * amount));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovering = true;
            _closeDelay.Stop();

            if (!_expanded)
            {
                Open();
            }

            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovering = false;

            if (!_pinned && !_sliderActive && !_dragArmed)
            {
                _closeDelay.Start();
            }

            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                float left;
                float right;
                float centerY;

                if (_expanded
                    && TryGetSliderGeometry(out left, out right, out centerY)
                    && e.X >= left - Scale(1)
                    && e.X <= right + Scale(8))
                {
                    _sliderActive = true;
                    Capture = true;
                    SetSliderFromX(e.X);
                    return;
                }

                _dragArmed = true;
                _dragMoved = false;
                _dragStartCursor = Cursor.Position;
                _dragStartAnchor = _anchorX;
                Capture = true;
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_sliderActive)
            {
                SetSliderFromX(e.X);
                return;
            }

            if (_dragArmed)
            {
                int dx = Cursor.Position.X - _dragStartCursor.X;

                if (!_dragMoved && Math.Abs(dx) > 3)
                {
                    _dragMoved = true;
                }

                if (_dragMoved)
                {
                    _anchorX = ClampAnchorX(_dragStartAnchor + dx);
                    ApplyLayout();
                }

                return;
            }

            Cursor cursor = Cursors.Default;
            float left;
            float right;
            float centerY;

            if (_expanded
                && TryGetSliderGeometry(out left, out right, out centerY)
                && e.X >= left - Scale(1)
                && e.X <= right + Scale(8))
            {
                cursor = Cursors.Hand;
            }
            else if (_expanded)
            {
                cursor = Cursors.SizeWE;
            }

            if (Cursor != cursor)
            {
                Cursor = cursor;
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_sliderActive && e.Button == MouseButtons.Left)
            {
                _sliderActive = false;
                Capture = false;
                SaveState();

                if (!_hovering)
                {
                    _closeDelay.Start();
                }

                return;
            }

            if (_dragArmed && e.Button == MouseButtons.Left)
            {
                _dragArmed = false;
                Capture = false;

                if (_dragMoved)
                {
                    SaveState();
                }
                else
                {
                    TogglePin();
                }

                return;
            }

            base.OnMouseUp(e);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);

            if (DateTime.UtcNow < _ignoreDeactivateUntil)
            {
                return;
            }

            if (_expanded && !_pinned && !_sliderActive && !_dragArmed)
            {
                CollapseTab();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                UnpinAndClose();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void UnpinAndClose()
        {
            _pinned = false;
            _hovering = false;
            CollapseTab();
        }

        private void TogglePin()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastToggleUtc).TotalMilliseconds < 300d)
            {
                return;
            }

            _lastToggleUtc = now;
            _pinned = !_pinned;
            _settings.Expanded = _pinned;

            if (_pinned)
            {
                Open();
                SaveState();
                return;
            }

            SaveState();

            if (!_hovering)
            {
                CollapseTab();
            }
        }

        private void SetSliderFromX(int x)
        {
            float left;
            float right;
            float centerY;

            if (!TryGetSliderGeometry(out left, out right, out centerY))
            {
                return;
            }

            float knobRadius = ScaleF(KnobSize) / 2f;
            float from = left + knobRadius;
            float span = right - left - knobRadius * 2f;

            if (span <= 0f)
            {
                return;
            }

            float ratio = (x - from) / span;
            ratio = Math.Max(0f, Math.Min(1f, ratio));

            _dim.SetBrightness((int)Math.Round(ratio * 100f));
            Invalidate();
        }

        private int ClampAnchorX(int x)
        {
            Screen screen = Screen.FromPoint(new Point(x, _anchorTop + 1));
            Rectangle area = screen.WorkingArea;
            int half = (int)Math.Ceiling(ScaleF(OpenWidth) / 2f);
            int min = area.Left + half;
            int max = area.Right - half;

            if (max < min)
            {
                return area.Left + area.Width / 2;
            }

            return Math.Max(min, Math.Min(x, max));
        }

        private void RestorePosition()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int anchor = area.Left + area.Width / 2;

            if (_settings.X != int.MinValue)
            {
                anchor = _settings.X;
                area = Screen.FromPoint(new Point(anchor, area.Top + 1)).WorkingArea;
            }

            _anchorTop = area.Top;
            _anchorX = ClampAnchorX(anchor);
        }

        private void SaveState()
        {
            _settings.Brightness = _dim.Brightness;
            _settings.Suspended = _dim.Suspended;
            _settings.Expanded = _pinned;
            _settings.X = _anchorX;
            _settings.Y = _anchorTop;
            _settings.Save();
        }

        private int Scale(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        private float ScaleF(float value)
        {
            return value * _scale;
        }

        private sealed class RoundCloseButton : Button
        {
            protected override bool ShowFocusCues { get { return false; } }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                if (Width <= 0 || Height <= 0) return;
                using (GraphicsPath circle = new GraphicsPath())
                {
                    circle.AddEllipse(ClientRectangle);
                    Region previous = Region;
                    Region = new Region(circle);
                    if (previous != null) previous.Dispose();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animate.Tick -= OnAnimateTick;
                _animate.Dispose();

                _closeDelay.Tick -= OnCloseDelayTick;
                _closeDelay.Dispose();

                Region region = Region;
                if (region != null)
                {
                    region.Dispose();
                }

                _fontValue.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
