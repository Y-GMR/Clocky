using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Clocky.Config;
using Clocky.Core;
using Clocky.Tray;
using Clocky.UI;

namespace Clocky;

public partial class App : System.Windows.Application
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    private HardwareEngine? _hardwareEngine;
    private TrayIconManager? _trayManager;
    private MainWindow? _mainWindow;
    private static AppConfig? _config;

    private static void LogDebug(string msg, bool force = false)
    {
        if (!force && _config?.EnableDebugLog != true) return;
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_debug.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize Global Exception Interception and Crash Reporting
        GlobalExceptionHandler.Initialize();

        LogDebug("OnStartup began.");

        try
        {
            _config = AppConfig.Load();
            LogDebug("Config loaded.");

            _hardwareEngine = new HardwareEngine(_config.PollingIntervalMs);
            LogDebug("HardwareEngine constructed.");

            _mainWindow = new MainWindow(_hardwareEngine, _config, () => _trayManager?.ReloadIcons());
            MainWindow = _mainWindow;
            LogDebug("MainWindow constructed.");

            _trayManager = new TrayIconManager(
                _config,
                onToggleMainWindow: ToggleMainWindow,
                onExitApp: ExitApp,
                onSelectTab: (tabIdx) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_mainWindow == null) return;
                        if (!_mainWindow.IsVisible || _mainWindow.WindowState == WindowState.Minimized)
                        {
                            _mainWindow.Show();
                            _mainWindow.WindowState = WindowState.Normal;
                            var helper = new WindowInteropHelper(_mainWindow);
                            if (helper.Handle != IntPtr.Zero)
                            {
                                ShowWindow(helper.Handle, SW_RESTORE);
                                SetForegroundWindow(helper.Handle);
                            }
                        }
                        _mainWindow.Topmost = _config?.AlwaysOnTop ?? true;
                        _mainWindow.Activate();
                        _mainWindow.Focus();
                        _mainWindow.SelectTab(tabIdx);
                    });
                }
            );
            LogDebug("TrayIconManager constructed.");

            _hardwareEngine.TelemetryUpdated += OnTelemetryUpdated;
            _hardwareEngine.Start();
            LogDebug("HardwareEngine started.");

            int targetTab = -1;
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--tab" && i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out int tabIdx))
                {
                    targetTab = tabIdx;
                }
            }

            LogDebug("Calling _mainWindow.Show()...");
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            _mainWindow.Focus();
            _mainWindow.Topmost = _config.AlwaysOnTop;
            LogDebug($"MainWindow Show called. IsVisible={_mainWindow.IsVisible}, WindowState={_mainWindow.WindowState}");

            if (targetTab >= 0)
            {
                _mainWindow.SelectTab(targetTab);
                LogDebug($"Selected tab {targetTab}.");
            }

            // Flush startup JIT and XAML cold allocations after initial layout
            Task.Run(async () =>
            {
                await Task.Delay(3000);
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            });
        }
        catch (Exception ex)
        {
            LogDebug($"Fatal exception during startup: {ex}", force: true);
        }
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private void OnTelemetryUpdated(TelemetrySnapshot snap)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            try
            {
                _trayManager?.UpdateTelemetry(snap);
                _mainWindow?.RecordHistorySamples(snap);
                if (_mainWindow != null && _mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized && !_mainWindow.IsDraggingOrResizing)
                {
                    _mainWindow.RenderSnapshot(snap);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error in OnTelemetryUpdated: {ex.Message}");
            }
        });
    }

    private void ToggleMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (_mainWindow == null) return;

                if (_mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
                {
                    _mainWindow.Hide();
                    EmptyWorkingSet(Process.GetCurrentProcess().Handle);
                    LogDebug("MainWindow hidden via Toggle.");
                }
                else
                {
                    _mainWindow.Show();
                    _mainWindow.WindowState = WindowState.Normal;
                    var helper = new WindowInteropHelper(_mainWindow);
                    if (helper.Handle != IntPtr.Zero)
                    {
                        ShowWindow(helper.Handle, SW_RESTORE);
                        SetForegroundWindow(helper.Handle);
                    }
                    _mainWindow.Topmost = _config?.AlwaysOnTop ?? true;
                    _mainWindow.Activate();
                    _mainWindow.Focus();
                    LogDebug("MainWindow shown via Toggle.");
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error in ToggleMainWindow: {ex}");
            }
        });
    }

    private void ExitApp()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                _hardwareEngine?.Stop();
                _hardwareEngine?.Dispose();
                _trayManager?.Dispose();
                _mainWindow?.Close();
                Shutdown();
            }
            catch (Exception ex)
            {
                LogDebug($"Error in ExitApp: {ex}");
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hardwareEngine?.Dispose();
        _trayManager?.Dispose();
        base.OnExit(e);
    }
}
