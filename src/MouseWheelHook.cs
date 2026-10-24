using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DimWidget
{
    internal sealed class MouseWheelHook : IDisposable
    {
        private readonly NativeMethods.LowLevelMouseProc _callback;
        private IntPtr _hook;

        public event Action<int> DimmingWheel;

        public MouseWheelHook()
        {
            _callback = HookCallback;
        }

        public void Install()
        {
            if (_hook != IntPtr.Zero)
            {
                return;
            }

            string moduleName = null;

            using (Process process = Process.GetCurrentProcess())
            {
                try
                {
                    using (ProcessModule module = process.MainModule)
                    {
                        if (module != null)
                        {
                            moduleName = module.ModuleName;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }

            _hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _callback,
                NativeMethods.GetModuleHandle(moduleName),
                0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && wParam == (IntPtr)NativeMethods.WM_MOUSEWHEEL)
                {
                    bool ctrlDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
                    bool altDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;

                    if (ctrlDown && altDown)
                    {
                        NativeMethods.MouseLowLevelHookStruct info =
                            (NativeMethods.MouseLowLevelHookStruct)Marshal.PtrToStructure(
                                lParam,
                                typeof(NativeMethods.MouseLowLevelHookStruct));

                        int delta = (short)((info.MouseData >> 16) & 0xFFFF);
                        Action<int> handler = DimmingWheel;

                        if (handler != null && delta != 0)
                        {
                            handler(delta);
                        }

                        return (IntPtr)1;
                    }
                }
            }
            catch (Exception)
            {
            }

            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
