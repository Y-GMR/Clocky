using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Clocky.Core;

public class UpdateManifest
{
    public string Version { get; set; } = "1.0.0";
    public string ReleaseDate { get; set; } = "";
    public string Changelog { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public bool Mandatory { get; set; } = false;
}

public static class UpdateManager
{
    public static readonly Version CurrentVersion = new Version(1, 0, 6);
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<(bool HasUpdate, UpdateManifest? Manifest, string? Message)> CheckForUpdatesAsync(string feedUrl)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
            return (false, null, "No update feed URL specified.");

        try
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Clocky-Telemetry/1.0.0 (Windows NT)");

            string json = await _httpClient.GetStringAsync(feedUrl);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (manifest == null || string.IsNullOrEmpty(manifest.Version))
                return (false, null, "Invalid update manifest received.");

            if (Version.TryParse(manifest.Version, out var remoteVer))
            {
                if (remoteVer > CurrentVersion)
                {
                    return (true, manifest, $"New version v{manifest.Version} is available!");
                }
                else
                {
                    return (false, manifest, $"Clocky is up to date (v{CurrentVersion}).");
                }
            }

            return (false, manifest, $"Current version v{CurrentVersion} is up to date.");
        }
        catch (HttpRequestException ex)
        {
            return (false, null, $"Update check offline: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, null, $"Update error: {ex.Message}");
        }
    }

    public static async Task<string> DownloadUpdateAsync(string downloadUrl, IProgress<int>? progress = null)
    {
        string updatesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocky", "Updates");
        Directory.CreateDirectory(updatesDir);

        string targetFile = Path.Combine(updatesDir, "Clocky_Update.exe");
        if (File.Exists(targetFile))
        {
            try { File.Delete(targetFile); } catch { }
        }

        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var stream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;

            if (totalBytes > 0 && progress != null)
            {
                int pct = (int)((totalRead * 100) / totalBytes);
                progress.Report(pct);
            }
        }

        return targetFile;
    }

    public static void ApplyUpdateAndRestart(string newExePath)
    {
        string currentExe = Process.GetCurrentProcess().MainModule?.FileName 
                         ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Clocky.exe");
        int currentPid = Process.GetCurrentProcess().Id;

        string scriptPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocky", "Updates", "apply_update.ps1");

        string scriptContent = $@"
param()
$ErrorActionPreference = 'SilentlyContinue'
Start-Sleep -Milliseconds 600
try {{
    Stop-Process -Id {currentPid} -Force -ErrorAction SilentlyContinue
}} catch {{}}
Start-Sleep -Milliseconds 400

$attempts = 0
while ($attempts -lt 10) {{
    try {{
        Move-Item -Path '{newExePath}' -Destination '{currentExe}' -Force
        break
    }} catch {{
        Start-Sleep -Milliseconds 500
        $attempts++
    }}
}}

Start-Process -FilePath '{currentExe}'
";

        File.WriteAllText(scriptPath, scriptContent);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(psi);
        System.Windows.Application.Current?.Shutdown();
    }
}
