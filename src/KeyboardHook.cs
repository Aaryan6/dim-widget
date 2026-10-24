using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DimWidget
{
    internal sealed class KeyboardHook : IDisposable
    {
        public const int Brighter = 1;
        public const int Dimmer = 2;
        public const int Reset = 3;
        public const int Pause = 4;

        private readonly NativeMethods.LowLevelKeyboardProc _callback;
        private readonly HashSet<uint> _held = new HashSet<uint>();
        private IntPtr _hook;

        public event Action<int> Pressed;

        public KeyboardHook()
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
                NativeMethods.WH_KEYBOARD_LL,
                _callback,
                NativeMethods.GetModuleHandle(moduleName),
                0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int message = wParam.ToInt32();
                    bool down = message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN;
                    bool up = message == NativeMethods.WM_KEYUP || message == NativeMethods.WM_SYSKEYUP;

                    if (down || up)
                    {
                        NativeMethods.KeyboardLowLevelHookStruct info =
                            (NativeMethods.KeyboardLowLevelHookStruct)Marshal.PtrToStructure(
                                lParam,
                                typeof(NativeMethods.KeyboardLowLevelHookStruct));

                        if ((info.Flags & NativeMethods.LLKHF_UP) != 0)
                        {
                            up = true;
                            down = false;
                        }

                        if (up)
                        {
                            _held.Remove(info.VirtualKey);
                        }
                        else if (IsShortcut(info.VirtualKey))
                        {
                            if (_held.Add(info.VirtualKey))
                            {
                                Action<int> handler = Pressed;
                                if (handler != null)
                                {
                                    handler(ShortcutFor(info.VirtualKey));
                                }
                            }

                            return (IntPtr)1;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsShortcut(uint virtualKey)
        {
            if (ShortcutFor(virtualKey) == 0)
            {
                return false;
            }

            bool ctrlDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
            bool altDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
            return ctrlDown && altDown;
        }

        private static int ShortcutFor(uint virtualKey)
        {
            switch (virtualKey)
            {
                case NativeMethods.VK_OEM_PLUS:
                case NativeMethods.VK_ADD:
                    return Brighter;

                case NativeMethods.VK_OEM_MINUS:
                case NativeMethods.VK_SUBTRACT:
                    return Dimmer;

                case 0x30:
                case NativeMethods.VK_NUMPAD0:
                    return Reset;

                case 0x44:
                    return Pause;

                default:
                    return 0;
            }
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
