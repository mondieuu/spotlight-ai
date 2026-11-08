using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Odak;

/// <summary>
/// Software Update: checks the project's latest GitHub release and, when it is newer, downloads its
/// SpotlightSetup.exe and runs it in silent update mode. The new version replaces the program in place and
/// restarts; settings, the file index and downloads are kept. No uninstall or reinstall is needed.
/// </summary>
public static class Updater
{
    private const string LatestApi = "https://api.github.com/repos/mondieuu/spotlight-ai/releases/latest";
    private const string AssetName = "SpotlightAISetup.exe";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    public enum State { Idle, Checking, UpToDate, Available, Downloading, Failed }

    public sealed record Release(Version Version, string Tag, string DownloadUrl, long Size, string Sha256);

    public static State Status { get; private set; } = State.Idle;
    public static Release? Latest { get; private set; }
    public static double Progress { get; private set; }
    public static string? Error { get; private set; }

    /// <summary>Raised (on any thread) whenever the status changes.</summary>
    public static event Action? Changed;

    private static Timer? _timer;
    private static string UpdateDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotlightAI", "update");

    public static Version Current => Normalize(Installer.Version);

    /// <summary>"v1.4" / "1.4" / "1.4.0" → 1.4.0, so versions compare correctly.</summary>
    public static Version Normalize(string text)
    {
        var t = text.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(t, out var v)) return new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>Starts the automatic checks: shortly after start, then every 6 hours (if enabled in Settings).</summary>
    public static void Start()
    {
        CleanUp();
        _timer = new Timer(_ =>
        {
            if (AppSettings.Current.AutoCheckUpdates) _ = CheckAsync();
        }, null, TimeSpan.FromSeconds(30), CheckEvery);
    }

    /// <summary>Removes the setup file left from a finished update.</summary>
    private static void CleanUp()
    {
        try { if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, recursive: true); } catch { }
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Spotlight AI-for-Windows/{Installer.Version}");
        return http;
    }

    public static async Task CheckAsync()
    {
        if (Status is State.Checking or State.Downloading) return;
        Set(State.Checking);
        try
        {
            using var http = NewClient();
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            string? url = null;
            long size = 0;
            string? sha256 = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                url = asset.GetProperty("browser_download_url").GetString();
                size = asset.GetProperty("size").GetInt64();
                // GitHub publishes each asset's digest as "sha256:<hex>".
                if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha256 = d["sha256:".Length..];
            }
            var version = Normalize(tag);
            // A release that cannot be verified is never offered.
            if (url != null && sha256 != null && version > Current && url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                Latest = new Release(version, tag, url, size, sha256);
                Set(State.Available);
            }
            else
            {
                Latest = null;
                Set(State.UpToDate);
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Set(State.Failed);
        }
    }

    /// <summary>
    /// Downloads the new setup and returns its path; the caller then starts it with <see cref="Launch"/>
    /// and quits so the files can be replaced.
    /// </summary>
    public static async Task<string?> DownloadAsync()
    {
        var release = Latest;
        if (release == null || Status == State.Downloading) return null;
        Progress = 0;
        Set(State.Downloading);
        try
        {
            Directory.CreateDirectory(UpdateDir);
            var file = Path.Combine(UpdateDir, AssetName);
            var partial = file + ".part";
            using var http = NewClient();
            using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.Size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(partial))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read, shown = -1;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (total <= 0) continue;
                    Progress = (double)done / total;
                    // Listeners redraw: tell them once per percent, not on every read.
                    int percent = (int)(Progress * 100);
                    if (percent != shown) { shown = percent; Changed?.Invoke(); }
                }
            }
            // Only a file matching the digest GitHub published is ever run.
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new IOException(L.T("The download failed verification and was deleted.", "İndirilen dosya doğrulanamadı ve silindi."));
            }
            File.Move(partial, file, overwrite: true);
            return file;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Set(State.Failed);
            return null;
        }
    }

    /// <summary>Runs the downloaded setup in silent update mode.</summary>
    public static void Launch(string setupPath) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setupPath, "--update") { UseShellExecute = true });

    private static void Set(State state)
    {
        Status = state;
        Changed?.Invoke();
    }
}
