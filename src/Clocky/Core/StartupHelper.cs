using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Clocky.Core;

public static class StartupHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Clocky";

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetStartup(bool enable, bool startMinimized = false)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                }

                if (!string.IsNullOrEmpty(exePath))
                {
                    string command = startMinimized ? $"\"{exePath}\" --minimized" : $"\"{exePath}\"";
                    key.SetValue(AppName, command);
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch { }
    }
}
