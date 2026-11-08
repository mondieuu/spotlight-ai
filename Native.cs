using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Odak;

/// <summary>Windows API: global hotkey and shell icons.</summary>
internal static class Native
{
    // ---- Global hotkey
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const uint VK_SPACE = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---- Live glass: our window stays hidden from screen capture, so we can see what is behind it.
    public const uint WDA_NONE = 0x0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    /// <summary>Waits until the desktop compositor has presented the next frame.</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    // ---- Screen capture for the live glass
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int dx, int dy, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    private const uint SRCCOPY = 0x00CC0020;

    /// <summary>Copies screen rectangles into a reusable 32-bit bitmap, so a frame allocates nothing.</summary>
    public sealed class ScreenGrabber : IDisposable
    {
        private readonly IntPtr _screen, _mem, _dib, _old, _bits;
        private bool _disposed;

        public ScreenGrabber(int width, int maxHeight)
        {
            _screen = GetDC(IntPtr.Zero);
            _mem = CreateCompatibleDC(_screen);
            var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -maxHeight, biPlanes = 1, biBitCount = 32 };
            _dib = CreateDIBSection(_screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            if (_dib != IntPtr.Zero) _old = SelectObject(_mem, _dib);
        }

        /// <summary>Copies the rectangle into <paramref name="pixels"/> (top-down BGRX); false if the screen cannot be read.</summary>
        public bool Copy(int x, int y, int width, int height, int[] pixels)
        {
            if (_disposed || _dib == IntPtr.Zero) return false;
            if (!BitBlt(_mem, 0, 0, width, height, _screen, x, y, SRCCOPY)) return false;
            GdiFlush();
            Marshal.Copy(_bits, pixels, 0, width * height);
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_old != IntPtr.Zero) SelectObject(_mem, _old);
            if (_dib != IntPtr.Zero) DeleteObject(_dib);
            if (_mem != IntPtr.Zero) DeleteDC(_mem);
            ReleaseDC(IntPtr.Zero, _screen);
        }
    }

    // ---- The monitor under the mouse
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Work area (physical pixels) and scale of the monitor the mouse is on; null if unknown.</summary>
    public static (RECT work, double scale)? MonitorUnderCursor()
    {
        if (!GetCursorPos(out var p)) return null;
        var monitor = MonitorFromPoint(p, 2 /* nearest */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        double scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        return (info.rcWork, scale);
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    /// <summary>Limits a window to a rectangle of this size; Windows owns the region afterwards.</summary>
    public static void SetWindowRegion(IntPtr hwnd, int width, int height)
    {
        var region = CreateRectRgn(0, 0, width, height);
        if (region != IntPtr.Zero && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }

    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

    /// <summary>Moves a window to physical coordinates; it takes the DPI of the monitor it lands on.</summary>
    public static void MoveWindowTo(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010 /* no size, no z-order, no activate */);

    // ---- Shell icons (the real icons of Start menu apps)
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out BITMAP bmp);

    private const int SIIGBF_ICONONLY = 0x4;

    // Shell icons work reliably on an STA thread; a single background STA queue is used.
    private static readonly System.Collections.Concurrent.BlockingCollection<(string path, int px, System.Threading.Tasks.TaskCompletionSource<BitmapSource?> tcs)> IconQueue = StartIconThread();

    private static System.Collections.Concurrent.BlockingCollection<(string, int, System.Threading.Tasks.TaskCompletionSource<BitmapSource?>)> StartIconThread()
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<(string, int, System.Threading.Tasks.TaskCompletionSource<BitmapSource?>)>();
        var thread = new System.Threading.Thread(() =>
        {
            foreach (var (path, px, tcs) in queue.GetConsumingEnumerable())
            {
                try { tcs.TrySetResult(ShellIcon(path, px)); }
                catch { tcs.TrySetResult(null); }
            }
        })
        { IsBackground = true, Name = "Spotlight simgeleri" };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        return queue;
    }

    /// <summary>Loads the icon on the background STA thread.</summary>
    public static System.Threading.Tasks.Task<BitmapSource?> ShellIconAsync(string parsingName, int px)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<BitmapSource?>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        IconQueue.Add((parsingName, px, tcs));
        return tcs.Task;
    }

    /// <summary>Returns the shell item's icon with transparency preserved (null = not found).</summary>
    public static BitmapSource? ShellIcon(string parsingName, int px)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null) return null;
        try
        {
            if (factory.GetImage(new SIZE { cx = px, cy = px }, SIIGBF_ICONONLY, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
            try
            {
                GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bmp);
                if (bmp.bmBits == IntPtr.Zero || bmp.bmBitsPixel != 32)
                {
                    var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    fallback.Freeze();
                    return fallback;
                }
                int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight), stride = bmp.bmWidthBytes;
                var buffer = new byte[stride * h];
                Marshal.Copy(bmp.bmBits, buffer, 0, buffer.Length);
                if (bmp.bmHeight > 0)
                {
                    // Bottom-up DIB: flip the rows.
                    var flipped = new byte[buffer.Length];
                    for (int y = 0; y < h; y++) Buffer.BlockCopy(buffer, y * stride, flipped, (h - 1 - y) * stride, stride);
                    buffer = flipped;
                }
                var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buffer, stride);
                image.Freeze();
                return image;
            }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
        finally { Marshal.ReleaseComObject(factory); }
    }
}
