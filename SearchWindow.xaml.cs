using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Odak;

/// <summary>
/// The glass panel. Each AI service runs on its own official website inside it, and the user signs in there with
/// their own account. The app never sees passwords or reads conversations; sign-ins stay in a local browser profile.
/// A service's browser is only created when first opened, and is suspended while the panel is hidden.
/// </summary>
public partial class SearchWindow : Window
{
    private const int HotkeyId = 0x0D4B;
    private const double GlassLeft = 40, GlassTop = 24, GlassWidth = 860, GlassMaxHeight = 680, GlassRadius = 28, ShadowBelow = 56;

    private readonly Dictionary<AiProvider, WebView2CompositionControl> _views = new();
    private CoreWebView2Environment? _environment;
    private Backdrop? _backdrop;
    private bool _useMagnifier;
    private bool _captureExcluded, _closing;
    private double _panelHeight = GlassMaxHeight, _windowScale = 1;
    private WinKeyHook? _winKey;

    private const double CompactHeight = 68;
    private readonly Spring _open, _height;
    private bool _expanded;
    private readonly HashSet<AiProvider> _stale = new(); // has an old conversation on screen
    private readonly HashSet<AiProvider> _loaded = new();
    private readonly Dictionary<AiProvider, string> _pending = new();
    private string? _sending;

    public Action<string, string, string?>? Notify { get; set; }
    public string HotkeyText { get; private set; } = "Alt + Shift + Space";
    public event Action? HotkeyChanged;

    /// <summary>What opens the panel, for the tray and Settings.</summary>
    public string OpenWithText => AppSettings.Current.UseWindowsKey && _winKey != null ? "Win" : HotkeyText;

    private static bool ReduceMotion => !SystemParameters.ClientAreaAnimation;
    private static string WebProfile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotlightAI", "web");

