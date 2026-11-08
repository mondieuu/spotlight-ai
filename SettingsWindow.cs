using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Odak;

/// <summary>
/// Settings, styled after Apple's Settings: grouped rounded lists, coloured icon squares, iOS switches and
/// segmented controls. Every change applies immediately; there is no Save button.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _instance;
    private readonly SearchWindow _search;
    private readonly StackPanel _content = new() { Margin = new Thickness(22, 14, 22, 24) };
    private readonly Border _card = new() { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1) };
    private bool _builtDark, _builtTurkish;
    private Button? _recorder;
    private TextBlock? _recorderNote;
    private bool _recording;

    private Brush _fg = Brushes.White, _fgDim = Brushes.Gray, _groupBg = Brushes.Transparent, _segBg = Brushes.Transparent,
        _segOn = Brushes.Transparent, _hair = Brushes.Transparent, _switchOff = Brushes.Gray;

    private static readonly Brush SwitchOn = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));

    /// <summary>Opens the window, or brings the open one to the front.</summary>
    public static void Open(SearchWindow search)
    {
        if (_instance != null)
        {
            _instance.Activate();
            return;
        }
        _instance = new SettingsWindow(search);
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.Activate();
    }

    private SettingsWindow(SearchWindow search)
    {
        _search = search;
        Width = 580;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        Topmost = true;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/odak.ico")); } catch { }

        _card.Child = _content;
        _card.Effect = new DropShadowEffect { BlurRadius = 46, ShadowDepth = 14, Direction = 270, Opacity = 0.38 };
        _card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && !_recording) DragMove(); };
        Content = new Border { Padding = new Thickness(30, 20, 30, 44), Child = _card };
        PreviewKeyDown += OnKey;

        AppSettings.Changed += OnSettingsChanged;
        Updater.Changed += OnUpdaterChanged;
        Closed += (_, _) =>
        {
            AppSettings.Changed -= OnSettingsChanged;
            Updater.Changed -= OnUpdaterChanged;
            if (_recording) _search.ResumeHotkey();
        };
        Rebuild();
        // Like Apple's Software Update pane: look for a new version as soon as Settings opens.
        // Stays offline if automatic checks are turned off; "Check Now" still works.
        if (AppSettings.Current.AutoCheckUpdates && Updater.Status is Updater.State.Idle or Updater.State.Failed or Updater.State.UpToDate) _ = Updater.CheckAsync();
    }

    /// <summary>Only theme and language changes redraw the window, so switches can finish their animation.</summary>
    private void OnSettingsChanged()
    {
        var s = AppSettings.Current;
        if (s.IsDark != _builtDark || s.IsTurkish != _builtTurkish) Dispatcher.Invoke(Rebuild);
    }

    private void Rebuild()
    {
        var s = AppSettings.Current;
        bool dark = _builtDark = s.IsDark;
        _builtTurkish = s.IsTurkish;
        _fg = Frozen(dark ? Colors.White : Colors.Black);
        _fgDim = Frozen(dark ? Color.FromArgb(0x99, 0xEB, 0xEB, 0xF5) : Color.FromArgb(0x99, 0x3C, 0x3C, 0x43));
        _groupBg = Frozen(dark ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Colors.White);
        _segBg = Frozen(dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xE9, 0xE9, 0xEB));
        _segOn = Frozen(dark ? Color.FromRgb(0x63, 0x63, 0x66) : Colors.White);
        _hair = Frozen(dark ? Color.FromArgb(0x5C, 0x54, 0x54, 0x58) : Color.FromArgb(0x4A, 0x3C, 0x3C, 0x43));
        _switchOff = Frozen(dark ? Color.FromRgb(0x39, 0x39, 0x3D) : Color.FromRgb(0xE9, 0xE9, 0xEB));
        _card.Background = Frozen(dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF2, 0xF7));
        _card.BorderBrush = Frozen(dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        Title = L.T("Spotlight AI Settings", "Spotlight AI Ayarları");

        _content.Children.Clear();
        _content.Children.Add(TitleBar());
        _content.Children.Add(AppCard());

        // Appearance
        // Appearance
        _content.Children.Add(Group(
            Row("", Color.FromRgb(0xAF, 0x52, 0xDE), L.T("Appearance", "Görünüm"), Segmented(
                new[] { L.T("System", "Sistem"), L.T("Light", "Açık"), L.T("Dark", "Koyu") },
                (int)s.Theme,
                i => { s.Theme = (ThemeMode)i; s.Save(); }))));
        _content.Children.Add(Footnote(L.T("System follows the Windows light or dark app mode.",
                                           "Sistem, Windows'un açık/koyu uygulama moduna uyar.")));

        // Language and search
        _content.Children.Add(Group(
            Row("", Color.FromRgb(0x0A, 0x84, 0xFF), L.T("Language", "Dil"), Segmented(
                new[] { "English", "Türkçe" },
                s.IsTurkish ? 1 : 0,
                i => { s.Language = i == 1 ? "tr" : "en"; s.Save(); })),
            KeyRow(s),
            ModelRow(s)));
        _content.Children.Add(Footnote(L.T("Your key is encrypted on this PC and only sent to OpenRouter. What you ask goes to OpenRouter and the model you chose, under their terms and pricing; conversations are not saved here. Model names are listed at openrouter.ai/models.",
                                           "Anahtarın bu bilgisayarda şifrelenir ve yalnızca OpenRouter'a gönderilir. Sorduğun şeyler OpenRouter'a ve seçtiğin modele, onların koşul ve ücretleriyle gider; sohbetler burada saklanmaz. Model adları openrouter.ai/models sayfasında.")));

        // General
        _recorder = RecorderButton();
        _content.Children.Add(Group(
            Row("", Color.FromRgb(0x8E, 0x8E, 0x93), L.T("Keyboard shortcut", "Klavye kısayolu"), _recorder),
            Row("\uE765", Color.FromRgb(0x0A, 0x84, 0xFF), L.T("Open with Windows key", "Windows tuşuyla aç"),
                Switch(s.UseWindowsKey, on =>
                {
                    s.UseWindowsKey = on;
                    s.Save();
                    bool ok = _search.ApplyWindowsKey();
                    Dispatcher.BeginInvoke(() =>
                    {
                        Rebuild();
                        if (!ok) SetNote(L.T("Windows did not allow the Windows key option.", "Windows, Windows tuşu seçeneğine izin vermedi."), true);
                    });
                })),
            Row("", Color.FromRgb(0x34, 0xC7, 0x59), L.T("Start with Windows", "Windows ile başlat"),
                Switch(App.IsStartupEnabled(), on => App.SetStartup(on)))));
        _recorderNote = Footnote(L.T("Click the shortcut, then press the new combination. Esc cancels. With the Windows key option, Win+ shortcuts keep working and the Start menu still opens from its taskbar button.",
                                     "Kısayola tıkla, sonra yeni tuş kombinasyonuna bas. Esc iptal eder. Windows tuşu seçeneğinde Win+ kısayolları çalışmaya devam eder, Başlat menüsü görev çubuğundaki düğmesinden yine açılır."));
        _content.Children.Add(_recorderNote);

        // Software Update
        _updateStatus = new TextBlock { Foreground = _fgDim, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _updateButton = new Button
        {
            MinWidth = 104,
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = _fg,
            Background = _segBg,
            Template = RoundTemplate(8),
            Cursor = Cursors.Hand,
        };
        _updateButton.Click += (_, _) =>
        {
            if (Updater.Status == Updater.State.Available) ((App)Application.Current).InstallUpdate();
            else _ = Updater.CheckAsync();
        };
        var updateControl = new StackPanel { Orientation = Orientation.Horizontal };
        updateControl.Children.Add(_updateStatus);
        updateControl.Children.Add(_updateButton);
        _content.Children.Add(Group(
            Row("", Color.FromRgb(0x8E, 0x8E, 0x93), L.T("Software Update", "Yazılım Güncelleme"), updateControl),
            Row("", Color.FromRgb(0x5E, 0x5C, 0xE6), L.T("Check automatically", "Otomatik denetle"),
                Switch(s.AutoCheckUpdates, on => { s.AutoCheckUpdates = on; s.Save(); }))));
        _content.Children.Add(Footnote(L.T("New versions come from the project's GitHub releases and install in place; your settings stay.",
                                           "Yeni sürümler projenin GitHub sürümlerinden gelir ve yerinde kurulur; ayarların korunur.")));
        RefreshUpdate();
    }

    // ---- API key and model

    private Grid KeyRow(AppSettings s)
    {
        bool saved = s.GetKey() != null;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (saved)
        {
            panel.Children.Add(new TextBlock { Text = L.T("Saved", "Kayıtlı"), Foreground = SwitchOn, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            var remove = SmallButton(L.T("Remove", "Kaldır"));
            remove.Click += (_, _) => { s.SetKey(null); Dispatcher.BeginInvoke(Rebuild); };
            panel.Children.Add(remove);
        }
        else
        {
            var box = new PasswordBox { Width = 130, Height = 28, Background = _segBg, Foreground = _fg, CaretBrush = _fg, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0) };
            System.Windows.Automation.AutomationProperties.SetName(box, L.T("API key", "API anahtarı"));
            void Store()
            {
                if (box.Password.Trim().Length == 0) return;
                if (AiClient.FindKey(box.Password) is { } key)
                {
                    s.SetKey(key);
                    Dispatcher.BeginInvoke(Rebuild);
                    return;
                }
                box.Clear();
                SetNote(L.T("That is not an OpenRouter key. A key starts with sk-or-. Nothing was saved.", "Bu bir OpenRouter anahtarı değil. Anahtar sk-or- ile başlar. Hiçbir şey kaydedilmedi."), true);
            }
            var save = SmallButton(L.T("Save", "Kaydet"));
            save.Click += (_, _) => Store();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Store(); };
            var get = SmallButton(L.T("Get key", "Anahtar al"));
            get.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AiClient.KeyPage) { UseShellExecute = true });
            panel.Children.Add(box);
            panel.Children.Add(save);
            panel.Children.Add(get);
        }
        return Row("\uE192", Color.FromRgb(0x34, 0xC7, 0x59), L.T("OpenRouter key", "OpenRouter anahtarı"), panel);
    }

    private Grid ModelRow(AppSettings s)
    {
        var box = new TextBox { Text = s.GetModel(), Width = 250, Height = 28, Background = _segBg, Foreground = _fg, CaretBrush = _fg, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0) };
        box.LostFocus += (_, _) => s.SetModel(box.Text);
        return Row("\uE950", Color.FromRgb(0x5E, 0x5C, 0xE6), L.T("Model", "Model"), box);
    }

    private Button SmallButton(string text) => new()
    {
        Content = text, Height = 28, MinWidth = 60, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0), FontSize = 12.5,
        Foreground = _fg, Background = _segBg, Template = RoundTemplate(7), Cursor = Cursors.Hand,
    };

    // ---- Software Update row

    private TextBlock? _updateStatus;
    private Button? _updateButton;

    private void OnUpdaterChanged() => Dispatcher.BeginInvoke(RefreshUpdate);

    private void RefreshUpdate()
    {
        if (_updateStatus == null || _updateButton == null) return;
        var latest = Updater.Latest;
        _updateButton.IsEnabled = Updater.Status is not (Updater.State.Checking or Updater.State.Downloading);
        _updateButton.Background = Updater.Status == Updater.State.Available ? AccentBrush : _segBg;
        _updateButton.Foreground = Updater.Status == Updater.State.Available ? Brushes.White : _fg;
        _updateStatus.Foreground = Updater.Status == Updater.State.Failed ? Red : _fgDim;
        (_updateStatus.Text, _updateButton.Content) = Updater.Status switch
        {
            Updater.State.Checking => (L.T("Checking…", "Denetleniyor…"), L.T("Check Now", "Şimdi Denetle")),
            Updater.State.Available when latest != null => (L.T($"{latest.Version.ToString(3)} is available", $"{latest.Version.ToString(3)} hazır"), L.T("Update Now", "Şimdi Güncelle")),
            Updater.State.Downloading => ($"{Updater.Progress * 100:0}%", L.T("Updating…", "Güncelleniyor…")),
            Updater.State.UpToDate => (L.T("Up to date", "Güncel"), L.T("Check Now", "Şimdi Denetle")),
            Updater.State.Failed => (L.T("Could not check", "Denetlenemedi"), L.T("Try Again", "Tekrar Dene")),
            _ => ($"{Installer.Version}", L.T("Check Now", "Şimdi Denetle")),
        };
    }

    // ---- Title bar and app card

    private Grid TitleBar()
    {
        var bar = new Grid { Height = 30, Margin = new Thickness(-8, 0, -8, 10) };
        // macOS-style window buttons: red closes, the others are just for looks.
        var lights = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var close = Dot(Color.FromRgb(0xFF, 0x5F, 0x57), true);
        // Swallow the press so the card does not start a window drag (which used to eat the click).
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(); };
        close.ToolTip = L.T("Close", "Kapat");
        lights.Children.Add(close);
        lights.Children.Add(Dot(Color.FromRgb(0xFE, 0xBC, 0x2E), false));
        lights.Children.Add(Dot(Color.FromRgb(0x28, 0xC8, 0x40), false));
        bar.Children.Add(lights);
        bar.Children.Add(new TextBlock
        {
            Text = L.T("Settings", "Ayarlar"),
            Foreground = _fg,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return bar;
    }

    private static Ellipse Dot(Color c, bool active)
    {
        var e = new Ellipse
        {
            Width = 12,
            Height = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Fill = new SolidColorBrush(c),
            Opacity = active ? 1 : 0.55,
            Cursor = active ? Cursors.Hand : Cursors.Arrow,
        };
        return e;
    }

    /// <summary>The top card, like the Apple Account card in Settings.</summary>
    private Border AppCard()
    {
        var grid = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Image { Width = 56, Height = 56, Margin = new Thickness(0, 0, 14, 0) };
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/odak.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapFrame? best = null;
            foreach (var f in decoder.Frames) if (best == null || f.PixelWidth > best.PixelWidth) best = f;
            icon.Source = best;
        }
        catch { }
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Spotlight AI", Foreground = _fg, FontSize = 19, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") });
        text.Children.Add(new TextBlock { Text = L.T($"Version {Installer.Version} · Open with {_search.OpenWithText}", $"Sürüm {Installer.Version} · {_search.OpenWithText} ile aç"), Foreground = _fgDim, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return new Border { CornerRadius = new CornerRadius(12), Background = _groupBg, Child = grid, Margin = new Thickness(0, 0, 0, 22) };
    }

    // ---- Grouped list building blocks

    private Border Group(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            // Inset separator, starting after the icon like iOS.
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = _hair, Margin = new Thickness(54, 0, 0, 0) });
            stack.Children.Add(rows[i]);
        }
        return new Border { CornerRadius = new CornerRadius(12), Background = _groupBg, Child = stack };
    }

    private Grid Row(string glyph, Color iconColor, string label, UIElement control)
    {
        var g = new Grid { MinHeight = 50, Margin = new Thickness(12, 0, 12, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(iconColor),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        var text = new TextBlock { Text = label, Foreground = _fg, FontSize = 14.5, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 2);
        g.Children.Add(control);
        return g;
    }

    private TextBlock Footnote(string text) => new()
    {
        Text = text,
        Foreground = _fgDim,
        FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(14, 7, 14, 20),
    };

    /// <summary>An iOS switch: green when on; the knob springs across.</summary>
    private Border Switch(bool initial, Action<bool> onChange)
    {
        bool on = initial;
        var knobMove = new TranslateTransform(on ? 20 : 0, 0);
        var knob = new Ellipse
        {
            Width = 27,
            Height = 27,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2),
            RenderTransform = knobMove,
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1.5, Direction = 270, Opacity = 0.22 },
        };
        var track = new Border
        {
            Width = 51,
            Height = 31,
            CornerRadius = new CornerRadius(15.5),
            Background = on ? SwitchOn : _switchOff,
            Child = knob,
            Cursor = Cursors.Hand,
        };
        var spring = new Spring(on ? 20 : 0, v => knobMove.X = v);
        // Swallow the press so the card does not start a window drag, which would eat the release.
        track.MouseLeftButtonDown += (_, e) => e.Handled = true;
        track.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            on = !on;
            track.Background = on ? SwitchOn : _switchOff;
            spring.To(on ? 20 : 0, 0.3, 0.82);
            onChange(on);
        };
        return track;
    }

    /// <summary>A segmented control, like macOS/iOS.</summary>
    private Border Segmented(string[] options, int selected, Action<int> onSelect)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
        var buttons = new Button[options.Length];
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var b = new Button
            {
                Content = options[i],
                MinWidth = 50,
                Height = 27,
                Padding = new Thickness(9, 0, 9, 0),
                FontSize = 12.5,
                Foreground = _fg,
                Template = RoundTemplate(7),
                Cursor = Cursors.Hand,
            };
            buttons[i] = b;
            b.Click += (_, _) =>
            {
                Mark(buttons, index);
                onSelect(index);
            };
            panel.Children.Add(b);
        }
        Mark(buttons, selected);
        return new Border { CornerRadius = new CornerRadius(9), Background = _segBg, Child = panel };
    }

    private void Mark(Button[] buttons, int selected)
    {
        for (int i = 0; i < buttons.Length; i++)
        {
            bool on = i == selected;
            buttons[i].Background = on ? _segOn : Brushes.Transparent;
            buttons[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            buttons[i].Effect = on ? new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.2 } : null;
        }
    }

    // ---- Keyboard shortcut recorder

    private Button RecorderButton()
    {
        var b = new Button
        {
            Content = _search.HotkeyText,
            MinWidth = 120,
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = _fg,
            Background = _segBg,
            BorderBrush = Brushes.Transparent,
            Template = RoundTemplate(8, bordered: true),
            Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => StartRecording();
        return b;
    }

    private void StartRecording()
    {
        if (_recorder == null || _recording) return;
        _recording = true;
        // Free the current shortcut while recording, so pressing it again reaches this window.
        _search.SuspendHotkey();
        _search.CaptureWindowsKey(OnWindowsKeyRecorded);
        _recorder.Content = L.T("Type shortcut…", "Kısayola bas…");
        _recorder.BorderBrush = AccentBrush;
        SetNote(L.T("Press the new combination, e.g. Ctrl + Shift + Space, or just the Windows key. Esc cancels.",
                    "Yeni kombinasyona bas, ör. Ctrl + Shift + Space, ya da yalnızca Windows tuşuna bas. Esc iptal eder."), false);
        Activate();
        Focus();
    }

    /// <summary>The Windows key alone was pressed while recording: same as turning on the switch below.</summary>
    private void OnWindowsKeyRecorded()
    {
        if (!_recording) return;
        _recording = false;
        var s = AppSettings.Current;
        s.UseWindowsKey = true;
        s.Save();
        _search.ResumeHotkey();
        Rebuild();
        SetNote(L.T("Spotlight AI now opens with the Windows key.", "Spotlight AI artık Windows tuşuyla açılıyor."), false);
    }

    private void StopRecording()
    {
        _recording = false;
        if (_recorder == null) return;
        _recorder.Content = _search.HotkeyText;
        _recorder.BorderBrush = Brushes.Transparent;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_recording)
        {
            if (e.Key == Key.Escape) Close();
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _search.ResumeHotkey();
            StopRecording();
            SetNote(L.T("Shortcut unchanged.", "Kısayol değişmedi."), false);
            return;
        }
        // Wait for a real key; modifiers on their own are not a shortcut.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        uint mods = 0;
        var m = Keyboard.Modifiers;
        if ((m & ModifierKeys.Control) != 0) mods |= Native.MOD_CONTROL;
        if ((m & ModifierKeys.Alt) != 0) mods |= Native.MOD_ALT;
        if ((m & ModifierKeys.Shift) != 0) mods |= Native.MOD_SHIFT;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= Native.MOD_WIN;
        bool functionKey = key >= Key.F1 && key <= Key.F24;
        if (mods == 0 && !functionKey)
        {
            SetNote(L.T("Add Ctrl, Alt, Shift or Win to the key.", "Tuşa Ctrl, Alt, Shift ya da Win ekle."), true);
            return;
        }

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        bool ok = _search.TrySetHotkey(mods, vk);
        StopRecording();
        if (ok) SetNote(L.T($"Spotlight AI now opens with {_search.HotkeyText}.", $"Spotlight AI artık {_search.HotkeyText} ile açılıyor."), false);
        else SetNote(L.T($"{SearchWindow.FormatHotkey(mods, vk)} is already used by another app. Try another one.",
                         $"{SearchWindow.FormatHotkey(mods, vk)} başka bir uygulama tarafından kullanılıyor. Başka bir tane dene."), true);
    }

    private void SetNote(string text, bool error)
    {
        if (_recorderNote == null) return;
        _recorderNote.Text = text;
        _recorderNote.Foreground = error ? Red : _fgDim;
    }

    // ---- Helpers

    private static ControlTemplate RoundTemplate(double radius, bool bordered = false)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        if (bordered)
        {
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        }
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(OpacityProperty, 0.7));
        template.Triggers.Add(pressed);
        return template;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
