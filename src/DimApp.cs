using System;
using System.Threading;
using System.Windows.Forms;

namespace DimWidget
{
    internal sealed class DimApp : ApplicationContext
    {
        private readonly Settings _settings;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _pauseItem;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly KeyboardHook _keys;
        private readonly MouseWheelHook _wheel;
        private readonly DesktopWatcher _desktop;
        private readonly System.Windows.Forms.Timer _desktopTimer;
        private readonly Control _mainSync;
        private readonly Thread _showListener;
        private UiHost _ui;
        private string _trayText;
        private int _offDesktopTicks;
        private int _followAttempts;
        private bool _onDesktop = true;
        private DateTime _lastFollowUtc = DateTime.MinValue;
        private volatile bool _running = true;
        private bool _shutdownStarted;

        public DimApp(Settings settings, EventWaitHandle showEvent, bool startedByWindows)
        {
            _settings = settings;

            _mainSync = new Control();
            IntPtr mainHandle = _mainSync.Handle;
            GC.KeepAlive(mainHandle);

            _ui = CreateUi();

            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;
            _menu.Opening += OnMenuOpening;

            ToolStripMenuItem showItem = new ToolStripMenuItem("Show controls");
            showItem.Click += OnMenuShow;
            _menu.Items.Add(showItem);

            _pauseItem = new ToolStripMenuItem("Pause dimming");
            _pauseItem.Click += OnMenuPause;
            _menu.Items.Add(_pauseItem);

            ToolStripMenuItem presets = new ToolStripMenuItem("Brightness");
            presets.DropDownItems.Add(CreatePreset("100%", 100));
            presets.DropDownItems.Add(CreatePreset("75%", 75));
            presets.DropDownItems.Add(CreatePreset("50%", 50));
            presets.DropDownItems.Add(CreatePreset("25%", 25));
            _menu.Items.Add(presets);

            _autoStartItem = new ToolStripMenuItem("Start with Windows");
            _autoStartItem.Click += OnMenuAutoStart;
            _menu.Items.Add(_autoStartItem);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem quitItem = new ToolStripMenuItem("Quit Dim");
            quitItem.Click += OnMenuQuit;
            _menu.Items.Add(quitItem);

            _tray = new NotifyIcon();
            _tray.Icon = Theme.CreateTrayIcon();
            _tray.Text = "Dim";
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.MouseClick += OnTrayClick;

            _keys = new KeyboardHook();
            _keys.Pressed += OnHotkey;
            _keys.Install();

            _wheel = new MouseWheelHook();
            _wheel.DimmingWheel += OnDimmingWheel;
            _wheel.Install();

            _desktop = new DesktopWatcher();
            _desktopTimer = new System.Windows.Forms.Timer();
            _desktopTimer.Interval = 80;
            _desktopTimer.Tick += OnDesktopTick;

            // A fresh launch always starts as a notch; desktop switches may
            // still preserve the current expanded state in RestartUi.
            if (!_ui.Start(true))
            {
                DebugLog.Write("start failed: ui thread did not come up");
            }

            UpdateTrayText();

            _showListener = new Thread(ShowListenerLoop);
            _showListener.IsBackground = true;
            _showListener.Name = "DimWidget.ShowListener";
            _showListener.Start(showEvent);

            if (_desktop.Available)
            {
                _desktopTimer.Start();
            }

            if (settings.FirstRun && !startedByWindows)
            {
                settings.FirstRun = false;
                settings.Save();

                _tray.ShowBalloonTip(
                    6000,
                    "Dim is running",
                    "Grab the tab at the top of the screen, or use Ctrl+Alt+scroll wheel.",
                    ToolTipIcon.None);
            }
        }

        private UiHost CreateUi()
        {
            UiHost ui = new UiHost(_settings);
            ui.Changed += OnUiChanged;
            ui.QuitRequested += delegate { OnMain(Shutdown); };
            return ui;
        }

        private void ShowListenerLoop(object state)
        {
            EventWaitHandle handle = (EventWaitHandle)state;

            while (_running)
            {
                try
                {
                    if (handle.WaitOne(400) && _running)
                    {
                        Post(delegate()
                        {
                            WidgetForm widget = _ui.Widget;
                            if (widget != null)
                            {
                                widget.ExpandAndFocus();
                            }
                        });
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private void Post(Action action)
        {
            UiHost ui = _ui;
            if (ui != null)
            {
                ui.Post(action);
            }
        }

        private void OnMain(Action action)
        {
            if (_mainSync.IsHandleCreated)
            {
                try
                {
                    _mainSync.BeginInvoke(action);
                    return;
                }
                catch (Exception)
                {
                }
            }

            action();
        }

        private void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            Post(delegate()
            {
                WidgetForm widget = _ui.Widget;
                if (widget == null)
                {
                    return;
                }

                if (widget.Expanded)
                {
                    widget.Collapse();
                    return;
                }

                if ((DateTime.UtcNow - widget.LastCollapsedUtc).TotalMilliseconds < 400d)
                {
                    return;
                }

                widget.ExpandAndFocus();
            });
        }

        private ToolStripMenuItem CreatePreset(string text, int value)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Tag = value;
            item.Click += OnMenuPreset;
            return item;
        }

        private void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            UiHost ui = _ui;
            _pauseItem.Checked = ui != null && ui.Suspended;
            _autoStartItem.Checked = _settings.AutoStart;
        }

        private void OnMenuShow(object sender, EventArgs e)
        {
            Post(delegate()
            {
                WidgetForm widget = _ui.Widget;
                if (widget != null)
                {
                    widget.ExpandAndFocus();
                }
            });
        }

        private void OnMenuPause(object sender, EventArgs e)
        {
            Post(delegate()
            {
                DimController dim = _ui.Dim;
                if (dim != null)
                {
                    dim.ToggleSuspended();
                }
            });
        }

        private void OnMenuPreset(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null || !(item.Tag is int))
            {
                return;
            }

            SetBrightness((int)item.Tag);
        }

