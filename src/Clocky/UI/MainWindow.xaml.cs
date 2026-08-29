using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Clocky.Config;
using Clocky.Core;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;
using MediaBrushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using WpfToolTip = System.Windows.Controls.ToolTip;
using WpfProgressBar = System.Windows.Controls.ProgressBar;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfPath = System.Windows.Shapes.Path;

namespace Clocky.UI;

public partial class MainWindow : Window
{
    private const int MaxHistoryPoints = 60;

    // Test Server Interop Properties
    public int ActiveTabIndex
    {
        get
        {
            if (TabAllSensors?.IsChecked == true) return 0;
            if (TabCpu?.IsChecked == true) return 1;
            if (TabGpu?.IsChecked == true) return 2;
            if (TabMemoryDisks?.IsChecked == true) return 3;
            if (TabPower?.IsChecked == true) return 4;
            if (TabNetwork?.IsChecked == true) return 5;
            if (TabProcesses?.IsChecked == true) return 6;
            if (TabTray?.IsChecked == true) return 7;
            return 0;
        }
    }
    public int CurrentTab => ActiveTabIndex;
    public string CurrentThemeMode => _currentThemeMode;
    public bool IsAlwaysOnTop => _config?.AlwaysOnTop ?? false;
    public int ActiveSensorsCount => _engine?.CurrentSnapshot?.AllSensors?.Count ?? 0;
    public string HdrCpuText => HdrCpu?.Text ?? "";
    public string HdrGpuText => HdrGpu?.Text ?? "";
    public string HdrPowerText => HdrPower?.Text ?? "";
    public string HdrRamText => HdrRam?.Text ?? "";
    public TelemetrySnapshot? LatestSnapshot => _latestSnapshot;

    private readonly HardwareEngine _engine;
    private readonly AppConfig _config;
    private readonly Action? _onReloadTrayIcons;
#if DEBUG
    private readonly ClockyTestServer? _testServer;
#endif

    private string _currentThemeMode = "System";
    private bool _isDarkTheme = true;
    private string _activeSortColumn = "";
    private ListSortDirection? _activeSortDirection = null;
    private TelemetrySnapshot? _latestSnapshot;
    private string _sensorFilter = "";

    private string _processFilter = "";
    private string _activeProcessSortColumn = "";
    private ListSortDirection? _activeProcessSortDirection = null;

    // CPU Visual Core Controls (24 Threads: 16 P-Core + 8 E-Core)
    private readonly List<Canvas> _pCoreCanvases = new();
    private readonly List<TextBlock> _pCoreClockLabels = new();
    private readonly List<TextBlock> _pCoreValueLabels = new();
    private readonly List<List<float>> _pCoreHistories = new();

    private readonly List<Canvas> _eCoreCanvases = new();
    private readonly List<TextBlock> _eCoreClockLabels = new();
    private readonly List<TextBlock> _eCoreValueLabels = new();
    private readonly List<List<float>> _eCoreHistories = new();

    // CPU 4 Core Metric Graphs Rolling History Buffers
    private readonly List<float> _cpuLoadHistory = new();
    private readonly List<float> _cpuTempHistory = new();
    private readonly List<float> _cpuPowerHistory = new();
    private readonly List<float> _cpuFreqHistory = new();

    // GPU 4 Core Metric Graphs Rolling History Buffers
    private readonly List<float> _gpuUtilHistory = new();
    private readonly List<float> _gpuTempHistory = new();
    private readonly List<float> _gpuPowerHistory = new();
    private readonly List<float> _gpuVramHistory = new();

    // GPU Hardware Engine Rolling History Buffers
    private readonly List<float> _gpu3dHistory = new();
    private readonly List<float> _gpuComputeHistory = new();
    private readonly List<float> _gpuDecoderHistory = new();
    private readonly List<float> _gpuEncoderHistory = new();
    private readonly List<float> _gpuCopyHistory = new();
    private readonly List<float> _gpuMemCtrlHistory = new();

    // Global Rolling History Buffers
    private readonly List<float> _ramHistory = new();
    private readonly List<float> _diskReadHistory = new();
    private readonly List<float> _diskWriteHistory = new();
    private readonly List<float> _powerHistory = new();
    private readonly List<float> _netDownHistory = new();
    private readonly List<float> _netUpHistory = new();

    // Instant Continuous Hover State Cache (Canvas -> (NearestIndex, PtX, PtY, InterpVal))
    private readonly Dictionary<Canvas, (int SampleIdx, double PtX, double PtY, float Val)> _activeHoverStates = new();
    private readonly Dictionary<Canvas, GraphHoverOverlay> _graphOverlays = new();

    // =========================================================================
    // COLOR PALETTE CONSTANTS & HELPERS
    // =========================================================================
    private static readonly MediaColor DiscordBlurple = MediaColor.FromRgb(114, 137, 218);      // #7289DA
    private static readonly MediaColor DiscordBlurpleDark = MediaColor.FromRgb(91, 110, 174);    // #5B6EAE
    private static readonly MediaColor DiscordDarkWindow = MediaColor.FromRgb(30, 33, 36);       // #1E2124
    private static readonly MediaColor DiscordDarkPanel = MediaColor.FromRgb(40, 43, 48);        // #282B30
    private static readonly MediaColor DiscordDarkCard = MediaColor.FromRgb(54, 57, 62);         // #36393E
    private static readonly MediaColor DiscordDarkBorder = MediaColor.FromRgb(66, 69, 73);       // #424549
    private static readonly MediaColor DiscordDarkSecondary = MediaColor.FromRgb(185, 187, 190); // #B9BBBE
    private static readonly MediaColor DiscordDarkMuted = MediaColor.FromRgb(114, 118, 125);     // #72767D

    private static readonly MediaColor LightWindow = MediaColor.FromRgb(240, 243, 246);          // #F0F3F6
    private static readonly MediaColor LightPanel = MediaColor.FromRgb(255, 255, 255);           // #FFFFFF
    private static readonly MediaColor LightCard = MediaColor.FromRgb(248, 250, 252);            // #F8FAFC
    private static readonly MediaColor LightSunken = MediaColor.FromRgb(229, 233, 238);          // #E5E9EE
    private static readonly MediaColor LightBorder = MediaColor.FromRgb(211, 217, 226);          // #D3D9E2
    private static readonly MediaColor LightPrimaryText = MediaColor.FromRgb(15, 23, 42);        // #0F172A
    private static readonly MediaColor LightSecondaryText = MediaColor.FromRgb(71, 85, 105);     // #475569
    private static readonly MediaColor LightMutedText = MediaColor.FromRgb(148, 163, 184);       // #94A3B8

    public static readonly MediaColor MetricGreen = MediaColor.FromRgb(34, 197, 94);   // #22C55E (Read, Upload)
    public static readonly MediaColor MetricRed = MediaColor.FromRgb(239, 68, 68);     // #EF4444 (Write, Download)

    private MediaColor WaveformStroke => DiscordBlurple;
    private MediaColor WaveformFill => _isDarkTheme ? MediaColor.FromArgb(35, 114, 137, 218) : MediaColor.FromArgb(35, 114, 137, 218);
    private MediaColor TextPrimaryColor => _isDarkTheme ? MediaColor.FromRgb(255, 255, 255) : LightPrimaryText;
    private MediaColor TextSecondaryColor => _isDarkTheme ? DiscordDarkSecondary : LightSecondaryText;

