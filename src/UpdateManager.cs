using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DimWidget
{
    internal sealed class UpdateNotificationState
    {
        private readonly string _path;
        private Version _lastNotified;

        public UpdateNotificationState() : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DimWidget", "update-notification.txt")) { }

        internal UpdateNotificationState(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path)) UpdateChecker.TryParseVersion(File.ReadAllText(path), out _lastNotified);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public bool ShouldNotify(AvailableUpdate update)
        {
            return _lastNotified == null || update.Version > _lastNotified;
        }

        public void MarkNotified(AvailableUpdate update)
        {
            _lastNotified = update.Version;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, update.Tag);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class UpdateManager : IDisposable
    {
        internal const int StartupDelayMilliseconds = 10000;
        internal const int DailyIntervalMilliseconds = 24 * 60 * 60 * 1000;
        private readonly UpdateChecker _checker;
        private readonly UpdateNotificationState _notifications;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly int _interval;
        private bool _checking;
        private bool _disposed;

        public event EventHandler<AvailableUpdate> UpdateAvailable;
        public AvailableUpdate LatestAvailable { get; private set; }

        public UpdateManager() : this(new UpdateChecker(), new UpdateNotificationState(),
            StartupDelayMilliseconds, DailyIntervalMilliseconds) { }

        internal UpdateManager(UpdateChecker checker, UpdateNotificationState notifications, int startupDelay, int interval)
        {
            _checker = checker;
            _notifications = notifications;
            _interval = interval;
            _timer = new System.Windows.Forms.Timer { Interval = startupDelay };
            _timer.Tick += OnTimer;
        }

        public void Start()
        {
            if (!_disposed) _timer.Start();
        }

        private void OnTimer(object sender, EventArgs e)
        {
            _timer.Stop();
            _timer.Interval = _interval;
            _timer.Start();
            _ = CheckNowAsync();
        }

        public async Task CheckNowAsync()
        {
            if (_disposed || _checking) return;
            _checking = true;
            try
            {
                AvailableUpdate update = await _checker.CheckAsync(_cancellation.Token);
                if (_disposed) return;
                LatestAvailable = update;
                if (update == null) return;
                EventHandler<AvailableUpdate> handler = UpdateAvailable;
                if (handler != null && _notifications.ShouldNotify(update))
                {
                    handler(this, update);
                    _notifications.MarkNotified(update);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Offline, rate-limited, or malformed responses must never interrupt dimming.
                DebugLog.Write("update check failed: " + ex.GetType().Name);
            }
            finally { _checking = false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTimer;
            _timer.Dispose();
            _cancellation.Cancel();
            _checker.Dispose();
            _cancellation.Dispose();
        }
    }
}
