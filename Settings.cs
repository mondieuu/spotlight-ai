using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Odak;

public enum ThemeMode { System, Light, Dark }

public enum AiProvider { ChatGpt, Gemini }

/// <summary>
/// User preferences, stored in %LOCALAPPDATA%\Spotlight AI\settings.json.
/// English is the default language; Turkish is fully supported.
/// </summary>
public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>"en" (default) or "tr".</summary>
    public string Language { get; set; } = "en";
    public AiProvider Provider { get; set; } = AiProvider.ChatGpt;
    /// <summary>Global shortcut: modifier flags (Alt = 1, Ctrl = 2, Shift = 4, Win = 8) and virtual-key code.</summary>
    public uint HotkeyModifiers { get; set; } = 0x5;
    public uint HotkeyKey { get; set; } = 0x20;
    /// <summary>A tap of the Windows key opens Spotlight AI instead of the Start menu.</summary>
    public bool UseWindowsKey { get; set; }
    /// <summary>Check GitHub for a new version automatically (shortly after start, then every 6 hours).</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>Raised after any setting changes so open windows can update live.</summary>
    public static event Action? Changed;

    private static string File_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotlightAI", "settings.json");

    public bool IsTurkish => Language == "tr";

    /// <summary>Resolves "System" to the Windows app theme.</summary>
    public bool IsDark
    {
        get
        {
            if (Theme == ThemeMode.Dark) return true;
            if (Theme == ThemeMode.Light) return false;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v == 0;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        Changed?.Invoke();
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(File_))
            {
                var json = File.ReadAllText(File_);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                // Version 1.0 and earlier builds also offered other services.
                if (!Enum.IsDefined(loaded.Provider)) loaded.Provider = AiProvider.ChatGpt;
                if (json.Contains("\"ProtectedKey\"")) File.WriteAllText(File_, JsonSerializer.Serialize(loaded, new JsonSerializerOptions { WriteIndented = true }));
                return loaded;
            }
        }
        catch { }
        return new AppSettings();
    }
}

/// <summary>Localization: <c>L.T("English", "Türkçe")</c> picks the text for the current language.</summary>
public static class L
{
    public static string T(string en, string tr) => AppSettings.Current.IsTurkish ? tr : en;
}
