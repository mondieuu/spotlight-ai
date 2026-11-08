using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Odak;

/// <summary>
/// Odak runs in the background: its icon sits in the tray, and Alt+Space opens the search window.
/// Only one instance runs; if launched again, the instance already running stays.
/// </summary>
public partial class App : Application
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ShowSignal = "SpotlightAI.Show", SettingsSignal = "SpotlightAI.ShowSettings";
    private Mutex? _single;
    private SearchWindow? _window;
    private Forms.NotifyIcon? _tray;
    private string? _lastFile;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Uninstall (Settings › Apps or the tray menu) and setup ("SpotlightSetup.exe") modes.
        if (e.Args.Contains("--uninstall"))
        {
            if (e.Args.Contains("--quiet"))
            {
                Installer.Uninstall(deleteDownloads: false);
                Shutdown();
                return;
            }
            ShowSetup(uninstall: true);
            return;
        }
        if (Installer.IsSetupExe && e.Args.Contains("--update"))
        {
            // Started by Software Update: replace the program silently and start the new version.
            try
            {
                Installer.UpdateInPlace();
                Installer.LaunchInstalled("--tray --updated");
                Shutdown();
            }
            catch
            {
                ShowSetup(uninstall: false); // fall back to the normal setup window
            }
            return;
        }
        if (Installer.IsSetupExe)
        {
            ShowSetup(uninstall: false);
            return;
        }

        _single = new Mutex(true, "SpotlightAI.Single", out bool first);
        if (!first)
        {
            // Already running: ask that instance to show itself, so a shortcut click is never ignored.
            if (!e.Args.Contains("--tray"))
            {
                Native.AllowSetForegroundWindow(-1);
                var name = e.Args.Contains("--settings") ? SettingsSignal : ShowSignal;
                if (EventWaitHandle.TryOpenExisting(name, out var signal))
                {
                    signal.Set();
                    signal.Dispose();
                }
            }
            Shutdown();
            return;
        }

        _window = new SearchWindow();
        _window.InitializeHidden();
        _window.Notify = ShowNotification;
        CreateTray();
        ListenForSecondLaunch();

        // Show once on first launch as a reminder of the shortcut.
        if (!e.Args.Contains("--tray")) _window.ShowSpotlight();
        // --settings: open the Settings window directly (e.g. from a shortcut).
        if (e.Args.Contains("--settings")) SettingsWindow.Open(_window);

        // Software Update: automatic checks, plus a note right after an update finished.
        Updater.Changed += () => Dispatcher.BeginInvoke(OnUpdaterChanged);
        Updater.Start();
        if (e.Args.Contains("--updated"))
            ShowBalloon(L.T("Spotlight AI was updated", "Spotlight AI güncellendi"),
                        L.T($"You are now on version {Installer.Version}.", $"Artık {Installer.Version} sürümünü kullanıyorsun."), null);
    }

    /// <summary>Waits (without using CPU) for another launch asking this instance to show itself.</summary>
    private void ListenForSecondLaunch()
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
        var settings = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsSignal);
        var handles = new WaitHandle[] { show, settings };
        new Thread(() =>
        {
            while (true)
            {
                int which = WaitHandle.WaitAny(handles);
                Dispatcher.BeginInvoke(() =>
                {
                    if (_window == null) return;
                    if (which == 0) _window.ShowSpotlight();
                    else SettingsWindow.Open(_window);
                });
            }
        })
        { IsBackground = true, Name = "Spotlight AI second launch" }.Start();
    }

    // ---- Software Update

    private Version? _announced;
    private Updater.State _menuState = Updater.State.Idle;

    private void OnUpdaterChanged()
    {
        // Progress ticks do not change the menu: rebuild it only when the state changes.
        if (Updater.Status != _menuState)
        {
            _menuState = Updater.Status;
            BuildMenu();
        }
        if (Updater.Status == Updater.State.Available && Updater.Latest is { } r && r.Version != _announced)
        {
            _announced = r.Version;
            ShowBalloon(L.T($"Spotlight AI {r.Version.ToString(3)} is available", $"Spotlight AI {r.Version.ToString(3)} hazır"),
                        L.T("Click to update. Your settings stay.", "Güncellemek için tıkla. Ayarların korunur."),
                        InstallUpdate);
        }
    }

    /// <summary>Downloads the new version, starts its silent update and quits so the files can be replaced.</summary>
    internal async void InstallUpdate()
    {
        var setup = await Updater.DownloadAsync();
        if (setup == null)
        {
            ShowBalloon(L.T("Update failed", "Güncellenemedi"), Updater.Error ?? "", null);
            return;
        }
        try
        {
            Updater.Launch(setup);
            Quit();
        }
        catch (Exception ex)
        {
            ShowBalloon(L.T("Update failed", "Güncellenemedi"), ex.Message, null);
        }
    }

    private void ShowSetup(bool uninstall)
    {
        var window = new SetupWindow(uninstall);
        window.Closed += (_, _) => Shutdown();
        window.Show();
        window.Activate();
    }

    private void CreateTray()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/odak.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = $"Spotlight AI — {_window!.OpenWithText}",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => _window.ShowSpotlight();
        _tray.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            action?.Invoke();
        };

        BuildMenu();
        // Rebuild the menu when the language or the shortcut changes in Settings.
        AppSettings.Changed += () => Dispatcher.Invoke(BuildMenu);
        _window.HotkeyChanged += () =>
        {
            _tray.Text = $"Spotlight AI — {_window.OpenWithText}";
            BuildMenu();
        };
    }

    private void BuildMenu()
    {
        if (_tray == null || _window == null) return;
        var menu = new Forms.ContextMenuStrip();
        if (Updater.Status == Updater.State.Available && Updater.Latest is { } update)
        {
            var item = menu.Items.Add(L.T($"Update to {update.Version.ToString(3)}…", $"{update.Version.ToString(3)} sürümüne güncelle…"), null, (_, _) => InstallUpdate());
            item.Font = new System.Drawing.Font(item.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(new Forms.ToolStripSeparator());
        }
        menu.Items.Add(L.T($"Open ({_window.OpenWithText})", $"Aç ({_window.OpenWithText})"), null, (_, _) => _window.ShowSpotlight());
        menu.Items.Add(L.T("Settings…", "Ayarlar…"), null, (_, _) => SettingsWindow.Open(_window));
        var startup = new Forms.ToolStripMenuItem(L.T("Start with Windows", "Windows ile başlat")) { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        if (Installer.IsInstalled)
        {
            menu.Items.Add(L.T("Uninstall Spotlight AI…", "Spotlight AI'ı kaldır…"), null, (_, _) =>
                Process.Start(new ProcessStartInfo(Installer.InstalledExe, "--uninstall") { UseShellExecute = true }));
        }
        menu.Items.Add(L.T("Quit", "Çıkış"), null, (_, _) => Quit());
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private Action? _balloonAction;

    /// <summary>Download notifications: clicking one shows the file in File Explorer.</summary>
    private void ShowNotification(string title, string text, string? file)
    {
        _lastFile = file;
        ShowBalloon(title, text, file == null ? null : () =>
        {
            if (_lastFile != null && File.Exists(_lastFile))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastFile}\"") { UseShellExecute = true });
        }, file == null);
    }

    private void ShowBalloon(string title, string text, Action? onClick, bool warning = false)
    {
        _balloonAction = onClick;
        _tray?.ShowBalloonTip(5000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    // ---- "Start with Windows" (current user only, HKCU\...\Run)

    internal static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("SpotlightAI") != null;
    }

    internal static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        if (enabled) key.SetValue("SpotlightAI", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("SpotlightAI", throwOnMissingValue: false);
    }

    private void Quit()
    {
        _window?.UnregisterHotkey();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _single?.ReleaseMutex();
        Shutdown();
    }
}
