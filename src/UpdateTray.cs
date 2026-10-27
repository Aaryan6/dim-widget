using System;
using System.Windows.Forms;

namespace DimWidget
{
    internal sealed class UpdateTray : IDisposable
    {
        private readonly UpdateManager _updates;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _menuItem;
        private readonly Action<Uri> _openPage;
        private Uri _notificationPage;
        private bool _disposed;

        public UpdateTray(UpdateManager updates, NotifyIcon tray, ToolStripMenuItem menuItem, Action<Uri> openPage)
        {
            _updates = updates;
            _tray = tray;
            _menuItem = menuItem;
            _openPage = openPage;
            _updates.UpdateAvailable += OnUpdateAvailable;
            _menuItem.Click += OnCheckForUpdates;
            _tray.BalloonTipClicked += OnNotificationClicked;
        }

        private void OnCheckForUpdates(object sender, EventArgs e)
        {
            if (_disposed) return;
            _ = _updates.CheckNowAsync();
            _openPage(UpdateChecker.DownloadPage);
        }

        private void OnUpdateAvailable(object sender, AvailableUpdate update)
        {
            if (_disposed) return;
            _notificationPage = update.DownloadPage;
            _tray.BalloonTipTitle = "Dim update available";
            _tray.BalloonTipText = "Dim " + update.DisplayVersion + " is ready. Click to open the download page.";
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(8000);
        }

        private void OnNotificationClicked(object sender, EventArgs e)
        {
            if (!_disposed && _notificationPage != null) _openPage(_notificationPage);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _updates.UpdateAvailable -= OnUpdateAvailable;
            _menuItem.Click -= OnCheckForUpdates;
            _tray.BalloonTipClicked -= OnNotificationClicked;
        }
    }
}
