using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Clocky.Config;
using Clocky.Core;

namespace Clocky.Tray;

public class TrayIconManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly Dictionary<string, ClockyTrayIcon> _trayIcons = new();
    private readonly Dictionary<string, (string Val, int Bg, int Fg, int? Border, string Tooltip)> _lastTrayState = new();
    private ClockyTrayIcon? _mainAppIcon;
    private readonly Action _onToggleMainWindow;
    private readonly Action _onExitApp;
    private readonly Action<int>? _onSelectTab;

    public TrayIconManager(AppConfig config, Action onToggleMainWindow, Action onExitApp, Action<int>? onSelectTab = null)
    {
        _config = config;
        _onToggleMainWindow = onToggleMainWindow;
        _onExitApp = onExitApp;
        _onSelectTab = onSelectTab;

        InitializeIcons();
    }

    public void ReloadIcons() => InitializeIcons();

    public void InitializeIcons()
    {
        _lastTrayState.Clear();

        // 1. Permanent Main Clocky Application Tray Icon with unique uID 100
        if (_mainAppIcon == null)
        {
            _mainAppIcon = new ClockyTrayIcon(100, "MainApp");
            _mainAppIcon.ContextMenuStrip = BuildMainAppContextMenu();
            _mainAppIcon.SetIcon(CreateClockyAppIcon(), "Clocky");
            _mainAppIcon.LeftClick += () => _onToggleMainWindow();
        }
        else
        {
            _mainAppIcon.ContextMenuStrip = BuildMainAppContextMenu();
        }

        // 2. Differential Sensor Badges Update (Each with distinct uID and HWND)
        var enabledSensors = _config.TraySensors.Where(s => s.Enabled).OrderBy(s => s.Order).ToList();
        var enabledIds = new HashSet<string>(enabledSensors.Select(s => s.Id));

        // Remove sensors that have been disabled
        var toRemove = _trayIcons.Keys.Where(k => !enabledIds.Contains(k)).ToList();
        foreach (var id in toRemove)
        {
            if (_trayIcons.TryGetValue(id, out var icon))
            {
                icon.Dispose();
                _trayIcons.Remove(id);
            }
        }

        // Add or refresh enabled sensor badges
        var sensorMenu = BuildSensorOnlyContextMenu();

        foreach (var sensor in enabledSensors)
        {
            int sensorUid = 200 + sensor.Order * 10 + (sensor.Id.GetHashCode() & 0xFF);

            if (!_trayIcons.TryGetValue(sensor.Id, out var notifyIcon))
            {
                notifyIcon = new ClockyTrayIcon(sensorUid, sensor.Id);
                notifyIcon.ContextMenuStrip = sensorMenu;

                string sensorIdRef = sensor.Id;
                notifyIcon.LeftClick += () =>
                {
                    // Left-clicking a sensor badge jumps to that specific tab
                    int targetTab = sensorIdRef switch
                    {
                        "cpu.temp" or "cpu.power" or "cpu.util" => 1, // Tab 2: CPU
                        "gpu.temp" or "gpu.power" or "gpu.util" => 2, // Tab 3: GPU
                        "ram.percent" => 3,                          // Tab 4: Memory & Storage
                        "system.power" or "battery.life" => 4,       // Tab 5: Power & Battery
                        _ => 0                                       // Tab 1: All Sensors
                    };
                    _onSelectTab?.Invoke(targetTab);
                };

                var col = ColorTranslator.FromHtml(sensor.BackgroundColorHex);
                var textCol = ColorTranslator.FromHtml(sensor.TextColorHex);
                using var badge = BadgeRenderer.RenderBadge("--", col, textCol, null, 32);
                notifyIcon.SetIcon(badge, $"Clocky - {sensor.Label}");

                _trayIcons[sensor.Id] = notifyIcon;
            }
            else
            {
                notifyIcon.ContextMenuStrip = sensorMenu;
            }
        }
    }

    private Icon CreateClockyAppIcon()
    {
        try
        {
            if (!string.IsNullOrEmpty(Environment.ProcessPath))
            {
                var exeIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (exeIcon != null) return exeIcon;
            }
        }
        catch { }

        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(iconPath))
            {
                return new Icon(iconPath);
            }
        }
        catch { }

        return BadgeRenderer.RenderBadge("CK", Color.FromArgb(88, 101, 242), Color.White, Color.FromArgb(140, 255, 255, 255), 32);
    }

    private ContextMenuStrip BuildMainAppContextMenu()
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("Open Clocky Dashboard") { Font = new Font(menu.Font, FontStyle.Bold) };
        openItem.Click += (s, e) => _onToggleMainWindow();
        menu.Items.Add(openItem);

        var prefItem = new ToolStripMenuItem("Preferences...");
        prefItem.Click += (s, e) =>
        {
            _onSelectTab?.Invoke(7); // Tab 7: Tray & Preferences
        };
        menu.Items.Add(prefItem);

        menu.Items.Add(new ToolStripSeparator());

        var sensorsMenu = new ToolStripMenuItem("Tray Sensors");
        foreach (var sensor in _config.TraySensors)
        {
            var item = new ToolStripMenuItem(sensor.Label)
            {
                Checked = sensor.Enabled,
                CheckOnClick = true
            };
            var sRef = sensor;
            item.CheckedChanged += (s, e) =>
            {
                sRef.Enabled = item.Checked;
                _config.Save();
                InitializeIcons();
            };
            sensorsMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(sensorsMenu);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit Clocky");
        exitItem.Click += (s, e) => _onExitApp();
        menu.Items.Add(exitItem);

        return menu;
    }

    private ContextMenuStrip BuildSensorOnlyContextMenu()
    {
        var menu = new ContextMenuStrip();

        var header = new ToolStripMenuItem("Tray Sensors") 
        { 
            Enabled = false, 
            Font = new Font(menu.Font, FontStyle.Bold) 
        };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        foreach (var sensor in _config.TraySensors)
        {
            var item = new ToolStripMenuItem(sensor.Label)
            {
                Checked = sensor.Enabled,
                CheckOnClick = true
            };
            var sRef = sensor;
            item.CheckedChanged += (s, e) =>
            {
                sRef.Enabled = item.Checked;
                _config.Save();
                InitializeIcons();
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    public void UpdateTelemetry(TelemetrySnapshot snap)
    {
        var cpuBrand = HardwareVendorHelper.DetectCpuVendor(snap.CpuName);
        var gpuBrand = HardwareVendorHelper.DetectGpuVendor(snap.GpuName);

        foreach (var sensor in _config.TraySensors.Where(s => s.Enabled))
        {
            if (!_trayIcons.TryGetValue(sensor.Id, out var icon))
                continue;

            string displayValue = "--";
            string tooltip = $"Clocky - {sensor.Label}: ";

            Color bg = Color.FromArgb(15, 23, 42);
            Color fg = Color.White;
            Color? border = null;

            bool hasCustomBg = !string.IsNullOrEmpty(sensor.BackgroundColorHex);

            switch (sensor.Id)
            {
                case "cpu.temp":
                    displayValue = Math.Round(snap.CpuPackageTemp).ToString("0");
                    tooltip += $"{snap.CpuPackageTemp:0.#} °C ({cpuBrand.VendorName})";
                    bg = hasCustomBg ? ColorTranslator.FromHtml(sensor.BackgroundColorHex) : ColorTranslator.FromHtml(cpuBrand.PrimaryColorHex);
                    fg = ColorTranslator.FromHtml(cpuBrand.TextColorHex);
                    border = ColorTranslator.FromHtml(cpuBrand.BorderColorHex);
                    break;

                case "gpu.temp":
                    displayValue = Math.Round(snap.GpuCoreTemp).ToString("0");
                    tooltip += $"{snap.GpuCoreTemp:0.#} °C ({gpuBrand.VendorName})";
                    bg = hasCustomBg ? ColorTranslator.FromHtml(sensor.BackgroundColorHex) : ColorTranslator.FromHtml(gpuBrand.PrimaryColorHex);
                    fg = ColorTranslator.FromHtml(gpuBrand.TextColorHex);
                    border = ColorTranslator.FromHtml(gpuBrand.BorderColorHex);
                    break;

                case "cpu.power":
                    displayValue = Math.Round(snap.CpuPackagePower).ToString("0");
                    tooltip += $"{snap.CpuPackagePower:0.#} W ({cpuBrand.VendorName})";
                    bg = hasCustomBg ? ColorTranslator.FromHtml(sensor.BackgroundColorHex) : ColorTranslator.FromHtml(cpuBrand.SecondaryColorHex);
                    fg = ColorTranslator.FromHtml(cpuBrand.TextColorHex);
                    border = ColorTranslator.FromHtml(cpuBrand.BorderColorHex);
                    break;

                case "gpu.power":
                    displayValue = Math.Round(snap.GpuPowerDraw).ToString("0");
                    tooltip += $"{snap.GpuPowerDraw:0.#} W ({gpuBrand.VendorName})";
                    bg = hasCustomBg ? ColorTranslator.FromHtml(sensor.BackgroundColorHex) : ColorTranslator.FromHtml(gpuBrand.SecondaryColorHex);
                    fg = ColorTranslator.FromHtml(gpuBrand.TextColorHex);
                    border = ColorTranslator.FromHtml(gpuBrand.BorderColorHex);
                    break;

                case "system.power":
                    displayValue = Math.Round(snap.TotalSystemPowerWatts).ToString("0");
                    tooltip += $"{snap.TotalSystemPowerWatts:0.#} W (Platform)";
                    bg = hasCustomBg ? ColorTranslator.FromHtml(sensor.BackgroundColorHex) : Color.FromArgb(15, 23, 42);
                    fg = Color.White;
                    border = Color.FromArgb(51, 65, 85);
                    break;

                case "battery.life":
                    if (snap.HasBattery)
                    {
                        if (snap.IsAcConnected)
                        {
                            displayValue = "AC";
                            tooltip += "AC Connected (Charging)";
                            bg = Color.FromArgb(22, 101, 52); // Green
                        }
                        else if (snap.BatteryTimeRemaining.HasValue)
                        {
                            var t = snap.BatteryTimeRemaining.Value;
                            displayValue = t.TotalHours >= 1 ? $"{t.Hours}h" : $"{t.Minutes}m";
                            tooltip += $"{t.Hours}h {t.Minutes}m Remaining ({snap.BatteryPercent:0}%)";
                            bg = hasCustomBg ? ColorTranslator.FromHtml(sensor.BackgroundColorHex) : Color.FromArgb(51, 65, 85);
                        }
                        else
                        {
                            displayValue = $"{Math.Round(snap.BatteryPercent)}%";
                            tooltip += $"{snap.BatteryPercent:0}%";
                            bg = hasCustomBg ? ColorTranslator.FromHtml(sensor.BackgroundColorHex) : Color.FromArgb(51, 65, 85);
                        }
                    }
                    else
                    {
                        displayValue = "AC";
                        tooltip += "Desktop / AC Power";
                        bg = Color.FromArgb(15, 23, 42);
                    }
                    fg = Color.White;
                    break;

                case "ram.percent":
                    displayValue = Math.Round(snap.RamUsagePercent).ToString("0");
                    tooltip += $"{snap.RamUsagePercent:0}% ({snap.RamUsedGb:0.1}/{snap.RamTotalGb:0.1} GB)";
                    bg = hasCustomBg ? ColorTranslator.FromHtml(sensor.BackgroundColorHex) : Color.FromArgb(217, 119, 6);
                    fg = Color.White;
                    break;

                case "cpu.util":
                    displayValue = Math.Round(snap.CpuTotalUtil).ToString("0");
                    tooltip += $"{snap.CpuTotalUtil:0}% ({cpuBrand.VendorName})";
                    bg = hasCustomBg ? ColorTranslator.FromHtml(sensor.BackgroundColorHex) : ColorTranslator.FromHtml(cpuBrand.PrimaryColorHex);
                    fg = ColorTranslator.FromHtml(cpuBrand.TextColorHex);
                    border = ColorTranslator.FromHtml(cpuBrand.BorderColorHex);
                    break;

                case "gpu.util":
                    displayValue = Math.Round(snap.GpuCoreUtil).ToString("0");
                    tooltip += $"{snap.GpuCoreUtil:0}% ({gpuBrand.VendorName})";
                    bg = hasCustomBg ? ColorTranslator.FromHtml(sensor.BackgroundColorHex) : ColorTranslator.FromHtml(gpuBrand.PrimaryColorHex);
                    fg = ColorTranslator.FromHtml(gpuBrand.TextColorHex);
                    border = ColorTranslator.FromHtml(gpuBrand.BorderColorHex);
                    break;
            }

            string cleanTooltip = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
            int bgArgb = bg.ToArgb();
            int fgArgb = fg.ToArgb();
            int? borderArgb = border?.ToArgb();

            if (_lastTrayState.TryGetValue(sensor.Id, out var last) &&
                last.Val == displayValue &&
                last.Bg == bgArgb &&
                last.Fg == fgArgb &&
                last.Border == borderArgb &&
                last.Tooltip == cleanTooltip)
            {
                continue;
            }

            _lastTrayState[sensor.Id] = (displayValue, bgArgb, fgArgb, borderArgb, cleanTooltip);

            try
            {
                using var badge = BadgeRenderer.RenderBadge(displayValue, bg, fg, border, 32);
                icon.SetIcon(badge, cleanTooltip);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_mainAppIcon != null)
        {
            _mainAppIcon.Dispose();
            _mainAppIcon = null;
        }

        foreach (var icon in _trayIcons.Values)
        {
            icon.Dispose();
        }
        _trayIcons.Clear();
    }
}
