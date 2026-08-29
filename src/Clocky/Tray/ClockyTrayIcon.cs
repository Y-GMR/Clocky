using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Interop;

namespace Clocky.Tray;

/// <summary>
/// High-performance Win32 Taskbar Notification Icon with dedicated HWND and unique uID.
/// Unlinks Clocky tray icons in Windows 11 taskbar so each badge and app icon can be pinned/dragged independently.
/// </summary>
public class ClockyTrayIcon : IDisposable
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIM_SETVERSION = 0x00000004;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_STATE = 0x00000008;
    private const int NIF_SHOWTIP = 0x00000080;

    private const int NOTIFYICON_VERSION_4 = 4;
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1024;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const int NIN_SELECT = WM_USER + 0;
    private const int NIN_KEYSELECT = WM_USER + 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private readonly int _uId;
    private readonly HwndSource _hwndSource;
    private bool _isAdded = false;
    private bool _disposed = false;
    private string _currentTip = "";

    public ContextMenuStrip? ContextMenuStrip { get; set; }
    public event Action? LeftClick;

    public ClockyTrayIcon(int uId, string name)
    {
        _uId = uId;
        var parameters = new HwndSourceParameters($"ClockyTray_{name}_{uId}")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)0x80000000) // WS_POPUP invisible message window
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
    }

    public void SetIcon(Icon icon, string tooltip)
    {
        if (_disposed || _hwndSource.IsDisposed) return;

        _currentTip = tooltip.Length > 127 ? tooltip.Substring(0, 127) : tooltip;

        var data = CreateDataStruct();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.hIcon = icon.Handle;
        data.szTip = _currentTip;

        if (!_isAdded)
        {
            if (Shell_NotifyIcon(NIM_ADD, ref data))
            {
                _isAdded = true;
                data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
                Shell_NotifyIcon(NIM_SETVERSION, ref data);
            }
        }
        else
        {
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    private NOTIFYICONDATA CreateDataStruct()
    {
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwndSource.Handle,
            uID = _uId,
            uCallbackMessage = WM_TRAYICON
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int eventMsg = (int)lParam & 0xFFFF;
            if (eventMsg == WM_LBUTTONUP || eventMsg == WM_LBUTTONDOWN || eventMsg == WM_LBUTTONDBLCLK || eventMsg == NIN_SELECT || eventMsg == NIN_KEYSELECT)
            {
                // Dispatch on UI thread
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    LeftClick?.Invoke();
                }));
                handled = true;
            }
            else if (eventMsg == WM_RBUTTONUP || eventMsg == WM_CONTEXTMENU || eventMsg == WM_RBUTTONDOWN)
            {
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    ShowContextMenu();
                }));
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (ContextMenuStrip == null) return;

        try
        {
            SetForegroundWindow(_hwndSource.Handle);
            var pt = System.Windows.Forms.Cursor.Position;
            ContextMenuStrip.Show(pt.X, pt.Y);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isAdded)
        {
            var data = CreateDataStruct();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _isAdded = false;
        }

        try
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
        }
        catch { }
    }
}
