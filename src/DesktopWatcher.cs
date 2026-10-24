using System;
using System.Runtime.InteropServices;

namespace DimWidget
{
    // Thin wrapper over IVirtualDesktopManager. Only the "is this window on the
    // desktop that is currently displayed" query is used: when it turns false the
    // user has switched desktops and our windows have to follow.
    internal sealed class DesktopWatcher : IDisposable
    {
        private static readonly Guid ClsidVirtualDesktopManager =
            new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A");

        private IVirtualDesktopManager _manager;
        private bool _available;

        public DesktopWatcher()
        {
            try
            {
                Type type = Type.GetTypeFromCLSID(ClsidVirtualDesktopManager);
                if (type != null)
                {
                    _manager = (IVirtualDesktopManager)Activator.CreateInstance(type);
                    _available = _manager != null;
                }
            }
            catch (Exception)
            {
                _manager = null;
                _available = false;
            }
        }

        public bool Available
        {
            get { return _available; }
        }

        public bool IsOnCurrentDesktop(IntPtr hwnd)
        {
            if (!_available || hwnd == IntPtr.Zero)
            {
                return true;
            }

            int onCurrent;
            if (_manager.IsWindowOnCurrentVirtualDesktop(hwnd, out onCurrent) != 0)
            {
                return true;
            }

            return onCurrent != 0;
        }

        public void Dispose()
        {
            if (_manager != null)
            {
                Marshal.ReleaseComObject(_manager);
                _manager = null;
            }

            _available = false;
        }

        [ComImport]
        [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopManager
        {
            [PreserveSig]
            int IsWindowOnCurrentVirtualDesktop([In] IntPtr topLevelWindow, [Out] out int onCurrentDesktop);

            [PreserveSig]
            int GetWindowDesktopId([In] IntPtr topLevelWindow, [Out] out Guid desktopId);

            [PreserveSig]
            int MoveWindowToDesktop([In] IntPtr topLevelWindow, [In] ref Guid desktopId);
        }
    }
}
