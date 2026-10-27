using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DimWidget
{
    // A single premultiplied surface preserves translucent pixels and the
    // notch's antialiased outline. Native system acrylic fills a rectangle
    // even when a custom window region is set, so it cannot be used here.
    internal static class GlassSurface
    {
        public static void Present(IntPtr window, Point location, Size size, Action<Graphics> draw)
        {
            if (size.Width < 1 || size.Height < 1) return;
            using (Bitmap buffer = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
            {
                using (Graphics graphics = Graphics.FromImage(buffer))
                {
                    graphics.Clear(Color.Transparent);
                    Theme.Prepare(graphics);
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    draw(graphics);
                }
                PresentBitmap(window, location, buffer);
            }
        }

        private static void PresentBitmap(IntPtr window, Point location, Bitmap bitmap)
        {
            IntPtr source = CreateCompatibleDC(IntPtr.Zero);
            IntPtr dib = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            try
            {
                if (source == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                BitmapInfo info = new BitmapInfo();
                info.Size = (uint)Marshal.SizeOf(typeof(BitmapInfo));
                info.Width = bitmap.Width;
                info.Height = -bitmap.Height;
                info.Planes = 1;
                info.BitCount = 32;
                IntPtr pixels;
                dib = CreateDIBSection(source, ref info, 0, out pixels, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || pixels == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                previous = SelectObject(source, dib);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    byte[] row = new byte[bitmap.Width * 4];
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(pixels, y * row.Length), row.Length);
                    }
                }
                finally { bitmap.UnlockBits(data); }

                NativePoint origin = new NativePoint { X = location.X, Y = location.Y };
                NativePoint offset = new NativePoint();
                NativeSize size = new NativeSize { Width = bitmap.Width, Height = bitmap.Height };
                BlendFunction blend = new BlendFunction { ConstantAlpha = 255, AlphaFormat = 1 };
                if (!UpdateLayeredWindow(window, IntPtr.Zero, ref origin, ref size, source,
                    ref offset, 0, ref blend, 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(source, previous);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (source != IntPtr.Zero) DeleteDC(source);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize { public int Width, Height; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction { public byte Operation, Flags, ConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int XPelsPerMeter, YPelsPerMeter;
            public uint ClrUsed, ClrImportant;
        }

        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-updatelayeredwindow
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destination,
            ref NativePoint location, ref NativeSize size, IntPtr source, ref NativePoint sourceLocation,
            int colorKey, ref BlendFunction blend, uint flags);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage,
            out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr dc);
    }
}
