using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
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
    public string? Sha256 { get; set; }
    public bool Mandatory { get; set; } = false;
}

public static class UpdateManager
{
    public static readonly Version CurrentVersion = GetCurrentAssemblyVersion();
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

    public static Version NormalizeVersion(Version v)
    {
        return new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));
    }

    private static Version GetCurrentAssemblyVersion()
    {
        try
        {
            var asm = typeof(UpdateManager).Assembly;
            var asmVer = asm.GetName().Version;
            if (asmVer != null && (asmVer.Major > 0 || asmVer.Minor > 0 || asmVer.Build > 0))
            {
                return NormalizeVersion(asmVer);
            }

            string? procPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(procPath) && File.Exists(procPath))
            {
                var info = FileVersionInfo.GetVersionInfo(procPath);
                if (!string.IsNullOrEmpty(info.ProductVersion))
                {
                    string cleanVer = info.ProductVersion.Split('+')[0].Trim();
                    if (Version.TryParse(cleanVer, out var parsed))
                        return NormalizeVersion(parsed);
                }
            }
        }
        catch { }

        return new Version(1, 0, 7);
    }

    public static async Task<(bool HasUpdate, UpdateManifest? Manifest, string? Message)> CheckForUpdatesAsync(string feedUrl)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
            return (false, null, "No update feed URL specified.");

        try
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Clocky-Telemetry/1.0.0 (Windows NT)");

            using var req = new HttpRequestMessage(HttpMethod.Get, feedUrl);
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var resp = await _httpClient.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            string json = await resp.Content.ReadAsStringAsync();
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (manifest == null || string.IsNullOrEmpty(manifest.Version))
                return (false, null, "Invalid update manifest received.");

            if (Version.TryParse(manifest.Version, out var remoteVer))
            {
                var normRemote = NormalizeVersion(remoteVer);
                var normCurrent = NormalizeVersion(CurrentVersion);

                if (normRemote > normCurrent)
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

    public static async Task<string> DownloadUpdateAsync(string downloadUrl, string? expectedSha256 = null, IProgress<int>? progress = null)
    {
        string updatesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocky", "Updates");
        Directory.CreateDirectory(updatesDir);

        string tempFile = Path.Combine(updatesDir, $"Clocky_Update_{Guid.NewGuid():N}.tmp");
        string finalFile = Path.Combine(updatesDir, "Clocky_Update.exe");

        try
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
            if (File.Exists(finalFile))
            {
                try { File.Delete(finalFile); } catch { }
            }

            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
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

                // Verify exact byte count if Content-Length header was present
                if (totalBytes > 0 && totalRead < totalBytes)
                {
                    throw new IOException($"Download stream truncated. Received {totalRead} of {totalBytes} bytes.");
                }

                // Minimum sanity check: Standalone single-file Clocky is at least 30 MB
                if (totalRead < 30_000_000)
                {
                    throw new IOException($"Downloaded payload is too small ({totalRead} bytes). Expected complete binary.");
                }
            }

            // Cryptographic Hash Verification: Mandatory SHA256 validation
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
                throw new InvalidOperationException("Update manifest did not include a SHA256 hash - refusing to install an unverified binary.");
            }

            string actualHash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var verifyStream = File.OpenRead(tempFile))
            {
                byte[] hashBytes = await sha.ComputeHashAsync(verifyStream);
                actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            if (!string.Equals(actualHash, expectedSha256.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
                throw new InvalidOperationException($"Update binary verification failed. Expected SHA256: {expectedSha256}, Actual: {actualHash}. Installation rejected.");
            }

            // Atomic promotion to final update file
            if (File.Exists(finalFile))
            {
                try { File.Delete(finalFile); } catch { }
            }
            File.Move(tempFile, finalFile, overwrite: true);

            return finalFile;
        }
        catch
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
            if (File.Exists(finalFile))
            {
                try { File.Delete(finalFile); } catch { }
            }
            throw;
        }
    }

    public static void ApplyUpdateAndRestart(string newExePath)
    {
        string currentExe = Process.GetCurrentProcess().MainModule?.FileName 
                         ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Clocky.exe");
        int currentPid = Process.GetCurrentProcess().Id;

        // Pre-flight check: Verify source executable exists and satisfies minimum size
        if (!File.Exists(newExePath))
        {
            throw new FileNotFoundException("Update executable not found on disk.", newExePath);
        }

        var newFileInfo = new FileInfo(newExePath);
        if (newFileInfo.Length < 30_000_000)
        {
            throw new InvalidOperationException($"Update executable is corrupted or truncated ({newFileInfo.Length} bytes). Aborting update.");
        }

        string scriptPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocky", "Updates", "apply_update.ps1");

        string scriptDir = Path.GetDirectoryName(scriptPath)!;
        if (!Directory.Exists(scriptDir))
        {
            Directory.CreateDirectory(scriptDir);
        }

        string escapedNew = newExePath.Replace("'", "''");
        string escapedCurrent = currentExe.Replace("'", "''");

        string scriptContent = $@"
param()
$ErrorActionPreference = 'SilentlyContinue'
Start-Sleep -Milliseconds 600
try {{
    Stop-Process -Id {currentPid} -Force -ErrorAction SilentlyContinue
}} catch {{}}
Start-Sleep -Milliseconds 400

$src = '{escapedNew}'
$dst = '{escapedCurrent}'
$bak = ""$dst.bak""

# Minimum size constraint for valid Clocky standalone binary (30 MB)
$minBytes = 30000000

if (Test-Path -LiteralPath $src) {{
    $srcItem = Get-Item -LiteralPath $src
    if ($srcItem.Length -ge $minBytes) {{
        # 1. Create a safe rollback backup of current working executable
        if (Test-Path -LiteralPath $dst) {{
            try {{
                Copy-Item -LiteralPath $dst -Destination $bak -Force
            }} catch {{}}
        }}

        # 2. Attempt to swap new binary into destination
        $swapped = $false
        $attempts = 0
        while ($attempts -lt 10) {{
            try {{
                Move-Item -LiteralPath $src -Destination $dst -Force
                if ((Test-Path -LiteralPath $dst) -and ((Get-Item -LiteralPath $dst).Length -ge $minBytes)) {{
                    $swapped = $true
                    break
                }}
            }} catch {{
                Start-Sleep -Milliseconds 500
                $attempts++
            }}
        }}

        # 3. Automatic Rollback if swap failed or corrupted destination
        if (-not $swapped) {{
            if (Test-Path -LiteralPath $bak) {{
                try {{
                    Copy-Item -LiteralPath $bak -Destination $dst -Force
                    Start-Process -FilePath $dst
                }} catch {{}}
            }}
        }} else {{
            # 4. Launch new executable and verify startup stability
            $launched = $false
            if (Test-Path -LiteralPath $dst) {{
                try {{
                    $newProc = Start-Process -FilePath $dst -PassThru -ErrorAction Stop
                    if ($newProc -and -not $newProc.HasExited) {{
                        Start-Sleep -Seconds 3
                        if (-not $newProc.HasExited) {{
                            $launched = $true
                        }}
                    }}
                }} catch {{}}
            }}

            # 5. If new process failed or crashed immediately, rollback to backup
            if (-not $launched) {{
                if (Test-Path -LiteralPath $bak) {{
                    try {{
                        Copy-Item -LiteralPath $bak -Destination $dst -Force
                        Start-Process -FilePath $dst
                    }} catch {{}}
                }}
            }} else {{
                # New binary is running healthy: safely remove rollback backup
                try {{
                    Remove-Item -LiteralPath $bak -Force -ErrorAction SilentlyContinue
                }} catch {{}}
            }}
        }}
    }}
}}
";

        File.WriteAllText(scriptPath, scriptContent, Encoding.UTF8);

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
