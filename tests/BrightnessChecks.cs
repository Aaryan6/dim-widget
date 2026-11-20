using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace DimWidget
{
    internal static class BrightnessChecks
    {
        private static int _checks;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            using (Control dispatcher = new Control())
            {
                IntPtr handle = dispatcher.Handle;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                try
                {
                    FakeBackend backend = new FakeBackend();
                    using (WindowsBrightness brightness = new WindowsBrightness(backend))
                    {
                        Wait(() => brightness.Available);
                        Check(brightness.Brightness == 40 && backend.Writes == 0, "startup reads without changing brightness");
                        int uiThread = Thread.CurrentThread.ManagedThreadId;
                        brightness.Changed += delegate { Check(Thread.CurrentThread.ManagedThreadId == uiThread, "events run on UI thread"); };

                        brightness.SetBrightness(20);
                        brightness.SetBrightness(60);
                        brightness.SetBrightness(80);
                        Wait(() => backend.Writes == 1 && backend.Value == 80);
                        Pump(100);
                        Check(backend.Writes == 1 && brightness.Brightness == 80, "slider writes coalesce to latest value");

                        backend.Value = 25;
                        brightness.Refresh();
                        Wait(() => brightness.Brightness == 25);
                        Check(backend.Writes == 1, "external Windows changes sync without writing back");

                        backend.FailWrite = true;
                        brightness.SetBrightness(70);
                        Wait(() => brightness.Error != null);
                        Check(brightness.Brightness == 25, "failed write rolls back optimistic value");
                        backend.FailWrite = false;
                        brightness.SetBrightness(70);
                        Wait(() => backend.Value == 70 && brightness.Error == null);

                        backend.Present = false;
                        brightness.Refresh();
                        Wait(() => !brightness.Available);
                        int previousWrites = backend.Writes;
                        brightness.SetBrightness(90);
                        Pump(120);
                        Check(backend.Writes == previousWrites, "unsupported display cannot receive brightness writes");
                        backend.Present = true;
                        brightness.Refresh();
                        Wait(() => brightness.Available);
                    }

                    BrightnessState sparse = new BrightnessState { Levels = new byte[] { 0, 20, 50, 100 } };
                    Check(sparse.NearestLevel(62) == 50 && sparse.NearestLevel(-5) == 0
                        && sparse.NearestLevel(105) == 100, "values clamp and snap to supported hardware levels");

                    FakeBackend delayed = new FakeBackend { Delay = 150 };
                    WindowsBrightness disposed = new WindowsBrightness(delayed);
                    int callbacks = 0;
                    disposed.Changed += delegate { callbacks++; };
                    disposed.Dispose();
                    Pump(250);
                    Check(callbacks == 0 && delayed.Writes == 0, "disposing during a read stops callbacks and writes");

                    WidgetCheck();
                    UnifiedControllerCheck();

                    if (Array.IndexOf(args, "--live") >= 0) LiveCheck();
                    Console.WriteLine("PASS: " + _checks + " brightness checks.");
                    return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            }
        }

        private static void LiveCheck()
        {
            WmiBrightnessBackend backend = new WmiBrightnessBackend();
            BrightnessState original = backend.Read();
            Check(original != null, "live laptop display supports Windows brightness");
            int next = original.NearestLevel(original.Value < 95 ? original.Value + 5 : original.Value - 5);
            Check(next != original.Value, "live display has a distinct test brightness");
            try
            {
                using (DimController dim = new DimController(100, false, backend))
                {
                    RemoveTestOverlays(dim);
                    Wait(() => dim.SystemBrightness.Available);
                    int position = DimController.HardwareMinimumPosition + (int)Math.Round(
                        (next - original.Minimum) * (100d - DimController.HardwareMinimumPosition)
                        / Math.Max(1, original.Maximum - original.Minimum));
                    dim.SetBrightness(position);
                    Wait(() => backend.Read().Value == next);
                    Check(dim.SystemBrightness.Error == null && dim.Brightness == 100,
                        "combined controller changes actual Windows brightness without overlay");
                    dim.SetBrightness(10);
                    Wait(() => backend.Read().Value == original.Minimum);
                    Check(dim.Brightness == 50, "combined controller reaches actual hardware minimum before extra dimming");
                }
            }
            finally
            {
                backend.Write(original, original.Value);
                Wait(() => backend.Read().Value == original.Value);
                Check(true, "original Windows brightness restored");
            }
        }

        private static void WidgetCheck()
        {
            FakeBackend backend = new FakeBackend();
            using (DimController dim = new DimController(100, false, backend))
            using (WidgetForm widget = new WidgetForm(dim, new Settings()))
            {
                RemoveTestOverlays(dim);
                Wait(() => dim.SystemBrightness.Available);
                FieldInfo open = typeof(WidgetForm).GetField("_open", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo expanded = typeof(WidgetForm).GetField("_expanded", BindingFlags.NonPublic | BindingFlags.Instance);
                open.SetValue(widget, 1f);
                expanded.SetValue(widget, true);
                widget.SyncFromController();
                float scale = widget.ClientSize.Width / 194f;
                Check(Math.Abs(widget.ClientSize.Height - 32 * scale) <= 1, "supported display uses one compact row");

                // Exercise the widget's input handlers against the fake display.
                // No input is sent to the user's desktop or running app.
                MethodInfo down = typeof(WidgetForm).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                down.Invoke(widget, new object[] { new MouseEventArgs(MouseButtons.Left, 1, (int)(73 * scale), (int)(16 * scale), 0) });
                Wait(() => backend.Value == 40);
                widget.Capture = false;
                Check(dim.Brightness == 100, "slider controls Windows brightness in the normal range");

                using (Bitmap frame = new Bitmap(widget.Width, widget.Height))
                using (Graphics graphics = Graphics.FromImage(frame))
                {
                    typeof(WidgetForm).GetMethod("DrawContents", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(widget, new object[] { graphics });
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "brightness-widget.png");
                    frame.Save(path, ImageFormat.Png);
                    Console.WriteLine("Widget render: " + path);
                }

                dim.SetBrightness(0);
                widget.SyncFromController();
                using (Bitmap frame = new Bitmap(widget.Width, widget.Height))
                using (Graphics graphics = Graphics.FromImage(frame))
                {
                    typeof(WidgetForm).GetMethod("DrawContents", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(widget, new object[] { graphics });
                    frame.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "brightness-widget-extra-dim.png"), ImageFormat.Png);
                }

                backend.Present = false;
                dim.SystemBrightness.Refresh();
                Wait(() => !dim.SystemBrightness.Available);
                widget.SyncFromController();
                Check(Math.Abs(widget.ClientSize.Width - 194 * scale) <= 1, "unsupported display keeps the same single row");
            }
        }

        private static void UnifiedControllerCheck()
        {
            FakeBackend backend = new FakeBackend();
            using (DimController dim = new DimController(100, false, backend))
            {
                RemoveTestOverlays(dim);
                Wait(() => dim.SystemBrightness.Available);
                Check(dim.BrightnessText == "40%" && dim.ControlBrightness == 52 && backend.Writes == 0,
                    "combined slider reads current hardware brightness without writing");

                dim.SetBrightness(60);
                Wait(() => backend.Value == 50);
                Pump(50);
                Check(dim.Brightness == 100 && dim.BrightnessText == "50%", "normal range uses hardware with no overlay");
                dim.SetBrightness(20);
                Wait(() => backend.Value == 0);
                Pump(50);
                Check(dim.Brightness == 100 && dim.BrightnessText == "0%", "marker is the hardware minimum with no extra dimming");
                int writes = backend.Writes;
                dim.SetBrightness(10);
                Check(dim.Brightness == 50 && dim.ControlBrightness == 10 && dim.BrightnessText == "-50%",
                    "below minimum keeps hardware at minimum and adds dimming");
                Pump(100);
                Check(backend.Writes == writes && backend.Value == 0, "extra dimming does not rewrite hardware at its minimum");
                dim.SetBrightness(0);
                Check(dim.Brightness == 0 && dim.BrightnessText == "-100%", "left edge applies maximum extra dimming");
                dim.Nudge(5);
                Check(dim.Brightness == 25 && dim.ControlBrightness == 5, "shortcuts adjust combined range below hardware minimum");

                dim.SetBrightness(60);
                Wait(() => backend.Value == 50);
                Pump(50);
                Check(dim.Brightness == 100, "moving right removes overlay and increases hardware brightness");
                dim.SetBrightness(10);
                Wait(() => backend.Value == 0);
                Pump(50);
                backend.Value = 45;
                dim.SystemBrightness.Refresh();
                Wait(() => dim.Brightness == 100 && dim.BrightnessText == "45%");
                Check(dim.ControlBrightness == 56, "Windows brightness keys exit extra dimming and sync the slider");

                backend.FailWrite = true;
                dim.SetBrightness(10);
                Wait(() => dim.SystemBrightness.Error != null);
                Check(dim.Brightness == 100 && dim.BrightnessText == "45%", "failed minimum transition restores actual hardware state");
                backend.FailWrite = false;
                dim.SetBrightness(10);
                Wait(() => backend.Value == 0 && dim.SystemBrightness.Error == null);
                Pump(50);
                dim.SetSuspended(true);
                Check(dim.ControlBrightness == 20 && dim.BrightnessText == "0%", "pause removes only extra dimming");
                dim.SetSuspended(false);
                Check(dim.ControlBrightness == 10 && dim.BrightnessText == "-50%", "resume restores extra dimming");

                backend.Present = false;
                dim.SystemBrightness.Refresh();
                Wait(() => !dim.SystemBrightness.Available);
                writes = backend.Writes;
                dim.SetBrightness(75);
                Check(dim.Brightness == 75 && dim.ControlBrightness == 75 && backend.Writes == writes,
                    "unsupported display uses software dimming across the whole range");
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            _checks++;
        }

        private static void RemoveTestOverlays(DimController dim)
        {
            // These controller checks must not dim the user's actual desktop.
            List<OverlayForm> overlays = (List<OverlayForm>)typeof(DimController)
                .GetField("_overlays", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dim);
            foreach (OverlayForm overlay in overlays) overlay.Dispose();
            overlays.Clear();
        }

        private static void Wait(Func<bool> condition)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < end) Pump(10);
            Check(condition(), "operation completed");
        }

        private static void Pump(int milliseconds)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            do { Application.DoEvents(); Thread.Sleep(5); } while (DateTime.UtcNow < end);
        }

        private sealed class FakeBackend : IWindowsBrightnessBackend
        {
            public volatile int Value = 40;
            public volatile int Writes;
            public volatile bool Present = true;
            public volatile bool FailWrite;
            public int Delay;

            public BrightnessState Read()
            {
                if (Delay > 0) Thread.Sleep(Delay);
                return Present ? new BrightnessState { InstanceName = "test", Value = Value } : null;
            }

            public void Write(BrightnessState display, int value)
            {
                if (FailWrite) throw new InvalidOperationException("Simulated driver failure");
                Value = value;
                Writes++;
            }
        }
    }
}