        private void OnMenuAutoStart(object sender, EventArgs e)
        {
            OnMain(delegate()
            {
                _settings.AutoStart = !_settings.AutoStart;
            });
        }

        private void OnMenuQuit(object sender, EventArgs e)
        {
            OnMain(Shutdown);
        }

        private void OnHotkey(int id)
        {
            switch (id)
            {
                case KeyboardHook.Brighter:
                    Nudge(5);
                    break;

                case KeyboardHook.Dimmer:
                    Nudge(-5);
                    break;

                case KeyboardHook.Reset:
                    SetBrightness(100);
                    break;

                case KeyboardHook.Pause:
                    Post(delegate()
                    {
                        DimController dim = _ui.Dim;
                        if (dim != null)
                        {
                            dim.ToggleSuspended();
                        }
                    });
                    break;
            }
        }

        private void OnDimmingWheel(int delta)
        {
            Nudge(delta > 0 ? 4 : -4);
        }

        private void Nudge(int delta)
        {
            Post(delegate()
            {
                DimController dim = _ui.Dim;
                if (dim != null)
                {
                    dim.Nudge(delta);
                }
            });
        }

        private void SetBrightness(int value)
        {
            Post(delegate()
            {
                DimController dim = _ui.Dim;
                if (dim != null)
                {
                    dim.SetBrightness(value);
                }
            });
        }

        private void OnUiChanged(object sender, EventArgs e)
        {
            OnMain(UpdateTrayText);
        }

        private void UpdateTrayText()
        {
            if (_shutdownStarted)
            {
                return;
            }

            UiHost ui = _ui;
            string text = (ui != null && ui.Suspended)
                ? "Dim · paused"
                : "Dim · " + (ui == null ? 0 : ui.Brightness) + "%";

            if (text == _trayText)
            {
                return;
            }

            _trayText = text;

            try
            {
                _tray.Text = text;
            }
            catch (Exception)
            {
            }
        }

        private void OnDesktopTick(object sender, EventArgs e)
        {
            if (_shutdownStarted)
            {
                return;
            }

            UiHost ui = _ui;
            if (ui == null)
            {
                return;
            }

            IntPtr probe = ui.WidgetHandle;
            if (probe == IntPtr.Zero)
            {
                return;
            }

            if (_desktop.IsOnCurrentDesktop(probe))
            {
                if (!_onDesktop)
                {
                    _onDesktop = true;
                    DebugLog.Write("desktop check: window is on screen (hwnd=" + probe + ")");
                }

                _offDesktopTicks = 0;
                _followAttempts = 0;
                return;
            }

            if (_onDesktop)
            {
                _onDesktop = false;
                DebugLog.Write("desktop check: window is off screen (hwnd=" + probe + ")");
            }

            _offDesktopTicks++;
            if (_offDesktopTicks < 2)
            {
                return;
            }

            if ((DateTime.UtcNow - _lastFollowUtc).TotalMilliseconds < (_followAttempts >= 6 ? 5000d : 500d))
            {
                return;
            }

            _offDesktopTicks = 0;
            _lastFollowUtc = DateTime.UtcNow;
            _followAttempts++;

            RestartUi();
        }

        // The user switched virtual desktops. Windows pins a window to the
        // desktop its thread was born on, and there is no supported call to move
        // it, so the visible half of the app is rebuilt on a brand new thread,
        // which lands on the desktop currently on screen.
        private void RestartUi()
        {
            int x;
            int y;
            bool expanded;

            _ui.CaptureState(out x, out y, out expanded);

            DebugLog.Write("restart captured expanded=" + expanded + " at " + x + "," + y
                + " followAttempts=" + _followAttempts);

            _settings.Brightness = _ui.Brightness;
            _settings.Suspended = _ui.Suspended;
            _settings.X = x;
            _settings.Y = y;
            _settings.Expanded = expanded;

            UiHost previous = _ui;
            previous.Changed -= OnUiChanged;
            previous.Stop();
            previous.Dispose();

            _ui = CreateUi();

            if (!_ui.Start(false))
            {
                DebugLog.Write("restart failed: ui thread did not come up");
            }

            UpdateTrayText();
        }

        private void Shutdown()
        {
            if (_shutdownStarted)
            {
                return;
            }

            _shutdownStarted = true;
            _running = false;

            _desktopTimer.Stop();
            _desktopTimer.Tick -= OnDesktopTick;
            _desktopTimer.Dispose();

            UiHost ui = _ui;
            int x;
            int y;
            bool expanded;

            if (ui != null)
            {
                ui.CaptureState(out x, out y, out expanded);
                _settings.Brightness = ui.Brightness;
                _settings.Suspended = ui.Suspended;
            }
            else
            {
                x = _settings.X;
                y = _settings.Y;
                expanded = _settings.Expanded;
            }

            _settings.Expanded = expanded;
            _settings.X = x;
            _settings.Y = y;
            _settings.Save();

            if (ui != null)
            {
                ui.Changed -= OnUiChanged;
                ui.Stop();
                ui.Dispose();
                _ui = null;
            }

            try
            {
                _tray.Visible = false;
                _tray.Dispose();
                _menu.Dispose();
            }
            catch (Exception)
            {
            }

            _wheel.Dispose();
            _keys.Dispose();
            _desktop.Dispose();

            try
            {
                _mainSync.Dispose();
            }
            catch (Exception)
            {
            }

            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Shutdown();
            }

            base.Dispose(disposing);
        }
    }
}
