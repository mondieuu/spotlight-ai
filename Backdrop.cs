using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Odak;

/// <summary>
/// Live Liquid Glass. While the panel is open, a background thread copies the screen region behind it about
/// 30 times a second, so videos and moving windows carry on behind the glass. The read leaves the panel out
/// (see MagCapture), so the glass never captures itself and the panel still shows in Discord and screenshots.
/// If that is unavailable, the window is excluded from capture instead, and does not appear in them.
///
/// Kept cheap on purpose: only the rows the panel really covers are read, a frame identical to the previous
/// one is dropped before any processing, the curved bezel uses a precomputed map, and buffers are reused.
/// When the panel closes, the thread stops and everything is released.
///
/// On Windows versions without capture exclusion (before 10 2004), it falls back to a single still image.
/// </summary>
internal sealed class Backdrop
{
    private const int MinFrameMs = 30;   // about 30 frames per second
    private const int BlurRadius = 2;
    private const int Saturation = 276;  // 1.08 × 256: a touch of vibrancy
    // Light comes from the top left, like Apple's Liquid Glass.
    private const double LightX = -0.5, LightY = -0.866;

    private readonly int _x, _y, _w, _maxH;
    private readonly double _scale, _radiusDip;
    private readonly bool _live;
    private readonly Dispatcher _ui;
    private readonly Action<ImageSource> _setImage;
    private readonly Native.ScreenGrabber? _grabber;
    private readonly bool _magnifier;
    private readonly object _swap = new();
    private readonly int[] _sumR, _sumG, _sumB;

    private int[] _raw = Array.Empty<int>(), _source = Array.Empty<int>(), _tmp = Array.Empty<int>(),
        _clean = Array.Empty<int>(), _back = Array.Empty<int>(), _front = Array.Empty<int>();
    private int _sourceH, _mapH, _frontH, _pending;
    private volatile int _wantH;
    private volatile bool _stop;
    private Thread? _thread;
    private WriteableBitmap? _bitmap;

    // Bezel map: for each pixel of the curved edge, where it samples from and how bright its highlight is.
    private int _mapCount;
    private int[] _mapX = new int[4096], _mapY = new int[4096], _mapSpec = new int[4096];
    private float[] _mapDx = new float[4096], _mapDy = new float[4096];

    public Backdrop(int x, int y, int width, int maxHeight, double scale, double radiusDip, double heightDip, bool live, bool magnifier, Action<ImageSource> setImage)
    {
        _x = x; _y = y; _w = width; _maxH = maxHeight;
        _magnifier = magnifier;
        _scale = scale; _radiusDip = radiusDip; _live = live;
        _setImage = setImage;
        _ui = Dispatcher.CurrentDispatcher;
        if (!magnifier) _grabber = new Native.ScreenGrabber(width, maxHeight);
        _sumR = new int[width]; _sumG = new int[width]; _sumB = new int[width];
        _wantH = ToPixels(heightDip);

        // The first frame is ready before the window appears. A still image captures the full height once.
        if (!_live) _wantH = _maxH;
        NextFrame(grab: true);
        if (!_live) { _wantH = ToPixels(heightDip); NextFrame(grab: false); ReleaseGrabber(); }
        Present();
    }

    public void Start()
    {
        if (!_live || _stop) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Spotlight AI glass" };
        _thread.Start();
    }

    /// <summary>Stops the thread; the buffers are released with this object.</summary>
    public void Stop()
    {
        _stop = true;
        if (_thread == null) ReleaseGrabber();
    }

    public void SetHeight(double heightDip)
    {
        int h = ToPixels(heightDip);
        if (h == _wantH) return;
        _wantH = h;
        if (_live || _stop) return;
        NextFrame(grab: false);
        Present();
    }

    private int ToPixels(double dip) => Math.Clamp((int)Math.Round(dip * _scale), 1, _maxH);

