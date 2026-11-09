using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Odak;

/// <summary>Install and uninstall window (Apple style: simple, single focus, dark glass card).</summary>
public sealed class SetupWindow : Window
{
    private enum Stage { Start, Busy, Done, Error }

    private readonly bool _uninstall;
    private Stage _stage = Stage.Start;
    private string _errorTitle = "", _errorMessage = "";
    private readonly TextBlock _title = new(), _body = new(), _note = new();
    private readonly CheckBox _startup = Check(true);
    private readonly CheckBox _desktop = Check(false);
    private readonly CheckBox _downloads = Check(false);
    private readonly Button _primary, _secondary, _langEn, _langTr;
    private readonly StackPanel _options = new() { Margin = new Thickness(0, 22, 0, 0) };
    private readonly Grid _buttons = new() { Margin = new Thickness(0, 26, 0, 0) };
    private Action? _primaryAction;

    private static readonly Brush Fg = Frozen(Colors.White);
    private static readonly Brush FgDim = Frozen(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));
    private static readonly Brush Destructive = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly Brush Secondary = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    public SetupWindow(bool uninstall)
    {
        _uninstall = uninstall;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/odak.ico")); } catch { }

        _primary = PillButton(uninstall ? Destructive : Accent, 44, 15);
        _secondary = PillButton(Secondary, 44, 15);
        _primary.Click += (_, _) => _primaryAction?.Invoke();
        _secondary.Click += (_, _) => Close();

        // Language switch (top right). English is the default; the choice is also used by the app.
        _langEn = PillButton(Brushes.Transparent, 26, 12);
        _langTr = PillButton(Brushes.Transparent, 26, 12);
        _langEn.Content = "EN";
        _langTr.Content = "TR";
        _langEn.Width = _langTr.Width = 40;
        _langEn.Click += (_, _) => SetLanguage("en");
        _langTr.Click += (_, _) => SetLanguage("tr");
        var lang = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -14, -16, 0) };
        lang.Children.Add(_langEn);
        lang.Children.Add(_langTr);

        var icon = new Image { Width = 84, Height = 84, Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Center };
        try
        {
            // Use the largest (256 px) frame in the .ico; scaling up a small one looks blurry.
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/odak.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapFrame? best = null;
            foreach (var f in decoder.Frames) if (best == null || f.PixelWidth > best.PixelWidth) best = f;
            icon.Source = best;
        }
        catch { }
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

        _title.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        _title.FontSize = 26;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Fg;
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.TextAlignment = TextAlignment.Center;

        _body.FontSize = 14;
        _body.Foreground = FgDim;
        _body.TextWrapping = TextWrapping.Wrap;
        _body.TextAlignment = TextAlignment.Center;
        _body.LineHeight = 20;
        _body.Margin = new Thickness(0, 8, 0, 0);

        _note.FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI");
        _note.FontSize = 11.5;
        _note.Foreground = FgDim;
        _note.TextWrapping = TextWrapping.Wrap;
        _note.TextAlignment = TextAlignment.Center;
        _note.Margin = new Thickness(0, 18, 0, 0);

        _buttons.ColumnDefinitions.Add(new ColumnDefinition());
        _buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        _buttons.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_primary, 2);
        _buttons.Children.Add(_secondary);
        _buttons.Children.Add(_primary);

        var content = new StackPanel { Margin = new Thickness(36, 30, 36, 30) };
        content.Children.Add(lang);
        content.Children.Add(icon);
        content.Children.Add(_title);
        content.Children.Add(_body);
        content.Children.Add(_options);
        content.Children.Add(_buttons);
        content.Children.Add(_note);

        var card = new Border
        {
            CornerRadius = new CornerRadius(26),
            Background = new LinearGradientBrush(Color.FromRgb(0x2C, 0x2C, 0x30), Color.FromRgb(0x1C, 0x1C, 0x1E), 90),
            BorderBrush = new LinearGradientBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 60),
            BorderThickness = new Thickness(1.2),
            Child = content,
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.45 },
        };
        Content = new Border { Padding = new Thickness(30, 20, 30, 40), Child = card };
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && _stage != Stage.Busy) Close(); };

        Render();
    }

    private void SetLanguage(string language)
    {
        AppSettings.Current.Language = language;
        AppSettings.Current.Save();
        Render();
    }

    /// <summary>Fills every text for the current stage and language.</summary>
    private void Render()
    {
        bool tr = AppSettings.Current.IsTurkish;
        _langEn.Background = tr ? Brushes.Transparent : Secondary;
        _langTr.Background = tr ? Secondary : Brushes.Transparent;
        _langEn.FontWeight = tr ? FontWeights.Normal : FontWeights.SemiBold;
        _langTr.FontWeight = tr ? FontWeights.SemiBold : FontWeights.Normal;
        Title = _uninstall ? L.T("Uninstall Spotlight AI", "Spotlight AI'ı Kaldır") : L.T("Spotlight AI Setup", "Spotlight AI Kurulumu");
        _secondary.Content = L.T("Cancel", "Vazgeç");
        Label(_startup, L.T("Start with Windows (runs in the background, opens with Alt+Shift+Space)", "Windows ile başlat (arka planda, Alt+Shift+Space ile açılır)"));
        Label(_desktop, L.T("Add a desktop shortcut", "Masaüstüne kısayol ekle"));
        Label(_downloads, L.T("Also delete my downloaded MP3/MP4 files (Downloads\\Spotlight AI)", "İndirdiğim MP3/MP4 dosyalarını da sil (İndirilenler\\Spotlight AI)"));

        switch (_stage)
        {
            case Stage.Start when _uninstall:
                _title.Text = L.T("Uninstall Spotlight AI?", "Spotlight AI kaldırılsın mı?");
                _body.Text = L.T("The program, settings, your sign-ins in this app, shortcuts and registry entries will be removed. Nothing from Spotlight AI is left on your PC.",
                                 "Program, ayarlar, bu uygulamadaki oturumların, kısayollar ve kayıt defteri girdileri silinir. Bilgisayarda Spotlight AI'tan iz kalmaz.");
                SetOptions();
                _primary.Content = L.T("Uninstall", "Kaldır");
                _note.Text = "";
                _primaryAction = RunUninstall;
                break;
            case Stage.Start:
                bool update = Installer.IsInstalled;
                _title.Text = update ? L.T("Update Spotlight AI", "Spotlight AI'ı Güncelle") : "Spotlight AI";
                _body.Text = L.T("ChatGPT and Gemini in a glass panel, with your own account. Open it with Alt+Shift+Space.",
                                 "Kendi hesabınla ChatGPT ve Gemini, cam bir panelde. Alt+Shift+Space ile aç.");
                SetOptions(_startup, _desktop);
                _primary.Content = update ? L.T("Update", "Güncelle") : L.T("Install", "Kur");
                _note.Text = L.T("Installs for this user only; no administrator rights needed.", "Yalnızca bu kullanıcıya kurulur, yönetici izni gerekmez.") + "\n" + Installer.InstallDir;
                _primaryAction = RunInstall;
                break;
            case Stage.Busy:
                _primary.Content = _uninstall ? L.T("Uninstalling…", "Kaldırılıyor…") : L.T("Installing…", "Kuruluyor…");
                break;
            case Stage.Done when _uninstall:
                _title.Text = L.T("Spotlight AI was uninstalled", "Spotlight AI kaldırıldı");
                _body.Text = L.T("All its files and settings were deleted. The program folder is removed when you close this window.",
                                 "Tüm dosyaları ve ayarları silindi. Bu pencereyi kapatınca program klasörü de kaldırılacak.");
                SetOptions();
                _note.Text = "";
                _primary.Content = L.T("Close", "Kapat");
                _primaryAction = Close;
                break;
            case Stage.Done:
                _title.Text = L.T("Spotlight AI is installed", "Spotlight AI kuruldu");
                _body.Text = L.T("Open it from the Start menu or from anywhere with Alt+Shift+Space. To uninstall: Settings › Apps › Installed apps › Spotlight AI.",
                                 "Başlat menüsünden ya da her yerden Alt+Shift+Space ile açabilirsin. Kaldırmak için: Ayarlar › Uygulamalar › Yüklü uygulamalar › Spotlight AI.");
                SetOptions();
                _note.Text = "";
                _primary.Content = L.T("Open Spotlight AI", "Spotlight AI'ı Aç");
                _primaryAction = () => { Installer.LaunchInstalled(); Close(); };
                break;
            case Stage.Error:
                _title.Text = _errorTitle;
                _body.Text = _errorMessage;
                _primary.Content = L.T("Try again", "Tekrar dene");
                _primaryAction = _uninstall ? RunUninstall : RunInstall;
                break;
        }
    }

    private void SetOptions(params CheckBox[] boxes)
    {
        _options.Children.Clear();
        foreach (var b in boxes) _options.Children.Add(b);
    }

    private async void RunInstall()
    {
        SetBusy();
        try
        {
            bool startup = _startup.IsChecked == true, desktop = _desktop.IsChecked == true;
            await Task.Run(() => Installer.Install(startup, desktop));
            ShowDone();
        }
        catch (Exception ex)
        {
            ShowError(L.T("Setup could not finish", "Kurulum tamamlanamadı"), ex.Message);
        }
    }

    private async void RunUninstall()
    {
        SetBusy();
        try
        {
            bool downloads = _downloads.IsChecked == true;
            await Task.Run(() => Installer.Uninstall(downloads));
            ShowDone();
        }
        catch (Exception ex)
        {
            ShowError(L.T("Uninstall could not finish", "Kaldırma tamamlanamadı"), ex.Message);
        }
    }

    // ---- Stages

    private void SetBusy()
    {
        _stage = Stage.Busy;
        _primary.IsEnabled = _secondary.IsEnabled = _langEn.IsEnabled = _langTr.IsEnabled = false;
        foreach (var child in _options.Children) ((UIElement)child).IsEnabled = false;
        Render();
    }

    private void ShowDone()
    {
        _stage = Stage.Done;
        _secondary.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_primary, 0);
        Grid.SetColumnSpan(_primary, 3);
        _primary.Background = Accent;
        _primary.IsEnabled = true;
        // After uninstalling, changing the language would write a settings file again: keep it locked.
        _langEn.IsEnabled = _langTr.IsEnabled = !_uninstall;
        Render();
        _primary.Focus();
    }

    private void ShowError(string title, string message)
    {
        _stage = Stage.Error;
        _errorTitle = title;
        _errorMessage = message;
        _primary.IsEnabled = _secondary.IsEnabled = _langEn.IsEnabled = _langTr.IsEnabled = true;
        foreach (var child in _options.Children) ((UIElement)child).IsEnabled = true;
        Render();
    }

    // ---- Helpers

    private static CheckBox Check(bool on) => new()
    {
        Content = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5 },
        IsChecked = on,
        Foreground = Fg,
        Margin = new Thickness(4, 6, 0, 6),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static void Label(CheckBox box, string text) => ((TextBlock)box.Content).Text = text;

    /// <summary>A pill-shaped button that dims slightly the moment it is pressed.</summary>
    private static Button PillButton(Brush background, double height, double fontSize)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "Bg" };
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(height >= 40 ? 12 : 8));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(OpacityProperty, 0.75));
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.6));
        template.Triggers.Add(pressed);
        template.Triggers.Add(disabled);

        return new Button
        {
            Height = height,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
            Background = background,
            Template = template,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
        };
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
