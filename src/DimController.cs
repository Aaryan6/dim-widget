using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace DimWidget
{
    internal sealed class DimController : IDisposable
    {
        private const double MaxOpacity = 0.94d;

        private readonly List<OverlayForm> _overlays = new List<OverlayForm>();
        private readonly Timer _watchdog;
        private string _screenSignature;
        private int _brightness;
        private bool _suspended;
        private bool _disposed;

        public event EventHandler Changed;
        public event EventHandler TopmostTick;

        public DimController(int brightness, bool suspended)
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
            int next = Settings.Clamp(value);
            if (next == _brightness)
            {
                return;
            }

            _brightness = next;
            Apply();
            RaiseChanged();
        }

        public void Nudge(int delta)
        {
            SetBrightness(_brightness + delta);
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