    private void ReleaseGrabber() => _grabber?.Dispose();

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            while (!_stop)
            {
                // Paced by the compositor, so a frame is never read twice between two screen refreshes.
                if (Native.DwmFlush() != 0) Thread.Sleep(16);
                if (clock.ElapsedMilliseconds < MinFrameMs) continue;
                clock.Restart();
                NextFrame(grab: true);
            }
        }
        catch { /* the still-visible last frame stays */ }
        finally { ReleaseGrabber(); }
    }

    private void NextFrame(bool grab)
    {
        int h = _wantH, count = _w * h;
        bool resized = h != _mapH;
        if (grab)
        {
            Grow(ref _raw, count);
            if (!(_magnifier ? MagCapture.Copy(_x, _y, _w, h, _maxH, _raw) : _grabber!.Copy(_x, _y, _w, h, _raw))) return; // e.g. the secure desktop: keep the last frame
            // Nothing moved behind the glass: skip all the work.
            if (!resized && h == _sourceH && _raw.AsSpan(0, count).SequenceEqual(_source.AsSpan(0, count))) return;
            Grow(ref _source, count);
            (_raw, _source) = (_source, _raw);
            _sourceH = h;
        }
        Grow(ref _tmp, count);
        Grow(ref _clean, count);
        Grow(ref _back, count);

        Soften(_source, _tmp, _clean, h);
        if (resized) BuildBezelMap(h);
        Array.Copy(_clean, _back, count);
        ApplyBezel(h);

        lock (_swap)
        {
            (_back, _front) = (_front, _back);
            _frontH = h;
        }
        if (Thread.CurrentThread != _ui.Thread && Interlocked.Exchange(ref _pending, 1) == 0)
            _ui.BeginInvoke(Present, DispatcherPriority.Render);
    }

    private static void Grow(ref int[] buffer, int count)
    {
        if (buffer.Length < count) buffer = new int[count];
    }

    /// <summary>Shows the newest frame. UI thread only.</summary>
    private void Present()
    {
        Interlocked.Exchange(ref _pending, 0);
        if (_stop && _bitmap != null) return;
        lock (_swap)
        {
            int h = _frontH;
            if (h <= 0) return;
            if (_bitmap == null || _bitmap.PixelHeight != h)
            {
                double dpi = 96.0 * _scale;
                _bitmap = new WriteableBitmap(_w, h, dpi, dpi, PixelFormats.Bgr32, null);
                _setImage(_bitmap);
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, _w, h), _front, _w * 4, 0);
        }
    }

    /// <summary>
    /// A very light softening (about 2 px) plus vibrancy: everything behind stays visible, but text behind
    /// no longer collides with Spotlight AI's own text.
    /// </summary>
    private void Soften(int[] src, int[] tmp, int[] dst, int h)
    {
        int w = _w, r = BlurRadius, div = 2 * r + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w, rs = 0, gs = 0, bs = 0;
            for (int i = -r; i <= r; i++)
            {
                int c = src[row + Math.Clamp(i, 0, w - 1)];
                rs += (c >> 16) & 255; gs += (c >> 8) & 255; bs += c & 255;
            }
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = ((rs / div) << 16) | ((gs / div) << 8) | (bs / div);
                int o = src[row + Math.Max(x - r, 0)], n = src[row + Math.Min(x + r + 1, w - 1)];
                rs += ((n >> 16) & 255) - ((o >> 16) & 255);
                gs += ((n >> 8) & 255) - ((o >> 8) & 255);
                bs += (n & 255) - (o & 255);
            }
        }

        // Vertical pass, one running sum per column, walking rows so it stays cache friendly.
        Array.Clear(_sumR); Array.Clear(_sumG); Array.Clear(_sumB);
        for (int i = -r; i <= r; i++)
        {
            int row = Math.Clamp(i, 0, h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                int c = tmp[row + x];
                _sumR[x] += (c >> 16) & 255; _sumG[x] += (c >> 8) & 255; _sumB[x] += c & 255;
            }
        }
        for (int y = 0; y < h; y++)
        {
            int row = y * w, oldRow = Math.Max(y - r, 0) * w, newRow = Math.Min(y + r + 1, h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                int cr = _sumR[x] / div, cg = _sumG[x] / div, cb = _sumB[x] / div;
                int l = (54 * cr + 183 * cg + 19 * cb) >> 8;
                cr = Math.Clamp(l + (((cr - l) * Saturation) >> 8), 0, 255);
                cg = Math.Clamp(l + (((cg - l) * Saturation) >> 8), 0, 255);
                cb = Math.Clamp(l + (((cb - l) * Saturation) >> 8), 0, 255);
                dst[row + x] = (cr << 16) | (cg << 8) | cb;
                int o = tmp[oldRow + x], n = tmp[newRow + x];
                _sumR[x] += ((n >> 16) & 255) - ((o >> 16) & 255);
                _sumG[x] += ((n >> 8) & 255) - ((o >> 8) & 255);
                _sumB[x] += (n & 255) - (o & 255);
            }
        }
    }

    /// <summary>
    /// Precomputes the curved bezel for this panel height. Like a convex lens, the bend stays near zero inside
    /// and rises steeply toward the rim. Only recalculated when the height changes.
    /// </summary>
    private void BuildBezelMap(int h)
    {
        _mapH = h;
        _mapCount = 0;
        int w = _w;
        double r = _radiusDip * _scale, band = 16 * _scale, shift = 14 * _scale;
        double cx = w / 2.0, cy = h / 2.0, hx = w / 2.0 - r, hy = h / 2.0 - r;
        int side = (int)Math.Ceiling(band);

        for (int py = 0; py < h; py++)
        {
            // Rows between the corners only have bezel pixels at their two ends.
            bool fullRow = py < r + 1 || py >= h - r - 1;
            for (int px = 0; px < w; px++)
            {
                if (!fullRow && px == side) px = Math.Max(side, w - side);
                double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
                double qx = Math.Abs(dx) - hx, qy = Math.Abs(dy) - hy;
                double nx, ny, dist;
                if (qx > 0 && qy > 0)
                {
                    double len = Math.Sqrt(qx * qx + qy * qy);
                    dist = len - r;
                    nx = qx / len * Math.Sign(dx);
                    ny = qy / len * Math.Sign(dy);
                }
                else if (qx > qy) { dist = qx - r; nx = Math.Sign(dx); ny = 0; }
                else { dist = qy - r; nx = 0; ny = Math.Sign(dy); }

                double inside = -dist;
                if (inside >= band) continue;

                double t = 1 - Math.Max(0, inside) / band;
                double slope = t / Math.Sqrt(1 - 0.96 * t * t);
                double amount = shift * Math.Min(slope, 3.5) / 3.5;
                // Specular rim: bright where the bezel faces the light, softer on the opposite side.
                double facing = nx * LightX + ny * LightY;
                double thin = Math.Pow(t, 10), soft = t * t * t;
                double spec = thin * (0.42 * Math.Max(facing, 0) + 0.2 * Math.Max(-facing, 0)) + soft * 0.05 * Math.Max(facing, 0);

                if (_mapCount == _mapX.Length)
                {
                    int size = _mapCount * 2;
                    Array.Resize(ref _mapX, size); Array.Resize(ref _mapY, size); Array.Resize(ref _mapSpec, size);
                    Array.Resize(ref _mapDx, size); Array.Resize(ref _mapDy, size);
                }
                int m = _mapCount++;
                _mapX[m] = px; _mapY[m] = py;
                _mapDx[m] = (float)(-nx * amount); _mapDy[m] = (float)(-ny * amount);
                _mapSpec[m] = (int)(spec * 256);
            }
        }
    }

    private void ApplyBezel(int h)
    {
        var src = _clean;
        var dst = _back;
        int w = _w;
        for (int m = 0; m < _mapCount; m++)
        {
            int px = _mapX[m], py = _mapY[m], spec = _mapSpec[m];
            float dx = _mapDx[m], dy = _mapDy[m];
            // Dispersion: red bends a little more than blue.
            int cr = Sample(src, w, h, px + dx * 1.1f, py + dy * 1.1f, 16);
            int cg = Sample(src, w, h, px + dx, py + dy, 8);
            int cb = Sample(src, w, h, px + dx * 0.9f, py + dy * 0.9f, 0);
            cr += ((255 - cr) * spec) >> 8;
            cg += ((255 - cg) * spec) >> 8;
            cb += ((255 - cb) * spec) >> 8;
            dst[py * w + px] = (cr << 16) | (cg << 8) | cb;
        }
    }

    /// <summary>Bilinear sample of one colour channel.</summary>
    private static int Sample(int[] src, int w, int h, float x, float y, int bits)
    {
        x = Math.Clamp(x, 0, w - 1.001f);
        y = Math.Clamp(y, 0, h - 1.001f);
        int x0 = (int)x, y0 = (int)y;
        float fx = x - x0, fy = y - y0;
        int row = y0 * w, next = h > 1 ? row + w : row;
        float a = ((src[row + x0] >> bits) & 255) * (1 - fx) + ((src[row + x0 + 1] >> bits) & 255) * fx;
        float b = ((src[next + x0] >> bits) & 255) * (1 - fx) + ((src[next + x0 + 1] >> bits) & 255) * fx;
        return (int)(a * (1 - fy) + b * fy);
    }
}
