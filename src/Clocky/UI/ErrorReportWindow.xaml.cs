using System;
using System.Diagnostics;
using System.IO;
using Clocky.Core;
using WpfApp = System.Windows.Application;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;

namespace Clocky.UI;

public partial class ErrorReportWindow : System.Windows.Window
{
    private readonly Exception _exception;
    private readonly string _sourceContext;
    private readonly string _logPath;
    private readonly string _diagnosticReport;

    public ErrorReportWindow(Exception exception, string sourceContext, string logPath)
    {
        _exception = exception;
        _sourceContext = sourceContext;
        _logPath = logPath;

        InitializeComponent();

        TxtExceptionType.Text = exception.GetType().FullName ?? "Unknown Exception";
        TxtExceptionMessage.Text = exception.Message;
        TxtLogPath.Text = logPath;

        _diagnosticReport = GlobalExceptionHandler.GenerateDiagnosticReport(exception, sourceContext);
        TxtStackTrace.Text = _diagnosticReport;
    }

    private void BtnEmailReport_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            string mailtoUri = GlobalExceptionHandler.BuildEmailUri(_exception, _sourceContext);
            Process.Start(new ProcessStartInfo
            {
                FileName = mailtoUri,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // Fallback: Copy to clipboard if mail client failed to open
            WpfClipboard.SetText(_diagnosticReport);
            WpfMessageBox.Show(
                $"Unable to launch default email client.\n\nThe diagnostic error report has been COPIED to your clipboard.\nPlease email it to:\n{GlobalExceptionHandler.DeveloperEmail}",
                "Clocky - Error Report Copied",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Information);
        }
    }

    private void BtnCopyClipboard_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            WpfClipboard.SetText(_diagnosticReport);
            BtnCopyClipboard.Content = "Copied to Clipboard!";
        }
        catch { }
    }

    private void BtnOpenLogsFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            string logsDir = GlobalExceptionHandler.GetLogsDirectory();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{logsDir}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void BtnRestart_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName 
                          ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Clocky.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            });
            WpfApp.Current.Shutdown();
        }
        catch
        {
            WpfApp.Current.Shutdown();
        }
    }

    private void BtnClose_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        this.Close();
    }
}
