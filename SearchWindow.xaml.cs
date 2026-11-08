using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Odak;

/// <summary>
/// The glass panel: one field, and the answer streaming in right below it. Closing it forgets the conversation;
/// nothing is written to disk.
/// </summary>
public partial class SearchWindow : Window
{
    private const int HotkeyId = 0x0D4B;
    private const double GlassLeft = 40, GlassTop = 24, GlassWidth = 720, GlassMaxHeight = 540, GlassRadius = 28, ShadowBelow = 56, CompactHeight = 68;

    private readonly List<ChatTurn> _history = new();
    private CancellationTokenSource? _request;
    private Backdrop? _backdrop;
    private bool _useMagnifier, _captureExcluded, _closing;
    private double _windowScale = 1;
    private WinKeyHook? _winKey;
    private readonly Spring _open, _height;

    public Action<string, string, string?>? Notify { get; set; }
    public string HotkeyText { get; private set; } = "Alt + Shift + Space";
    public event Action? HotkeyChanged;

    /// <summary>What opens the panel, for the tray and Settings.</summary>
    public string OpenWithText => AppSettings.Current.UseWindowsKey && _winKey != null ? "Win" : HotkeyText;

    private static bool ReduceMotion => !SystemParameters.ClientAreaAnimation;

    public SearchWindow()
    {
        InitializeComponent();
        _open = new Spring(0, ApplyOpen);
        _height = new Spring(CompactHeight, h => Panel.Height = Math.Max(CompactHeight, h));
        Deactivated += (_, _) => HideSpotlight();
        AppSettings.Changed += () => Dispatcher.Invoke(ApplyAppearance);
        ApplyAppearance();
    }

    private void ApplyOpen(double p)
    {
        Panel.Opacity = Math.Clamp(p, 0, 1);
        PanelScale.ScaleX = PanelScale.ScaleY = ReduceMotion ? 1 : 0.94 + 0.06 * p;
    }

