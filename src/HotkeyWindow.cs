using System;
using System.Windows.Forms;

namespace DimWidget
{
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int> HotkeyPressed;

        public HotkeyWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "DimWidget.Hotkeys";
            CreateHandle(cp);
        }

        public bool TryRegister(int id, uint modifiers, uint virtualKey)
        {
            return NativeMethods.RegisterHotKey(Handle, id, modifiers, virtualKey);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                Action<int> handler = HotkeyPressed;
                if (handler != null)
                {
                    handler(m.WParam.ToInt32());
                }

                return;
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }
}
