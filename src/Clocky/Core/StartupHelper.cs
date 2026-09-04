using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Clocky.Core;

public static class StartupHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string TaskName = "Clocky";
    private const string AppName = "Clocky";

    public static bool IsStartupEnabled()
    {
        try
        {
            // 1. Primary check: Windows Task Scheduler (Required for elevated binaries)
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/query /tn \"{TaskName}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            if (proc?.ExitCode == 0) return true;

            // 2. Legacy fallback: HKCU Run registry key
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
            // Always clean up legacy HKCU Run entry to prevent dead/ignored keys
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                runKey?.DeleteValue(AppName, false);
            }
            catch { }

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                }

                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

                string arguments = startMinimized ? "--minimized" : "";

                // Windows UAC Architecture:
                // Applications with <requestedExecutionLevel level="requireAdministrator" />
                // are silently blocked by Windows Explorer from auto-starting via HKCU/HKLM Run keys.
                // An elevated Scheduled Task configured with HighestAvailable runlevel is the authoritative,
                // standard Windows mechanism to auto-start elevated utilities on user logon without UAC prompts.
                string escapedExePath = SecurityElement.Escape(exePath);
                string escapedArgs = SecurityElement.Escape(arguments);

                string xmlContent = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Clocky Hardware Telemetry Auto-Start</Description>
    <URI>\{TaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{escapedExePath}</Command>
      {(string.IsNullOrEmpty(arguments) ? "" : $"<Arguments>{escapedArgs}</Arguments>")}
    </Exec>
  </Actions>
</Task>";

                string tempXml = Path.Combine(Path.GetTempPath(), $"Clocky_Startup_{Guid.NewGuid():N}.xml");
                File.WriteAllText(tempXml, xmlContent, System.Text.Encoding.Unicode);

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/create /tn \"{TaskName}\" /xml \"{tempXml}\" /f",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit();
                }
                finally
                {
                    if (File.Exists(tempXml))
                    {
                        try { File.Delete(tempXml); } catch { }
                    }
                }
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/delete /tn \"{TaskName}\" /f",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
            }
        }
        catch { }
    }
}
