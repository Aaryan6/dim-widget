using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace DimWidget
{
    internal sealed class DimController : IDisposable
    {
        private const double MaxOpacity = 0.94d;
        internal const int HardwareMinimumPosition = 20;

        private readonly List<OverlayForm> _overlays = new List<OverlayForm>();
        private readonly Timer _watchdog;
        private string _screenSignature;
        private int _brightness;
        private bool _suspended;
        private bool _disposed;

        public WindowsBrightness SystemBrightness { get; private set; }

        public event EventHandler Changed;
        public event EventHandler TopmostTick;

        public DimController(int brightness, bool suspended)
            : this(brightness, suspended, new WmiBrightnessBackend()) { }

        internal DimController(int brightness, bool suspended, IWindowsBrightnessBackend brightnessBackend)
        {
            _brightness = Settings.Clamp(brightness);
            _suspended = suspended;
            _screenSignature = ComputeScreenSignature();
            BuildOverlays();

            _watchdog = new Timer();
            _watchdog.Interval = 1200;
            _watchdog.Tick += OnWatchdog;
            _watchdog.Start();

            Apply();
            SystemBrightness = new WindowsBrightness(brightnessBackend);
            SystemBrightness.Changed += OnSystemBrightnessChanged;
        }

        private void OnSystemBrightnessChanged(object sender, EventArgs e)
        {
            // Brightness keys and Windows settings can lift the screen out of
            // the extra-dim range without leaving an old overlay behind.
            if (SystemBrightness.Available && SystemBrightness.Brightness > SystemBrightness.Minimum
                && _brightness < 100)
            {
                _brightness = 100;
                Apply();
            }
            RaiseChanged();
        }

        public int ControlBrightness
        {
            get
            {
                if (!SystemBrightness.Available) return _suspended ? 100 : _brightness;
                if (!_suspended && _brightness < 100)
                    return (int)Math.Round(_brightness * HardwareMinimumPosition / 100d);
                double span = Math.Max(1, SystemBrightness.Maximum - SystemBrightness.Minimum);
                return HardwareMinimumPosition + (int)Math.Round(
                    (SystemBrightness.Brightness - SystemBrightness.Minimum) * (100 - HardwareMinimumPosition) / span);
            }
        }

        public string BrightnessText
        {
            get
            {
                if (!SystemBrightness.Available) return (_suspended ? 100 : _brightness) + "%";
                if (!_suspended && _brightness < 100) return "-" + (100 - _brightness) + "%";
                return SystemBrightness.Brightness + "%";
            }
        }

        public int Brightness
        {
            get { return _brightness; }
        }

        public bool Suspended
        {
            get { return _suspended; }
        }

        // Fraction of the desktop that still shows through the dim overlay.
        // The widget paints a blurred copy of the screen and has to re-grade it
        // when this changes, so the copy matches the wallpaper around it.
        public double VisibleFactor
        {
            get { return _suspended ? 1d : 1d - (100d - _brightness) / 100d * MaxOpacity; }
        }

        public void SetBrightness(int value)
        {
            SetBrightness(value, false);
        }

        internal void SetSliderBrightness(double value)
        {
            SetBrightness(value, true);
        }

        private static int RoundBrightness(double value, bool snap)
        {
            return snap ? (int)(Math.Round(value / 5d, MidpointRounding.AwayFromZero) * 5d)
                : (int)Math.Round(value);
        }

        private void SetBrightness(double value, bool snap)
        {
            double position = Math.Max(0d, Math.Min(100d, value));
            int next = RoundBrightness(position, snap);
            if (SystemBrightness.Available)
            {
                int hardware;
                if (position < HardwareMinimumPosition)
                {
                    hardware = SystemBrightness.Minimum;
                    next = RoundBrightness(position * 100d / HardwareMinimumPosition, snap);
                }
                else
                {
                    hardware = RoundBrightness(SystemBrightness.Minimum
                        + (position - HardwareMinimumPosition) * (SystemBrightness.Maximum - SystemBrightness.Minimum)
                        / (100 - HardwareMinimumPosition), snap);
                    hardware = Math.Max(SystemBrightness.Minimum, Math.Min(SystemBrightness.Maximum, hardware));
                    next = 100;
                }
                // Update Windows first; its optimistic event clears any old
                // overlay before the requested extra dimming is applied below.
                SystemBrightness.SetBrightness(hardware);
            }
            bool resume = _suspended;
            if (next == _brightness && !resume)
            {
                return;
            }

            _brightness = next;
            _suspended = false;
            Apply();
            RaiseChanged();
        }

        public void Nudge(int delta)
        {
            SetBrightness(ControlBrightness + delta);
        }

        public void SetSuspended(bool value)
        {
            if (value == _suspended)
            {
                return;
            }

            _suspended = value;
            Apply();
            RaiseChanged();
        }

        public void ToggleSuspended()
        {
            SetSuspended(!_suspended);
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void BuildOverlays()
        {
            List<OverlayForm> previous = new List<OverlayForm>(_overlays);
            _overlays.Clear();

            foreach (Screen screen in Screen.AllScreens)
            {
                _overlays.Add(new OverlayForm(screen.Bounds));
            }

            _screenSignature = ComputeScreenSignature();

            // Old overlays go away only after the new ones exist, so switching
            // desktops never shows an undimmed frame.
            foreach (OverlayForm overlay in previous)
            {
                overlay.Dispose();
            }
        }

        private void Apply()
        {
            double alpha = 0d;

            if (!_suspended)
            {
                alpha = (100d - _brightness) / 100d * MaxOpacity;
            }

            foreach (OverlayForm overlay in _overlays)
            {
                overlay.ApplyAlpha(alpha);
            }
        }

        private void OnWatchdog(object sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            string signature = ComputeScreenSignature();
            if (!string.Equals(signature, _screenSignature, StringComparison.Ordinal))
            {
                BuildOverlays();
                Apply();
            }

            foreach (OverlayForm overlay in _overlays)
            {
                overlay.Promote();
            }

            EventHandler handler = TopmostTick;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private static string ComputeScreenSignature()
        {
            StringBuilder sb = new StringBuilder();

            foreach (Screen screen in Screen.AllScreens)
            {
                sb.Append(screen.Bounds.X).Append(',')
                  .Append(screen.Bounds.Y).Append(',')
                  .Append(screen.Bounds.Width).Append(',')
                  .Append(screen.Bounds.Height).Append(';');
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            SystemBrightness.Changed -= OnSystemBrightnessChanged;
            SystemBrightness.Dispose();

            _watchdog.Stop();
            _watchdog.Tick -= OnWatchdog;
            _watchdog.Dispose();

            foreach (OverlayForm overlay in _overlays)
            {
                overlay.Dispose();
            }

            _overlays.Clear();
        }
    }
}
