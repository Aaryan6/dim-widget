using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DimWidget
{
    internal static class UpdateChecks
    {
        private static int _checks;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            _checks++;
        }

        private static string Release(string tag, string assetState = "uploaded", bool prerelease = false, bool draft = false)
        {
            return "{\"tag_name\":\"" + tag + "\",\"draft\":" + draft.ToString().ToLowerInvariant()
                + ",\"prerelease\":" + prerelease.ToString().ToLowerInvariant()
                + ",\"html_url\":\"file:///untrusted.exe\",\"assets\":[{\"name\":\"DimWidget.exe\",\"state\":\""
                + assetState + "\"}]}";
        }

        private static AvailableUpdate Fetch(string json, Version current = null)
        {
            using (UpdateChecker checker = new UpdateChecker(new FakeHttp { Json = json }, current ?? new Version(1, 0, 6)))
                return checker.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        private static void PumpUntil(Func<bool> condition, int timeout = 3000)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeout);
            while (!condition() && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); }
            Check(condition(), "Timed out waiting for update operation");
        }

        private static void PumpFor(int milliseconds)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); }
        }

        private static void CheckNow(UpdateManager manager)
        {
            Task check = manager.CheckNowAsync();
            PumpUntil(() => check.IsCompleted);
            check.GetAwaiter().GetResult();
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (Control dispatcher = new Control())
            {
                IntPtr handle = dispatcher.Handle;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string directory = Path.Combine(Path.GetTempPath(), "dim-update-checks-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);

                AvailableUpdate newer = Fetch(Release("v1.0.7"));
                Check(newer != null && newer.Version == new Version(1, 0, 7, 0), "Newer version was missed");
                Check(newer.DownloadPage.AbsoluteUri == "https://github.com/Aaryan6/dim-widget/releases/tag/v1.0.7",
                    "Untrusted response URL was used as browser destination");
                Check(newer.DisplayVersion == "v1.0.7", "Version display is incorrect");
                Check(Fetch(Release("v1.0.6")) == null, "Equal version was announced");
                Check(Fetch(Release("v1.0.5")) == null, "Older version was announced");
                Check(Fetch(Release("v1.10.0"), new Version(1, 9, 9)) != null, "Version comparison was alphabetical");
                Check(Fetch(Release("v1.2"), new Version(1, 2, 0)) == null, "Missing patch was not normalized");
                Check(Fetch(Release("1.0.7")) != null, "Version without v prefix was missed");
                Check(Fetch(Release("v1.0.6.1")) != null, "Fourth version component was missed");
                Check(Fetch(Release("v1.0.6+build.12")) == null, "Build metadata caused a duplicate update");
                Check(Fetch(Release("v9.0.0", prerelease: true)) == null, "Prerelease was announced");
                Check(Fetch(Release("v9.0.0", draft: true)) == null, "Draft was announced");
                Check(Fetch(Release("v9.0.0-beta")) == null, "Prerelease tag was treated as stable");
                Check(Fetch(Release("not-a-version")) == null, "Malformed version was announced");
                Check(Fetch(Release("v1.0.7", "new")) == null, "An unfinished upload was announced");
                Check(Fetch(Release("v1.0.7").Replace("DimWidget.exe", "source.zip")) == null,
                    "Release without executable was announced");
                Check(Fetch("{\"tag_name\":\"v1.0.7\",\"draft\":false,\"prerelease\":false}") == null,
                    "Missing assets were announced");

                string statePath = Path.Combine(directory, "notification.txt");
                UpdateNotificationState state = new UpdateNotificationState(statePath);
                Check(state.ShouldNotify(newer), "First update was suppressed");
                state.MarkNotified(newer);
                Check(!state.ShouldNotify(newer), "Same update was announced twice");
                Check(!new UpdateNotificationState(statePath).ShouldNotify(newer), "Notification history did not survive restart");
                Check(state.ShouldNotify(Fetch(Release("v1.0.8"))), "New release after a notification was suppressed");
                File.WriteAllText(statePath, "invalid version");
                Check(new UpdateNotificationState(statePath).ShouldNotify(newer), "Corrupted notification history blocked updates");

                int uiThread = Thread.CurrentThread.ManagedThreadId;
                FakeHttp scheduled = new FakeHttp { Json = Release("v1.0.7"), DelayMilliseconds = 20 };
                using (UpdateManager manager = new UpdateManager(new UpdateChecker(scheduled, new Version(1, 0, 6)),
                    new UpdateNotificationState(Path.Combine(directory, "scheduled.txt")), 20, 100))
                {
                    int announcements = 0;
                    manager.UpdateAvailable += delegate {
                        Check(Thread.CurrentThread.ManagedThreadId == uiThread, "Notification left the UI thread");
                        announcements++;
                    };
                    manager.Start();
                    PumpUntil(() => announcements == 1);
                    PumpUntil(() => scheduled.Calls >= 2 && scheduled.Completed >= 2);
                    Check(announcements == 1, "Recurring check repeated the same notification");
                    Check(scheduled.ValidHeaders, "GitHub request headers are missing");
                    Check(UpdateManager.StartupDelayMilliseconds == 10000
                        && UpdateManager.DailyIntervalMilliseconds == 86400000, "Production schedule is wrong");
                    scheduled.Json = Release("v1.0.8");
                    PumpUntil(() => announcements == 2);
                    Check(manager.LatestAvailable.Version == new Version(1, 0, 8, 0), "Cached update was not refreshed");
                    scheduled.Json = Release("v1.0.6");
                    PumpUntil(() => manager.LatestAvailable == null);
                }
                int stoppedAt = scheduled.Calls;
                PumpFor(150);
                Check(scheduled.Calls == stoppedAt, "Disposed timer kept making requests");

                foreach (int failure in new int[] { 403, 429, 500, 0, -1 })
                {
                    FakeHttp broken = new FakeHttp { Status = failure > 0 ? (HttpStatusCode)failure : HttpStatusCode.OK,
                        ThrowOffline = failure == 0, Json = failure == -1 ? "invalid json" : Release("v1.0.7") };
                    using (UpdateManager manager = new UpdateManager(new UpdateChecker(broken, new Version(1, 0, 6)),
                        new UpdateNotificationState(Path.Combine(directory, "failure-" + failure + ".txt")), 1000, 1000))
                    {
                        int announcements = 0;
                        manager.UpdateAvailable += delegate { announcements++; };
                        CheckNow(manager);
                        Check(announcements == 0 && manager.LatestAvailable == null, "Failure generated an update alert");
                        broken.Status = HttpStatusCode.OK;
                        broken.ThrowOffline = false;
                        broken.Json = Release("v1.0.7");
                        CheckNow(manager);
                        Check(announcements == 1, "Check did not recover after a failure");
                    }
                }

                FakeHttp slow = new FakeHttp { Json = Release("v1.0.7"), DelayMilliseconds = 5000 };
                UpdateManager cancelling = new UpdateManager(new UpdateChecker(slow, new Version(1, 0, 6)),
                    new UpdateNotificationState(Path.Combine(directory, "cancelled.txt")), 20, 50);
                int cancelledAlerts = 0;
                cancelling.UpdateAvailable += delegate { cancelledAlerts++; };
                Task pending = cancelling.CheckNowAsync();
                _ = cancelling.CheckNowAsync();
                Check(slow.Calls == 1, "Overlapping checks started multiple requests");
                cancelling.Dispose();
                PumpUntil(() => pending.IsCompleted);
                pending.GetAwaiter().GetResult();
                _ = cancelling.CheckNowAsync();
                Check(slow.Calls == 1 && cancelledAlerts == 0, "Shutdown did not cancel pending notifications");

                FakeHttp trayHttp = new FakeHttp { Json = Release("v1.0.7") };
                using (UpdateManager manager = new UpdateManager(new UpdateChecker(trayHttp, new Version(1, 0, 6)),
                    new UpdateNotificationState(Path.Combine(directory, "tray.txt")), 1000, 1000))
                using (NotifyIcon tray = new NotifyIcon())
                using (ToolStripMenuItem menu = new ToolStripMenuItem("Check for updates"))
                {
                    // Keep the test tray invisible; assert its actual message and
                    // route both click events to a recorder instead of a browser.
                    System.Collections.Generic.List<Uri> opened = new System.Collections.Generic.List<Uri>();
                    UpdateTray binding = new UpdateTray(manager, tray, menu, opened.Add);
                    System.Reflection.MethodInfo notificationClick = typeof(NotifyIcon).GetMethod("OnBalloonTipClicked",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    notificationClick.Invoke(tray, null);
                    Check(opened.Count == 0, "Welcome notification unexpectedly opened updates");
                    CheckNow(manager);
                    Check(tray.BalloonTipTitle == "Dim update available" && tray.BalloonTipText.Contains("v1.0.7"),
                        "Tray notification does not describe the newer version");
                    notificationClick.Invoke(tray, null);
                    Check(opened.Count == 1 && opened[0] == newer.DownloadPage, "Notification click opened the wrong page");
                    menu.PerformClick();
                    PumpFor(50);
                    Check(opened.Count == 2 && opened[1] == UpdateChecker.DownloadPage,
                        "Update menu did not open the latest release page");
                    trayHttp.Json = Release("v1.0.6");
                    CheckNow(manager);
                    menu.PerformClick();
                    PumpFor(50);
                    Check(opened.Count == 3 && opened[2] == UpdateChecker.DownloadPage,
                        "Update menu without a cached newer release opened the wrong page");
                    binding.Dispose();
                    menu.PerformClick();
                    notificationClick.Invoke(tray, null);
                    Check(opened.Count == 3, "Disposed tray binding kept opening browser pages");
                }

                Version assemblyVersion = typeof(AppVersion).Assembly.GetName().Version;
                Version expectedVersion;
                Check(UpdateChecker.TryParseVersion(AppVersion.Current, out expectedVersion)
                    && assemblyVersion == expectedVersion, "Executable version metadata is wrong");
                if (Array.IndexOf(args, "--live") >= 0)
                {
                    using (UpdateChecker live = new UpdateChecker(new HttpClientHandler(), new Version(0, 0)))
                    {
                        AvailableUpdate published = live.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                        Check(published != null, "Live GitHub release could not be read");
                        Console.WriteLine("Live GitHub release: " + published.Tag);
                        using (UpdateChecker production = new UpdateChecker())
                        {
                            AvailableUpdate result = production.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                            Check((result != null) == (published.Version > expectedVersion),
                                "Production HTTP configuration returned an inconsistent update");
                        }
                    }
                }
                Console.WriteLine("PASS: " + _checks + " checks. Temporary state: " + directory);
            }
            return 0;
        }

        private sealed class FakeHttp : HttpMessageHandler
        {
            public string Json;
            public HttpStatusCode Status = HttpStatusCode.OK;
            public int DelayMilliseconds;
            public bool ThrowOffline;
            public int Calls, Completed;
            public bool ValidHeaders = true;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Calls++;
                ValidHeaders &= request.RequestUri.AbsoluteUri == "https://api.github.com/repos/Aaryan6/dim-widget/releases/latest"
                    && request.Headers.UserAgent.ToString().Contains("DimWidget/")
                    && request.Headers.Contains("X-GitHub-Api-Version") && request.Headers.Accept.Count > 0;
                if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, cancellation).ConfigureAwait(false);
                if (ThrowOffline) throw new HttpRequestException("Offline test");
                Completed++;
                return new HttpResponseMessage(Status) { Content = new StringContent(Json) };
            }
        }
    }
}