    public MainWindow(HardwareEngine engine, AppConfig config, Action? onReloadTrayIcons = null)
    {
        _engine = engine;
        _config = config;
        _onReloadTrayIcons = onReloadTrayIcons;

        InitializeComponent();

#if DEBUG
        // Initialize embedded native test server (Debug builds only)
        _testServer = new ClockyTestServer(this, config);
#endif

        InitializeTheme();

        if (TxtPid != null) TxtPid.Text = Process.GetCurrentProcess().Id.ToString();

        BuildCpuVisualGrid();
        SetupGraphHoverInteractivity();
        SyncTrayControls();

        if (ChkEnableDebugLog != null) ChkEnableDebugLog.IsChecked = _config.EnableDebugLog;
        if (ChkAlwaysOnTopOption != null) ChkAlwaysOnTopOption.IsChecked = _config.AlwaysOnTop;

        this.Topmost = _config.AlwaysOnTop;
        if (BtnAlwaysOnTop != null) BtnAlwaysOnTop.IsChecked = _config.AlwaysOnTop;

        Loaded += (s, e) =>
        {
            this.Topmost = _config.AlwaysOnTop;
            if (BtnAlwaysOnTop != null) BtnAlwaysOnTop.IsChecked = _config.AlwaysOnTop;
            if (ChkAutoCheckUpdates != null) ChkAutoCheckUpdates.IsChecked = _config.AutoCheckUpdates;
            if (ChkCloseToTray != null) ChkCloseToTray.IsChecked = _config.CloseToTray;
            if (ChkMinimizeToTray != null) ChkMinimizeToTray.IsChecked = _config.MinimizeToTray;
            if (ChkAlwaysOnTopOption != null) ChkAlwaysOnTopOption.IsChecked = _config.AlwaysOnTop;
            if (ChkEnableDebugLog != null) ChkEnableDebugLog.IsChecked = _config.EnableDebugLog;
            UpdateTitleBarTheme(_isDarkTheme);
            CheckForUpdatesOnStartup();
        };

        SourceInitialized += (s, e) =>
        {
            UpdateTitleBarTheme(_isDarkTheme);
            var source = (System.Windows.Interop.HwndSource?)PresentationSource.FromVisual(this);
            source?.AddHook(WndProc);
        };

        Closing += (s, e) =>
        {
            if (_config != null && _config.CloseToTray)
            {
                e.Cancel = true;
                this.Hide();
                TrimWorkingSet();
            }
        };

        StateChanged += (s, e) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                TrimWorkingSet();
                if (_config != null && _config.MinimizeToTray)
                {
                    this.Hide();
                }
            }
        };
    }

    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private bool _isDraggingOrResizing = false;

    public bool IsDraggingOrResizing => _isDraggingOrResizing;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ENTERSIZEMOVE)
        {
            _isDraggingOrResizing = true;
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            _isDraggingOrResizing = false;
            if (_latestSnapshot != null)
            {
                RenderSnapshot(_latestSnapshot);
            }
        }
        return IntPtr.Zero;
    }

    [DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hwProc);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    public void UpdateTitleBarTheme(bool isDark)
    {
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) return;

            int darkMode = isDark ? 1 : 0;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            if (hr != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
            }

            // On Windows 11, set caption color and text color to match the theme background
            if (isDark)
            {
                // COLORREF format 0x00BBGGRR: DiscordDarkWindow is #1E2124 (R:30, G:33, B:36 -> 0x0024211E)
                int darkCaptionColor = 0x0024211E;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref darkCaptionColor, sizeof(int));
                int lightTextColor = 0x00FFFFFF;
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref lightTextColor, sizeof(int));
            }
            else
            {
                // LightWindow is #F0F3F6 (R:240, G:243, B:246 -> 0x00F6F3F0)
                int lightCaptionColor = 0x00F6F3F0;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref lightCaptionColor, sizeof(int));
                int darkTextColor = 0x002A170F; // #0F172A
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref darkTextColor, sizeof(int));
            }
        }
        catch { }
    }

    private void TrimWorkingSet()
    {
        try
        {
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch { }
    }

    // =========================================================================
    // THEME MANAGEMENT ENGINE
    // =========================================================================
    private void InitializeTheme()
    {
        string pref = _config?.ThemePreference ?? "System";
        _currentThemeMode = pref;

        if (RadioThemeLight != null && pref == "Light") RadioThemeLight.IsChecked = true;
        else if (RadioThemeDark != null && pref == "Dark") RadioThemeDark.IsChecked = true;
        else if (RadioThemeSystem != null) RadioThemeSystem.IsChecked = true;

        ApplyTheme(pref);
    }

    private static bool DetectSystemDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            if (val is int intVal) return intVal == 0;
        }
        catch { }
        return true;
    }

    public void ApplyTheme(string themeMode)
    {
        _currentThemeMode = themeMode;
        _isDarkTheme = themeMode switch
        {
            "Light" => false,
            "Dark" => true,
            _ => DetectSystemDarkTheme()
        };

        var res = this.Resources;

        if (_isDarkTheme)
        {
            // Discord Dark Palette
            res["BrushWindowBg"] = new SolidColorBrush(DiscordDarkWindow);
            res["BrushPanelBg"] = new SolidColorBrush(DiscordDarkPanel);
            res["BrushCardBg"] = new SolidColorBrush(DiscordDarkCard);
            res["BrushCardSunken"] = new SolidColorBrush(DiscordDarkPanel);
            res["BrushCardBorder"] = new SolidColorBrush(DiscordDarkBorder);
            res["BrushBorderHighlight"] = new SolidColorBrush(MediaColor.FromArgb(20, 255, 255, 255));
            res["BrushTextPrimary"] = new SolidColorBrush(MediaColor.FromRgb(255, 255, 255));
            res["BrushTextSecondary"] = new SolidColorBrush(DiscordDarkSecondary);
            res["BrushTextMuted"] = new SolidColorBrush(DiscordDarkMuted);
            res["BrushAccentCobalt"] = new SolidColorBrush(DiscordBlurple);
            res["BrushAccentCobaltDark"] = new SolidColorBrush(DiscordBlurpleDark);
            res["BrushAccentSlate"] = new SolidColorBrush(DiscordDarkSecondary);
            res["BrushMetricGreen"] = new SolidColorBrush(MetricGreen);
            res["BrushMetricRed"] = new SolidColorBrush(MetricRed);
            res["BrushHeaderVital"] = new SolidColorBrush(MediaColor.FromRgb(255, 255, 255));
            res["BrushGridLine"] = new SolidColorBrush(MediaColor.FromArgb(30, 255, 255, 255));
            res["SkeuoCardShadow"] = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 315, Color = MediaColor.FromRgb(15, 17, 19), Opacity = 0.8 };
            res["SkeuoButtonShadow"] = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Direction = 315, Color = MediaColor.FromRgb(15, 17, 19), Opacity = 0.65 };
            res["SkeuoActiveShadow"] = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 315, Color = DiscordBlurple, Opacity = 0.4 };
            res["SkeuoSunkenShadow"] = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Direction = 135, Color = MediaColor.FromRgb(15, 17, 19), Opacity = 0.5 };
        }
        else
        {
            // Studio Light Palette with Discord Blue
            res["BrushWindowBg"] = new SolidColorBrush(LightWindow);
            res["BrushPanelBg"] = new SolidColorBrush(LightPanel);
            res["BrushCardBg"] = new SolidColorBrush(LightCard);
            res["BrushCardSunken"] = new SolidColorBrush(LightSunken);
            res["BrushCardBorder"] = new SolidColorBrush(LightBorder);
            res["BrushBorderHighlight"] = new SolidColorBrush(MediaColor.FromArgb(200, 255, 255, 255));
            res["BrushTextPrimary"] = new SolidColorBrush(LightPrimaryText);
            res["BrushTextSecondary"] = new SolidColorBrush(LightSecondaryText);
            res["BrushTextMuted"] = new SolidColorBrush(LightMutedText);
            res["BrushAccentCobalt"] = new SolidColorBrush(DiscordBlurple);
            res["BrushAccentCobaltDark"] = new SolidColorBrush(DiscordBlurpleDark);
            res["BrushAccentSlate"] = new SolidColorBrush(MediaColor.FromRgb(100, 116, 139));
            res["BrushMetricGreen"] = new SolidColorBrush(MetricGreen);
            res["BrushMetricRed"] = new SolidColorBrush(MetricRed);
            res["BrushHeaderVital"] = new SolidColorBrush(LightPrimaryText);
            res["BrushGridLine"] = new SolidColorBrush(MediaColor.FromArgb(50, 0, 0, 0));
            res["SkeuoCardShadow"] = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 315, Color = MediaColor.FromArgb(30, 0, 0, 0), Opacity = 0.4 };
            res["SkeuoButtonShadow"] = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Direction = 315, Color = MediaColor.FromArgb(20, 0, 0, 0), Opacity = 0.3 };
            res["SkeuoActiveShadow"] = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 315, Color = DiscordBlurple, Opacity = 0.35 };
            res["SkeuoSunkenShadow"] = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1.5, Direction = 135, Color = MediaColor.FromArgb(30, 0, 0, 0), Opacity = 0.25 };
        }

        UpdateTitleBarTheme(_isDarkTheme);

        if (ImgClockyBrand != null || ImgClockySidebarBrand != null)
        {
            try
            {
                string logoUri = _isDarkTheme 
                    ? "pack://application:,,,/Assets/clocky_title_dark.png" 
                    : "pack://application:,,,/Assets/clocky_title_light.png";
                var bmp = new BitmapImage(new Uri(logoUri, UriKind.RelativeOrAbsolute));
                if (ImgClockyBrand != null) ImgClockyBrand.Source = bmp;
                if (ImgClockySidebarBrand != null) ImgClockySidebarBrand.Source = bmp;
            }
            catch { }
        }

        PanelTopCpu?.Children.Clear();
        PanelTopGpu?.Children.Clear();
        PanelTopRam?.Children.Clear();
        PanelTopNet?.Children.Clear();
        PanelDisksContainer?.Children.Clear();
        PanelNetworkAdapters?.Children.Clear();

        BuildCpuVisualGrid();

        foreach (var overlay in _graphOverlays.Values)
        {
            overlay.UpdateThemeBrushes(_isDarkTheme);
        }

        if (_latestSnapshot != null)
        {
            RenderSnapshot(_latestSnapshot);
        }
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;

        if (RadioThemeSystem?.IsChecked == true)
        {
            _config.ThemePreference = "System";
            ApplyTheme("System");
        }
        else if (RadioThemeLight?.IsChecked == true)
        {
            _config.ThemePreference = "Light";
            ApplyTheme("Light");
        }
        else if (RadioThemeDark?.IsChecked == true)
        {
            _config.ThemePreference = "Dark";
            ApplyTheme("Dark");
        }
        _config.Save();
    }

    private void BtnAlwaysOnTop_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        bool isTop = BtnAlwaysOnTop?.IsChecked == true;
        this.Topmost = isTop;
        _config.AlwaysOnTop = isTop;
        if (ChkAlwaysOnTopOption != null) ChkAlwaysOnTopOption.IsChecked = isTop;
        _config.Save();
    }

    // =========================================================================
    // ADAPTIVE CPU VISUAL CORE TOPOLOGY (AMD RYZEN, UNIFORM INTEL & HYBRID INTEL)
    // =========================================================================
    private string _lastConfiguredCpuName = "";
    private int _lastConfiguredThreadCount = 0;

    private void EnsureCpuVisualGridConfigured(TelemetrySnapshot? snap)
    {
        if (GridPCores == null || GridECores == null) return;

        string cpuName = snap?.CpuName ?? "";
        int threadCount = snap?.CpuCores?.Count > 0 ? snap.CpuCores.Count : Environment.ProcessorCount;

        if (_lastConfiguredThreadCount == threadCount && _lastConfiguredCpuName == cpuName && _pCoreCanvases.Count > 0)
            return;

        _lastConfiguredCpuName = cpuName;
        _lastConfiguredThreadCount = threadCount;

        bool hasExplicitECores = snap?.CpuCores?.Any(c => c.CoreType == "E-Core") == true;
        bool isIntelHybrid = hasExplicitECores || (cpuName.Contains("Core", StringComparison.OrdinalIgnoreCase) && 
            (cpuName.Contains("12th", StringComparison.OrdinalIgnoreCase) || 
             cpuName.Contains("13th", StringComparison.OrdinalIgnoreCase) || 
             cpuName.Contains("14th", StringComparison.OrdinalIgnoreCase) || 
             cpuName.Contains("Ultra", StringComparison.OrdinalIgnoreCase)) && threadCount > 8);

        MediaColor cellBg = _isDarkTheme ? DiscordDarkPanel : LightSunken;
        MediaColor cellBorder = _isDarkTheme ? DiscordDarkBorder : LightBorder;
        MediaColor titleColor = TextSecondaryColor;
        MediaColor clockColor = TextPrimaryColor;

        GridPCores.Children.Clear();
        _pCoreCanvases.Clear();
        _pCoreClockLabels.Clear();
        _pCoreValueLabels.Clear();
        _pCoreHistories.Clear();

        GridECores.Children.Clear();
        _eCoreCanvases.Clear();
        _eCoreClockLabels.Clear();
        _eCoreValueLabels.Clear();
        _eCoreHistories.Clear();

        if (isIntelHybrid)
        {
            // Hybrid Intel: Separate P-Core and E-Core sections
            int pThreadCount = snap?.CpuCores?.Count(c => c.CoreType == "P-Core") ?? 0;
            int eCoreCount = snap?.CpuCores?.Count(c => c.CoreType == "E-Core") ?? 0;

            if (pThreadCount <= 0 || eCoreCount <= 0)
            {
                pThreadCount = (threadCount >= 24) ? 16 : (threadCount >= 16 ? 12 : 8);
                eCoreCount = Math.Max(0, threadCount - pThreadCount);
            }

            if (HdrPCores != null)
            {
                HdrPCores.Text = $"PERFORMANCE CORES ({pThreadCount / 2}P / {pThreadCount}T)";
                HdrPCores.Visibility = Visibility.Visible;
            }
            if (HdrECores != null)
            {
                HdrECores.Text = $"EFFICIENCY CORES ({eCoreCount}E)";
                HdrECores.Visibility = eCoreCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            GridPCores.Visibility = Visibility.Visible;
            GridECores.Visibility = eCoreCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            int pCols = 8;
            int pRows = (int)Math.Ceiling((double)pThreadCount / pCols);
            GridPCores.Columns = pCols;
            GridPCores.Rows = pRows;

            for (int i = 0; i < pThreadCount; i++)
            {
                int coreNum = (i / 2) + 1;
                int smtNum = (i % 2) + 1;
                AddCoreCell(GridPCores, _pCoreCanvases, _pCoreClockLabels, _pCoreValueLabels, _pCoreHistories, 
                    $"P{coreNum}.T{smtNum}", cellBg, cellBorder, titleColor, clockColor);
            }

            if (eCoreCount > 0)
            {
                int eCols = 8;
                int eRows = (int)Math.Ceiling((double)eCoreCount / eCols);
                GridECores.Columns = eCols;
                GridECores.Rows = eRows;

                for (int i = 0; i < eCoreCount; i++)
                {
                    AddCoreCell(GridECores, _eCoreCanvases, _eCoreClockLabels, _eCoreValueLabels, _eCoreHistories, 
                        $"E{i + 1}", cellBg, cellBorder, titleColor, clockColor);
                }
            }
        }
        else
        {
            // AMD Ryzen / Uniform Intel CPUs: Single Adaptive Grid
            if (HdrPCores != null)
            {
                HdrPCores.Text = $"CPU CORES & THREADS ({threadCount} Threads)";
                HdrPCores.Visibility = Visibility.Visible;
            }
            if (HdrECores != null) HdrECores.Visibility = Visibility.Collapsed;
            GridECores.Visibility = Visibility.Collapsed;

            int cols = threadCount <= 8 ? threadCount : (threadCount <= 16 ? 8 : 8);
            if (cols <= 0) cols = 4;
            int rows = (int)Math.Ceiling((double)threadCount / cols);
            GridPCores.Columns = cols;
            GridPCores.Rows = rows;

            for (int i = 0; i < threadCount; i++)
            {
                int coreNum = (i / 2) + 1;
                int smtNum = (i % 2) + 1;
                string label = (threadCount > 8) ? $"C{coreNum}.T{smtNum}" : $"T{i + 1}";
                AddCoreCell(GridPCores, _pCoreCanvases, _pCoreClockLabels, _pCoreValueLabels, _pCoreHistories, 
                    label, cellBg, cellBorder, titleColor, clockColor);
            }
        }

        AttachCpuCoreHovers();
    }

    private static void AddCoreCell(System.Windows.Controls.Primitives.UniformGrid parentGrid, List<Canvas> canvases, List<TextBlock> clockLabels, 
        List<TextBlock> valLabels, List<List<float>> histories, string label, 
        MediaColor cellBg, MediaColor cellBorder, MediaColor titleColor, MediaColor clockColor)
    {
        var cell = new Border
        {
            Background = new SolidColorBrush(cellBg),
            BorderBrush = new SolidColorBrush(cellBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(2),
            Padding = new Thickness(5, 3, 5, 3)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var dockTop = new DockPanel();
        var titleLbl = new TextBlock { Text = label, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(titleColor) };
        var clockLbl = new TextBlock { Text = "0.0 GHz", FontSize = 9, FontFamily = new MediaFontFamily("Consolas"), FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(clockColor), HorizontalAlignment = WpfHorizontalAlignment.Right };
        DockPanel.SetDock(titleLbl, Dock.Left);
        DockPanel.SetDock(clockLbl, Dock.Right);
        dockTop.Children.Add(titleLbl);
        dockTop.Children.Add(clockLbl);
        Grid.SetRow(dockTop, 0);
        grid.Children.Add(dockTop);

        var sparkCanvas = new Canvas { Height = 26, Margin = new Thickness(0, 1, 0, 1), Background = MediaBrushes.Transparent, ClipToBounds = true };
        Grid.SetRow(sparkCanvas, 1);
        grid.Children.Add(sparkCanvas);

        var valueLbl = new TextBlock { Text = "0% ±0%", FontSize = 8.5, FontFamily = new MediaFontFamily("Consolas"), Foreground = new SolidColorBrush(titleColor) };
        Grid.SetRow(valueLbl, 2);
        grid.Children.Add(valueLbl);

        cell.Child = grid;
        parentGrid.Children.Add(cell);

        canvases.Add(sparkCanvas);
        clockLabels.Add(clockLbl);
        valLabels.Add(valueLbl);
        histories.Add(new List<float>());
    }

    private void BuildCpuVisualGrid()
    {
        _lastConfiguredThreadCount = 0; // Force re-build
        EnsureCpuVisualGridConfigured(_latestSnapshot);
    }

    // =========================================================================
    // REAL-TIME CONTINUOUS HOVER OVERLAY & TOOLTIP ENGINE (ZERO-ALLOCATION, 60+ FPS)
    // =========================================================================
    private class GraphHoverOverlay
    {
        private static readonly Dictionary<MediaColor, SolidColorBrush> _brushCache = new();
        public static SolidColorBrush GetBrush(MediaColor color)
        {
            if (!_brushCache.TryGetValue(color, out var b))
            {
                b = new SolidColorBrush(color);
                b.Freeze();
                _brushCache[color] = b;
            }
            return b;
        }

        public Line Hairline { get; } = new()
        {
            StrokeThickness = 1,
            Tag = "HoverElement",
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        public Ellipse DotOuter { get; } = new()
        {
            Width = 9,
            Height = 9,
            StrokeThickness = 1.2,
            Tag = "HoverElement",
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        public Ellipse DotInner { get; } = new()
        {
            Width = 5,
            Height = 5,
            StrokeThickness = 1.2,
            Tag = "HoverElement",
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        public WpfToolTip Tooltip { get; }
        public TextBlock TooltipTitle { get; }
        public List<(DockPanel Row, TextBlock Label, TextBlock Value)> TooltipRows { get; } = new();

        public double LastX = double.NaN;
        public int LastIndex = -1;
        public float CachedMax { get; set; } = 1f;
        public int CachedMaxSampleCount { get; set; } = -1;
        public long LastMoveTimestamp { get; set; } = 0;

        public GraphHoverOverlay(bool isDarkTheme)
        {
            var dashes = new DoubleCollection { 2, 2 };
            dashes.Freeze();
            Hairline.StrokeDashArray = dashes;

            var sp = new StackPanel { Margin = new Thickness(4) };
            TooltipTitle = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4)
            };
            sp.Children.Add(TooltipTitle);

            for (int i = 0; i < 4; i++)
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), Visibility = Visibility.Collapsed };
                var lText = new TextBlock { FontSize = 10, Margin = new Thickness(0, 0, 8, 0) };
                var vText = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.Bold, FontFamily = new MediaFontFamily("Consolas") };
                DockPanel.SetDock(lText, Dock.Left);
                DockPanel.SetDock(vText, Dock.Right);
                row.Children.Add(lText);
                row.Children.Add(vText);
                sp.Children.Add(row);
                TooltipRows.Add((row, lText, vText));
            }

            Tooltip = new WpfToolTip
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Content = sp,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
                HorizontalOffset = 12,
                VerticalOffset = 12
            };

            UpdateThemeBrushes(isDarkTheme);
        }

        public void UpdateThemeBrushes(bool isDarkTheme)
        {
            Hairline.Stroke = GetBrush(isDarkTheme ? MediaColor.FromArgb(140, 114, 137, 218) : MediaColor.FromArgb(160, 114, 137, 218));
            DotOuter.Fill = GetBrush(MediaColor.FromArgb(80, 114, 137, 218));
            DotOuter.Stroke = GetBrush(DiscordBlurple);
            DotInner.Fill = GetBrush(isDarkTheme ? MediaColor.FromRgb(255, 255, 255) : DiscordBlurple);
            DotInner.Stroke = GetBrush(isDarkTheme ? DiscordBlurple : MediaColor.FromRgb(255, 255, 255));

            Tooltip.Background = GetBrush(isDarkTheme ? DiscordDarkPanel : LightPanel);
            Tooltip.BorderBrush = GetBrush(isDarkTheme ? DiscordDarkBorder : LightBorder);
            TooltipTitle.Foreground = GetBrush(isDarkTheme ? MediaColor.FromRgb(255, 255, 255) : LightPrimaryText);
        }

        public void EnsureAttached(Canvas canvas)
        {
            if (!canvas.Children.Contains(Hairline)) canvas.Children.Add(Hairline);
            if (!canvas.Children.Contains(DotOuter)) canvas.Children.Add(DotOuter);
            if (!canvas.Children.Contains(DotInner)) canvas.Children.Add(DotInner);
        }

        public void Hide()
        {
            Hairline.Visibility = Visibility.Collapsed;
            DotOuter.Visibility = Visibility.Collapsed;
            DotInner.Visibility = Visibility.Collapsed;
            Tooltip.IsOpen = false;
            LastX = double.NaN;
            LastIndex = -1;
        }

        public void UpdatePosition(double ptX, double ptY, double canvasHeight)
        {
            Hairline.X1 = ptX;
            Hairline.X2 = ptX;
            Hairline.Y1 = 0;
            Hairline.Y2 = canvasHeight;

            Canvas.SetLeft(DotOuter, ptX - 4.5);
            Canvas.SetTop(DotOuter, ptY - 4.5);

            Canvas.SetLeft(DotInner, ptX - 2.5);
            Canvas.SetTop(DotInner, ptY - 2.5);

            if (Hairline.Visibility != Visibility.Visible)
            {
                Hairline.Visibility = Visibility.Visible;
                DotOuter.Visibility = Visibility.Visible;
                DotInner.Visibility = Visibility.Visible;
            }
        }

        public void SetRow(int index, string label, string val, MediaColor color, MediaColor secColor)
        {
            if (index >= 0 && index < TooltipRows.Count)
            {
                var (row, lText, vText) = TooltipRows[index];
                lText.Text = label;
                lText.Foreground = GetBrush(secColor);
                vText.Text = val;
                vText.Foreground = GetBrush(color);
                row.Visibility = Visibility.Visible;
            }
        }

        public void ShowTooltip(FrameworkElement target, string title, int visibleRows)
        {
            TooltipTitle.Text = title;
            for (int i = 0; i < TooltipRows.Count; i++)
            {
                TooltipRows[i].Row.Visibility = i < visibleRows ? Visibility.Visible : Visibility.Collapsed;
            }

            if (target.ToolTip != Tooltip)
            {
                target.ToolTip = Tooltip;
            }
            if (!Tooltip.IsOpen)
            {
                Tooltip.IsOpen = true;
            }
        }
    }

    private GraphHoverOverlay GetOrCreateOverlay(Canvas canvas)
    {
        if (!_graphOverlays.TryGetValue(canvas, out var overlay))
        {
            overlay = new GraphHoverOverlay(_isDarkTheme);
            _graphOverlays[canvas] = overlay;
        }
        overlay.EnsureAttached(canvas);
        return overlay;
    }

    private void SetupMetricGraphHover(Canvas canvas, List<float> history, string title, string metricLabel, string unit, MediaColor color, Func<float, string>? customFormatter = null, float fixedMax = 0f)
    {
        if (canvas == null) return;

        canvas.Background = MediaBrushes.Transparent;
        canvas.ClipToBounds = true;

        canvas.MouseMove += (s, e) =>
        {
            if (history.Count == 0 || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
            var pos = e.GetPosition(canvas);
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;

            var overlay = GetOrCreateOverlay(canvas);

            // 1. Sub-pixel distance filter
            if (!double.IsNaN(overlay.LastX) && Math.Abs(pos.X - overlay.LastX) < 0.75)
                return;

            // 2. High-precision time gate (~144fps / 7ms cap) to guard against 1000Hz+ high polling mouse spam
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (overlay.LastMoveTimestamp > 0 && (now - overlay.LastMoveTimestamp) * 1000 / System.Diagnostics.Stopwatch.Frequency < 7)
                return;
            overlay.LastMoveTimestamp = now;

            overlay.LastX = pos.X;

            double stepX = w / Math.Max(1, MaxHistoryPoints - 1);
            int startIndex = Math.Max(0, MaxHistoryPoints - history.Count);
            double minX = startIndex * stepX;
            double maxX = (MaxHistoryPoints - 1) * stepX;

            double clampedX = Math.Clamp(pos.X, minX, maxX);
            float floatIdx = (float)((clampedX - minX) / Math.Max(0.001, stepX));
            int idx0 = Math.Clamp((int)Math.Floor(floatIdx), 0, history.Count - 1);
            int idx1 = Math.Clamp((int)Math.Ceiling(floatIdx), 0, history.Count - 1);
            float frac = floatIdx - (float)Math.Floor(floatIdx);

            float val = history[idx0] * (1f - frac) + history[idx1] * frac;
            float actualMax = fixedMax;
            if (actualMax <= 0f)
            {
                if (overlay.CachedMaxSampleCount != history.Count)
                {
                    float m = 1f;
                    for (int i = 0; i < history.Count; i++)
                    {
                        if (history[i] > m) m = history[i];
                    }
                    overlay.CachedMax = m;
                    overlay.CachedMaxSampleCount = history.Count;
                }
                actualMax = overlay.CachedMax;
            }

            double ptX = pos.X;
            double ptY = h - (Math.Clamp(val, 0f, actualMax) / actualMax * h);

            int nearestIdx = Math.Clamp((int)Math.Round(floatIdx), 0, history.Count - 1);
            _activeHoverStates[canvas] = (nearestIdx, ptX, ptY, val);

            // 1. Instantly move pre-allocated hairline & snapping dot (0 allocations)
            overlay.UpdatePosition(ptX, ptY, h);

            // 2. Only format & update tooltip text when hovering over a different sample index
            if (nearestIdx != overlay.LastIndex)
            {
                overlay.LastIndex = nearestIdx;
                double secAgo = (history.Count - 1 - nearestIdx) * ((_config?.PollingIntervalMs ?? 1000) / 1000.0);
                DateTime t = _engine?.CurrentSnapshot?.Timestamp.AddSeconds(-secAgo) ?? DateTime.Now;
                string valStr = customFormatter != null ? customFormatter(val) : $"{val:F1} {unit}";

                overlay.SetRow(0, metricLabel, valStr, TextPrimaryColor, TextSecondaryColor);
                overlay.SetRow(1, "Time:", $"{t:HH:mm:ss} ({FormatRelativeTime(secAgo)})", TextSecondaryColor, TextSecondaryColor);
                overlay.ShowTooltip(canvas, title, 2);
            }
            else if (!overlay.Tooltip.IsOpen)
            {
                overlay.ShowTooltip(canvas, title, 2);
            }
        };

        canvas.MouseLeave += (s, e) =>
        {
            _activeHoverStates.Remove(canvas);
            if (_graphOverlays.TryGetValue(canvas, out var overlay))
            {
                overlay.Hide();
            }
        };
    }

    private void SetupDualSeriesHover(Canvas canvas, List<float> s1, List<float> s2, string title, string s1Label, string s2Label, string unit, MediaColor s1Color, MediaColor s2Color, float baseMax = 100f)
    {
        if (canvas == null) return;
        canvas.Background = MediaBrushes.Transparent;
        canvas.ClipToBounds = true;

        canvas.MouseMove += (s, e) =>
        {
            if ((s1.Count == 0 && s2.Count == 0) || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
            var pos = e.GetPosition(canvas);
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;

            var overlay = GetOrCreateOverlay(canvas);

            // 1. Sub-pixel distance filter
            if (!double.IsNaN(overlay.LastX) && Math.Abs(pos.X - overlay.LastX) < 0.75)
                return;

            // 2. High-precision time gate (~144fps / 7ms cap)
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (overlay.LastMoveTimestamp > 0 && (now - overlay.LastMoveTimestamp) * 1000 / System.Diagnostics.Stopwatch.Frequency < 7)
                return;
            overlay.LastMoveTimestamp = now;

            overlay.LastX = pos.X;

            int maxCount = Math.Max(s1.Count, s2.Count);
            double stepX = w / Math.Max(1, MaxHistoryPoints - 1);
            int startIndex = Math.Max(0, MaxHistoryPoints - maxCount);
            double minX = startIndex * stepX;
            double maxX = (MaxHistoryPoints - 1) * stepX;

            double clampedX = Math.Clamp(pos.X, minX, maxX);
            float floatIdx = (float)((clampedX - minX) / Math.Max(0.001, stepX));
            
            float val1 = 0f, val2 = 0f;
            if (s1.Count > 0)
            {
                int s1Start = Math.Max(0, maxCount - s1.Count);
                float s1FloatIdx = floatIdx - s1Start;
                int idx0 = Math.Clamp((int)Math.Floor(s1FloatIdx), 0, s1.Count - 1);
                int idx1 = Math.Clamp((int)Math.Ceiling(s1FloatIdx), 0, s1.Count - 1);
                float frac = s1FloatIdx - (float)Math.Floor(s1FloatIdx);
                val1 = s1[idx0] * (1f - Math.Clamp(frac, 0f, 1f)) + s1[idx1] * Math.Clamp(frac, 0f, 1f);
            }
            if (s2.Count > 0)
            {
                int s2Start = Math.Max(0, maxCount - s2.Count);
                float s2FloatIdx = floatIdx - s2Start;
                int idx0 = Math.Clamp((int)Math.Floor(s2FloatIdx), 0, s2.Count - 1);
                int idx1 = Math.Clamp((int)Math.Ceiling(s2FloatIdx), 0, s2.Count - 1);
                float frac = s2FloatIdx - (float)Math.Floor(s2FloatIdx);
                val2 = s2[idx0] * (1f - Math.Clamp(frac, 0f, 1f)) + s2[idx1] * Math.Clamp(frac, 0f, 1f);
            }

            if (overlay.CachedMaxSampleCount != maxCount)
            {
                float m = baseMax;
                for (int i = 0; i < s1.Count; i++) if (s1[i] > m) m = s1[i];
                for (int i = 0; i < s2.Count; i++) if (s2[i] > m) m = s2[i];
                overlay.CachedMax = m <= 0 ? 1f : m;
                overlay.CachedMaxSampleCount = maxCount;
            }
            float currentMax = overlay.CachedMax;

            float trackedVal = Math.Max(val1, val2);
            double ptX = pos.X;
            double ptY = h - (Math.Clamp(trackedVal, 0f, currentMax) / currentMax * h);

            int nearestIdx = Math.Clamp((int)Math.Round(floatIdx), 0, maxCount - 1);
            _activeHoverStates[canvas] = (nearestIdx, ptX, ptY, trackedVal);

            overlay.UpdatePosition(ptX, ptY, h);

            if (nearestIdx != overlay.LastIndex)
            {
                overlay.LastIndex = nearestIdx;
                double secAgo = (maxCount - 1 - nearestIdx) * ((_config?.PollingIntervalMs ?? 1000) / 1000.0);
                DateTime t = _engine?.CurrentSnapshot?.Timestamp.AddSeconds(-secAgo) ?? DateTime.Now;

                string s1Str = unit == "KB/s" ? NetworkTracker.FormatSpeed(val1) : $"{val1:F1} {unit}";
                string s2Str = unit == "KB/s" ? NetworkTracker.FormatSpeed(val2) : $"{val2:F1} {unit}";

                overlay.SetRow(0, s1Label, s1Str, s1Color, TextSecondaryColor);
                overlay.SetRow(1, s2Label, s2Str, s2Color, TextSecondaryColor);
                overlay.SetRow(2, "Time:", $"{t:HH:mm:ss} ({FormatRelativeTime(secAgo)})", TextSecondaryColor, TextSecondaryColor);
                overlay.ShowTooltip(canvas, title, 3);
            }
            else if (!overlay.Tooltip.IsOpen)
            {
                overlay.ShowTooltip(canvas, title, 3);
            }
        };

        canvas.MouseLeave += (s, e) =>
        {
            _activeHoverStates.Remove(canvas);
            if (_graphOverlays.TryGetValue(canvas, out var overlay))
            {
                overlay.Hide();
            }
        };
    }

    private void SetupBatteryTimelineHover(Canvas canvas)
    {
        if (canvas == null) return;
        canvas.Background = MediaBrushes.Transparent;
        canvas.ClipToBounds = true;

        canvas.MouseMove += (s, e) =>
        {
            var rawHistory = _engine?.BatteryTracker?.History;
            if (rawHistory == null || rawHistory.Count == 0 || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;

            var pos = e.GetPosition(canvas);
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;

            var overlay = GetOrCreateOverlay(canvas);

            if (!double.IsNaN(overlay.LastX) && Math.Abs(pos.X - overlay.LastX) < 0.75)
                return;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (overlay.LastMoveTimestamp > 0 && (now - overlay.LastMoveTimestamp) * 1000 / System.Diagnostics.Stopwatch.Frequency < 7)
                return;
            overlay.LastMoveTimestamp = now;

            overlay.LastX = pos.X;

            DateTime todayMidnight = DateTime.Today;
            double cursorSec = Math.Clamp((pos.X / w) * 86400.0, 0, 86400);
            DateTime targetTime = todayMidnight.AddSeconds(cursorSec);

            var closest = FindClosestBatteryPoint(rawHistory, targetTime);
            if (closest != null)
            {
                double pointSec = (closest.Timestamp - todayMidnight).TotalSeconds;
                double ptX = (pointSec / 86400.0) * w;
                double ptY = h - (Math.Clamp(closest.Percent, 0f, 100f) / 100f * h);

                _activeHoverStates[canvas] = (0, ptX, ptY, closest.Percent);
                overlay.UpdatePosition(ptX, ptY, h);

                int closestIdx = (int)pointSec;
                if (closestIdx != overlay.LastIndex)
                {
                    overlay.LastIndex = closestIdx;
                    overlay.SetRow(0, "Battery Level:", $"{closest.Percent:F0}%", TextPrimaryColor, TextSecondaryColor);
                    overlay.SetRow(1, "Time of Day:", $"{closest.Timestamp:HH:mm:ss}", TextSecondaryColor, TextSecondaryColor);
                    overlay.SetRow(2, "State:", closest.IsAc ? "AC Connected" : "On Battery", closest.IsAc ? MediaColor.FromRgb(34, 197, 94) : MediaColor.FromRgb(249, 115, 22), TextSecondaryColor);
                    overlay.ShowTooltip(canvas, "BATTERY 24H TIMELINE", 3);
                }
                else if (!overlay.Tooltip.IsOpen)
                {
                    overlay.ShowTooltip(canvas, "BATTERY 24H TIMELINE", 3);
                }
            }
        };

        canvas.MouseLeave += (s, e) =>
        {
            _activeHoverStates.Remove(canvas);
            if (_graphOverlays.TryGetValue(canvas, out var overlay))
            {
                overlay.Hide();
            }
        };
    }

    private static BatteryPoint? FindClosestBatteryPoint(IReadOnlyList<BatteryPoint> list, DateTime targetTime)
    {
        if (list.Count == 0) return null;
        int low = 0, high = list.Count - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (list[mid].Timestamp < targetTime)
                low = mid + 1;
            else
                high = mid - 1;
        }
        if (low >= list.Count) return list[list.Count - 1];
        if (high < 0) return list[0];

        var diffLow = Math.Abs((list[low].Timestamp - targetTime).TotalSeconds);
        var diffHigh = Math.Abs((list[high].Timestamp - targetTime).TotalSeconds);
        return diffLow < diffHigh ? list[low] : list[high];
    }

    private void AttachCpuCoreHovers()
    {
        MediaColor blue = DiscordBlurple;
        for (int i = 0; i < _pCoreCanvases.Count; i++)
        {
            int threadIdx = i;
            int coreNum = (threadIdx / 2) + 1;
            int smtNum = (threadIdx % 2) + 1;
            string threadTitle = $"P-CORE {coreNum} (THREAD {smtNum})";
            var canvas = _pCoreCanvases[threadIdx];
            var hist = _pCoreHistories[threadIdx];
            SetupMetricGraphHover(canvas, hist, threadTitle, "Thread Load:", "%", blue, fixedMax: 100f);
        }

        for (int i = 0; i < _eCoreCanvases.Count; i++)
        {
            int eCoreIdx = i;
            string threadTitle = $"E-CORE {eCoreIdx + 1}";
            var canvas = _eCoreCanvases[eCoreIdx];
            var hist = _eCoreHistories[eCoreIdx];
            SetupMetricGraphHover(canvas, hist, threadTitle, "Core Load:", "%", blue, fixedMax: 100f);
        }
    }

    private void SetupGraphHoverInteractivity()
    {
        MediaColor blue = DiscordBlurple;

        // 1. CPU Tab Main Metric Graphs
        if (CanvasCpuLoad != null) SetupMetricGraphHover(CanvasCpuLoad, _cpuLoadHistory, "CPU TOTAL LOAD", "Load:", "%", blue, fixedMax: 100f);
        if (CanvasCpuTemp != null) SetupMetricGraphHover(CanvasCpuTemp, _cpuTempHistory, "CPU TEMPERATURE", "Temperature:", "°C", blue, fixedMax: 100f);
        if (CanvasCpuPower != null) SetupMetricGraphHover(CanvasCpuPower, _cpuPowerHistory, "CPU RAPL PACKAGE POWER", "Power Draw:", "W", blue, fixedMax: 120f);
        if (CanvasCpuFreq != null) SetupMetricGraphHover(CanvasCpuFreq, _cpuFreqHistory, "CPU CLOCK FREQUENCY", "Frequency:", "MHz", blue, val => $"{val:F0} MHz ({(val/1000f):F2} GHz)", fixedMax: 5000f);

        // 2. CPU Individual Core Threads Hover (16 P-Cores + 8 E-Cores)
        AttachCpuCoreHovers();

        // 3. GPU Tab Graphs
        if (CanvasGpuUtil != null) SetupMetricGraphHover(CanvasGpuUtil, _gpuUtilHistory, "GPU CORE UTILIZATION", "Core Load:", "%", blue, fixedMax: 100f);
        if (CanvasGpuTemp != null) SetupMetricGraphHover(CanvasGpuTemp, _gpuTempHistory, "GPU TEMPERATURE", "Temperature:", "°C", blue, fixedMax: 100f);
        if (CanvasGpuPower != null) SetupMetricGraphHover(CanvasGpuPower, _gpuPowerHistory, "GPU BOARD POWER DRAW", "Power:", "W", blue, fixedMax: 100f);
        if (CanvasGpuVram != null) SetupMetricGraphHover(CanvasGpuVram, _gpuVramHistory, "GPU VRAM ALLOCATION", "VRAM in Use:", "GB", blue, val => $"{val:F1} GB", fixedMax: 8f);
        if (CanvasGpu3d != null) SetupMetricGraphHover(CanvasGpu3d, _gpu3dHistory, "3D GRAPHICS ENGINE", "3D Load:", "%", blue, fixedMax: 100f);
        if (CanvasGpuCompute != null) SetupMetricGraphHover(CanvasGpuCompute, _gpuComputeHistory, "COMPUTE / GPGPU", "Compute:", "%", blue, fixedMax: 100f);
        if (CanvasGpuDecoder != null) SetupMetricGraphHover(CanvasGpuDecoder, _gpuDecoderHistory, "VIDEO DECODE (NVDEC)", "Decode:", "%", blue, fixedMax: 100f);
        if (CanvasGpuEncoder != null) SetupMetricGraphHover(CanvasGpuEncoder, _gpuEncoderHistory, "VIDEO ENCODE (NVENC)", "Encode:", "%", blue, fixedMax: 100f);
        if (CanvasGpuCopy != null) SetupMetricGraphHover(CanvasGpuCopy, _gpuCopyHistory, "DMA / MEMORY COPY", "DMA Load:", "%", blue, fixedMax: 100f);
        if (CanvasGpuMemCtrl != null) SetupMetricGraphHover(CanvasGpuMemCtrl, _gpuMemCtrlHistory, "PCIE BUS INTERFACE", "Bus Load:", "%", blue, fixedMax: 100f);

        // 4. Memory & Storage Tab Graphs
        if (CanvasRamGraph != null) SetupMetricGraphHover(CanvasRamGraph, _ramHistory, "RAM ALLOCATION", "RAM in Use:", "%", blue, val => $"{((val/100f) * (_latestSnapshot?.RamTotalGb ?? 32f)):F1} GB ({val:F1}%)", fixedMax: 100f);
        if (CanvasDiskGraph != null) SetupDualSeriesHover(CanvasDiskGraph, _diskReadHistory, _diskWriteHistory, "STORAGE I/O ACTIVITY", "Read:", "Write:", "MB/s", MetricGreen, MetricRed, baseMax: 100f);

        // 5. Power & Battery Tab Graphs
        if (CanvasPowerGraph != null) SetupMetricGraphHover(CanvasPowerGraph, _powerHistory, "PLATFORM TOTAL POWER", "Total Draw:", "W", blue, val => $"{val:F1} W", fixedMax: 150f);
        if (CanvasBatteryTimeline != null) SetupBatteryTimelineHover(CanvasBatteryTimeline);

        // 6. Network & Internet Tab Graphs
        if (CanvasNetworkGraph != null) SetupDualSeriesHover(CanvasNetworkGraph, _netDownHistory, _netUpHistory, "NETWORK THROUGHPUT", "Download:", "Upload:", "KB/s", MetricRed, MetricGreen, baseMax: 500f);
    }

    private static string FormatRelativeTime(double secAgo)
    {
        if (secAgo < 1) return "Now";
        if (secAgo < 60) return $"-{secAgo:F0}s";
        return $"-{(secAgo / 60.0):F1}m";
    }

    public Rect GetElementScreenRect(string name)
    {
        FrameworkElement? el = name.ToLowerInvariant() switch
        {
            "canvascpuload" => CanvasCpuLoad,
            "canvascputemp" => CanvasCpuTemp,
            "canvascpupower" => CanvasCpuPower,
            "canvascpufreq" => CanvasCpuFreq,
            "canvasgpuutil" => CanvasGpuUtil,
            "canvasgputemp" => CanvasGpuTemp,
            "canvasgpupower" => CanvasGpuPower,
            "canvasgpuvram" => CanvasGpuVram,
            "canvasramgraph" => CanvasRamGraph,
            "canvasdiskgraph" => CanvasDiskGraph,
            "canvaspowergraph" => CanvasPowerGraph,
            "canvasbatterytimeline" => CanvasBatteryTimeline,
            "canvasnetworkgraph" => CanvasNetworkGraph,
            _ => null
        };

        if (el == null || !el.IsVisible || el.ActualWidth <= 0 || el.ActualHeight <= 0)
            return Rect.Empty;

        try
        {
            var pt = el.PointToScreen(new Point(0, 0));
            return new Rect(pt.X, pt.Y, el.ActualWidth, el.ActualHeight);
        }
        catch
        {
            return Rect.Empty;
        }
    }

    // =========================================================================
    // TAB SELECTION & LIFECYCLE
    // =========================================================================
    public void SelectTab(int index)
    {
        if (TabAllSensors == null || TabCpu == null || TabGpu == null || 
            TabMemoryDisks == null || TabPower == null || TabNetwork == null || TabTray == null ||
            ViewAllSensors == null || ViewCpu == null || ViewGpu == null || 
            ViewMemoryDisks == null || ViewPower == null || ViewNetwork == null || ViewTray == null)
        {
            return;
        }

        // Dynamically enable detailed per-process and per-interface metrics only when those tabs are open
        if (_engine != null)
        {
            _engine.TrackDetailedProcesses = (index == 6);
            _engine.TrackDetailedNetwork = (index == 5);
        }

        TabAllSensors.IsChecked = index == 0;
        TabCpu.IsChecked = index == 1;
        TabGpu.IsChecked = index == 2;
        TabMemoryDisks.IsChecked = index == 3;
        TabPower.IsChecked = index == 4;
        TabNetwork.IsChecked = index == 5;
        if (TabProcesses != null) TabProcesses.IsChecked = index == 6;
        TabTray.IsChecked = index == 7;

        ViewAllSensors.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewCpu.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ViewGpu.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ViewMemoryDisks.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        ViewPower.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        ViewNetwork.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        if (ViewProcesses != null) ViewProcesses.Visibility = index == 6 ? Visibility.Visible : Visibility.Collapsed;
        ViewTray.Visibility = index == 7 ? Visibility.Visible : Visibility.Collapsed;

        if (_latestSnapshot != null)
        {
            RenderSnapshot(_latestSnapshot);
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender == TabAllSensors) SelectTab(0);
        else if (sender == TabCpu) SelectTab(1);
        else if (sender == TabGpu) SelectTab(2);
        else if (sender == TabMemoryDisks) SelectTab(3);
        else if (sender == TabPower) SelectTab(4);
        else if (sender == TabNetwork) SelectTab(5);
        else if (sender == TabProcesses) SelectTab(6);
        else if (sender == TabTray) SelectTab(7);
    }

    private void BtnResetStats_Click(object sender, RoutedEventArgs e)
    {
        _engine?.ResetStatistics();
    }

    // =========================================================================
    // TELEMETRY RECORDING & RENDERING (ZERO-ALLOCATION SPLIT)
    // =========================================================================
    public void RecordHistorySamples(TelemetrySnapshot snap)
    {
        _latestSnapshot = snap;

        PushHistory(_cpuLoadHistory, snap.CpuTotalUtil);
        PushHistory(_cpuTempHistory, snap.CpuPackageTemp);
        PushHistory(_cpuPowerHistory, snap.CpuPackagePower);
        PushHistory(_cpuFreqHistory, snap.CpuMaxFrequency);

        for (int i = 0; i < _pCoreHistories.Count; i++)
        {
            float tLoad = (i < snap.CpuCores.Count) ? snap.CpuCores[i].Load : snap.CpuTotalUtil;
            PushHistory(_pCoreHistories[i], tLoad);
        }

        for (int i = 0; i < _eCoreHistories.Count; i++)
        {
            int eIdx = _pCoreHistories.Count + i;
            float eLoad = (eIdx < snap.CpuCores.Count) ? snap.CpuCores[eIdx].Load : 0f;
            PushHistory(_eCoreHistories[i], eLoad);
        }

        PushHistory(_gpuUtilHistory, snap.GpuCoreUtil);
        PushHistory(_gpuTempHistory, snap.GpuCoreTemp);
        PushHistory(_gpuPowerHistory, snap.GpuPowerDraw);
        PushHistory(_gpuVramHistory, snap.GpuVramUsedGb);

        PushHistory(_gpu3dHistory, snap.Gpu3dUtil);
        PushHistory(_gpuComputeHistory, snap.GpuComputeUtil);
        PushHistory(_gpuDecoderHistory, snap.GpuVideoDecoderUtil);
        PushHistory(_gpuEncoderHistory, snap.GpuVideoEncoderUtil);
        PushHistory(_gpuCopyHistory, snap.GpuCopyUtil);
        PushHistory(_gpuMemCtrlHistory, snap.GpuCoreUtil * 0.5f);

        PushHistory(_ramHistory, snap.RamUsagePercent);
        float totalReadSpeed = snap.TotalDiskReadSpeedMBps > 0 ? snap.TotalDiskReadSpeedMBps : snap.Disks.Sum(d => d.ReadSpeedMBps);
        float totalWriteSpeed = snap.TotalDiskWriteSpeedMBps > 0 ? snap.TotalDiskWriteSpeedMBps : snap.Disks.Sum(d => d.WriteSpeedMBps);
        PushHistory(_diskReadHistory, totalReadSpeed);
        PushHistory(_diskWriteHistory, totalWriteSpeed);
        PushHistory(_powerHistory, snap.TotalSystemPowerWatts);
        PushHistory(_netDownHistory, snap.TotalNetDownloadSpeedKBps);
        PushHistory(_netUpHistory, snap.TotalNetUploadSpeedKBps);
    }

    public void RenderSnapshot(TelemetrySnapshot snap)
    {
        // If window is minimized, hidden in tray, or actively being dragged/resized by the user, skip visual tree repainting
        if (this.Visibility != Visibility.Visible || this.WindowState == WindowState.Minimized || _isDraggingOrResizing)
        {
            return;
        }

        EnsureCpuVisualGridConfigured(snap);

        if (TxtSystemModelName != null) TxtSystemModelName.Text = snap.SystemModelName;

        // 1. Top Responsive 4 Vitals Bar
        if (HdrCpu != null) HdrCpu.Text = $"{snap.CpuTotalUtil:F0}% ({snap.CpuPackageTemp:F0}°C • {snap.CpuPackagePower:F0}W)";
        if (HdrGpu != null) HdrGpu.Text = $"{snap.GpuCoreUtil:F0}% ({snap.GpuCoreTemp:F0}°C • {snap.GpuPowerDraw:F0}W)";
        if (HdrPower != null) HdrPower.Text = $"{snap.TotalSystemPowerWatts:F1} W ({(snap.IsAcConnected ? "AC" : "DC")})";
        if (HdrRam != null) HdrRam.Text = $"{snap.RamUsedGb:F1} / {snap.RamTotalGb:F0} GB";

        if (TxtSensorsCount != null) TxtSensorsCount.Text = snap.AllSensors.Count.ToString();
        if (TxtLastUpdated != null) TxtLastUpdated.Text = $"Last Polled: {snap.Timestamp:HH:mm:ss.fff}";

        // Tab 1: All Sensors Grid
        if (ViewAllSensors != null && ViewAllSensors.Visibility == Visibility.Visible)
        {
            ApplySensorSortingAndFilter(snap.AllSensors);
        }

        // Tab 2: CPU Topology & Oscilloscopes
        if (ViewCpu != null && ViewCpu.Visibility == Visibility.Visible)
        {
            if (CpuPkgTempText != null) CpuPkgTempText.Text = $"{snap.CpuPackageTemp:F1} °C";
            if (CpuPkgPowerText != null) CpuPkgPowerText.Text = $"{snap.CpuPackagePower:F1} W";

            float curAvgClock = snap.CpuCores.Count > 0 ? snap.CpuCores.Average(c => c.Clock) : snap.CpuMaxFrequency;
            if (CpuMaxFreqText != null) CpuMaxFreqText.Text = $"{curAvgClock:F0} / {snap.CpuMaxFrequency:F0} MHz";
            if (CpuTotalLoadText != null) CpuTotalLoadText.Text = $"{snap.CpuTotalUtil:F1} %";
            if (CpuVidText != null) CpuVidText.Text = $"{snap.CpuVoltage:F3} V";

            if (TxtCpuLoadVital != null) TxtCpuLoadVital.Text = $"{snap.CpuTotalUtil:F1}%";
            if (TxtCpuTempVital != null) TxtCpuTempVital.Text = $"{snap.CpuPackageTemp:F1}°C";
            if (TxtCpuPowerVital != null) TxtCpuPowerVital.Text = $"{snap.CpuPackagePower:F1} W";
            if (TxtCpuFreqVital != null) TxtCpuFreqVital.Text = $"{snap.CpuMaxFrequency:F0} MHz";

            DrawSparkWaveform(CanvasCpuLoad, _cpuLoadHistory, 100f, WaveformStroke, WaveformFill);
            DrawSparkWaveform(CanvasCpuTemp, _cpuTempHistory, 100f, WaveformStroke, WaveformFill);
            DrawSparkWaveform(CanvasCpuPower, _cpuPowerHistory, 120f, WaveformStroke, WaveformFill);
            DrawSparkWaveform(CanvasCpuFreq, _cpuFreqHistory, 5000f, WaveformStroke, WaveformFill);

            for (int i = 0; i < _pCoreCanvases.Count; i++)
            {
                int physIdx = i / 2;
                float clk = physIdx < snap.CpuCores.Count && snap.CpuCores[physIdx].Clock > 0 ? snap.CpuCores[physIdx].Clock : snap.CpuMaxFrequency;
                _pCoreClockLabels[i].Text = $"{(clk / 1000f):F1} GHz";

                float curLoad = _pCoreHistories[i].Count > 0 ? _pCoreHistories[i].Last() : 0f;
                float avgLoad = _pCoreHistories[i].Count > 0 ? _pCoreHistories[i].Average() : 0f;
                UpdateLabelWithAvg(_pCoreValueLabels[i], curLoad, avgLoad, TextPrimaryColor);

                DrawSparkWaveform(_pCoreCanvases[i], _pCoreHistories[i], 100f, WaveformStroke, WaveformFill);
            }

            for (int i = 0; i < _eCoreCanvases.Count; i++)
            {
                int physIdx = (_pCoreCanvases.Count / 2) + i;
                float clk = physIdx < snap.CpuCores.Count && snap.CpuCores[physIdx].Clock > 0 ? snap.CpuCores[physIdx].Clock : (snap.CpuMaxFrequency * 0.75f);
                _eCoreClockLabels[i].Text = $"{(clk / 1000f):F1} GHz";

                float curLoad = _eCoreHistories[i].Count > 0 ? _eCoreHistories[i].Last() : 0f;
                float avgLoad = _eCoreHistories[i].Count > 0 ? _eCoreHistories[i].Average() : 0f;
                UpdateLabelWithAvg(_eCoreValueLabels[i], curLoad, avgLoad, TextPrimaryColor);

                DrawSparkWaveform(_eCoreCanvases[i], _eCoreHistories[i], 100f, WaveformStroke, WaveformFill);
            }
        }

        // Tab 3: GPU Engines & Observability
        if (ViewGpu != null && ViewGpu.Visibility == Visibility.Visible)
        {
            if (GpuTempsText != null) GpuTempsText.Text = $"{snap.GpuCoreTemp:F0} °C";
            if (GpuPowerText != null) GpuPowerText.Text = snap.GpuPowerDraw > 0 ? $"{snap.GpuPowerDraw:F1} W" : "iGPU (Host PKG)";
            if (GpuClocksText != null) GpuClocksText.Text = $"{snap.GpuCoreClock:F0} / {snap.GpuMemoryClock:F0} MHz";
            if (GpuVramSummaryText != null)
            {
                if (snap.GpuVramTotalGb > 0)
                    GpuVramSummaryText.Text = $"{snap.GpuVramUsedGb:F1} / {snap.GpuVramTotalGb:F1} GB";
                else
                    GpuVramSummaryText.Text = $"{snap.GpuSharedVramGb:F1} GB (Shared VRAM)";
            }
            if (GpuVoltFanText != null) GpuVoltFanText.Text = snap.GpuVoltage > 0 ? $"{snap.GpuVoltage:F3} V" : "Auto";

            if (TxtGpuUtilVital != null) TxtGpuUtilVital.Text = $"{snap.GpuCoreUtil:F1}%";
            if (TxtGpuTempVital != null) TxtGpuTempVital.Text = $"{snap.GpuCoreTemp:F1} °C";
            if (TxtGpuPowerVital != null) TxtGpuPowerVital.Text = $"{snap.GpuPowerDraw:F1} W";
            if (TxtGpuVramVital != null) TxtGpuVramVital.Text = $"{snap.GpuVramUsedGb:F1} GB";

            DrawSparkWaveform(CanvasGpuUtil, _gpuUtilHistory, 100f, WaveformStroke, WaveformFill);
            DrawSparkWaveform(CanvasGpuTemp, _gpuTempHistory, 100f, WaveformStroke, WaveformFill);
            DrawSparkWaveform(CanvasGpuPower, _gpuPowerHistory, 100f, WaveformStroke, WaveformFill);
            DrawSparkWaveform(CanvasGpuVram, _gpuVramHistory, snap.GpuVramTotalGb > 0 ? snap.GpuVramTotalGb : 8f, WaveformStroke, WaveformFill);

            if (TxtGpu3dUtil != null) TxtGpu3dUtil.Text = $"{snap.Gpu3dUtil:F1}%";
            if (TxtGpuComputeUtil != null) TxtGpuComputeUtil.Text = $"{snap.GpuComputeUtil:F1}%";
            if (TxtGpuDecoderUtil != null) TxtGpuDecoderUtil.Text = $"{snap.GpuVideoDecoderUtil:F1}%";
            if (TxtGpuEncoderUtil != null) TxtGpuEncoderUtil.Text = $"{snap.GpuVideoEncoderUtil:F1}%";
            if (TxtGpuCopyUtil != null) TxtGpuCopyUtil.Text = $"{snap.GpuCopyUtil:F1}%";
            if (TxtGpuMemCtrlUtil != null) TxtGpuMemCtrlUtil.Text = $"{snap.GpuMemoryControllerUtil:F1}%";

            DrawSparkWaveform(CanvasGpu3d, _gpu3dHistory, 100f, WaveformStroke, WaveformFill);
            DrawSparkWaveform(CanvasGpuCompute, _gpuComputeHistory, 100f, WaveformStroke, WaveformFill);
            DrawSparkWaveform(CanvasGpuDecoder, _gpuDecoderHistory, 100f, WaveformStroke, WaveformFill);
            DrawSparkWaveform(CanvasGpuEncoder, _gpuEncoderHistory, 100f, WaveformStroke, WaveformFill);
            DrawSparkWaveform(CanvasGpuCopy, _gpuCopyHistory, 100f, WaveformStroke, WaveformFill);
            DrawSparkWaveform(CanvasGpuMemCtrl, _gpuMemCtrlHistory, 100f, WaveformStroke, WaveformFill);

            if (TxtGpuPcieRx != null) TxtGpuPcieRx.Text = $"{snap.GpuPcieRxMbps:F1} MB/s";
            if (TxtGpuPcieTx != null) TxtGpuPcieTx.Text = $"{snap.GpuPcieTxMbps:F1} MB/s";
            if (TxtGpuVramDetail != null) TxtGpuVramDetail.Text = $"{snap.GpuVramUsedGb:F1} / {snap.GpuVramTotalGb:F1} GB";
            if (TxtGpuSharedVram != null) TxtGpuSharedVram.Text = $"{snap.GpuSharedVramGb:F1} / 16.0 GB";
        }

        // Tab 4: Memory & Storage
        if (ViewMemoryDisks != null && ViewMemoryDisks.Visibility == Visibility.Visible)
        {
            if (TxtRamPercentHeader != null) TxtRamPercentHeader.Text = $"{snap.RamUsagePercent:F1}% Utilized";
            if (BarRamAlloc != null) BarRamAlloc.Value = Math.Clamp(snap.RamUsagePercent, 0, 100);
            if (TxtRamUsedDetail != null) TxtRamUsedDetail.Text = $"In Use: {snap.RamUsedGb:F1} GB";
            if (TxtRamAvailDetail != null) TxtRamAvailDetail.Text = $"Available: {snap.RamAvailableGb:F1} GB";
            if (TxtRamSpeedDetail != null) TxtRamSpeedDetail.Text = $"Speed: {snap.RamSpeedMt} MT/s";
            if (TxtRamTotalDetail != null) TxtRamTotalDetail.Text = $"Total: {snap.RamTotalGb:F1} GB";

            if (TxtRamGraphVitals != null) TxtRamGraphVitals.Text = $"In Use: {snap.RamUsedGb:F1} GB ({snap.RamUsagePercent:F1}%)";
            DrawSparkWaveform(CanvasRamGraph, _ramHistory, 100f, WaveformStroke, WaveformFill);

            float totalReadSpeed = snap.TotalDiskReadSpeedMBps > 0 ? snap.TotalDiskReadSpeedMBps : snap.Disks.Sum(d => d.ReadSpeedMBps);
            float totalWriteSpeed = snap.TotalDiskWriteSpeedMBps > 0 ? snap.TotalDiskWriteSpeedMBps : snap.Disks.Sum(d => d.WriteSpeedMBps);
            if (TxtDiskReadVital != null) TxtDiskReadVital.Text = $"{totalReadSpeed:F1} MB/s";
            if (TxtDiskWriteVital != null) TxtDiskWriteVital.Text = $"{totalWriteSpeed:F1} MB/s";

            float diskMax = Math.Max(50f, Math.Max(
                _diskReadHistory.Count > 0 ? _diskReadHistory.Max() : 0f,
                _diskWriteHistory.Count > 0 ? _diskWriteHistory.Max() : 0f));
            DrawDualSeriesGraph(CanvasDiskGraph, _diskReadHistory, _diskWriteHistory, diskMax, MetricGreen, MetricRed);

            RenderStoragePartitionCards(snap.Disks);
        }

        // Tab 5: Power & Continuous Battery Timeline
        if (ViewPower != null && ViewPower.Visibility == Visibility.Visible)
        {
            if (TxtPowerPsys != null) TxtPowerPsys.Text = $"{snap.TotalSystemPowerWatts:F1} W";
            if (TxtPowerCpu != null) TxtPowerCpu.Text = $"{snap.CpuPackagePower:F1} W";
            if (TxtPowerGpu != null) TxtPowerGpu.Text = $"{snap.GpuPowerDraw:F1} W";

            if (TxtPowerGraphVitals != null) TxtPowerGraphVitals.Text = $"Current: {snap.TotalSystemPowerWatts:F1} W • Peak: {(_powerHistory.Count > 0 ? _powerHistory.Max() : 0):F1} W";
            DrawSparkWaveform(CanvasPowerGraph, _powerHistory, 150f, WaveformStroke, WaveformFill);

            float batCap = _engine?.BatteryTracker?.FullCapacityWh ?? 60.0f;
            float estCycles = batCap > 0 ? (snap.BatteryCumulativeChargedWh / batCap) : 0f;
            if (TxtCycleStats != null) TxtCycleStats.Text = $"Cycles: {snap.BatteryCycleCount} (ACPI) • Energy Added: {snap.BatteryCumulativeChargedWh:F1} Wh (~{estCycles:F1} cycles)";
            DrawContinuousBatteryTimeline(CanvasBatteryTimeline, _engine?.BatteryTracker?.History ?? new List<BatteryPoint>(), snap.BatteryPercent);

            if (TxtBatteryCharge != null) TxtBatteryCharge.Text = $"Level: {snap.BatteryPercent:F0}% • {(snap.IsAcConnected ? "AC Connected (Charging)" : "On Battery (Discharging)")}";
            if (TxtBatteryRate != null)
            {
                TxtBatteryRate.Text = snap.IsAcConnected 
                    ? $"Charge Rate: {snap.BatteryChargeRateWatts:F1} W" 
                    : $"Discharge: {snap.BatteryDischargeRateWatts:F1} W";
            }
        }

        // Tab 6: Internet & Network Interfaces
        if (ViewNetwork != null && ViewNetwork.Visibility == Visibility.Visible)
        {
            if (TxtNetDownSpeed != null) TxtNetDownSpeed.Text = snap.FormattedTotalNetDown;
            if (TxtNetDownStats != null) TxtNetDownStats.Text = $"Peak: {NetworkTracker.FormatSpeed(_netDownHistory.Count > 0 ? _netDownHistory.Max() : 0f)} • In: {snap.FormattedTotalNetBytesRecv}";
            if (TxtNetUpSpeed != null) TxtNetUpSpeed.Text = snap.FormattedTotalNetUp;
            if (TxtNetUpStats != null) TxtNetUpStats.Text = $"Peak: {NetworkTracker.FormatSpeed(_netUpHistory.Count > 0 ? _netUpHistory.Max() : 0f)} • Out: {snap.FormattedTotalNetBytesSent}";

            if (TxtActiveNetName != null) TxtActiveNetName.Text = snap.ActiveNetworkName;
            if (TxtActiveNetIp != null) TxtActiveNetIp.Text = string.IsNullOrEmpty(snap.ActiveNetworkIp) ? "IPv4: --" : $"IPv4: {snap.ActiveNetworkIp}";
            if (TxtNetDataIn != null) TxtNetDataIn.Text = $"↓ In: {snap.FormattedTotalNetBytesRecv}";
            if (TxtNetDataOut != null) TxtNetDataOut.Text = $"↑ Out: {snap.FormattedTotalNetBytesSent}";

            if (TxtNetGraphDownVital != null) TxtNetGraphDownVital.Text = snap.FormattedTotalNetDown;
            if (TxtNetGraphUpVital != null) TxtNetGraphUpVital.Text = snap.FormattedTotalNetUp;

            float netMax = Math.Max(200f, Math.Max(
                _netDownHistory.Count > 0 ? _netDownHistory.Max() : 0f,
                _netUpHistory.Count > 0 ? _netUpHistory.Max() : 0f));
            DrawDualSeriesGraph(CanvasNetworkGraph, _netDownHistory, _netUpHistory, netMax, MetricRed, MetricGreen);

            if (TxtActiveNicCount != null) TxtActiveNicCount.Text = $"{snap.NetworkInterfaces.Count(n => n.IsUp)} Active / {snap.NetworkInterfaces.Count} Total";
            var priNic = snap.NetworkInterfaces.FirstOrDefault(n => n.IsUp && !string.IsNullOrEmpty(n.Gateway));
            if (TxtDefaultGateway != null) TxtDefaultGateway.Text = priNic != null && !string.IsNullOrEmpty(priNic.Gateway) ? priNic.Gateway : "--";
            if (TxtDnsServers != null) TxtDnsServers.Text = priNic != null && !string.IsNullOrEmpty(priNic.Dns) ? priNic.Dns : "--";
            if (TxtTotalLinkSpeed != null)
            {
                float totalLink = snap.NetworkInterfaces.Where(n => n.IsUp).Sum(n => n.SpeedMbps);
                TxtTotalLinkSpeed.Text = totalLink >= 1000 ? $"{(totalLink / 1000f):F1} Gbps" : $"{totalLink:F0} Mbps";
            }
            if (TxtInterfacesTotalCount != null) TxtInterfacesTotalCount.Text = $"{snap.NetworkInterfaces.Count} Adapters";

            RenderNetworkInterfaceCards(snap.NetworkInterfaces);
        }

        // Tab 7: Processes & Top Apps
        if (ViewProcesses != null && ViewProcesses.Visibility == Visibility.Visible && snap.Processes != null)
        {
            RenderProcessTopCards(snap.Processes);
            ApplyProcessSortingAndFilter(snap.Processes.AllProcesses);
        }
    }

    private static void UpdateLabelWithAvg(TextBlock lbl, float cur, float avg, MediaColor curColor)
    {
        if (lbl == null) return;
        lbl.Inlines.Clear();
        var curRun = new System.Windows.Documents.Run($"{cur:F0}%")
        {
            Foreground = new SolidColorBrush(curColor),
            FontWeight = FontWeights.Bold
        };
        var avgRun = new System.Windows.Documents.Run($" a:{avg:F0}%")
        {
            Foreground = new SolidColorBrush(MediaColor.FromArgb(140, 148, 163, 184)),
            FontSize = 8
        };
        lbl.Inlines.Add(curRun);
        lbl.Inlines.Add(avgRun);
    }

    private static void PushHistory(List<float> buffer, float val, int maxPoints = MaxHistoryPoints)
    {
        buffer.Add(val);
        while (buffer.Count > maxPoints) buffer.RemoveAt(0);
    }

    // =========================================================================
    // OSCILLOSCOPE & STUDIO METRIC GRID ENGINE
    // =========================================================================
    private void DrawGraphGrid(Canvas canvas, double w, double h, int horizDivs = 4, int vertDivs = 4, bool showBorder = true)
    {
        if (canvas == null || w <= 0 || h <= 0) return;

        MediaColor gridColor = _isDarkTheme ? MediaColor.FromArgb(48, 148, 163, 184) : MediaColor.FromArgb(48, 71, 85, 105);
        MediaColor centerColor = _isDarkTheme ? MediaColor.FromArgb(80, 148, 163, 184) : MediaColor.FromArgb(75, 71, 85, 105);
        MediaColor borderColor = _isDarkTheme ? MediaColor.FromArgb(50, 255, 255, 255) : MediaColor.FromArgb(40, 0, 0, 0);

        if (showBorder)
        {
            canvas.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = w,
                Height = h,
                Stroke = new SolidColorBrush(borderColor),
                StrokeThickness = 1.0,
                Fill = MediaBrushes.Transparent,
                IsHitTestVisible = false
            });
        }

        // Horizontal Grid Lines (25%, 50%, 75%, etc.)
        for (int i = 1; i < horizDivs; i++)
        {
            double gy = Math.Round((h * i) / horizDivs);
            bool isCenter = (horizDivs % 2 == 0) && (i == horizDivs / 2);
            canvas.Children.Add(new Line
            {
                X1 = 0, Y1 = gy, X2 = w, Y2 = gy,
                Stroke = new SolidColorBrush(isCenter ? centerColor : gridColor),
                StrokeThickness = isCenter ? 1.0 : 0.8,
                StrokeDashArray = isCenter ? new DoubleCollection { 4, 3 } : new DoubleCollection { 2, 4 },
                IsHitTestVisible = false
            });
        }

        // Vertical Time Grid Lines (25%, 50%, 75% of time span)
        for (int j = 1; j < vertDivs; j++)
        {
            double gx = Math.Round((w * j) / vertDivs);
            bool isCenter = (vertDivs % 2 == 0) && (j == vertDivs / 2);
            canvas.Children.Add(new Line
            {
                X1 = gx, Y1 = 0, X2 = gx, Y2 = h,
                Stroke = new SolidColorBrush(isCenter ? centerColor : gridColor),
                StrokeThickness = isCenter ? 1.0 : 0.8,
                StrokeDashArray = isCenter ? new DoubleCollection { 4, 3 } : new DoubleCollection { 2, 4 },
                IsHitTestVisible = false
            });
        }
    }

    // =========================================================================
    // CONTINUOUS BATTERY 24-HOUR TIMELINE RENDERER
    // =========================================================================
    private void DrawContinuousBatteryTimeline(Canvas canvas, IReadOnlyList<BatteryPoint> rawHistory, float currentPercent)
    {
        if (canvas == null) return;
        canvas.Children.Clear();
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        DrawGraphGrid(canvas, w, h, horizDivs: 4, vertDivs: 6, showBorder: true);

        // 24-hour time ticks (00:00, 04:00, 08:00, 12:00, 16:00, 20:00, 24:00)
        for (int hr = 0; hr <= 24; hr += 4)
        {
            double tx = (hr / 24.0) * w;
            var lbl = new TextBlock
            {
                Text = $"{hr:D2}:00",
                Foreground = new SolidColorBrush(_isDarkTheme ? MediaColor.FromRgb(100, 116, 139) : MediaColor.FromRgb(148, 163, 184)),
                FontSize = 8,
                FontFamily = new MediaFontFamily("Consolas"),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(lbl, Math.Clamp(tx - 12, 2, w - 28));
            Canvas.SetTop(lbl, h - 12);
            canvas.Children.Add(lbl);
        }

        if (rawHistory == null || rawHistory.Count == 0) return;

        DateTime todayMidnight = DateTime.Today;
        var pts = new PointCollection();

        for (int i = 0; i < rawHistory.Count; i++)
        {
            var p = rawHistory[i];
            double sec = (p.Timestamp - todayMidnight).TotalSeconds;
            if (sec >= 0 && sec <= 86400)
            {
                double px = (sec / 86400.0) * w;
                double py = h - (Math.Clamp(p.Percent, 0f, 100f) / 100f * h);
                pts.Add(new Point(px, py));
            }
        }

        if (pts.Count > 1)
        {
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(pts[0], false, false);
                ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
            }

            canvas.Children.Add(new WpfPath
            {
                Data = geom,
                Stroke = new SolidColorBrush(DiscordBlurple),
                StrokeThickness = 1.8,
                IsHitTestVisible = false
            });
        }

        if (_graphOverlays.TryGetValue(canvas, out var overlay))
        {
            overlay.EnsureAttached(canvas);
            if (_activeHoverStates.TryGetValue(canvas, out var hover))
            {
                overlay.UpdatePosition(hover.PtX, hover.PtY, h);
            }
        }
    }

    // =========================================================================
    // DUAL-SERIES GRAPH (STORAGE READ/WRITE & NETWORK DOWN/UP)
    // =========================================================================
    private void DrawDualSeriesGraph(Canvas canvas, List<float> s1, List<float> s2, float baseMax, MediaColor col1, MediaColor col2)
    {
        if (canvas == null) return;
        canvas.Children.Clear();
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        DrawGraphGrid(canvas, w, h, horizDivs: 4, vertDivs: 4, showBorder: true);

        float maxVal = baseMax;
        if (s1.Count > 0) maxVal = Math.Max(maxVal, s1.Max());
        if (s2.Count > 0) maxVal = Math.Max(maxVal, s2.Max());
        if (maxVal <= 0) maxVal = 1f;

        void DrawSeries(List<float> series, MediaColor col)
        {
            if (series.Count < 2) return;
            double stepX = w / Math.Max(1, MaxHistoryPoints - 1);
            int startIdx = Math.Max(0, MaxHistoryPoints - series.Count);

            var pts = new PointCollection();
            for (int i = 0; i < series.Count; i++)
            {
                double px = (startIdx + i) * stepX;
                double py = h - (Math.Clamp(series[i], 0f, maxVal) / maxVal * h);
                pts.Add(new Point(px, py));
            }

            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(pts[0], false, false);
                ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
            }

            canvas.Children.Add(new WpfPath
            {
                Data = geom,
                Stroke = new SolidColorBrush(col),
                StrokeThickness = 1.4,
                IsHitTestVisible = false
            });
        }

        DrawSeries(s1, col1);
        DrawSeries(s2, col2);

        if (_graphOverlays.TryGetValue(canvas, out var overlay))
        {
            overlay.CachedMax = maxVal;
            overlay.CachedMaxSampleCount = Math.Max(s1.Count, s2.Count);
            overlay.EnsureAttached(canvas);
            if (_activeHoverStates.TryGetValue(canvas, out var hover))
            {
                overlay.UpdatePosition(hover.PtX, hover.PtY, h);
            }
        }
    }

    // =========================================================================
    // GENERAL SPARK WAVEFORM RENDERER
    // =========================================================================
    private void DrawSparkWaveform(Canvas canvas, List<float> data, float baseMax, MediaColor strokeColor, MediaColor fillColor)
    {
        if (canvas == null) return;
        canvas.Children.Clear();
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        int hDivs = h <= 45 ? 2 : 4;
        int vDivs = h <= 45 ? 3 : 4;
        DrawGraphGrid(canvas, w, h, horizDivs: hDivs, vertDivs: vDivs, showBorder: true);

        if (data.Count < 2) return;

        float maxVal = baseMax;
        if (data.Count > 0 && data.Max() > baseMax) maxVal = data.Max();
        if (maxVal <= 0) maxVal = 1f;

        double stepX = w / Math.Max(1, MaxHistoryPoints - 1);
        int startIdx = Math.Max(0, MaxHistoryPoints - data.Count);

        var pts = new PointCollection();
        for (int i = 0; i < data.Count; i++)
        {
            double px = (startIdx + i) * stepX;
            double py = h - (Math.Clamp(data[i], 0f, maxVal) / maxVal * h);
            pts.Add(new Point(px, py));
        }

        // Fill geometry
        var fillGeom = new StreamGeometry();
        using (var ctx = fillGeom.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, h), true, true);
            ctx.LineTo(pts[0], true, false);
            ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
            ctx.LineTo(new Point(pts.Last().X, h), true, false);
        }

        canvas.Children.Add(new WpfPath
        {
            Data = fillGeom,
            Fill = new SolidColorBrush(fillColor),
            IsHitTestVisible = false
        });

        // Stroke line geometry
        var strokeGeom = new StreamGeometry();
        using (var ctx = strokeGeom.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }

        canvas.Children.Add(new WpfPath
        {
            Data = strokeGeom,
            Stroke = new SolidColorBrush(strokeColor),
            StrokeThickness = 1.4,
            IsHitTestVisible = false
        });

        if (_graphOverlays.TryGetValue(canvas, out var overlay))
        {
            overlay.CachedMax = maxVal;
            overlay.CachedMaxSampleCount = data.Count;
            overlay.EnsureAttached(canvas);
            if (_activeHoverStates.TryGetValue(canvas, out var hover))
            {
                overlay.UpdatePosition(hover.PtX, hover.PtY, h);
            }
        }
    }

    // =========================================================================
    // STORAGE PARTITION CARDS (3-COLUMN GRID WITH CHARACTER ELLIPSIS)
    // =========================================================================
    private void RenderStoragePartitionCards(IReadOnlyList<DiskTelemetry> disks)
    {
        if (PanelDisksContainer == null) return;
        PanelDisksContainer.Children.Clear();

        if (disks.Count == 0)
        {
            var noDiskLbl = new TextBlock
            {
                Text = "No active fixed partitions detected.",
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted"),
                FontSize = 11,
                Margin = new Thickness(4)
            };
            PanelDisksContainer.Children.Add(noDiskLbl);
            return;
        }

        foreach (var disk in disks)
        {
            var card = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("BrushCardBg"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BrushCardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10, 8, 10, 8),
                Effect = (System.Windows.Media.Effects.Effect)FindResource("SkeuoButtonShadow")
            };

            var stack = new StackPanel();

            // 1. Top Header Line (3-Column Grid: Badges | Drive Model with Ellipsis | Temp & Live Speeds Right)
            var gridTop = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            gridTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gridTop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gridTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Col 0: Drive letter & SMART health badge
            var leftBadges = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            
            var badge = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("BrushAccentCobalt"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 0)
            };
            badge.Child = new TextBlock
            {
                Text = disk.DriveLetter.TrimEnd('\\'),
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = new MediaFontFamily("Consolas")
            };
            leftBadges.Children.Add(badge);

            if (disk.HasSmartHealth && disk.HealthPercent > 0)
            {
                var healthBadge = new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("BrushCardSunken"),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("BrushCardBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 2, 6, 2)
                };
                healthBadge.Child = new TextBlock
                {
                    Text = $"{disk.HealthPercent:F0}% ({disk.HealthStatus})",
                    FontWeight = FontWeights.Bold,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(MetricGreen)
                };
                leftBadges.Children.Add(healthBadge);
            }
            Grid.SetColumn(leftBadges, 0);
            gridTop.Children.Add(leftBadges);

            // Col 1: Drive Name & File System (with CharacterEllipsis to prevent overlapping)
            var modelTxt = new TextBlock
            {
                Text = $"{disk.Name} • {disk.FileSystem}",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(modelTxt, 1);
            gridTop.Children.Add(modelTxt);

            // Col 2: Temp & Live Speeds (Guaranteed Right-Aligned: Read in Green, Write in Red)
            var rightInfo = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = WpfHorizontalAlignment.Right };
            if (disk.Temperature > 0)
            {
                rightInfo.Children.Add(new TextBlock
                {
                    Text = $"Temp: {disk.Temperature:F0} °C  ",
                    FontSize = 10.5,
                    Foreground = (System.Windows.Media.Brush)FindResource("BrushTextSecondary"),
                    FontWeight = FontWeights.SemiBold
                });
            }

            rightInfo.Children.Add(new TextBlock
            {
                Text = $"R: {disk.ReadSpeedMBps:F1} MB/s",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = new SolidColorBrush(MetricGreen),
                FontFamily = new MediaFontFamily("Consolas")
            });
            rightInfo.Children.Add(new TextBlock
            {
                Text = " • ",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextSecondary"),
                FontFamily = new MediaFontFamily("Consolas")
            });
            rightInfo.Children.Add(new TextBlock
            {
                Text = $"W: {disk.WriteSpeedMBps:F1} MB/s",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = new SolidColorBrush(MetricRed),
                FontFamily = new MediaFontFamily("Consolas")
            });
            Grid.SetColumn(rightInfo, 2);
            gridTop.Children.Add(rightInfo);

            stack.Children.Add(gridTop);

            // 2. Storage Utilization Bar
            var bar = new WpfProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp(disk.UsedPercent, 0, 100),
                Foreground = new SolidColorBrush(MetricGreen),
                Margin = new Thickness(0, 2, 0, 4)
            };
            stack.Children.Add(bar);

            // 3. Bottom Labels: Free vs Total
            var dockBot = new DockPanel();
            var freeLbl = new TextBlock
            {
                Text = $"Free: {disk.FreeGb:F1} GB",
                FontSize = 10.5,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextSecondary"),
                FontWeight = FontWeights.SemiBold
            };
            DockPanel.SetDock(freeLbl, Dock.Left);
            dockBot.Children.Add(freeLbl);

            var usedLbl = new TextBlock
            {
                Text = $"{disk.UsedPercent:F1}% Used (Total: {disk.TotalGb:F1} GB)",
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextPrimary"),
                HorizontalAlignment = WpfHorizontalAlignment.Right
            };
            DockPanel.SetDock(usedLbl, Dock.Right);
            dockBot.Children.Add(usedLbl);

            stack.Children.Add(dockBot);
            card.Child = stack;
            PanelDisksContainer.Children.Add(card);
        }
    }

    private void RenderNetworkInterfaceCards(List<NetworkInterfaceTelemetry> nics)
    {
        if (PanelNetworkAdapters == null) return;
        PanelNetworkAdapters.Children.Clear();

        if (nics == null || nics.Count == 0)
        {
            PanelNetworkAdapters.Children.Add(new TextBlock
            {
                Text = "No active network adapters detected.",
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted"),
                FontSize = 11,
                Margin = new Thickness(6)
            });
            return;
        }

        foreach (var nic in nics)
        {
            var card = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("BrushCardSunken"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BrushCardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Effect = (DropShadowEffect)FindResource("SkeuoButtonShadow")
            };

            var stack = new StackPanel();

            // 1. Top Row: Status badge + Interface Type badge + Name + Real-time throughput
            var gridTop = new Grid();
            gridTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gridTop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gridTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftBadges = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // Status Badge
            var statusBadge = new Border
            {
                Background = nic.IsUp ? new SolidColorBrush(MediaColor.FromRgb(34, 197, 94)) : new SolidColorBrush(MediaColor.FromRgb(66, 69, 73)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 6, 0)
            };
            statusBadge.Child = new TextBlock
            {
                Text = nic.IsUp ? "CONNECTED" : "DOWN",
                FontWeight = FontWeights.Bold,
                FontSize = 9,
                Foreground = MediaBrushes.White
            };
            leftBadges.Children.Add(statusBadge);

            // Type Badge
            var typeBadge = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("BrushCardBg"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BrushCardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 6, 0)
            };
            typeBadge.Child = new TextBlock
            {
                Text = nic.InterfaceType,
                FontWeight = FontWeights.SemiBold,
                FontSize = 9.5,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextPrimary")
            };
            leftBadges.Children.Add(typeBadge);

            // Interface Name (Column 1 with Star width and ellipsis)
            var nameTxt = new TextBlock
            {
                Text = nic.Name,
                FontWeight = FontWeights.Bold,
                FontSize = 11.5,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 8, 0)
            };

            Grid.SetColumn(leftBadges, 0);
            gridTop.Children.Add(leftBadges);

            Grid.SetColumn(nameTxt, 1);
            gridTop.Children.Add(nameTxt);

            // Live Throughput (Right aligned: Down in Red, Up in Green)
            var liveSpeedPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = WpfHorizontalAlignment.Right };
            liveSpeedPanel.Children.Add(new TextBlock
            {
                Text = $"↓ {nic.DownloadSpeedFormatted}",
                FontWeight = FontWeights.Bold,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(MetricRed),
                FontFamily = new MediaFontFamily("Consolas"),
                Margin = new Thickness(0, 0, 8, 0)
            });
            liveSpeedPanel.Children.Add(new TextBlock
            {
                Text = $"↑ {nic.UploadSpeedFormatted}",
                FontWeight = FontWeights.Bold,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(MetricGreen),
                FontFamily = new MediaFontFamily("Consolas")
            });
            Grid.SetColumn(liveSpeedPanel, 2);
            gridTop.Children.Add(liveSpeedPanel);

            stack.Children.Add(gridTop);

            // 2. Hardware Description & Link Speed
            var descTxt = new TextBlock
            {
                Text = $"{nic.Description}{(string.IsNullOrEmpty(nic.SpeedFormatted) ? "" : $" • {nic.SpeedFormatted}")}",
                FontSize = 9.5,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextSecondary"),
                Margin = new Thickness(0, 3, 0, 2),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            stack.Children.Add(descTxt);

            // 3. Addressing Details (IPv4, MAC, Gateway)
            var addrDock = new DockPanel { Margin = new Thickness(0, 1, 0, 3) };
            string ipInfo = !string.IsNullOrEmpty(nic.Ipv4Address) ? $"IPv4: {nic.Ipv4Address}" : (nic.IsUp ? "IPv4: DHCP / Assigned" : "No IP Address");
            if (!string.IsNullOrEmpty(nic.Gateway)) ipInfo += $" • GW: {nic.Gateway}";

            var ipLbl = new TextBlock
            {
                Text = ipInfo,
                FontSize = 9.5,
                FontFamily = new MediaFontFamily("Consolas"),
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextPrimary")
            };
            DockPanel.SetDock(ipLbl, Dock.Left);
            addrDock.Children.Add(ipLbl);

            if (!string.IsNullOrEmpty(nic.MacAddress))
            {
                var macLbl = new TextBlock
                {
                    Text = $"MAC: {nic.MacAddress}",
                    FontSize = 9,
                    FontFamily = new MediaFontFamily("Consolas"),
                    Foreground = (System.Windows.Media.Brush)FindResource("BrushTextSecondary"),
                    HorizontalAlignment = WpfHorizontalAlignment.Right
                };
                DockPanel.SetDock(macLbl, Dock.Right);
                addrDock.Children.Add(macLbl);
            }
            stack.Children.Add(addrDock);

            // 4. Mini Real-Time Activity Sparkline
            if (nic.DownloadHistory.Count > 1)
            {
                var spark = new Canvas
                {
                    Height = 20,
                    Margin = new Thickness(0, 2, 0, 3),
                    Background = MediaBrushes.Transparent,
                    ClipToBounds = true
                };
                float sparkMax = Math.Max(50f, nic.DownloadHistory.Max());
                DrawSparkWaveform(spark, nic.DownloadHistory, sparkMax, WaveformStroke, WaveformFill);
                stack.Children.Add(spark);
            }

            // 5. Bottom Lifetime / Session Stats
            var botDock = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var dataStats = new TextBlock
            {
                Text = $"Total In: {nic.FormattedTotalReceived} • Total Out: {nic.FormattedTotalSent}",
                FontSize = 9,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextSecondary"),
                FontFamily = new MediaFontFamily("Consolas")
            };
            DockPanel.SetDock(dataStats, Dock.Left);
            botDock.Children.Add(dataStats);
            stack.Children.Add(botDock);

            card.Child = stack;
            PanelNetworkAdapters.Children.Add(card);
        }
    }

    private const string SensorFilterPlaceholder = "Filter sensors (e.g. CPU, Power, GPU)...";

    private void TxtSensorFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtSensorFilter == null) return;
        _sensorFilter = TxtSensorFilter.Text.Trim();
        if (_sensorFilter.StartsWith("Filter sensors", StringComparison.OrdinalIgnoreCase)) _sensorFilter = "";
        if (_latestSnapshot != null) ApplySensorSortingAndFilter(_latestSnapshot.AllSensors);
    }

    private void GridAllSensors_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var column = e.Column;
        var sortMember = column.SortMemberPath;

        if (string.IsNullOrEmpty(sortMember)) return;

        if (_activeSortColumn != sortMember)
        {
            _activeSortColumn = sortMember;
            _activeSortDirection = ListSortDirection.Ascending;
        }
        else
        {
            if (_activeSortDirection == ListSortDirection.Ascending)
                _activeSortDirection = ListSortDirection.Descending;
            else if (_activeSortDirection == ListSortDirection.Descending)
            {
                _activeSortDirection = null;
                _activeSortColumn = "";
            }
            else
                _activeSortDirection = ListSortDirection.Ascending;
        }

        if (GridAllSensors != null)
        {
            foreach (var col in GridAllSensors.Columns) col.SortDirection = null;
            if (!string.IsNullOrEmpty(_activeSortColumn)) column.SortDirection = _activeSortDirection;
        }

        if (_latestSnapshot != null) ApplySensorSortingAndFilter(_latestSnapshot.AllSensors);
    }

    private void ApplySensorSortingAndFilter(IEnumerable<SensorRecord>? source = null)
    {
        if (GridAllSensors == null) return;
        // Prevent background telemetry tick from interrupting active mouse drag/resize
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;

        var sensors = source ?? _latestSnapshot?.AllSensors;
        if (sensors == null) return;

        IEnumerable<SensorRecord> filtered = sensors;
        if (!string.IsNullOrEmpty(_sensorFilter) && !_sensorFilter.StartsWith("Filter sensors", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(s =>
                s.Name.Contains(_sensorFilter, StringComparison.OrdinalIgnoreCase) ||
                s.Category.Contains(_sensorFilter, StringComparison.OrdinalIgnoreCase) ||
                s.FormattedCurrent.Contains(_sensorFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(_activeSortColumn) && _activeSortDirection.HasValue)
        {
            bool asc = _activeSortDirection == ListSortDirection.Ascending;
            filtered = _activeSortColumn switch
            {
                "Category" => asc ? filtered.OrderBy(s => s.Category) : filtered.OrderByDescending(s => s.Category),
                "Name" => asc ? filtered.OrderBy(s => s.Name) : filtered.OrderByDescending(s => s.Name),
                "Value" => asc ? filtered.OrderBy(s => s.Value) : filtered.OrderByDescending(s => s.Value),
                "FormattedCurrent" => asc ? filtered.OrderBy(s => s.Value) : filtered.OrderByDescending(s => s.Value),
                "Min" => asc ? filtered.OrderBy(s => s.Min) : filtered.OrderByDescending(s => s.Min),
                "FormattedMin" => asc ? filtered.OrderBy(s => s.Min) : filtered.OrderByDescending(s => s.Min),
                "Max" => asc ? filtered.OrderBy(s => s.Max) : filtered.OrderByDescending(s => s.Max),
                "FormattedMax" => asc ? filtered.OrderBy(s => s.Max) : filtered.OrderByDescending(s => s.Max),
                "Avg" => asc ? filtered.OrderBy(s => s.Avg) : filtered.OrderByDescending(s => s.Avg),
                "FormattedAvg" => asc ? filtered.OrderBy(s => s.Avg) : filtered.OrderByDescending(s => s.Avg),
                "Unit" => asc ? filtered.OrderBy(s => s.Unit) : filtered.OrderByDescending(s => s.Unit),
                _ => filtered
            };
        }

        GridAllSensors.ItemsSource = filtered.ToList();
    }

    private void BtnResetSort_Click(object sender, RoutedEventArgs e)
    {
        _activeSortColumn = "";
        _activeSortDirection = null;
        if (GridAllSensors != null)
        {
            foreach (var col in GridAllSensors.Columns) col.SortDirection = null;
        }
        if (_latestSnapshot != null) ApplySensorSortingAndFilter(_latestSnapshot.AllSensors);
    }

    private void TxtSensorFilter_GotFocus(object sender, RoutedEventArgs e)
    {
        if (TxtSensorFilter != null && TxtSensorFilter.Text.StartsWith("Filter sensors", StringComparison.OrdinalIgnoreCase)) TxtSensorFilter.Text = "";
    }

    private void TxtSensorFilter_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TxtSensorFilter != null && string.IsNullOrWhiteSpace(TxtSensorFilter.Text)) TxtSensorFilter.Text = SensorFilterPlaceholder;
    }

    // =========================================================================
    // TRAY PREFERENCES & CONTROLS
    // =========================================================================
    private void SyncTrayControls()
    {
        SyncTrayCheckboxes();
        if (_config != null)
        {
            if (ChkStartWithWindows != null) ChkStartWithWindows.IsChecked = StartupHelper.IsStartupEnabled() || _config.StartWithWindows;
            if (ChkStartMinimized != null) ChkStartMinimized.IsChecked = _config.StartMinimized;
            if (ChkCloseToTray != null) ChkCloseToTray.IsChecked = _config.CloseToTray;
            if (ChkMinimizeToTray != null) ChkMinimizeToTray.IsChecked = _config.MinimizeToTray;
            if (ChkAlwaysOnTopOption != null) ChkAlwaysOnTopOption.IsChecked = _config.AlwaysOnTop;
            if (ChkEnableDebugLog != null) ChkEnableDebugLog.IsChecked = _config.EnableDebugLog;
        }
    }

    private void SyncTrayCheckboxes()
    {
        if (_config == null) return;
        if (ChkTrayCpuTemp != null) ChkTrayCpuTemp.IsChecked = _config.TraySensors.FirstOrDefault(s => s.Id == "cpu.temp")?.Enabled ?? false;
        if (ChkTrayGpuTemp != null) ChkTrayGpuTemp.IsChecked = _config.TraySensors.FirstOrDefault(s => s.Id == "gpu.temp")?.Enabled ?? false;
        if (ChkTrayCpuPower != null) ChkTrayCpuPower.IsChecked = _config.TraySensors.FirstOrDefault(s => s.Id == "cpu.power")?.Enabled ?? false;
        if (ChkTrayGpuPower != null) ChkTrayGpuPower.IsChecked = _config.TraySensors.FirstOrDefault(s => s.Id == "gpu.power")?.Enabled ?? false;
        if (ChkTraySystemPower != null) ChkTraySystemPower.IsChecked = _config.TraySensors.FirstOrDefault(s => s.Id == "system.power")?.Enabled ?? false;
        if (ChkTrayBattery != null) ChkTrayBattery.IsChecked = _config.TraySensors.FirstOrDefault(s => s.Id == "battery.life")?.Enabled ?? false;
    }

    private void ChkTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        if (ChkTrayCpuTemp != null) SetSensorState("cpu.temp", ChkTrayCpuTemp.IsChecked == true);
        if (ChkTrayGpuTemp != null) SetSensorState("gpu.temp", ChkTrayGpuTemp.IsChecked == true);
        if (ChkTrayCpuPower != null) SetSensorState("cpu.power", ChkTrayCpuPower.IsChecked == true);
        if (ChkTrayGpuPower != null) SetSensorState("gpu.power", ChkTrayGpuPower.IsChecked == true);
        if (ChkTraySystemPower != null) SetSensorState("system.power", ChkTraySystemPower.IsChecked == true);
        if (ChkTrayBattery != null) SetSensorState("battery.life", ChkTrayBattery.IsChecked == true);

        _config.Save();
        _onReloadTrayIcons?.Invoke();
    }

    private void BtnSaveTrayConfig_Click(object sender, RoutedEventArgs e)
    {
        _config?.Save();
        _onReloadTrayIcons?.Invoke();
    }

    private void SetSensorState(string id, bool enabled)
    {
        if (_config == null) return;
        var s = _config.TraySensors.FirstOrDefault(x => x.Id == id);
        if (s != null) s.Enabled = enabled;
    }

    private void BtnResetVendorColors_Click(object sender, RoutedEventArgs e)
    {
        _config?.Save();
        _onReloadTrayIcons?.Invoke();
    }

    private void TxtHexColor_Changed(object sender, TextChangedEventArgs e)
    {
    }

    private void ChkStartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        bool enabled = ChkStartWithWindows?.IsChecked == true;
        _config.StartWithWindows = enabled;
        StartupHelper.SetStartup(enabled, _config.StartMinimized);
        _config.Save();
    }

    private void ChkStartMinimized_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        bool min = ChkStartMinimized?.IsChecked == true;
        _config.StartMinimized = min;
        if (_config.StartWithWindows || StartupHelper.IsStartupEnabled())
        {
            StartupHelper.SetStartup(true, min);
        }
        _config.Save();
    }

    private void ChkCloseToTray_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        _config.CloseToTray = ChkCloseToTray?.IsChecked == true;
        _config.Save();
    }

    private void ChkMinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        _config.MinimizeToTray = ChkMinimizeToTray?.IsChecked == true;
        _config.Save();
    }

    private void ChkEnableDebugLog_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        _config.EnableDebugLog = ChkEnableDebugLog?.IsChecked == true;
        _config.Save();
    }

    private void ChkAlwaysOnTopOption_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        bool top = ChkAlwaysOnTopOption?.IsChecked == true;
        _config.AlwaysOnTop = top;
        this.Topmost = top;
        if (BtnAlwaysOnTop != null) BtnAlwaysOnTop.IsChecked = top;
        _config.Save();
    }

    // =========================================================================
    // PROCESS OBSERVABILITY & TOP RESOURCE CONSUMERS
    // =========================================================================
    private void RenderProcessTopCards(ProcessTelemetrySnapshot procs)
    {
        if (procs == null) return;

        // 1. Top CPU
        var cpuItems = new List<(int Rank, int Pid, string Name, string MetricStr, float Val, float MaxVal, MediaColor ValueColor)>(3);
        for (int i = 0; i < Math.Min(3, procs.TopCpu.Count); i++)
        {
            var p = procs.TopCpu[i];
            cpuItems.Add((i + 1, p.Pid, p.DisplayName, p.FormattedCpu, p.CpuPercent, 100f, TextPrimaryColor));
        }
        UpdateTopProcessPanel(PanelTopCpu, cpuItems);

        // 2. Top GPU
        var gpuItems = new List<(int Rank, int Pid, string Name, string MetricStr, float Val, float MaxVal, MediaColor ValueColor)>(3);
        for (int i = 0; i < Math.Min(3, procs.TopGpu.Count); i++)
        {
            var p = procs.TopGpu[i];
            string valStr = p.GpuPercent > 0.05f ? p.FormattedGpu : (p.GpuVramMb > 0 ? p.FormattedGpuVram : $"{p.CpuPercent:F1}%");
            gpuItems.Add((i + 1, p.Pid, p.DisplayName, valStr, Math.Max(p.GpuPercent, 1f), 100f, TextPrimaryColor));
        }
        UpdateTopProcessPanel(PanelTopGpu, gpuItems);

        // 3. Top RAM (Working Set)
        var ramItems = new List<(int Rank, int Pid, string Name, string MetricStr, float Val, float MaxVal, MediaColor ValueColor)>(3);
        float maxRam = procs.TopRam.Count > 0 ? Math.Max(1f, procs.TopRam[0].WorkingSetMb) : 100f;
        for (int i = 0; i < Math.Min(3, procs.TopRam.Count); i++)
        {
            var p = procs.TopRam[i];
            ramItems.Add((i + 1, p.Pid, p.DisplayName, p.FormattedWorkingSet, p.WorkingSetMb, maxRam, TextPrimaryColor));
        }
        UpdateTopProcessPanel(PanelTopRam, ramItems);

        // 4. Top Internet & Network
        var netItems = new List<(int Rank, int Pid, string Name, string MetricStr, float Val, float MaxVal, MediaColor ValueColor)>(3);
        int currentPid = Environment.ProcessId;
        var activeNet = procs.AllProcesses
            .Where(p => p.Pid != currentPid && !p.Name.Equals("Clocky", StringComparison.OrdinalIgnoreCase) && (p.NetDownSpeedKBps > 0.05f || p.NetUpSpeedKBps > 0.05f))
            .OrderByDescending(p => p.NetDownSpeedKBps + p.NetUpSpeedKBps)
            .Take(3)
            .ToList();

        if (activeNet.Count > 0)
        {
            float maxSpeed = activeNet.Max(p => Math.Max(p.NetDownSpeedKBps, p.NetUpSpeedKBps));
            for (int i = 0; i < activeNet.Count; i++)
            {
                var p = activeNet[i];
                bool isDown = p.NetDownSpeedKBps >= p.NetUpSpeedKBps;
                string metricStr = isDown ? $"↓ {p.FormattedNetDown}" : $"↑ {p.FormattedNetUp}";
                float speedVal = isDown ? p.NetDownSpeedKBps : p.NetUpSpeedKBps;
                MediaColor col = isDown ? MetricRed : MetricGreen;
                netItems.Add((i + 1, p.Pid, p.DisplayName, metricStr, speedVal, maxSpeed, col));
            }
        }
        else
        {
            var idleNet = procs.TopNetDown.Take(3).ToList();
            for (int i = 0; i < idleNet.Count; i++)
            {
                var p = idleNet[i];
                netItems.Add((i + 1, p.Pid, p.DisplayName, "Idle (0 KB/s)", 0f, 100f, TextSecondaryColor));
            }
        }
        UpdateTopProcessPanel(PanelTopNet, netItems);
    }

    private void UpdateTopProcessPanel(StackPanel? panel, IReadOnlyList<(int Rank, int Pid, string Name, string MetricStr, float Val, float MaxVal, MediaColor ValueColor)> items)
    {
        if (panel == null) return;

        // Ensure 3 pre-allocated row containers exist
        while (panel.Children.Count < 3)
        {
            panel.Children.Add(BuildTopProcessRow(panel.Children.Count + 1, 0, "--", "--", 0f, 100f, TextPrimaryColor));
        }

        for (int i = 0; i < 3; i++)
        {
            if (panel.Children[i] is Border border && border.Child is StackPanel stack)
            {
                if (i < items.Count)
                {
                    var item = items[i];
                    border.Visibility = Visibility.Visible;

                    if (stack.Children.Count > 0 && stack.Children[0] is DockPanel topDock)
                    {
                        if (topDock.Children.Count > 0 && topDock.Children[0] is StackPanel leftStack)
                        {
                            if (leftStack.Children.Count > 0 && leftStack.Children[0] is Border rankBadge && rankBadge.Child is TextBlock rankTxt)
                                rankTxt.Text = $"#{item.Rank}";
                            if (leftStack.Children.Count > 1 && leftStack.Children[1] is TextBlock nameTxt)
                                nameTxt.Text = item.Name;
                        }
                        if (topDock.Children.Count > 1 && topDock.Children[1] is TextBlock valTxt)
                        {
                            valTxt.Text = item.MetricStr;
                            if (item.ValueColor == MetricRed)
                                valTxt.Foreground = new SolidColorBrush(MetricRed);
                            else if (item.ValueColor == MetricGreen)
                                valTxt.Foreground = new SolidColorBrush(MetricGreen);
                            else
                                valTxt.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");
                        }
                    }

                    if (stack.Children.Count > 1 && stack.Children[1] is WpfProgressBar bar)
                    {
                        bar.Maximum = Math.Max(1f, item.MaxVal);
                        bar.Value = Math.Clamp(item.Val, 0f, bar.Maximum);
                        if (item.ValueColor == MetricRed)
                            bar.Foreground = new SolidColorBrush(MetricRed);
                        else if (item.ValueColor == MetricGreen)
                            bar.Foreground = new SolidColorBrush(MetricGreen);
                        else
                            bar.SetResourceReference(WpfProgressBar.ForegroundProperty, "BrushAccentCobalt");
                    }
                }
                else
                {
                    border.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    private FrameworkElement BuildTopProcessRow(int rank, int pid, string name, string metricStr, float val, float maxVal, MediaColor valueColor)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 5, 7, 5),
            Margin = new Thickness(0, 0, 0, 4)
        };
        border.SetResourceReference(Border.BackgroundProperty, "BrushCardSunken");
        border.SetResourceReference(Border.BorderBrushProperty, "BrushCardBorder");

        var stack = new StackPanel();
        var topDock = new DockPanel();

        var leftStack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var rankBadge = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 5, 0)
        };
        rankBadge.SetResourceReference(Border.BackgroundProperty, "BrushAccentCobalt");
        rankBadge.Child = new TextBlock { Text = $"#{rank}", FontWeight = FontWeights.Bold, FontSize = 8.5, Foreground = MediaBrushes.White };
        leftStack.Children.Add(rankBadge);

        var nameTxt = new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.Bold,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 110
        };
        nameTxt.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");
        leftStack.Children.Add(nameTxt);
        DockPanel.SetDock(leftStack, Dock.Left);
        topDock.Children.Add(leftStack);

        var valTxt = new TextBlock
        {
            Text = metricStr,
            FontWeight = FontWeights.Bold,
            FontSize = 10,
            FontFamily = new MediaFontFamily("Consolas"),
            HorizontalAlignment = WpfHorizontalAlignment.Right
        };
        if (valueColor == MetricRed)
            valTxt.Foreground = new SolidColorBrush(MetricRed);
        else if (valueColor == MetricGreen)
            valTxt.Foreground = new SolidColorBrush(MetricGreen);
        else
            valTxt.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");

        DockPanel.SetDock(valTxt, Dock.Right);
        topDock.Children.Add(valTxt);

        stack.Children.Add(topDock);

        var bar = new WpfProgressBar
        {
            Height = 3,
            Minimum = 0,
            Maximum = Math.Max(1f, maxVal),
            Value = Math.Clamp(val, 0f, maxVal),
            Background = MediaBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 3, 0, 0)
        };
        if (valueColor == MetricRed)
            bar.Foreground = new SolidColorBrush(MetricRed);
        else if (valueColor == MetricGreen)
            bar.Foreground = new SolidColorBrush(MetricGreen);
        else
            bar.SetResourceReference(WpfProgressBar.ForegroundProperty, "BrushAccentCobalt");

        stack.Children.Add(bar);

        border.Child = stack;
        return border;
    }

    private void ApplyProcessSortingAndFilter(IEnumerable<ProcessItem>? source = null)
    {
        if (GridAllProcesses == null) return;
        // Prevent background telemetry tick from interrupting active mouse drag/resize
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;

        var procs = source ?? _latestSnapshot?.Processes?.AllProcesses;
        if (procs == null) return;

        IEnumerable<ProcessItem> filtered = procs;
        if (!string.IsNullOrEmpty(_processFilter) && !_processFilter.StartsWith("Filter processes", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(p =>
                p.DisplayName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains(_processFilter, StringComparison.OrdinalIgnoreCase) ||
                p.FormattedPid.Contains(_processFilter, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString().Contains(_processFilter, StringComparison.OrdinalIgnoreCase) ||
                p.FormattedCpu.Contains(_processFilter, StringComparison.OrdinalIgnoreCase) ||
                p.FormattedPrivateMemory.Contains(_processFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(_activeProcessSortColumn) && _activeProcessSortDirection.HasValue)
        {
            bool asc = _activeProcessSortDirection == ListSortDirection.Ascending;
            filtered = _activeProcessSortColumn switch
            {
                "Pid" => asc ? filtered.OrderBy(p => p.Pid) : filtered.OrderByDescending(p => p.Pid),
                "Name" => asc ? filtered.OrderBy(p => p.Name) : filtered.OrderByDescending(p => p.Name),
                "CpuPercent" => asc ? filtered.OrderBy(p => p.CpuPercent) : filtered.OrderByDescending(p => p.CpuPercent),
                "FormattedCpu" => asc ? filtered.OrderBy(p => p.CpuPercent) : filtered.OrderByDescending(p => p.CpuPercent),
                "GpuPercent" => asc ? filtered.OrderBy(p => p.GpuPercent) : filtered.OrderByDescending(p => p.GpuPercent),
                "FormattedGpu" => asc ? filtered.OrderBy(p => p.GpuPercent) : filtered.OrderByDescending(p => p.GpuPercent),
                "PrivateMemoryBytes" => asc ? filtered.OrderBy(p => p.PrivateMemoryBytes) : filtered.OrderByDescending(p => p.PrivateMemoryBytes),
                "FormattedPrivateMemory" => asc ? filtered.OrderBy(p => p.PrivateMemoryBytes) : filtered.OrderByDescending(p => p.PrivateMemoryBytes),
                "WorkingSetBytes" => asc ? filtered.OrderBy(p => p.WorkingSetBytes) : filtered.OrderByDescending(p => p.WorkingSetBytes),
                "FormattedWorkingSet" => asc ? filtered.OrderBy(p => p.WorkingSetBytes) : filtered.OrderByDescending(p => p.WorkingSetBytes),
                "NetDownSpeedKBps" => asc ? filtered.OrderBy(p => p.NetDownSpeedKBps) : filtered.OrderByDescending(p => p.NetDownSpeedKBps),
                "FormattedNetDown" => asc ? filtered.OrderBy(p => p.NetDownSpeedKBps) : filtered.OrderByDescending(p => p.NetDownSpeedKBps),
                "NetUpSpeedKBps" => asc ? filtered.OrderBy(p => p.NetUpSpeedKBps) : filtered.OrderByDescending(p => p.NetUpSpeedKBps),
                "FormattedNetUp" => asc ? filtered.OrderBy(p => p.NetUpSpeedKBps) : filtered.OrderByDescending(p => p.NetUpSpeedKBps),
                "ThreadCount" => asc ? filtered.OrderBy(p => p.ThreadCount) : filtered.OrderByDescending(p => p.ThreadCount),
                _ => filtered
            };
        }

        var list = filtered.ToList();
        GridAllProcesses.ItemsSource = list;
        if (TxtProcessCount != null) TxtProcessCount.Text = $"{list.Count} Active Processes";
    }

    private void GridAllProcesses_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var column = e.Column;
        var sortMember = column.SortMemberPath;
        if (string.IsNullOrEmpty(sortMember)) return;

        if (_activeProcessSortColumn != sortMember)
        {
            _activeProcessSortColumn = sortMember;
            _activeProcessSortDirection = ListSortDirection.Ascending;
        }
        else
        {
            if (_activeProcessSortDirection == ListSortDirection.Ascending)
                _activeProcessSortDirection = ListSortDirection.Descending;
            else if (_activeProcessSortDirection == ListSortDirection.Descending)
            {
                _activeProcessSortDirection = null;
                _activeProcessSortColumn = "";
            }
            else
                _activeProcessSortDirection = ListSortDirection.Ascending;
        }

        if (GridAllProcesses != null)
        {
            foreach (var col in GridAllProcesses.Columns) col.SortDirection = null;
            if (!string.IsNullOrEmpty(_activeProcessSortColumn)) column.SortDirection = _activeProcessSortDirection;
        }

        if (_latestSnapshot?.Processes != null) ApplyProcessSortingAndFilter(_latestSnapshot.Processes.AllProcesses);
    }

    private const string ProcessFilterPlaceholder = "Filter processes (e.g. chrome, code)...";

    private void TxtProcessFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtProcessFilter == null) return;
        _processFilter = TxtProcessFilter.Text.Trim();
        if (_processFilter.StartsWith("Filter processes", StringComparison.OrdinalIgnoreCase)) _processFilter = "";
        if (_latestSnapshot?.Processes != null) ApplyProcessSortingAndFilter(_latestSnapshot.Processes.AllProcesses);
    }

    private void TxtProcessFilter_GotFocus(object sender, RoutedEventArgs e)
    {
        if (TxtProcessFilter == null) return;
        if (TxtProcessFilter.Text.StartsWith("Filter processes", StringComparison.OrdinalIgnoreCase))
        {
            TxtProcessFilter.Text = "";
            TxtProcessFilter.Foreground = (System.Windows.Media.Brush)FindResource("BrushTextPrimary");
        }
    }

    private void TxtProcessFilter_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TxtProcessFilter == null) return;
        if (string.IsNullOrWhiteSpace(TxtProcessFilter.Text))
        {
            TxtProcessFilter.Text = ProcessFilterPlaceholder;
            TxtProcessFilter.Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted");
        }
    }

    // =========================================================================
    // BACKGROUND AUTO-UPDATER LOGIC
    // =========================================================================
    private string? _stagedUpdateExePath;

    private async void CheckForUpdatesOnStartup()
    {
        if (_config?.AutoCheckUpdates != true) return;

        await Task.Delay(2500); // Non-blocking startup delay
        try
        {
            var (hasUpdate, manifest, msg) = await UpdateManager.CheckForUpdatesAsync(_config.UpdateFeedUrl);
            if (hasUpdate && manifest != null && !string.IsNullOrEmpty(manifest.DownloadUrl))
            {
                if (TxtUpdateStatus != null) TxtUpdateStatus.Text = $"Downloading v{manifest.Version} in background...";
                string targetExe = await UpdateManager.DownloadUpdateAsync(manifest.DownloadUrl);
                _stagedUpdateExePath = targetExe;

                if (BadgeUpdateAvailable != null) BadgeUpdateAvailable.Visibility = Visibility.Visible;
                if (TxtUpdateBadge != null) TxtUpdateBadge.Text = $"Update v{manifest.Version} Ready (Click to Restart)";
                if (TxtUpdateStatus != null) TxtUpdateStatus.Text = $"Update v{manifest.Version} ready to install.";
            }
            else
            {
                if (TxtUpdateStatus != null) TxtUpdateStatus.Text = $"Clocky v{UpdateManager.CurrentVersion} is up to date.";
            }
        }
        catch { }
    }

    private async void BtnCheckUpdatesNow_Click(object sender, RoutedEventArgs e)
    {
        if (BtnCheckUpdatesNow == null || TxtUpdateStatus == null) return;
        BtnCheckUpdatesNow.IsEnabled = false;
        TxtUpdateStatus.Text = "Checking remote repository for updates...";

        try
        {
            var (hasUpdate, manifest, msg) = await UpdateManager.CheckForUpdatesAsync(_config.UpdateFeedUrl);
            if (hasUpdate && manifest != null && !string.IsNullOrEmpty(manifest.DownloadUrl))
            {
                if (!string.IsNullOrEmpty(_stagedUpdateExePath) && File.Exists(_stagedUpdateExePath))
                {
                    if (BadgeUpdateAvailable != null) BadgeUpdateAvailable.Visibility = Visibility.Visible;
                    if (TxtUpdateBadge != null) TxtUpdateBadge.Text = $"Update v{manifest.Version} Ready (Click to Restart)";
                    TxtUpdateStatus.Text = $"Update v{manifest.Version} is already downloaded and ready to apply! Click the banner below.";
                    return;
                }

                TxtUpdateStatus.Text = $"Downloading v{manifest.Version}...";
                string targetExe = await UpdateManager.DownloadUpdateAsync(manifest.DownloadUrl);
                _stagedUpdateExePath = targetExe;

                if (BadgeUpdateAvailable != null) BadgeUpdateAvailable.Visibility = Visibility.Visible;
                if (TxtUpdateBadge != null) TxtUpdateBadge.Text = $"Update v{manifest.Version} Ready (Click to Restart)";
                TxtUpdateStatus.Text = $"Update v{manifest.Version} downloaded! Click the banner below or restart to apply.";
            }
            else
            {
                TxtUpdateStatus.Text = msg ?? $"Clocky v{UpdateManager.CurrentVersion} is up to date.";
            }
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Text = $"Check failed: {ex.Message}";
        }
        finally
        {
            BtnCheckUpdatesNow.IsEnabled = true;
        }
    }

    private void ChkAutoCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        _config.AutoCheckUpdates = ChkAutoCheckUpdates?.IsChecked == true;
        _config.Save();
    }

    private void BadgeUpdateAvailable_Click(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrEmpty(_stagedUpdateExePath) && File.Exists(_stagedUpdateExePath))
        {
            var res = System.Windows.MessageBox.Show(
                "A new update for Clocky has been downloaded and verified.\n\nRestart Clocky now to apply the update?",
                "Clocky Update Ready",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information);

            if (res == System.Windows.MessageBoxResult.Yes)
            {
                UpdateManager.ApplyUpdateAndRestart(_stagedUpdateExePath);
            }
        }
    }
}
