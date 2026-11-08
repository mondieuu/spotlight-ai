using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Odak;

/// <summary>
/// Setup and trace-free uninstall. No admin rights needed: everything is installed for the current user only.
///
/// EVERY location setup touches (all of them are removed on uninstall):
///   • %LOCALAPPDATA%\Programs\Spotlight AI\            → the program
///   • %LOCALAPPDATA%\Spotlight AI\                     → file index, usage counts, yt-dlp/ffmpeg
///   • %TEMP%\.net\Spotlight AI\                        → .NET single-file app cache
///   • Start menu and (if chosen) desktop shortcuts
///   • HKCU\...\Run\Spotlight AI                        → "Start with Windows"
///   • HKCU\...\Uninstall\Spotlight AI                  → Settings &gt; Apps entry
///   • (optional) Downloads\Spotlight AI                 → MP3/MP4 files the user downloaded
/// </summary>
public static class Installer
{
    public const string AppName = "SpotlightAI";
    /// <summary>The app version, taken from &lt;Version&gt; in Odak.csproj (the only place to change it).</summary>
    public static readonly string Version =
        (typeof(Installer).Assembly.GetName().Version ?? new System.Version(1, 0, 0)).ToString(3);
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SpotlightAI";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    public static string InstalledExe => Path.Combine(InstallDir, AppName + ".exe");
    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    private static string LegacyDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odak");
    private static string LegacyExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Odak", "Odak.exe");
    private static string BundleCache => Path.Combine(Path.GetTempPath(), ".net", AppName);
    private static string SetupBundleCache => Path.Combine(Path.GetTempPath(), ".net", AppName + "Setup");
    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>Whether it was launched as the setup file ("SpotlightSetup.exe").</summary>
    public static bool IsSetupExe =>
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("Setup", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>Copies the program and creates the shortcuts and the "Apps" entry.</summary>
    public static void Install(bool startWithWindows, bool desktopShortcut)
    {
        StopOtherInstances();
        Directory.CreateDirectory(InstallDir);
        // The previous version may need a moment to release its file after quitting.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);
                break;
            }
            catch (IOException) when (attempt < 10)
            {
                System.Threading.Thread.Sleep(400);
            }
        }

        CreateShortcut(StartMenuLink, InstalledExe, "--open");
        if (desktopShortcut) CreateShortcut(DesktopLink, InstalledExe, "--open");
        else TryDelete(DesktopLink);

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (startWithWindows) run.SetValue(AppName, $"\"{InstalledExe}\" --tray");
            else run.DeleteValue(AppName, throwOnMissingValue: false);
        }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Spotlight AI");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "Spotlight AI");
        key.SetValue("DisplayIcon", $"\"{InstalledExe}\",0");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>Launches the installed Spotlight AI.</summary>
    public static void LaunchInstalled(string args = "--open") =>
        Process.Start(new ProcessStartInfo(InstalledExe, args) { UseShellExecute = true, WorkingDirectory = InstallDir });

    /// <summary>
    /// Silent in-place update (run by Software Update): replaces the program, keeps the user's choices for
    /// "Start with Windows" and the desktop shortcut, and leaves settings, the file index and downloads alone.
    /// </summary>
    public static void UpdateInPlace()
    {
        bool startup;
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey)) startup = run?.GetValue(AppName) != null;
        Install(startup, File.Exists(DesktopLink));
    }

    /// <summary>Removes everything. The program folder is deleted by a hidden command after this process exits.</summary>
    public static void Uninstall(bool deleteDownloads)
    {
        StopOtherInstances();

        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(AppName, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        TryDelete(StartMenuLink);
        TryDelete(DesktopLink);
        TryDeleteDir(DataDir);

        // A running exe cannot delete itself: a hidden PowerShell waits until this process exits (however long
        // the window stays open), then deletes the program folder and the .NET cache.
        var targets = new[] { InstallDir, BundleCache, SetupBundleCache };
        var list = string.Join(",", targets.Select(t => "'" + t.Replace("'", "''") + "'"));
        var script =
            $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; " +
            $"foreach ($i in 1..10) {{ foreach ($t in @({list})) {{ if (Test-Path -LiteralPath $t) {{ Remove-Item -LiteralPath $t -Recurse -Force -ErrorAction SilentlyContinue }} }}; " +
            $"if (-not (@({list}) | Where-Object {{ Test-Path -LiteralPath $_ }})) {{ break }}; Start-Sleep -Milliseconds 500 }}";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    /// <summary>Closes running Spotlight AI instances (except this process).</summary>
    private static void StopOtherInstances()
    {
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(AppName))
        {
            try
            {
                if (p.Id == me) continue;
                // Only our own program: another app that happens to be called Spotlight AI or Odak is left alone.
                var file = p.MainModule?.FileName;
                if (!string.Equals(file, InstalledExe, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(file, LegacyExe, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill();
                p.WaitForExit(3000);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    private static void CreateShortcut(string link, string target, string args)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = target;
        shortcut.Arguments = args;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target);
        shortcut.IconLocation = target + ",0";
        shortcut.Description = "Ask AI from a glass search box";
        shortcut.Save();
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
