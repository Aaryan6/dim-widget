using System;
using System.Threading;
using System.Windows.Forms;

namespace DimWidget
{
    internal static class Program
    {
        private const string MutexName = "DimWidget.Instance.5c1e9b7a";
        private const string ShowEventName = "DimWidget.Show.5c1e9b7a";

        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        using (EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName))
                        {
                            existing.Set();
                        }
                    }
                    catch (Exception)
                    {
                    }

                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool startedByWindows = false;
                foreach (string arg in args)
                {
                    if (string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase))
                    {
                        startedByWindows = true;
                    }
                }

                using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                {
                    Settings settings = Settings.Load();

                    using (DimApp app = new DimApp(settings, showEvent, startedByWindows))
                    {
                        Application.Run(app);
                    }
                }
            }
        }
    }
}
