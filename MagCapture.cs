using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Odak;

/// <summary>
/// Reads the screen with one window left out, using the Windows Magnification API. So the glass can show what is
/// behind it live, while the panel itself stays visible to Discord, OBS and screenshots.
///
/// Measured limits this design works around: resizing a magnifier window to a larger size crashes inside Windows,
/// and destroying and recreating one leaks about 1 MB each time. So there is one magnifier, on one permanent
/// thread that sleeps while idle, always at the panel's full size; it is only replaced if that size changes
/// (for example on a monitor with a different scale).
/// </summary>
internal static class MagCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct HEADER { public uint width, height; public Guid format; public uint stride, offset; public UIntPtr cbSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    private delegate bool ScalingCallback(IntPtr hwnd, IntPtr src, HEADER srcHeader, IntPtr dst, HEADER dstHeader, RECT unclipped, RECT clipped, IntPtr dirty);

    [DllImport("magnification.dll")] private static extern bool MagInitialize();
    [DllImport("magnification.dll")] private static extern bool MagSetWindowSource(IntPtr hwnd, RECT rect);
    [DllImport("magnification.dll")] private static extern bool MagSetWindowFilterList(IntPtr hwnd, int mode, int count, IntPtr[] list);
    [DllImport("magnification.dll")] private static extern bool MagSetImageScalingCallback(IntPtr hwnd, ScalingCallback callback);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private static readonly BlockingCollection<Action> Jobs = new();
    private static readonly ScalingCallback Callback = OnFrame; // kept in a field: Windows holds on to it
    private static Thread? _thread;
    private static IntPtr _host, _magnifier, _exclude;
    private static int _width, _height, _copyWidth, _copyHeight;
    private static int[]? _target;
    private static bool _copied;

    /// <summary>Prepares the magnifier to leave out this window. False if this PC cannot do it.</summary>
    public static bool Start(IntPtr exclude)
    {
        try
        {
            return Run(() =>
            {
                if (_host == IntPtr.Zero)
                {
                    if (!MagInitialize()) return false;
                    const uint Layered = 0x00080000, ToolWindow = 0x80, Transparent = 0x20, Popup = 0x80000000;
                    _host = CreateWindowEx(Layered | ToolWindow | Transparent, "Static", "SpotlightGlass", Popup, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                    if (_host == IntPtr.Zero) return false;
                    SetLayeredWindowAttributes(_host, 0, 255, 2);
                }
                _exclude = exclude;
                return true;
            });
        }
        catch { return false; } // magnification.dll is missing on some Windows editions
    }

    /// <summary>Copies a screen rectangle into <paramref name="pixels"/>, as if the excluded window were not there.</summary>
    public static bool Copy(int x, int y, int width, int height, int maxHeight, int[] pixels) => Run(() =>
    {
        if (!Prepare(width, maxHeight)) return false;
        // Set on every frame: a filter set while the window is still hidden has no effect.
        MagSetWindowFilterList(_magnifier, 0, 1, new[] { _exclude });
        _target = pixels; _copyWidth = width; _copyHeight = height; _copied = false;
        MagSetWindowSource(_magnifier, new RECT { L = x, T = y, R = x + width, B = y + maxHeight });
        Pump(); // the frame can arrive as a queued paint, so the target stays open until this is done
        _target = null;
        return _copied;
    });

    private static bool Prepare(int width, int height)
    {
        if (_magnifier != IntPtr.Zero && _width == width && _height == height) return true;
        if (_magnifier != IntPtr.Zero) DestroyWindow(_magnifier);
        const uint Child = 0x40000000, Visible = 0x10000000;
        _magnifier = CreateWindowEx(0, "Magnifier", "", Child | Visible, 0, 0, width, height, _host, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_magnifier == IntPtr.Zero) return false;
        _width = width; _height = height;
        return MagSetImageScalingCallback(_magnifier, Callback) && MagSetWindowFilterList(_magnifier, 0, 1, new[] { _exclude });
    }

    private static bool OnFrame(IntPtr hwnd, IntPtr src, HEADER srcHeader, IntPtr dst, HEADER dstHeader, RECT unclipped, RECT clipped, IntPtr dirty)
    {
        var target = _target;
        if (target == null) return true;
        int stride = (int)srcHeader.stride, rows = Math.Min(_copyHeight, (int)srcHeader.height), width = Math.Min(_copyWidth, stride / 4);
        var start = src + (int)srcHeader.offset;
        for (int row = 0; row < rows; row++) Marshal.Copy(start + row * stride, target, row * _copyWidth, width);
        _copied = rows == _copyHeight;
        return true;
    }

    private static void Pump()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>Runs on the magnifier's own thread and waits for the result.</summary>
    private static bool Run(Func<bool> job)
    {
        if (_thread == null)
        {
            _thread = new Thread(() =>
            {
                foreach (var next in Jobs.GetConsumingEnumerable()) next(); // sleeps here while the panel is closed
            })
            { IsBackground = true, Name = "Glass magnifier" };
            _thread.Start();
        }
        bool result = false;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        Jobs.Add(() =>
        {
            try { result = job(); }
            catch (Exception ex) { error = ex; }
            done.Set();
        });
        done.Wait();
        if (error != null) throw error;
        return result;
    }
}