    public void InitializeHidden()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor = Colors.Transparent;
        RegisterSavedHotkey(hwnd);
        ApplyWindowsKey();
    }

    // ---- Keyboard shortcut

    public void UnregisterHotkey()
    {
        Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
        StopWindowsKey();
    }

    public bool ApplyWindowsKey()
    {
        StopWindowsKey();
        bool ok = true;
        if (AppSettings.Current.UseWindowsKey)
        {
            _winKey = new WinKeyHook(() => Dispatcher.BeginInvoke(Toggle));
            ok = _winKey.IsActive;
            if (!ok) StopWindowsKey();
        }
        HotkeyChanged?.Invoke();
        return ok;
    }

    public void CaptureWindowsKey(Action onTap)
    {
        StopWindowsKey();
        _winKey = new WinKeyHook(() => Dispatcher.BeginInvoke(onTap));
    }

    private void StopWindowsKey()
    {
        _winKey?.Dispose();
        _winKey = null;
    }

    public bool TrySetHotkey(uint modifiers, uint key)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        ApplyWindowsKey();
        Native.UnregisterHotKey(hwnd, HotkeyId);
        if (!Native.RegisterHotKey(hwnd, HotkeyId, modifiers | Native.MOD_NOREPEAT, key))
        {
            var old = AppSettings.Current;
            Native.RegisterHotKey(hwnd, HotkeyId, old.HotkeyModifiers | Native.MOD_NOREPEAT, old.HotkeyKey);
            return false;
        }
        var s = AppSettings.Current;
        s.HotkeyModifiers = modifiers;
        s.HotkeyKey = key;
        HotkeyText = FormatHotkey(modifiers, key);
        s.Save();
        HotkeyChanged?.Invoke();
        return true;
    }

    public void SuspendHotkey()
    {
        Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
        StopWindowsKey();
    }

    public void ResumeHotkey()
    {
        RegisterSavedHotkey(new WindowInteropHelper(this).Handle);
        ApplyWindowsKey();
    }

    private void RegisterSavedHotkey(IntPtr hwnd)
    {
        var s = AppSettings.Current;
        if (Native.RegisterHotKey(hwnd, HotkeyId, s.HotkeyModifiers | Native.MOD_NOREPEAT, s.HotkeyKey))
            HotkeyText = FormatHotkey(s.HotkeyModifiers, s.HotkeyKey);
        else if (Native.RegisterHotKey(hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_NOREPEAT, Native.VK_SPACE))
            HotkeyText = FormatHotkey(Native.MOD_CONTROL, Native.VK_SPACE);
        else
            HotkeyText = L.T("no shortcut", "kısayol yok");
    }

    public static string FormatHotkey(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
        var k = KeyInterop.KeyFromVirtualKey((int)key);
        parts.Add(k switch
        {
            Key.Space => "Space",
            Key.OemPeriod => ".",
            Key.OemComma => ",",
            Key.OemQuestion => "/",
            Key.Oem1 => ";",
            Key.OemQuotes => "'",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
            _ => k.ToString(),
        });
        return string.Join(" + ", parts);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Toggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---- Show and hide

    public void Toggle()
    {
        if (IsVisible && !_closing) HideSpotlight();
        else ShowSpotlight();
    }

    public void ShowSpotlight()
    {
        if (IsVisible)
        {
            _closing = false;
            _open.To(1, ReduceMotion ? 0.2 : 0.32, 1.0);
            Activate();
            Query.Focus();
            return;
        }
        ApplyAppearance();
        var hwnd = new WindowInteropHelper(this).Handle;
        // The glass reads the screen behind the panel, leaving the panel out, so it never sees itself and the panel
        // stays visible to Discord and screenshots. PCs without the magnifier hide the panel from capture instead.
        _useMagnifier = MagCapture.Start(hwnd);
        _captureExcluded = !_useMagnifier && Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE)
                           && Native.GetWindowDisplayAffinity(hwnd, out var affinity) && affinity == Native.WDA_EXCLUDEFROMCAPTURE;
        int glassX, glassY;
        double scale;
        if (Native.MonitorUnderCursor() is { } monitor)
        {
            scale = monitor.scale;
            int workH = monitor.work.Bottom - monitor.work.Top;
            int x = monitor.work.Left + (monitor.work.Right - monitor.work.Left - (int)Math.Round(Width * scale)) / 2;
            int y = monitor.work.Top + (int)(workH * 0.2) - (int)Math.Round(GlassTop * scale);
            Native.MoveWindowTo(hwnd, x, y);
            Left = x / scale;
            Top = y / scale;
            glassX = x + (int)Math.Round(GlassLeft * scale);
            glassY = y + (int)Math.Round(GlassTop * scale);
        }
        else
        {
            var area = SystemParameters.WorkArea;
            scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + area.Height * 0.2 - GlassTop;
            glassX = (int)Math.Round((Left + GlassLeft) * scale);
            glassY = (int)Math.Round((Top + GlassTop) * scale);
        }
        _windowScale = scale;
        _height.Snap(CompactHeight);
        SetClickArea(CompactHeight);
        _backdrop = new Backdrop(glassX, glassY, (int)Math.Round(GlassWidth * scale), (int)Math.Round(GlassMaxHeight * scale),
                                 scale, GlassRadius, CompactHeight, _useMagnifier || _captureExcluded, _useMagnifier, SetGlass);
        _backdrop.Start();
        _closing = false;
        _open.Snap(0);
        Show();
        Activate();
        Native.SetForegroundWindow(hwnd);
        Query.Focus();
        _open.To(1, ReduceMotion ? 0.2 : 0.32, 1.0);
    }

    public void HideSpotlight()
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        _request?.Cancel();
        _open.To(0, ReduceMotion ? 0.15 : 0.22, 1.0, settled: () =>
        {
            if (!_closing) return;
            _closing = false;
            Hide();
            Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, Native.WDA_NONE);
            _backdrop?.Stop();
            _backdrop = null;
            Glass.Background = Brushes.Transparent;
            // Nothing stays: the next open starts empty, with a new conversation.
            _history.Clear();
            Query.Clear();
            SetAnswer("");
        });
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Panel.IsMouseOver) HideSpotlight();
    }

    // ---- Panel size follows the answer

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double target = Math.Min(e.NewSize.Height, GlassMaxHeight);
        if (!IsVisible || ReduceMotion)
        {
            _height.Snap(target);
            RequestGlass(target);
            return;
        }
        RequestGlass(Math.Max(target, Panel.ActualHeight));
        _height.To(target, 0.3, 1.0, settled: () => RequestGlass(target));
    }

    private void OnContentHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        ContentHost.Clip = new RectangleGeometry(new Rect(e.NewSize), GlassRadius, GlassRadius);

    private void RequestGlass(double heightDip)
    {
        _backdrop?.SetHeight(heightDip);
        SetClickArea(heightDip);
    }

    private void SetClickArea(double heightDip) =>
        Native.SetWindowRegion(new WindowInteropHelper(this).Handle, (int)Math.Ceiling(Width * _windowScale), (int)Math.Ceiling((GlassTop + heightDip + ShadowBelow) * _windowScale));

    private void SetGlass(ImageSource image) =>
        Glass.Background = new ImageBrush(image) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };

    private void ApplyAppearance()
    {
        bool dark = AppSettings.Current.IsDark;
        Resources["Fg"] = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E));
        Resources["FgDim"] = new SolidColorBrush(dark ? Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x3C, 0x3C, 0x43));
        // Stronger than Spotlight's tint: paragraphs have to stay readable over anything behind the glass.
        Resources["Tint"] = new SolidColorBrush(dark ? Color.FromArgb(0x8C, 0x1C, 0x1C, 0x1E) : Color.FromArgb(0xA6, 0xF7, 0xF7, 0xFA));
        Resources["Chip"] = new SolidColorBrush(dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        Resources["Hairline"] = new SolidColorBrush(dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        ContentStack.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = dark ? Colors.Black : Colors.White, BlurRadius = 9, ShadowDepth = 0, Opacity = dark ? 0.6 : 0.75,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
        };
        Placeholder.Text = L.T("Ask anything", "Bir şey sor");
    }

    // ---- Asking

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => SettingsWindow.Open(this);

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideSpotlight(); e.Handled = true; }
        else if (e.Key == Key.Enter) { _ = AskAsync(); e.Handled = true; }
        else if (e.Key == Key.Down && Answer.Text.Length > 0) { AnswerScroll.LineDown(); e.Handled = true; }
        else if (e.Key == Key.Up && Answer.Text.Length > 0) { AnswerScroll.LineUp(); e.Handled = true; }
    }

    private void OnAnswerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideSpotlight(); e.Handled = true; }
    }

    private void SetAnswer(string text)
    {
        Answer.Text = text;
        var visible = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AnswerScroll.Visibility = visible;
        Divider.Visibility = visible;
    }

    private async Task AskAsync()
    {
        var question = Query.Text.Trim();
        if (question.Length == 0) return;
        var settings = AppSettings.Current;
        var key = settings.GetKey();
        if (key == null)
        {
            SetAnswer(L.T("Add your OpenRouter API key in Settings to start.", "Başlamak için Ayarlar'a OpenRouter API anahtarını ekle."));
            SettingsWindow.Open(this);
            return;
        }

        _request?.Cancel();
        var request = _request = new CancellationTokenSource();
        _history.Add(new ChatTurn(true, question));
        Query.Clear();
        SetAnswer("…");
        var answer = new System.Text.StringBuilder();
        try
        {
            await foreach (var piece in AiClient.StreamAsync(key, settings.GetModel(), _history.ToArray(), request.Token))
            {
                if (request.IsCancellationRequested) return;
                answer.Append(piece);
                SetAnswer(answer.ToString());
                AnswerScroll.ScrollToEnd();
            }
            if (answer.Length == 0) SetAnswer(L.T("The service sent an empty answer.", "Hizmet boş cevap gönderdi."));
            else _history.Add(new ChatTurn(false, answer.ToString()));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is AiException or System.Net.Http.HttpRequestException or System.IO.IOException or System.Text.Json.JsonException)
        {
            if (request.IsCancellationRequested) return;
            if (_history.Count > 0 && _history[^1].IsUser && _history[^1].Text == question) _history.RemoveAt(_history.Count - 1);
            SetAnswer(ex is AiException ? ex.Message : L.T("Could not reach the service. Check your connection.", "Hizmete ulaşılamadı. Bağlantını kontrol et."));
            if (Query.Text.Length == 0) { Query.Text = question; Query.SelectAll(); }
        }
    }
}
