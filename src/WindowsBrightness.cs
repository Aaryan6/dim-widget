using System;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DimWidget
{
    internal sealed class BrightnessState
    {
        public string InstanceName;
        public int Value;
        public byte[] Levels;

        public int Minimum
        {
            get { int value = 0; if (Levels != null && Levels.Length > 0) { value = 100; foreach (byte level in Levels) value = Math.Min(value, level); } return value; }
        }

        public int Maximum
        {
            get { int value = 100; if (Levels != null && Levels.Length > 0) { value = 0; foreach (byte level in Levels) value = Math.Max(value, level); } return value; }
        }

        public int NearestLevel(int value)
        {
            int closest = Settings.Clamp(value);
            if (Levels == null || Levels.Length == 0) return closest;
            int distance = int.MaxValue;
            foreach (byte level in Levels)
            {
                int next = Math.Abs(level - Settings.Clamp(value));
                if (next < distance) { closest = level; distance = next; }
            }
            return closest;
        }
    }

    internal interface IWindowsBrightnessBackend
    {
        BrightnessState Read();
        void Write(BrightnessState display, int value);
    }

    internal sealed class WmiBrightnessBackend : IWindowsBrightnessBackend
    {
        public BrightnessState Read()
        {
            using (ManagementObjectSearcher search = new ManagementObjectSearcher(
                @"root\wmi", "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE"))
            using (ManagementObjectCollection monitors = search.Get())
            {
                foreach (ManagementObject monitor in monitors)
                {
                    using (monitor)
                    {
                        return new BrightnessState {
                            InstanceName = (string)monitor["InstanceName"],
                            Value = Convert.ToInt32(monitor["CurrentBrightness"]),
                            Levels = (byte[])monitor["Level"]
                        };
                    }
                }
            }
            return null;
        }

        public void Write(BrightnessState display, int value)
        {
            using (ManagementObjectSearcher search = new ManagementObjectSearcher(
                @"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE"))
            using (ManagementObjectCollection methods = search.Get())
            {
                foreach (ManagementObject method in methods)
                {
                    using (method)
                    {
                        if (!string.Equals((string)method["InstanceName"], display.InstanceName,
                            StringComparison.OrdinalIgnoreCase)) continue;
                        object result = method.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)value });
                        if (Convert.ToUInt32(result) != 0)
                            throw new InvalidOperationException("Windows rejected the brightness change.");
                        return;
                    }
                }
            }
            throw new InvalidOperationException("The laptop display is no longer available.");
        }
    }

    // All public state belongs to the UI thread. WMI runs off that thread,
    // with one operation at a time and only the latest slider value queued.
    internal sealed class WindowsBrightness : IDisposable
    {
        private readonly IWindowsBrightnessBackend _backend;
        private readonly Timer _timer;
        private readonly Control _dispatcher;
        private BrightnessState _display;
        private int? _pending;
        private bool _busy;
        private volatile bool _disposed;

        public bool Available { get { return _display != null; } }
        public int Brightness { get; private set; }
        public int Minimum { get { return _display == null ? 0 : _display.Minimum; } }
        public int Maximum { get { return _display == null ? 100 : _display.Maximum; } }
        public string Error { get; private set; }
        public event EventHandler Changed;

        public WindowsBrightness() : this(new WmiBrightnessBackend()) { }

        internal WindowsBrightness(IWindowsBrightnessBackend backend)
        {
            _backend = backend;
            _dispatcher = new Control();
            IntPtr handle = _dispatcher.Handle;
            _timer = new Timer { Interval = 1500 };
            _timer.Tick += OnTimer;
            _timer.Start();
            Refresh();
        }

        public void SetBrightness(int value)
        {
            if (_disposed || !Available) return;
            int next = _display.NearestLevel(value);
            if (next == Brightness) return;
            Brightness = next;
            _pending = next;
            RaiseChanged();
            // Debounce dragging before calling the display driver.
            _timer.Stop();
            _timer.Interval = 75;
            _timer.Start();
        }

        private void OnTimer(object sender, EventArgs e) { Refresh(); }

        internal void Refresh()
        {
            if (_disposed || _busy) return;
            _busy = true;
            _timer.Interval = 1500;
            int? pending = _pending;
            BrightnessState display = _display;
            _pending = null;
            Task<BrightnessState> operation = Task.Run(delegate {
                if (pending.HasValue && display != null) _backend.Write(display, pending.Value);
                return _backend.Read();
            });
            operation.ContinueWith(task => {
                // Marshal explicitly. Legacy .NET applications may not retain
                // a WinForms synchronization context across await continuations.
                if (_disposed) { var ignored = task.Exception; return; }
                try { _dispatcher.BeginInvoke(new Action(delegate { Complete(task); })); }
                catch (InvalidOperationException) { var ignored = task.Exception; }
            }, TaskScheduler.Default);
        }

        private void Complete(Task<BrightnessState> operation)
        {
            if (_disposed) { var ignored = operation.Exception; return; }
            try
            {
                BrightnessState actual = operation.GetAwaiter().GetResult();
                bool changed = Available != (actual != null) || Error != null;
                _display = actual;
                Error = null;
                if (!_pending.HasValue && actual != null)
                {
                    changed |= Brightness != actual.Value;
                    Brightness = actual.Value;
                }
                if (changed) RaiseChanged();
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                // Keep the last display after a failed write so the user can retry.
                // Do not present a failed optimistic value as the actual brightness.
                _pending = null;
                if (_display != null) Brightness = _display.Value;
                Error = "Windows brightness is unavailable. Try again.";
                DebugLog.Write("Windows brightness: " + ex.Message);
                RaiseChanged();
            }
            finally
            {
                _busy = false;
                if (!_disposed && _pending.HasValue)
                {
                    _timer.Interval = 75;
                    _timer.Start();
                }
            }
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            _dispatcher.Dispose();
        }
    }
}
