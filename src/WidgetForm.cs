using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DimWidget
{
    internal sealed class WidgetForm : Form
    {
        // Original compact notch geometry in 96-dpi units, attached to the
        // screen edge with square top corners and rounded bottom corners.
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
        private const float KnobSize = 13f;
        private const float TrackSize = 4f;

        private static readonly Color Panel = Color.FromArgb(12, 14, 16);
        private static readonly Color Ink = Color.FromArgb(244, 247, 248);
        private static readonly Color InkMuted = Color.FromArgb(195, 207, 215);
        private static readonly Color Accent = Color.FromArgb(204, 219, 228);
        private static readonly Color Track = Color.FromArgb(46, 255, 255, 255);

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
        private bool _translucent;
        private bool _paintingGlass;
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
            // GlassSurface supplies the single alpha-preserving window buffer.
            DoubleBuffered = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            KeyPreview = true;
            Text = "Dim";
            ClientSize = new Size(Scale(IdleWidth), Scale(IdleHeight));

            _fontValue = Theme.Ui(8.25f, FontStyle.Bold);

            _quitButton = new RoundCloseButton(this, _scale);
            _quitButton.AccessibleName = "Quit Dim";
            _quitButton.BackColor = Panel;
            _quitButton.ForeColor = Color.FromArgb(217, 224, 227);
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ConfigureGlass();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // UiHost creates the HWND while hidden. A layered window needs an
            // initial surface explicitly; invalidating the hidden HWND alone
            // does not guarantee WM_PAINT when a collapsed notch is shown.
            RenderGlass();
        }

        private void ConfigureGlass()
        {
            _translucent = !SystemInformation.HighContrast;
            Invalidate(true);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // Re-evaluate the solid fallback after theme or accessibility changes.
            if (m.Msg == 0x031E || m.Msg == 0x031A || m.Msg == 0x001A)
                ConfigureGlass();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // Keep the widget out of the shell's window switcher.
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED;
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
            Invalidate(true);
        }

        public void UpdateFromSettings()
        {
            Invalidate(true);
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

            Invalidate(true);
        }

        private float Radius
        {
            get
            {
                float radius = ScaleF(IdleRadius + (OpenRadius - IdleRadius) * _open);
                return Math.Min(radius, Math.Min(ClientSize.Width, ClientSize.Height) / 2f);
            }
        }

        private GraphicsPath BuildShape()
        {
            return BuildTabPath(new RectangleF(0f, 0f, ClientSize.Width, ClientSize.Height), Radius);
        }

        private static GraphicsPath BuildTabPath(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
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

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderGlass();
        }

        private void RenderGlass()
        {
            if (_paintingGlass || !IsHandleCreated || IsDisposed) return;
            _paintingGlass = true;
            try
            {
                GlassSurface.Present(Handle, Location, ClientSize, DrawContents);
            }
            finally { _paintingGlass = false; }
        }

        private void DrawContents(Graphics g)
        {
            DrawGlass(g);
            DrawIcon(g);
            DrawPanel(g);
            if (_quitButton != null && _expanded && _open >= 0.95f)
            {
                GraphicsState saved = g.Save();
                g.TranslateTransform(_quitButton.Left, _quitButton.Top);
                ((RoundCloseButton)_quitButton).DrawGlyph(g);
                g.Restore(saved);
            }
        }

        private void DrawGlass(Graphics g)
        {
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            if (width < 1 || height < 1) return;
            GraphicsState saved = g.Save();
            using (GraphicsPath plate = BuildShape())
            {
                g.SetClip(plate, CombineMode.Intersect);
                using (SolidBrush tint = new SolidBrush(Color.FromArgb(_translucent ? 205 : 255, Panel)))
                    g.FillPath(tint, plate);

                using (LinearGradientBrush body = new LinearGradientBrush(
                    new RectangleF(0f, 0f, width, height), Color.White, Color.Black, 90f))
                {
                    body.InterpolationColors = new ColorBlend {
                        Positions = new float[] { 0f, 0.17f, 0.46f, 0.53f, 0.86f, 1f },
                        Colors = new Color[] { Color.FromArgb(45, Color.White), Color.FromArgb(12, Color.White),
                            Color.FromArgb(0, Color.White), Color.FromArgb(10, Color.Black),
                            Color.FromArgb(24, Color.Black), Color.FromArgb(7, Color.White) }
                    };
                    g.FillPath(body, plate);
                }

                // Broad, slanted reflection from the approved glass reference.
                using (GraphicsPath reflection = new GraphicsPath())
                using (LinearGradientBrush shine = new LinearGradientBrush(
                    new RectangleF(0f, 0f, width, height * 0.65f),
                    Color.FromArgb(46, Color.White), Color.FromArgb(9, Color.White), 90f))
                {
                    reflection.AddPolygon(new PointF[] { new PointF(0f, 0f), new PointF(width, 0f),
                        new PointF(width, height * 0.28f), new PointF(0f, height * 0.58f) });
                    g.FillPath(shine, reflection);
                }
            }
            g.Restore(saved);

            float edge = ScaleF(1f);
            using (GraphicsPath rim = BuildTabPath(
                new RectangleF(edge, -edge, width - edge * 2f, height), Radius - edge))
            using (LinearGradientBrush silver = new LinearGradientBrush(
                new RectangleF(0f, 0f, width, height), Color.White, Color.White, 90f))
            {
                silver.InterpolationColors = new ColorBlend {
                    Positions = new float[] { 0f, 0.3f, 0.68f, 1f },
                    Colors = new Color[] { Color.FromArgb(218, Color.White), Color.FromArgb(78, Color.White),
                        Color.FromArgb(65, Color.White), Color.FromArgb(169, Color.White) }
                };
                using (Pen glow = new Pen(Color.FromArgb(28, Color.White), ScaleF(3f))) g.DrawPath(glow, rim);
                using (Pen line = new Pen(silver, ScaleF(0.85f))) g.DrawPath(line, rim);
            }
            float inset = ScaleF(2.8f);
            using (GraphicsPath inner = BuildTabPath(
                new RectangleF(inset, -inset, width - inset * 2f, height), Radius - inset))
            using (Pen line = new Pen(Color.FromArgb(34, Color.White), ScaleF(0.65f)))
                g.DrawPath(line, inner);
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

            // Retain Dim's split-disc mark in the cooler glass palette.
            using (GraphicsPath half = new GraphicsPath())
            {
                half.AddArc(circle, 90f, 180f);
                half.CloseFigure();

                Region previous = g.Clip;
                g.SetClip(half, CombineMode.Intersect);
                using (SolidBrush shade = new SolidBrush(Color.FromArgb(164, 194, 208)))
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

            RectangleF thumb = new RectangleF(knobX - knobRadius, centerY - knobRadius,
                knobRadius * 2f, knobRadius * 2f);
            using (SolidBrush shadow = new SolidBrush(Fade(Color.FromArgb(100, Color.Black), fade)))
            {
                RectangleF shadowBounds = thumb;
                shadowBounds.Offset(0f, ScaleF(1.2f));
                shadowBounds.Inflate(ScaleF(0.8f), ScaleF(0.8f));
                g.FillEllipse(shadow, shadowBounds);
            }
            using (LinearGradientBrush pearl = new LinearGradientBrush(thumb,
                Fade(Color.White, fade), Fade(Color.FromArgb(174, 192, 202), fade), 55f))
            using (Pen edge = new Pen(Fade(Color.FromArgb(210, Color.White), fade), ScaleF(0.8f)))
            {
                g.FillEllipse(pearl, thumb);
                g.DrawEllipse(edge, thumb);
            }

            float valueLeft = ClientSize.Width - ScaleF(RightPad + CloseWidth + CloseGap + ValueWidth);
            Rectangle box = new Rectangle(
                (int)Math.Round(valueLeft),
                0,
                (int)Math.Round(ScaleF(ValueWidth)),
                ClientSize.Height);

            Color ink = _dim.Suspended ? InkMuted : Ink;

            // GDI+ keeps the text alpha intact on the glass surface.
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            using (SolidBrush text = new SolidBrush(Fade(ink, fade)))
            {
                format.Alignment = StringAlignment.Far;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags |= StringFormatFlags.NoWrap;
                g.DrawString(_dim.Brightness + "%", _fontValue, text, box, format);
            }
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

            return Color.FromArgb((int)Math.Round(color.A * amount), color.R, color.G, color.B);
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
            private readonly WidgetForm _owner;
            private readonly float _scale;
            private bool _hovered;
            private bool _mousePressed;
            private bool _spacePressed;

            public RoundCloseButton(WidgetForm owner, float scale)
            {
                _owner = owner;
                _scale = scale;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.ResizeRedraw, true);
            }

            protected override bool ShowFocusCues { get { return false; } }

            protected override void OnPaintBackground(PaintEventArgs e) { }

            protected override void OnPaint(PaintEventArgs e)
            {
                // Retain Button's hit testing, keyboard and accessibility, but
                // draw its glyph into the parent's single layered surface.
                _owner.RenderGlass();
            }

            public void DrawGlyph(Graphics g)
            {
                bool pressed = Enabled && ((_mousePressed && _hovered) || _spacePressed);
                Color highlight = pressed ? Color.FromArgb(65, 156, 89, 89)
                    : Color.FromArgb(Enabled && _hovered ? 24 : 0, Color.White);
                using (SolidBrush fill = new SolidBrush(highlight))
                    g.FillEllipse(fill, new RectangleF(0f, 0f, Width - 1f, Height - 1f));

                float centerX = (ClientSize.Width - 1) / 2f;
                float centerY = (ClientSize.Height - 1) / 2f;
                float radius = 4f * _scale;
                using (Pen stroke = new Pen(ForeColor, 1f * _scale))
                {
                    stroke.StartCap = stroke.EndCap = LineCap.Round;
                    g.DrawLine(stroke, centerX - radius, centerY - radius,
                        centerX + radius, centerY + radius);
                    g.DrawLine(stroke, centerX + radius, centerY - radius,
                        centerX - radius, centerY + radius);
                }
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                _hovered = true;
                base.OnMouseEnter(e);
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                _hovered = false;
                base.OnMouseLeave(e);
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) _mousePressed = true;
                base.OnMouseDown(e);
                Invalidate();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) _mousePressed = false;
                base.OnMouseUp(e);
                Invalidate();
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                if (!Capture) _mousePressed = false;
                base.OnMouseCaptureChanged(e);
                Invalidate();
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space) _spacePressed = true;
                base.OnKeyDown(e);
                Invalidate();
            }

            protected override void OnKeyUp(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space) _spacePressed = false;
                base.OnKeyUp(e);
                Invalidate();
            }

            protected override void OnLostFocus(EventArgs e)
            {
                _spacePressed = false;
                base.OnLostFocus(e);
                Invalidate();
            }

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

                _fontValue.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
