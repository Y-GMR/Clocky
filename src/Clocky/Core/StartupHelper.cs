using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
        // 1. Primary check: Windows Task Scheduler COM API (in-process, no process spawn)
        try
        {
            Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType != null)
            {
                dynamic? scheduler = Activator.CreateInstance(schedulerType);
                if (scheduler != null)
                {
                    scheduler.Connect();
                    dynamic rootFolder = scheduler.GetFolder(@"\");
                    if (rootFolder != null)
                    {
                        try
                        {
                            dynamic task = rootFolder.GetTask(TaskName);
                            if (task is not null)
                            {
                                bool isEnabled = task.Enabled;
                                if (isEnabled) return true;
                            }
                        }
                        catch (COMException)
                        {
                            // Task does not exist in scheduler
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticRingBuffer.Log("StartupHelper:IsStartupEnabledCom", ex);
        }

        // 2. Secondary check: schtasks.exe CLI fallback
        try
        {
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
        }
        catch (Exception ex)
        {
            DiagnosticRingBuffer.Log("StartupHelper:IsStartupEnabledCli", ex);
        }

        // 3. Legacy fallback: HKCU Run registry key
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

                // Attempt in-process Task Scheduler COM registration (zero temporary disk files)
                bool comSucceeded = false;
                try
                {
                    Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
                    if (schedulerType != null)
                    {
                        dynamic? scheduler = Activator.CreateInstance(schedulerType);
                        if (scheduler != null)
                        {
                            scheduler.Connect();
                            dynamic rootFolder = scheduler.GetFolder(@"\");
                            if (rootFolder != null)
                            {
                                dynamic taskDef = scheduler.NewTask(0);
                                taskDef.RegistrationInfo.Description = "Clocky Hardware Telemetry Auto-Start";

                                // Trigger: Logon (TASK_TRIGGER_LOGON = 9)
                                dynamic trigger = taskDef.Triggers.Create(9);
                                trigger.Enabled = true;

                                // Principal: RunLevel HighestAvailable (1), InteractiveToken (3)
                                taskDef.Principal.RunLevel = 1;
                                taskDef.Principal.LogonType = 3;

                                // Settings
                                taskDef.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
                                taskDef.Settings.DisallowStartIfOnBatteries = false;
                                taskDef.Settings.StopIfGoingOnBatteries = false;
                                taskDef.Settings.ExecutionTimeLimit = "PT0S";
                                taskDef.Settings.Priority = 4;
                                taskDef.Settings.AllowStartOnDemand = true;
                                taskDef.Settings.Enabled = true;

                                // Action: Exec (TASK_ACTION_EXEC = 0)
                                dynamic action = taskDef.Actions.Create(0);
                                action.Path = exePath;
                                if (!string.IsNullOrEmpty(arguments))
                                {
                                    action.Arguments = arguments;
                                }

                                // Register: TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_INTERACTIVE_TOKEN = 3
                                rootFolder.RegisterTaskDefinition(TaskName, taskDef, 6, null, null, 3, null);
                                comSucceeded = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticRingBuffer.Log("StartupHelper:SetStartupCom", ex);
                }

                if (!comSucceeded)
                {
                    // Fallback to schtasks.exe CLI with temporary XML descriptor
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
                    catch (Exception ex)
                    {
                        DiagnosticRingBuffer.Log("StartupHelper:SetStartupCli", ex);
                    }
                    finally
                    {
                        if (File.Exists(tempXml))
                        {
                            try { File.Delete(tempXml); } catch { }
                        }
                    }
                }
            }
            else
            {
                // Disable startup: Try COM API first
                bool comDeleted = false;
                try
                {
                    Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
                    if (schedulerType != null)
                    {
                        dynamic? scheduler = Activator.CreateInstance(schedulerType);
                        if (scheduler != null)
                        {
                            scheduler.Connect();
                            dynamic rootFolder = scheduler.GetFolder(@"\");
                            if (rootFolder != null)
                            {
                                try
                                {
                                    rootFolder.DeleteTask(TaskName, 0);
                                    comDeleted = true;
                                }
                                catch (COMException)
                                {
                                    // Task does not exist
                                    comDeleted = true;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticRingBuffer.Log("StartupHelper:DeleteStartupCom", ex);
                }

                if (!comDeleted)
                {
                    try
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
                    catch (Exception ex)
                    {
                        DiagnosticRingBuffer.Log("StartupHelper:DeleteStartupCli", ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticRingBuffer.Log("StartupHelper:SetStartup", ex);
        }
    }
}
