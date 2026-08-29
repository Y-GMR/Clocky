using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Clocky.UI;
using WpfApp = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;

namespace Clocky.Core;

public static class GlobalExceptionHandler
{
    private static bool _isShowingDialog = false;
    private static readonly object _lock = new();

    public const string DeveloperEmail = "iwangpetradheerendra@gmail.com";

    public static void Initialize()
    {
        // 1. UI Thread Exceptions
        if (WpfApp.Current != null)
        {
            WpfApp.Current.DispatcherUnhandledException += (s, e) =>
            {
                HandleException(e.Exception, "WPF UI Thread Dispatcher");
                e.Handled = true; // Prevent abrupt hard termination so user can view/report the error
            };
        }

        // 2. Background Thread Pool Exceptions
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                HandleException(ex, "AppDomain Background Thread");
            }
        };

        // 3. Unobserved Async Task Exceptions
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            HandleException(e.Exception, "Async TaskScheduler");
            e.SetObserved();
        };
    }

    public static void HandleException(Exception ex, string sourceContext = "Application")
    {
        lock (_lock)
        {
            string logPath = LogCrashToFile(ex, sourceContext);

            if (_isShowingDialog) return;
            _isShowingDialog = true;

            try
            {
                WpfApp.Current?.Dispatcher?.Invoke(() =>
                {
                    var errorWin = new ErrorReportWindow(ex, sourceContext, logPath);
                    errorWin.ShowDialog();
                });
            }
            catch
            {
                // Fallback to native MessageBox if WPF visual tree failed
                WpfMessageBox.Show(
                    $"Clocky encountered an unexpected error:\n\n{ex.Message}\n\nA crash log was saved to:\n{logPath}",
                    "Clocky - Unexpected Error",
                    WpfMessageBoxButton.OK,
                    WpfMessageBoxImage.Error);
            }
            finally
            {
                _isShowingDialog = false;
            }
        }
    }

    public static string GetLogsDirectory()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocky", "Logs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string LogCrashToFile(Exception ex, string sourceContext)
    {
        try
        {
            string logsDir = GetLogsDirectory();
            string fileName = $"crash_{DateTime.Now:yyyyMMdd_HHmmss}_{Process.GetCurrentProcess().Id}.log";
            string fullPath = Path.Combine(logsDir, fileName);

            string report = GenerateDiagnosticReport(ex, sourceContext);
            File.WriteAllText(fullPath, report, Encoding.UTF8);
            return fullPath;
        }
        catch
        {
            return "Unable to save log file";
        }
    }

    public static string GenerateDiagnosticReport(Exception ex, string sourceContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Clocky Diagnostic Crash Report");
        sb.AppendLine($"**Timestamp:** {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        sb.AppendLine($"**Source Context:** {sourceContext}");
        sb.AppendLine($"**Clocky Version:** {UpdateManager.CurrentVersion} (Release standalone)");
        sb.AppendLine($"**Process ID:** {Process.GetCurrentProcess().Id}");
        sb.AppendLine();
        sb.AppendLine("## System Environment");
        sb.AppendLine($"- **OS:** {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"- **.NET Runtime:** {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"- **Processor Count:** {Environment.ProcessorCount} Threads");
        sb.AppendLine($"- **Machine Name:** {Environment.MachineName}");
        sb.AppendLine();
        sb.AppendLine("## Exception Details");
        sb.AppendLine($"- **Type:** `{ex.GetType().FullName}`");
        sb.AppendLine($"- **Message:** {ex.Message}");
        sb.AppendLine($"- **Target Site:** `{ex.TargetSite}`");
        sb.AppendLine();
        sb.AppendLine("### Stack Trace");
        sb.AppendLine("```text");
        sb.AppendLine(ex.ToString());
        sb.AppendLine("```");

        if (ex.InnerException != null)
        {
            sb.AppendLine();
            sb.AppendLine("### Inner Exception");
            sb.AppendLine($"- **Type:** `{ex.InnerException.GetType().FullName}`");
            sb.AppendLine($"- **Message:** {ex.InnerException.Message}");
            sb.AppendLine("```text");
            sb.AppendLine(ex.InnerException.ToString());
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    public static string BuildEmailUri(Exception ex, string sourceContext)
    {
        string subject = Uri.EscapeDataString($"[Clocky Bug Report] {ex.GetType().Name}: {ex.Message}");
        
        var bodySb = new StringBuilder();
        bodySb.AppendLine("Hi Petra,");
        bodySb.AppendLine();
        bodySb.AppendLine("Clocky encountered an unexpected issue on my computer. Here are the details:");
        bodySb.AppendLine();
        bodySb.AppendLine($"[System Environment]");
        bodySb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        bodySb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        bodySb.AppendLine($"CPU Threads: {Environment.ProcessorCount}");
        bodySb.AppendLine();
        bodySb.AppendLine($"[Exception]");
        bodySb.AppendLine($"Type: {ex.GetType().FullName}");
        bodySb.AppendLine($"Message: {ex.Message}");
        bodySb.AppendLine();
        bodySb.AppendLine($"[Stack Trace]");
        bodySb.AppendLine(ex.StackTrace ?? ex.ToString());

        string body = Uri.EscapeDataString(bodySb.ToString());
        return $"mailto:{DeveloperEmail}?subject={subject}&body={body}";
    }
}
