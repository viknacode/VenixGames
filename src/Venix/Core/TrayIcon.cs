using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Venix.Core;

/// <summary>
/// Thin wrapper over <c>Shell_NotifyIcon</c>: a notification-area icon owned by an invisible
/// top-level window (so the "TaskbarCreated" broadcast reaches it when Explorer restarts and the
/// icon is re-added). No Windows Forms dependency; the context menu is a WPF <c>ContextMenu</c>
/// opened by <see cref="TrayService"/>.
/// </summary>
public sealed unsafe class TrayIcon : IDisposable
{
    const uint IconId = 1;
    const int WM_APP = 0x8000, WM_TRAY = WM_APP + 0x11;
    const int WM_CONTEXTMENU = 0x007B, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
    const int NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    const uint NIIF_INFO = 0x01, NIIF_USER = 0x04, NIIF_LARGE_ICON = 0x20, NIIF_RESPECT_QUIET_TIME = 0x80;
    const uint NOTIFYICON_VERSION_4 = 4;
    const int SM_CXICON = 11, SM_CXSMICON = 49;
    const int WS_POPUP = unchecked((int)0x80000000), WS_EX_TOOLWINDOW = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr CreateIconFromResourceEx(byte* bits, uint size, bool icon, uint version, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    readonly HwndSource _source;
    readonly int _taskbarCreated;
    IntPtr _small, _large;
    string _tip = "";
    bool _added, _disposed;

    /// <summary>Left click, double click or balloon click.</summary>
    public event Action? Activated;
    /// <summary>Right click (or the context-menu key) on the icon.</summary>
    public event Action? ContextRequested;

    public IntPtr Handle => _source.Handle;

    public TrayIcon(string tip)
    {
        var p = new HwndSourceParameters("VenixTray") { Width = 0, Height = 0, PositionX = 0, PositionY = 0, WindowStyle = WS_POPUP, ExtendedWindowStyle = WS_EX_TOOLWINDOW };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        (_small, _large) = LoadIcons();
        _tip = Clip(tip, 127);
        Add();
    }

    NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _small,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = ""
    };

    void Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIcon(NIM_ADD, ref d)) return;
        _added = true;
        d.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref d);
    }

    /// <summary>Hover text (at most 127 characters). Only sent to the shell when it changes.</summary>
    public string Tip
    {
        get => _tip;
        set
        {
            var v = Clip(value, 127);
            if (v == _tip) return;
            _tip = v;
            if (!_added || _disposed) return;
            var d = Data(NIF_TIP | NIF_SHOWTIP);
            Shell_NotifyIcon(NIM_MODIFY, ref d);
        }
    }

    /// <summary>Notification toast (balloon) next to the icon, with the Venix icon.</summary>
    public void Notify(string title, string text)
    {
        if (!_added || _disposed) return;
        var d = Data(NIF_INFO | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        d.szInfoTitle = Clip(title, 63);
        d.szInfo = Clip(text, 255);
        d.dwInfoFlags = NIIF_RESPECT_QUIET_TIME | (_large != IntPtr.Zero ? NIIF_USER | NIIF_LARGE_ICON : NIIF_INFO);
        d.hBalloonIcon = _large;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY)
        {
            // NOTIFYICON_VERSION_4: LOWORD(lParam) = event, wParam = cursor position
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case NIN_SELECT or NIN_KEYSELECT or WM_LBUTTONDBLCLK or NIN_BALLOONUSERCLICK:
                    Activated?.Invoke();
                    break;
                case WM_CONTEXTMENU or WM_RBUTTONUP:
                    // a menu opened by a window that is not in the foreground would not close on an outside click
                    SetForegroundWindow(hwnd);
                    ContextRequested?.Invoke();
                    PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated && !_disposed)
        {
            _added = false;
            Add();
        }
        return IntPtr.Zero;
    }

    /* ---------------------------------------------------------- icon */

    /// <summary>Picks the best-sized images from Assets/venix.ico (the exe's own icon) for the tray and for toasts.</summary>
    static (IntPtr small, IntPtr large) LoadIcons()
    {
        try
        {
            using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/venix.ico"))?.Stream;
            if (s == null) return (IntPtr.Zero, IntPtr.Zero);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var ico = ms.ToArray();
            return (FromIco(ico, GetSystemMetrics(SM_CXSMICON)), FromIco(ico, GetSystemMetrics(SM_CXICON)));
        }
        catch { return (IntPtr.Zero, IntPtr.Zero); }
    }

    static IntPtr FromIco(byte[] ico, int want)
    {
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 2) != 1) return IntPtr.Zero;
        int count = BitConverter.ToUInt16(ico, 4), best = -1, bestScore = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var e = 6 + i * 16;
            if (e + 16 > ico.Length) break;
            int w = ico[e] == 0 ? 256 : ico[e];
            // prefer the exact size, then the next bigger one (downscaled), then the biggest smaller one
            var score = w == want ? 0 : w > want ? w - want : 1000 + want - w;
            if (score < bestScore) { bestScore = score; best = e; }
        }
        if (best < 0) return IntPtr.Zero;
        var size = BitConverter.ToInt32(ico, best + 8);
        var off = BitConverter.ToInt32(ico, best + 12);
        if (off < 0 || size <= 0 || off + size > ico.Length) return IntPtr.Zero;
        fixed (byte* p = &ico[off])
            return CreateIconFromResourceEx(p, (uint)size, true, 0x00030000, want, want, 0);
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added)
        {
            var d = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref d);
            _added = false;
        }
        _source.RemoveHook(Hook);
        _source.Dispose();
        if (_small != IntPtr.Zero) DestroyIcon(_small);
        if (_large != IntPtr.Zero) DestroyIcon(_large);
        _small = _large = IntPtr.Zero;
    }
}
