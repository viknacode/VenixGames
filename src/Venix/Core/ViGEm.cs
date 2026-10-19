using System.Runtime.InteropServices;

namespace Venix.Core;

/// <summary>
/// Minimal client for the ViGEmBus kernel driver (Nefarius Virtual Gamepad Emulation Bus), talking to the bus
/// device directly with the IOCTLs from the driver's public header (BusShared.h, MIT) — no native client DLL.
/// Nothing here runs unless the driver is already installed.
/// </summary>
internal static unsafe class ViGEm
{
    static readonly Guid BusInterface = new("96E42B22-F5E9-42F8-B043-ED0F932F014F");
    public const string Homepage = "https://github.com/nefarius/ViGEmBus/releases";

    /// <summary>Device-interface path of the bus, or null when the driver is not installed/running.</summary>
    public static string? DevicePath()
    {
        try
        {
            var guid = BusInterface;
            if (CM_Get_Device_Interface_List_SizeW(out var len, ref guid, null, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0 || len <= 1) return null;
            var buffer = new char[len];
            if (CM_Get_Device_Interface_ListW(ref guid, null, buffer, len, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0) return null;
            var s = new string(buffer);
            var end = s.IndexOf('\0');
            var first = end >= 0 ? s[..end] : s;
            return first.Length > 0 ? first : null;
        }
        catch { return null; }
    }

    public static bool IsInstalled => DevicePath() != null;

    // CTL_CODE(FILE_DEVICE_BUS_EXTENDER = 0x2A, function, METHOD_BUFFERED, access)
    const uint FILE_DEVICE_BUS_EXTENDER = 0x2A, FILE_READ_DATA = 1, FILE_WRITE_DATA = 2;
    static uint Ctl(uint function, uint access) => (FILE_DEVICE_BUS_EXTENDER << 16) | (access << 14) | (function << 2);
    public static readonly uint IOCTL_VIGEM_PLUGIN_TARGET = Ctl(0x801, FILE_WRITE_DATA);
    public static readonly uint IOCTL_VIGEM_UNPLUG_TARGET = Ctl(0x802, FILE_WRITE_DATA);
    public static readonly uint IOCTL_VIGEM_CHECK_VERSION = Ctl(0x803, FILE_WRITE_DATA);
    public static readonly uint IOCTL_VIGEM_WAIT_DEVICE_READY = Ctl(0x804, FILE_WRITE_DATA);
    public static readonly uint IOCTL_XUSB_SUBMIT_REPORT = Ctl(0x801 + 0x201, FILE_WRITE_DATA);
    public const uint VIGEM_COMMON_VERSION = 0x0001;
    public const uint TargetXbox360Wired = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct CheckVersion { public uint Size, Version; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PluginTarget { public uint Size, SerialNo, TargetType; public ushort VendorId, ProductId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SerialOnly { public uint Size, SerialNo; } // VIGEM_UNPLUG_TARGET / VIGEM_WAIT_DEVICE_READY

    [StructLayout(LayoutKind.Sequential)]
    public struct XusbReport
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XusbSubmitReport { public uint Size, SerialNo; public XusbReport Report; }

    /// <summary>Opens the bus for overlapped I/O, like the official client does.</summary>
    public static nint OpenBus(string path) =>
        CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nint.Zero, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH | FILE_FLAG_OVERLAPPED, nint.Zero);

    /// <summary>Buffered IOCTL with no output, waited synchronously. Returns the Win32 error (0 = success).</summary>
    public static int Ioctl(nint bus, uint code, void* input, int size)
    {
        var evt = CreateEventW(nint.Zero, false, false, null);
        if (evt == nint.Zero) return Marshal.GetLastWin32Error();
        try
        {
            var ov = new NativeOverlapped { EventHandle = evt };
            if (DeviceIoControl(bus, code, input, (uint)size, null, 0, out _, &ov)) return 0;
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_IO_PENDING) return err;
            return GetOverlappedResult(bus, &ov, out _, true) ? 0 : Marshal.GetLastWin32Error();
        }
        finally { CloseHandle(evt); }
    }

    public const int ERROR_IO_PENDING = 997, ERROR_INVALID_PARAMETER = 87, ERROR_ACCESS_DENIED = 5;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000, FILE_FLAG_OVERLAPPED = 0x40000000;
    const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern nint CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern nint CreateEventW(nint attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(nint device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResult(nint device, NativeOverlapped* overlapped, out uint transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(nint handle);
}

/// <summary>
/// Presents a HID controller (DualSense, DualShock 4, Switch Pro, generic) to a game as an Xbox 360 controller
/// for the duration of a play session.
/// <para>
/// Why ViGEmBus: games that only understand XInput can't see a DualSense at all, and there is no way to make
/// them see it without a kernel driver or a DLL injected into the game. Shipping an xinput1_3.dll shim next to
/// the game's executable was ruled out: it would need a native 32/64-bit DLL (not C#), trips anti-cheat, breaks
/// for games that load XInput from System32, and would have to be copied into every install folder. ViGEmBus is
/// the signed, open-source bus driver that Steam-alikes (DS4Windows, reWASD...) rely on; it is only used when it is
/// already installed on the machine — Venix never installs drivers — and otherwise the launcher just explains it.
/// Reports from the physical pad keep flowing through Raw Input (registered as a background sink while the game
/// runs) and are forwarded to the virtual target; when the game exits the target is unplugged.
/// Known limitation: games that also read the physical pad via DirectInput/raw HID will see two controllers
/// (Steam hides the real one with its HidHide driver; we don't).
/// </para>
/// </summary>
internal sealed unsafe class XInputEmulator : IDisposable
{
    nint _bus;
    uint _serial;
    PadState _last;
    bool _dirty;

    XInputEmulator(nint bus, uint serial) { _bus = bus; _serial = serial; }

    public static XInputEmulator? Start(out string? error)
    {
        error = null;
        var path = ViGEm.DevicePath();
        if (path == null) { error = "driver ViGEmBus não encontrado"; return null; }
        var bus = ViGEm.OpenBus(path);
        if (bus == nint.Zero || bus == -1) { error = "não foi possível abrir o ViGEmBus"; return null; }

        var version = new ViGEm.CheckVersion { Size = (uint)sizeof(ViGEm.CheckVersion), Version = ViGEm.VIGEM_COMMON_VERSION };
        var err = ViGEm.Ioctl(bus, ViGEm.IOCTL_VIGEM_CHECK_VERSION, &version, sizeof(ViGEm.CheckVersion));
        if (err != 0) { ViGEm.CloseHandle(bus); error = $"versão do ViGEmBus incompatível (erro {err})"; return null; }

        // like the official client: the first free serial wins
        for (uint serial = 1; serial <= 16; serial++)
        {
            var plug = new ViGEm.PluginTarget { Size = (uint)sizeof(ViGEm.PluginTarget), SerialNo = serial, TargetType = ViGEm.TargetXbox360Wired, VendorId = 0x045E, ProductId = 0x028E };
            if (ViGEm.Ioctl(bus, ViGEm.IOCTL_VIGEM_PLUGIN_TARGET, &plug, sizeof(ViGEm.PluginTarget)) != 0) continue;
            var ready = new ViGEm.SerialOnly { Size = (uint)sizeof(ViGEm.SerialOnly), SerialNo = serial };
            var r = ViGEm.Ioctl(bus, ViGEm.IOCTL_VIGEM_WAIT_DEVICE_READY, &ready, sizeof(ViGEm.SerialOnly));
            if (r == 0 || r == ViGEm.ERROR_INVALID_PARAMETER) return new XInputEmulator(bus, serial); // older drivers lack WAIT_DEVICE_READY
            var unplug = new ViGEm.SerialOnly { Size = (uint)sizeof(ViGEm.SerialOnly), SerialNo = serial };
            ViGEm.Ioctl(bus, ViGEm.IOCTL_VIGEM_UNPLUG_TARGET, &unplug, sizeof(ViGEm.SerialOnly));
            ViGEm.CloseHandle(bus);
            error = $"o controle virtual não ficou pronto (erro {r})";
            return null;
        }
        ViGEm.CloseHandle(bus);
        error = "nenhum slot livre no ViGEmBus";
        return null;
    }

    /// <summary>Forwards a normalized state to the virtual pad (UI thread; cheap, skips identical states).</summary>
    public void Submit(in PadState s)
    {
        if (_bus == nint.Zero) return;
        if (_dirty && Same(s, _last)) return;
        _last = s; _dirty = true;
        var report = new ViGEm.XusbSubmitReport
        {
            Size = (uint)sizeof(ViGEm.XusbSubmitReport), SerialNo = _serial,
            Report = new ViGEm.XusbReport
            {
                Buttons = s.Buttons,
                LeftTrigger = (byte)Math.Clamp(s.LT * 255f, 0, 255), RightTrigger = (byte)Math.Clamp(s.RT * 255f, 0, 255),
                ThumbLX = Axis(s.LX), ThumbLY = Axis(s.LY), ThumbRX = Axis(s.RX), ThumbRY = Axis(s.RY)
            }
        };
        var err = ViGEm.Ioctl(_bus, ViGEm.IOCTL_XUSB_SUBMIT_REPORT, &report, sizeof(ViGEm.XusbSubmitReport));
        if (err == ViGEm.ERROR_ACCESS_DENIED) Dispose(); // target vanished (driver unloaded)
    }

    static short Axis(float v) => (short)Math.Clamp(v * 32767f, -32768f, 32767f);
    static bool Same(in PadState a, in PadState b) =>
        a.Buttons == b.Buttons && a.LX == b.LX && a.LY == b.LY && a.RX == b.RX && a.RY == b.RY && a.LT == b.LT && a.RT == b.RT;

    public void Dispose()
    {
        if (_bus == nint.Zero) return;
        var unplug = new ViGEm.SerialOnly { Size = (uint)sizeof(ViGEm.SerialOnly), SerialNo = _serial };
        try { ViGEm.Ioctl(_bus, ViGEm.IOCTL_VIGEM_UNPLUG_TARGET, &unplug, sizeof(ViGEm.SerialOnly)); } catch { }
        ViGEm.CloseHandle(_bus);
        _bus = nint.Zero;
    }
}
