using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DimWidget
{
    // Opt-in trace for diagnosing window / desktop behaviour:
    //   set DIMWIDGET_DEBUG=1 and the app appends to %TEMP%\dimwidget-debug.log
    internal static class DebugLog
    {
        private static readonly bool Enabled =
            string.Equals(Environment.GetEnvironmentVariable("DIMWIDGET_DEBUG"), "1", StringComparison.Ordinal);

        private static readonly object Sync = new object();
        private static readonly string Path =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dimwidget-debug.log");

        public static void Write(string message)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                lock (Sync)
                {
                    File.AppendAllText(
                        Path,
                        DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                            + " [" + System.Threading.Thread.CurrentThread.ManagedThreadId + "] "
                            + message
                            + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
