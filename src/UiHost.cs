using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace DimWidget
{
    // Owns the windows the user actually sees. Windows belong to the virtual
    // desktop of the thread that created them, so this host can be torn down and
    // respawned on a fresh thread: a new thread has no desktop affinity yet and
    // therefore builds its windows on whatever desktop is on screen right now.
    internal sealed class UiHost : IDisposable
    {
        private readonly Settings _settings;

        private Thread _thread;
        private PumpForm _pump;
        private DimController _dim;
        private WidgetForm _widget;
        private bool _forceCollapsed;
        private volatile bool _running;
        private volatile bool _disposed;

        private int _brightness;
        private int _suspended;
        private long _widgetHandle;

        public UiHost(Settings settings)
        {
            _settings = settings;
            _brightness = settings.Brightness;
            _suspended = settings.Suspended ? 1 : 0;
        }

        public event EventHandler Changed;
        public event EventHandler QuitRequested;

        public bool Running
        {
            get { return _running; }
        }

        public int Brightness
        {
            get { return _brightness; }
        }

        public bool Suspended
        {
            get { return _suspended != 0; }
        }

        public IntPtr WidgetHandle
        {
            get { return (IntPtr)Interlocked.Read(ref _widgetHandle); }
        }

        public DimController Dim
        {
            get { return _dim; }
        }

        public WidgetForm Widget
        {
            get { return _widget; }
        }

        public bool Start(bool forceCollapsed)
        {
            _forceCollapsed = forceCollapsed;
            _running = true;

            AutoResetEvent ready = new AutoResetEvent(false);
            Thread thread = new Thread(Pump);
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Name = "DimWidget.Ui";
            _thread = thread;
            thread.Start(ready);

            return ready.WaitOne(8000) && _running;
        }

        private void Pump(object state)
        {
            AutoResetEvent ready = (AutoResetEvent)state;
            bool signalled = false;

            try
            {
                _pump = new PumpForm();
                _pump.Show();

                DimController dim = new DimController(_settings.Brightness, _settings.Suspended);
                WidgetForm widget = new WidgetForm(dim, _settings);
                widget.QuitRequested += delegate
                {
                    EventHandler handler = QuitRequested;
                    if (handler != null) handler(this, EventArgs.Empty);
                };

                _dim = dim;
                _widget = widget;
                _brightness = dim.Brightness;
                _suspended = dim.Suspended ? 1 : 0;
                Interlocked.Exchange(ref _widgetHandle, widget.Handle.ToInt64());

                widget.Start(_forceCollapsed);
                dim.Changed += OnDimChanged;
                dim.TopmostTick += OnTopmostTick;

                DebugLog.Write("uihost ready hwnd=" + widget.Handle + " expanded=" + widget.Expanded);

                signalled = true;
                ready.Set();
                Application.Run(_pump);
            }
            catch (Exception)
            {
                if (!signalled)
                {
                    ready.Set();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _widgetHandle, 0);
                _running = false;

                WidgetForm widget = _widget;
                DimController dim = _dim;
                _widget = null;
                _dim = null;

                if (widget != null)
                {
                    try
                    {
                        widget.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }

                if (dim != null)
                {
                    dim.Changed -= OnDimChanged;
                    dim.TopmostTick -= OnTopmostTick;

                    try
                    {
                        dim.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private void OnDimChanged(object sender, EventArgs e)
        {
            DimController dim = _dim;
            if (dim != null)
            {
                _brightness = dim.Brightness;
                _suspended = dim.Suspended ? 1 : 0;
            }

            WidgetForm widget = _widget;
            if (widget != null)
            {
                // Showing an overlay after 100% brightness or pause can put it
                // above the notch. Restore order now, before the next repaint,
                // rather than waiting for the periodic topmost check.
                widget.PromoteSelf();
                widget.SyncFromController();
            }

            EventHandler handler = Changed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void OnTopmostTick(object sender, EventArgs e)
        {
            WidgetForm widget = _widget;
            if (widget != null)
            {
                widget.PromoteSelf();
            }
        }

        public void Post(Action action)
        {
            if (_disposed)
            {
                return;
            }

            PumpForm pump = _pump;
            if (pump == null)
            {
                return;
            }

            try
            {
                pump.BeginInvoke(action);
            }
            catch (Exception)
            {
            }
        }

        public void CaptureState(out int x, out int y, out bool expanded)
        {
            x = _settings.X;
            y = _settings.Y;
            expanded = _settings.Expanded;

            PumpForm pump = _pump;
            WidgetForm widget = _widget;
            if (pump == null || widget == null)
            {
                return;
            }

            try
            {
                // Snapshot on the UI thread into locals the closure may capture.
                int anchorX = x;
                int anchorY = y;
                bool isExpanded = expanded;

                pump.Invoke(new Action(delegate()
                {
                    anchorX = widget.AnchorX;
                    anchorY = widget.AnchorTop;
                    isExpanded = widget.Expanded;
                }));

                x = anchorX;
                y = anchorY;
                expanded = isExpanded;
            }
            catch (Exception)
            {
            }
        }

        public void Stop()
        {
            _running = false;

            PumpForm pump = _pump;
            if (pump != null)
            {
                try
                {
                    pump.BeginInvoke(new Action(delegate() { pump.Close(); }));
                }
                catch (Exception)
                {
                }
            }

            Thread thread = _thread;
            _thread = null;
            _pump = null;

            if (thread != null && thread.IsAlive)
            {
                thread.Join(4000);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
        }

        private sealed class PumpForm : Form
        {
            public PumpForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                Bounds = new Rectangle(-4000, -4000, 1, 1);
                Opacity = 0d;
                TabStop = false;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    // The invisible message pump must not get a switcher thumbnail.
                    cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
                    return cp;
                }
            }

            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }
        }
    }
}