    public SearchWindow()
    {
        InitializeComponent();
        _open = new Spring(0, ApplyOpen);
        _height = new Spring(CompactHeight, h => Panel.Height = Math.Max(CompactHeight, h));
        Deactivated += (_, _) => HideSpotlight();
        AppSettings.Changed += () => Dispatcher.Invoke(() =>
        {
            ApplyAppearance();
            if (IsVisible && !_closing && _expanded) _ = ShowProviderAsync();
        });
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
        // The live glass reads the screen behind the window and must never see itself. The magnifier leaves this
        // window out of what it reads, so the panel stays visible to Discord and screenshots.
        _useMagnifier = MagCapture.Start(hwnd);
        // Fallback for PCs without it. Side effect: the panel does not appear in screenshots or screen recordings.
        _captureExcluded = !_useMagnifier && Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE)
                           && Native.GetWindowDisplayAffinity(hwnd, out var affinity) && affinity == Native.WDA_EXCLUDEFROMCAPTURE;
        int glassX, glassY;
        double scale;
        if (Native.MonitorUnderCursor() is { } monitor)
        {
            scale = monitor.scale;
            int workH = monitor.work.Bottom - monitor.work.Top;
            _panelHeight = Math.Min(GlassMaxHeight, workH / scale - 2 * GlassTop - ShadowBelow);
            int x = monitor.work.Left + (monitor.work.Right - monitor.work.Left - (int)Math.Round(Width * scale)) / 2;
            int y = monitor.work.Top + (int)Math.Min(workH * 0.14, Math.Max(0, workH - (_panelHeight + GlassTop + ShadowBelow) * scale));
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
            _panelHeight = Math.Min(GlassMaxHeight, area.Height - 2 * GlassTop - ShadowBelow);
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top;
            glassX = (int)Math.Round((Left + GlassLeft) * scale);
            glassY = (int)Math.Round((Top + GlassTop) * scale);
        }
        _windowScale = scale;
        // Every open starts clean: just the empty field.
        double start = CompactHeight;
        Content.Height = _panelHeight;
        _height.Snap(start);
        SetClickArea(start);
        _backdrop = new Backdrop(glassX, glassY, (int)Math.Round(GlassWidth * scale), (int)Math.Round(_panelHeight * scale),
                                 scale, GlassRadius, start, _useMagnifier || _captureExcluded, _useMagnifier, SetGlass);
        _backdrop.Start();
        _closing = false;
        _open.Snap(0);
        Show();
        Activate();
        Native.SetForegroundWindow(hwnd);
        Query.Focus();
        Query.SelectAll();
        _open.To(1, ReduceMotion ? 0.2 : 0.32, 1.0);
        MarkProvider();
    }

    private void SetClickArea(double heightDip) =>
        Native.SetWindowRegion(new WindowInteropHelper(this).Handle, (int)Math.Ceiling(Width * _windowScale), (int)Math.Ceiling((GlassTop + heightDip + ShadowBelow) * _windowScale));

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        double target = expanded ? _panelHeight : CompactHeight;
        WebHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (!expanded) Message.Visibility = Visibility.Collapsed;
        if (!IsVisible) return;
        // While it springs, the glass and click area cover the taller of the two heights.
        double cover = Math.Max(target, Panel.ActualHeight);
        _backdrop?.SetHeight(cover);
        SetClickArea(cover);
        void Settled() { _backdrop?.SetHeight(target); SetClickArea(target); }
        if (ReduceMotion) { _height.Snap(target); Settled(); }
        else _height.To(target, 0.35, 1.0, settled: Settled);
    }

    // ---- The field

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideSpotlight(); e.Handled = true; }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { _ = SubmitAsync(); e.Handled = true; }
    }

    /// <summary>Opens the chosen site in the panel and hands it what was typed, exactly as if the user had typed it there.</summary>
    private async Task SubmitAsync()
    {
        var text = Query.Text.Trim();
        var provider = AppSettings.Current.Provider;
        if (!_expanded) SetExpanded(true);
        await ShowProviderAsync();
        if (text.Length == 0) { FocusProvider(); return; }
        if (!_views.TryGetValue(provider, out var view) || view.CoreWebView2 == null) return;
        Message.Visibility = Visibility.Collapsed;
        _sending = text;
        if (_loaded.Contains(provider)) await SendAsync(view.CoreWebView2, provider, text);
        else _pending[provider] = text; // sent once the page has loaded
    }

    private static Task<string> SendAsync(CoreWebView2 core, AiProvider provider, string text)
    {
        var (editor, send) = provider switch
        {
            AiProvider.Gemini => (".ql-editor[contenteditable=true]", "button.send-button, button[aria-label*='Send' i], button[aria-label*='Gönder' i]"),
            _ => ("#prompt-textarea", "[data-testid=send-button], #composer-submit-button, button[aria-label*='Send' i], button[aria-label*='Gönder' i]"),
        };
        string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        // Only writes into the site's own input and presses its own button. It never reads the conversation.
        return core.ExecuteScriptAsync($$"""
            (async () => {
              const wait = ms => new Promise(r => setTimeout(r, ms));
              const visible = x => { const r = x.getBoundingClientRect(); return r.width > 40 && r.height > 8 && getComputedStyle(x).visibility !== 'hidden'; };
              const find = () => {
                const known = document.querySelector({{J(editor)}});
                if (known && visible(known)) return known;
                const all = [...document.querySelectorAll('textarea, [contenteditable=true], [role=textbox]')].filter(x => visible(x) && !x.readOnly && !x.disabled);
                return all.sort((a, b) => b.getBoundingClientRect().bottom - a.getBoundingClientRect().bottom)[0] || null;
              };
              let e = null;
              for (let i = 0; i < 60 && !e; i++) { e = find(); if (!e) await wait(250); }
              if (!e) { chrome.webview.postMessage('noeditor'); return; }
              e.focus();
              if (e.tagName === 'TEXTAREA') {
                Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(e, {{J(text)}});
                e.dispatchEvent(new Event('input', { bubbles: true }));
              } else {
                document.execCommand('insertText', false, {{J(text)}});
              }
              for (let i = 0; i < 40; i++) {
                const b = document.querySelector({{J(send)}});
                if (b && !b.disabled) { b.click(); chrome.webview.postMessage('sent'); return; }
                await wait(100);
              }
              // No button found: the sites also send on Enter.
              e.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }));
              await wait(400);
              const left = e.tagName === 'TEXTAREA' ? e.value : e.textContent;
              chrome.webview.postMessage(left && left.trim() ? 'nosend' : 'sent');
            })();
            """);
    }

    private void OnSiteMessage(string message)
    {
        switch (message)
        {
            case "esc":
                HideSpotlight();
                break;
            case "sent":
                if (Query.Text.Trim() == _sending) Query.Clear();
                Query.Focus();
                break;
            case "noeditor":
                ShowNote(L.T("Sign in on the page below, then press Enter again. Your text is still here.",
                             "Aşağıdaki sayfada giriş yap, sonra yeniden Enter'a bas. Yazın burada duruyor."));
                break;
            case "nosend":
                ShowNote(L.T("Your text is in the site's box. Press its send button.", "Yazın sitenin kutusunda. Sitenin gönder düğmesine bas."));
                break;
        }
    }

    private void ShowNote(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
    }

    public void HideSpotlight()
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        _open.To(0, ReduceMotion ? 0.15 : 0.22, 1.0, settled: () =>
        {
            if (!_closing) return;
            _closing = false;
            Hide();
            // Nothing stays: the field empties, the panel shrinks, and the next question starts a new conversation.
            SetExpanded(false);
            Query.Clear();
            _pending.Clear();
            foreach (var provider in _views.Keys) _stale.Add(provider);
            // Must be undone while hidden and set again on the next open: otherwise, after a Hide/Show cycle,
            // Windows returns this window's area as solid black to screen capture.
            Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, Native.WDA_NONE);
            _backdrop?.Stop();
            _backdrop = null;
            Glass.Background = Brushes.Transparent;
            SuspendViews();
        });
    }

    /// <summary>Clicks on the see-through margin around the panel count as clicking elsewhere.</summary>
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Panel.IsMouseOver) HideSpotlight();
    }

    // ---- Soft corners of the web view

    private const double WebCorner = 20, WebFeather = 7;

    /// <summary>A blurred rounded rectangle fades the site's edges into the glass. Drawn once per size, then just an image.</summary>
    private void OnWebHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = e.NewSize.Width, h = e.NewSize.Height;
        if (w < 1 || h < 1) return;
        var shape = new System.Windows.Controls.Border
        {
            Width = w, Height = h,
            Padding = new Thickness(WebFeather),
            Background = Brushes.Transparent,
            Child = new System.Windows.Controls.Border
            {
                Background = Brushes.Black,
                CornerRadius = new CornerRadius(WebCorner),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = WebFeather * 2, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality },
            },
        };
        shape.Measure(e.NewSize);
        shape.Arrange(new Rect(e.NewSize));
        double scale = Math.Max(1, _windowScale);
        // (the row below the field)
        var mask = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        mask.Render(shape);
        mask.Freeze();
        WebHost.OpacityMask = new ImageBrush(mask) { Stretch = Stretch.Fill };
    }

    private void SetGlass(ImageSource image) =>
        Glass.Background = new ImageBrush(image) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };

    private void ApplyAppearance()
    {
        bool dark = AppSettings.Current.IsDark;
        Resources["Fg"] = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E));
        Resources["FgDim"] = new SolidColorBrush(dark ? Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x3C, 0x3C, 0x43));
        Resources["Tint"] = new SolidColorBrush(dark ? Color.FromArgb(0x42, 0x1C, 0x1C, 0x1E) : Color.FromArgb(0x52, 0xF7, 0xF7, 0xFA));
        Resources["Chip"] = new SolidColorBrush(dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        MarkProvider();
    }

    private void MarkProvider()
    {
        Placeholder.Text = L.T($"Ask {ProviderName}", $"{ProviderName}'a sor").Replace("Gemini'a", "Gemini'ye").Replace("ChatGPT'a", "ChatGPT'ye");
        var current = AppSettings.Current.Provider;
        foreach (var (button, provider) in new[] { (ChatGptButton, AiProvider.ChatGpt), (GeminiButton, AiProvider.Gemini) })
        {
            button.SetResourceReference(BackgroundProperty, provider == current ? "Chip" : "NoChip");
            button.FontWeight = provider == current ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ---- The AI services

    private static string ProviderName => AppSettings.Current.Provider == AiProvider.Gemini ? "Gemini" : "ChatGPT";


    private static string HomeUrl(AiProvider p) => p switch
    {
        AiProvider.Gemini => "https://gemini.google.com/app",
        _ => "https://chatgpt.com/",
    };

    private void OnChatGptClicked(object sender, RoutedEventArgs e) => SelectProvider(AiProvider.ChatGpt);
    private void OnGeminiClicked(object sender, RoutedEventArgs e) => SelectProvider(AiProvider.Gemini);

    private void SelectProvider(AiProvider provider)
    {
        var s = AppSettings.Current;
        if (s.Provider != provider)
        {
            s.Provider = provider;
            s.Save();
        }
        MarkProvider();
        Query.Focus();
    }

    private void OnReloadClicked(object sender, RoutedEventArgs e)
    {
        if (_views.TryGetValue(AppSettings.Current.Provider, out var view)) view.CoreWebView2?.Reload();
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => SettingsWindow.Open(this);

    private async Task ShowProviderAsync()
    {
        var provider = AppSettings.Current.Provider;
        MarkProvider();
        if (!_expanded) return;
        try
        {
            Message.Visibility = Visibility.Collapsed;
            await EnsureViewAsync(provider);
            foreach (var (key, other) in _views)
            {
                other.Visibility = key == provider ? Visibility.Visible : Visibility.Hidden;
                if (key == provider && other.CoreWebView2 is { IsSuspended: true } core) core.Resume();
            }
            // Back to the site's start page, so the old conversation is not shown again.
            if (_stale.Remove(provider) && _views[provider].CoreWebView2 is { } fresh)
            {
                _loaded.Remove(provider);
                fresh.Navigate(HomeUrl(provider));
            }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _creating.Remove(provider);
            ShowMessage(L.T("This app needs the Microsoft Edge WebView2 Runtime. Install it from Microsoft, then open the panel again.",
                            "Bu uygulama Microsoft Edge WebView2 Runtime gerektirir. Microsoft'tan kurup paneli yeniden aç."));
        }
        catch (Exception ex)
        {
            _creating.Remove(provider);
            ShowMessage(ex.Message);
        }
    }

    private readonly Dictionary<AiProvider, Task> _creating = new();

    private Task EnsureViewAsync(AiProvider provider)
    {
        if (!_creating.TryGetValue(provider, out var task)) _creating[provider] = task = CreateViewAsync(provider);
        return task;
    }

    private async Task CreateViewAsync(AiProvider provider)
    {
        _environment ??= await CoreWebView2Environment.CreateAsync(null, WebProfile);
        var view = new WebView2CompositionControl();
        _views[provider] = view;
        WebHost.Children.Add(view);
        await view.EnsureCoreWebView2Async(_environment);
        Configure(view.CoreWebView2, provider);
        view.CoreWebView2.Navigate(HomeUrl(provider));
    }

    private void ShowMessage(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
    }

    private void FocusProvider()
    {
        if (_views.TryGetValue(AppSettings.Current.Provider, out var view)) view.Focus();
    }

    private void Configure(CoreWebView2 core, AiProvider provider)
    {
        core.NavigationCompleted += async (_, _) =>
        {
            _loaded.Add(provider);
            if (_pending.Remove(provider, out var text)) await SendAsync(core, provider, text);
        };
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = true;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        // Esc closes the panel. This is the only script added to these sites: it reads nothing and only reports that key.
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(
            "addEventListener('keydown',e=>{if(e.key==='Escape'&&!e.defaultPrevented)chrome.webview.postMessage('esc')});");
        core.WebMessageReceived += (_, e) =>
        {
            OnSiteMessage(e.TryGetWebMessageAsString());
        };
        // Sign-in pages stay inside the panel; ordinary links open in the default browser.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
            if (IsAccountHost(uri.Host)) core.Navigate(e.Uri);
            else Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        };
        core.PermissionRequested += (_, e) =>
        {
            // Nothing is granted silently; the app asks no site for the camera, microphone or location by itself.
            if (e.PermissionKind != CoreWebView2PermissionKind.ClipboardRead) e.State = CoreWebView2PermissionState.Deny;
        };
    }

    private static bool IsAccountHost(string host)
    {
        foreach (var d in new[] { "chatgpt.com", "openai.com", "google.com", "gemini.google.com", "apple.com", "live.com", "microsoft.com", "microsoftonline.com" })
            if (host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Hidden services give back most of their memory until the panel opens again.</summary>
    private void SuspendViews()
    {
        foreach (var view in _views.Values)
        {
            try
            {
                view.Visibility = Visibility.Hidden;
                if (view.CoreWebView2 is { IsSuspended: false } core) _ = core.TrySuspendAsync();
            }
            catch { }
        }
    }
}
