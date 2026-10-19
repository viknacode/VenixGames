using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Venix.Core;

public enum GamepadKind { Xbox, PlayStation, SwitchPro, Generic }

/// <summary>One connected controller.</summary>
public class GamepadInfo
{
    /// <summary>Raw Input device path (stable while plugged), or "xinput:N" for a slot without a HID node.</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public GamepadKind Kind { get; init; }
    /// <summary>True for controllers the XInput driver owns (Xbox 360/One/Series and compatible). Games read these directly.</summary>
    public bool IsXInput { get; init; }
    public int XInputSlot { get; set; } = -1;
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public nint Handle { get; init; }
    public bool Wireless { get; set; }
    /// <summary>XInput battery info; <see cref="XInput.BatteryTypeUnknown"/> when not available (HID pads).</summary>
    public byte BatteryType { get; set; } = XInput.BatteryTypeUnknown;
    public byte BatteryLevel { get; set; }
    /// <summary>Last normalized reading (HID pads are updated from WM_INPUT; XInput pads are read on demand).</summary>
    public PadState LastState { get; set; }
    internal HidLayout? Hid { get; set; }

    public string KindLabel => Kind switch { GamepadKind.Xbox => "Xbox", GamepadKind.PlayStation => "PlayStation", GamepadKind.SwitchPro => "Switch Pro", _ => "Genérico" };
    /// <summary>Pack URI of the illustration for this controller family (Assets/pad-*.png).</summary>
    public Uri ArtUri => new($"pack://application:,,,/Assets/pad-{(Kind == GamepadKind.PlayStation ? "playstation" : Kind == GamepadKind.Xbox ? "xbox" : "generic")}.png");
    public bool HasBattery => BatteryType is XInput.BatteryTypeAlkaline or XInput.BatteryTypeNimh;
    public string BatteryLabel => BatteryType switch
    {
        XInput.BatteryTypeWired => "Com fio",
        XInput.BatteryTypeAlkaline or XInput.BatteryTypeNimh => BatteryLevel switch { XInput.BatteryLevelEmpty => "Bateria descarregada", XInput.BatteryLevelLow => "Bateria baixa", XInput.BatteryLevelMedium => "Bateria média", _ => "Bateria cheia" },
        _ => ""
    };
    /// <summary>Segoe Fluent Icons battery glyph for the current level ("" when unknown).</summary>
    public string BatteryGlyph => !HasBattery ? "" : BatteryLevel switch { XInput.BatteryLevelEmpty => "", XInput.BatteryLevelLow => "", XInput.BatteryLevelMedium => "", _ => "" };
    public override string ToString() => Name;
}

/// <summary>
/// Detects controllers (hot-plug) and reads them.
/// <para>
/// Xbox-class pads go through XInput (xinput1_4.dll). Everything else — DualSense, DualShock 4, Switch Pro,
/// generic HID pads — is read through Raw Input: the main window registers for the Gamepad/Joystick usages
/// with RIDEV_DEVNOTIFY, so WM_INPUT_DEVICE_CHANGE tells us about plug/unplug within milliseconds, and
/// WM_INPUT delivers the HID reports which are decoded with the HID parser (hid.dll, HidP_*) using the
/// device's own report descriptor, so no per-controller byte layouts are needed. Nothing is polled while no
/// controller is present; WM_INPUT only arrives while Venix is the foreground window unless an emulation
/// session asks for background input.
/// </para>
/// <para>
/// Launching a game: Xbox pads need nothing. For a HID pad, if the ViGEmBus driver is installed we plug a
/// virtual Xbox 360 controller and forward the pad's reports to it while the game runs (see <see cref="ViGEm"/>);
/// otherwise we explain the option once. See <see cref="XInputEmulator"/> for why this approach was chosen.
/// </para>
/// </summary>
public sealed class GamepadService
{
    readonly AppStore _store;
    readonly List<GamepadInfo> _pads = new();
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _lateRefresh = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    readonly DispatcherTimer _battery = new() { Interval = TimeSpan.FromSeconds(30) };
    nint _hwnd;
    HwndSource? _source;
    bool _background;
    bool _vigemHintShown;
    XInputEmulator? _emu;
    GamepadInfo? _emuSource;
    byte[] _inputBuffer = new byte[256];

    public IReadOnlyList<GamepadInfo> Controllers => _pads;
    public GamepadInfo? Primary { get; private set; }
    public bool Any => _pads.Count > 0;
    /// <summary>A game launched from the library is running; navigation pauses and XInput emulation may be active.</summary>
    public bool GameRunning { get; private set; }
    /// <summary>XInput emulation for the running game is active (HID pad presented as a virtual Xbox 360 controller).</summary>
    public bool Emulating => _emu != null;

    /// <summary>Set/primary/battery changed (UI thread).</summary>
    public event Action? Changed;
    public event Action<GamepadInfo>? Connected;
    public event Action<GamepadInfo>? Disconnected;
    /// <summary>A HID (non-XInput) pad sent a report (UI thread).</summary>
    public event Action<GamepadInfo, PadState>? HidInput;
    public event Action<bool>? GameRunningChanged;
    /// <summary>Toast sink: (text, isError). Set by the main window.</summary>
    public Action<string, bool>? Notify { get; set; }

    public GamepadService(AppStore store)
    {
        _store = store;
        _refresh.Tick += (_, _) => { _refresh.Stop(); Refresh(); };
        _lateRefresh.Tick += (_, _) => { _lateRefresh.Stop(); Refresh(); };
        _battery.Tick += (_, _) => { if (UpdateBattery()) Changed?.Invoke(); };
        store.RunningChanged += OnRunning;
    }

    /// <summary>Hooks the window message loop and registers for controller notifications. Call once the window has a handle.</summary>
    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(Hook);
        Register(false);
        Refresh();
    }

    public void SetPrimary(GamepadInfo pad)
    {
        _store.Settings.GamepadPrimaryId = pad.Id;
        _store.SaveSoon();
        PickPrimary();
        Changed?.Invoke();
    }

    public void ScheduleRefresh()
    {
        _refresh.Stop(); _refresh.Start();
        _lateRefresh.Stop(); _lateRefresh.Start();
    }

    /* ---------------------------------------------------------- enumeration */

    void Refresh()
    {
        List<GamepadInfo> found;
        try { found = Enumerate(); }
        catch { return; }

        // A ViGEm target we plugged in shows up as an Xbox 360 pad; keep it out of the list.
        if (_emu != null) found.RemoveAll(f => f.IsXInput && f.VendorId == 0x045E && f.ProductId == 0x028E && _pads.All(p => p.Id != f.Id));

        var removed = _pads.Where(p => found.All(f => f.Id != p.Id)).ToList();
        var added = found.Where(f => _pads.All(p => p.Id != f.Id)).ToList();
        var slotChanged = false;
        foreach (var f in found)
        {
            var existing = _pads.FirstOrDefault(p => p.Id == f.Id);
            if (existing != null && existing.XInputSlot != f.XInputSlot) { existing.XInputSlot = f.XInputSlot; slotChanged = true; }
        }
        foreach (var r in removed) { _pads.Remove(r); r.Hid?.Dispose(); r.Hid = null; }
        foreach (var a in added) _pads.Add(a);

        var batteryChanged = UpdateBattery();
        var oldPrimary = Primary;
        PickPrimary();
        _battery.IsEnabled = _pads.Any(p => p.IsXInput && p.XInputSlot >= 0);

        if (added.Count > 0 || removed.Count > 0 || slotChanged || batteryChanged || oldPrimary != Primary) Changed?.Invoke();
        foreach (var r in removed) Disconnected?.Invoke(r);
        foreach (var a in added) Connected?.Invoke(a);
    }

    List<GamepadInfo> Enumerate()
    {
        var list = new List<GamepadInfo>();
        uint count = 0;
        var entrySize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, entrySize) == uint.MaxValue) count = 0;
        if (count > 0)
        {
            var devices = new RAWINPUTDEVICELIST[count];
            var n = GetRawInputDeviceList(devices, ref count, entrySize);
            if (n == uint.MaxValue) n = 0;
            for (int i = 0; i < n; i++)
            {
                var d = devices[i];
                if (d.dwType != RIM_TYPEHID) continue;
                var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
                var size = info.cbSize;
                if (GetRawInputDeviceInfoW(d.hDevice, RIDI_DEVICEINFO, ref info, ref size) == uint.MaxValue) continue;
                if (info.usUsagePage != 0x01 || info.usUsage is not (0x04 or 0x05 or 0x08)) continue;
                var path = DeviceName(d.hDevice);
                if (string.IsNullOrEmpty(path)) continue;
                if (list.Any(p => p.Id.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                var isXInput = path.Contains("IG_", StringComparison.OrdinalIgnoreCase);
                var (name, kind) = Identify(path, (ushort)info.dwVendorId, (ushort)info.dwProductId, isXInput);
                list.Add(new GamepadInfo
                {
                    Id = path, Handle = d.hDevice, Name = name, Kind = kind, IsXInput = isXInput,
                    VendorId = (ushort)info.dwVendorId, ProductId = (ushort)info.dwProductId,
                    Wireless = path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) || path.Contains("{00001124", StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        // pair XInput-capable HID nodes with the occupied XInput slots (XInput exposes no device path, so by order)
        var slots = new List<int>();
        for (int i = 0; i < XInput.MaxControllers; i++) if (XInput.TryGetState(i, out _)) slots.Add(i);
        var xpads = list.Where(p => p.IsXInput).ToList();
        for (int i = 0; i < xpads.Count; i++)
        {
            xpads[i].XInputSlot = i < slots.Count ? slots[i] : -1;
            if (xpads[i].XInputSlot >= 0 && XInput.TryGetCapabilities(xpads[i].XInputSlot, out var caps)) xpads[i].Wireless |= (caps.Flags & XInput.CapsWireless) != 0;
        }
        for (int i = xpads.Count; i < slots.Count; i++)
            list.Add(new GamepadInfo { Id = $"xinput:{slots[i]}", Name = "Controle Xbox", Kind = GamepadKind.Xbox, IsXInput = true, XInputSlot = slots[i], VendorId = 0x045E });
        return list;
    }

    static (string name, GamepadKind kind) Identify(string path, ushort vid, ushort pid, bool isXInput)
    {
        var product = ProductString(path);
        var kind = vid switch
        {
            0x054C => GamepadKind.PlayStation,
            0x057E => GamepadKind.SwitchPro,
            0x045E => GamepadKind.Xbox,
            _ => isXInput ? GamepadKind.Xbox : GamepadKind.Generic
        };
        if (kind == GamepadKind.Generic && product != null)
        {
            if (product.Contains("xbox", StringComparison.OrdinalIgnoreCase)) kind = GamepadKind.Xbox;
            else if (product.Contains("dualsense", StringComparison.OrdinalIgnoreCase) || product.Contains("dualshock", StringComparison.OrdinalIgnoreCase) || product.Contains("playstation", StringComparison.OrdinalIgnoreCase)) kind = GamepadKind.PlayStation;
            else if (product.Contains("pro controller", StringComparison.OrdinalIgnoreCase)) kind = GamepadKind.SwitchPro;
        }
        var name = kind switch
        {
            GamepadKind.PlayStation => pid switch { 0x0CE6 => "DualSense", 0x0DF2 => "DualSense Edge", 0x05C4 or 0x09CC or 0x0BA0 => "DualShock 4", _ => product ?? "Controle PlayStation" },
            GamepadKind.SwitchPro => pid == 0x2009 ? "Switch Pro Controller" : product ?? "Controle Nintendo",
            GamepadKind.Xbox => product is { Length: > 0 } && product.Contains("xbox", StringComparison.OrdinalIgnoreCase) ? product : "Controle Xbox",
            _ => product is { Length: > 0 } ? product : "Controle genérico"
        };
        return (name.Trim(), kind);
    }

    static unsafe string? DeviceName(nint device)
    {
        uint len = 0;
        if (GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, nint.Zero, ref len) == uint.MaxValue || len == 0) return null;
        var buffer = new char[len + 1];
        fixed (char* p = buffer)
        {
            var n = GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, (nint)p, ref len);
            if (n == uint.MaxValue || n == 0) return null;
            return new string(p, 0, (int)Math.Min(n, len)).TrimEnd('\0');
        }
    }

    static unsafe string? ProductString(string path)
    {
        var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, nint.Zero, OPEN_EXISTING, 0, nint.Zero);
        if (h == nint.Zero || h == -1) return null;
        try
        {
            var buffer = new char[128];
            fixed (char* p = buffer)
            {
                if (!HidD_GetProductString(h, p, (uint)(buffer.Length * 2))) return null;
                var s = new string(p).TrimEnd('\0').Trim();
                return s.Length > 0 ? s : null;
            }
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    bool UpdateBattery()
    {
        var changed = false;
        foreach (var p in _pads)
        {
            if (!p.IsXInput || p.XInputSlot < 0) continue;
            if (!XInput.TryGetBattery(p.XInputSlot, out var b)) continue;
            if (p.BatteryType != b.BatteryType || p.BatteryLevel != b.BatteryLevel) { p.BatteryType = b.BatteryType; p.BatteryLevel = b.BatteryLevel; changed = true; }
        }
        return changed;
    }

    void PickPrimary()
    {
        var wanted = _store.Settings.GamepadPrimaryId;
        Primary = _pads.FirstOrDefault(p => p.Id == wanted)
            ?? _pads.FirstOrDefault(p => p.IsXInput && p.XInputSlot >= 0)
            ?? _pads.FirstOrDefault(p => !p.IsXInput)
            ?? _pads.FirstOrDefault();
    }

    /* ---------------------------------------------------------- raw input */

    void Register(bool background)
    {
        if (_hwnd == nint.Zero) return;
        _background = background;
        uint flags = RIDEV_DEVNOTIFY | (background ? RIDEV_INPUTSINK : 0u);
        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x04, dwFlags = flags, hwndTarget = _hwnd }, // joystick
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x05, dwFlags = flags, hwndTarget = _hwnd }, // gamepad
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x08, dwFlags = flags, hwndTarget = _hwnd }, // multi-axis controller
        };
        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_INPUT_DEVICE_CHANGE) ScheduleRefresh();
        else if (msg == WM_INPUT) { try { OnInput(lParam); } catch { } }
        return nint.Zero;
    }

    unsafe void OnInput(nint hRawInput)
    {
        if (_pads.Count == 0) return;
        var headerSize = (uint)sizeof(RAWINPUTHEADER);
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, nint.Zero, ref size, headerSize);
        if (size == 0 || size > 65536) return;
        if (_inputBuffer.Length < size) _inputBuffer = new byte[size];
        fixed (byte* buf = _inputBuffer)
        {
            if (GetRawInputData(hRawInput, RID_INPUT, (nint)buf, ref size, headerSize) == uint.MaxValue) return;
            var header = (RAWINPUTHEADER*)buf;
            if (header->dwType != RIM_TYPEHID) return;
            GamepadInfo? pad = null;
            foreach (var p in _pads) if (p.Handle == header->hDevice) { pad = p; break; }
            if (pad == null || pad.IsXInput) return;
            pad.Hid ??= HidLayout.Load(pad.Handle);
            if (pad.Hid == null) return;

            var hid = (RAWHID*)(buf + headerSize);
            var data = buf + headerSize + sizeof(RAWHID);
            var reportLen = (int)hid->dwSizeHid;
            var state = pad.LastState;
            for (uint i = 0; i < hid->dwCount; i++, data += reportLen)
                state = pad.Hid.Parse(pad.Kind, data, reportLen, state);
            pad.LastState = state;
            HidInput?.Invoke(pad, state);
            if (_emu != null && pad == _emuSource) _emu.Submit(state);
        }
    }

    /* ---------------------------------------------------------- game sessions */

    void OnRunning(string _)
    {
        var any = _store.Library.Any(g => _store.IsRunning(g.Id));
        if (any == GameRunning) return;
        GameRunning = any;
        if (any) StartSession(); else StopSession();
        GameRunningChanged?.Invoke(any);
    }

    void StartSession()
    {
        if (!_store.Settings.GamepadLaunchSupport) return;
        var pad = Primary;
        if (pad == null || pad.IsXInput) return; // Xbox pads: the game talks XInput to them directly
        if (!ViGEm.IsInstalled)
        {
            if (_vigemHintShown) return;
            _vigemHintShown = true;
            Notify?.Invoke($"Para o jogo ver seu {pad.Name} como controle Xbox, instale o driver ViGEmBus. Veja Ajustes → Controle.", false);
            return;
        }
        var emu = XInputEmulator.Start(out var error);
        if (emu == null) { Notify?.Invoke("Emulação de controle Xbox indisponível: " + error, true); return; }
        _emu = emu;
        _emuSource = pad;
        pad.Hid ??= HidLayout.Load(pad.Handle);
        Register(true); // keep receiving the pad's reports while the game is in front
        emu.Submit(pad.LastState);
        Notify?.Invoke($"{pad.Name} apresentado ao jogo como controle Xbox", false);
    }

    void StopSession()
    {
        if (_emu == null) return;
        try { _emu.Dispose(); } catch { }
        _emu = null;
        _emuSource = null;
        Register(false);
        ScheduleRefresh();
    }

    /* ---------------------------------------------------------- native */

    const int WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE;
    const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_DEVNOTIFY = 0x00002000;
    const uint RIM_TYPEHID = 2;
    const uint RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000B;
    const uint RID_INPUT = 0x10000003;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public nint hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICELIST { public nint hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct RID_DEVICE_INFO
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        [FieldOffset(8)] public uint dwVendorId;
        [FieldOffset(12)] public uint dwProductId;
        [FieldOffset(16)] public uint dwVersionNumber;
        [FieldOffset(20)] public ushort usUsagePage;
        [FieldOffset(22)] public ushort usUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint dwType, dwSize; public nint hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWHID { public uint dwSizeHid, dwCount; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceInfoW(nint device, uint command, nint data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceInfoW(nint device, uint command, ref RID_DEVICE_INFO data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputData(nint rawInput, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern nint CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(nint handle);

    [DllImport("hid.dll", SetLastError = true)]
    static extern unsafe bool HidD_GetProductString(nint device, char* buffer, uint length);
}

/// <summary>
/// Report descriptor of a HID pad (preparsed data + the value caps we care about) and the translation of its
/// reports into <see cref="PadState"/>. Buttons are mapped by controller family since HID only numbers them.
/// </summary>
internal sealed unsafe class HidLayout : IDisposable
{
    nint _preparsed;
    readonly ushort[] _usages;
    readonly List<ValueCap> _values = new();

    record struct ValueCap(ushort UsagePage, ushort Usage, ushort LinkCollection, int Min, int Max, ushort Bits, bool HasNull);

    HidLayout(nint preparsed, uint maxUsages)
    {
        _preparsed = preparsed;
        _usages = new ushort[Math.Max(1, maxUsages)];
    }

    public static HidLayout? Load(nint device)
    {
        uint size = 0;
        if (GetRawInputDeviceInfoW(device, RIDI_PREPARSEDDATA, nint.Zero, ref size) == uint.MaxValue || size == 0) return null;
        var mem = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfoW(device, RIDI_PREPARSEDDATA, mem, ref size) == uint.MaxValue) { Marshal.FreeHGlobal(mem); return null; }
        if (HidP_GetCaps(mem, out var caps) != HIDP_STATUS_SUCCESS) { Marshal.FreeHGlobal(mem); return null; }

        var layout = new HidLayout(mem, HidP_MaxUsageListLength(HidP_Input, 0x09, mem));
        ushort n = caps.NumberInputValueCaps;
        if (n > 0)
        {
            var vc = new HIDP_VALUE_CAPS[n];
            fixed (HIDP_VALUE_CAPS* p = vc)
            {
                if (HidP_GetValueCaps(HidP_Input, p, ref n, mem) == HIDP_STATUS_SUCCESS)
                    for (int i = 0; i < n; i++)
                    {
                        var c = vc[i];
                        if (c.UsagePage != 0x01) continue;
                        // ranges (several usages in one cap) are expanded; we only need X,Y,Z,Rx,Ry,Rz,Hat
                        ushort from = c.IsRange != 0 ? c.UsageMin : c.Usage, to = c.IsRange != 0 ? c.UsageMax : c.Usage;
                        for (ushort u = from; u <= to && u <= 0x39; u++)
                            if (u is >= 0x30 and <= 0x35 or 0x39 && layout._values.All(v => v.Usage != u))
                                layout._values.Add(new ValueCap(c.UsagePage, u, c.LinkCollection, c.LogicalMin, c.LogicalMax, c.BitSize, c.HasNull != 0));
                    }
            }
        }
        return layout;
    }

    public PadState Parse(GamepadKind kind, byte* report, int length, PadState previous)
    {
        var s = previous;
        var map = kind switch { GamepadKind.PlayStation => PsButtons, GamepadKind.SwitchPro => SwitchButtons, _ => GenericButtons };

        // buttons (usage page 0x09, usages 1..N)
        uint count = (uint)_usages.Length;
        fixed (ushort* u = _usages)
        {
            var status = HidP_GetUsages(HidP_Input, 0x09, 0, u, ref count, _preparsed, report, (uint)length);
            if (status == HIDP_STATUS_SUCCESS)
            {
                ushort buttons = 0;
                bool lt = false, rt = false;
                for (int i = 0; i < count; i++)
                {
                    var idx = _usages[i] - 1;
                    if (idx < 0 || idx >= map.Length) continue;
                    buttons |= map[idx];
                    if (idx == 6) lt = true; // digital L2 / ZL / LT
                    if (idx == 7) rt = true;
                }
                s.Buttons = (ushort)((s.Buttons & DpadMask) | buttons);
                s.LT = lt ? 1f : 0f;
                s.RT = rt ? 1f : 0f;
            }
        }

        // axes and hat switch
        foreach (var v in _values)
        {
            if (HidP_GetUsageValue(HidP_Input, v.UsagePage, v.LinkCollection, v.Usage, out var raw, _preparsed, report, (uint)length) != HIDP_STATUS_SUCCESS) continue;
            if (v.Usage == 0x39)
            {
                var range = v.Max - v.Min + 1;
                long val = raw;
                if (v.Min < 0) val = Signed(raw, v.Bits);
                s.Buttons &= unchecked((ushort)~DpadMask);
                if (range > 0 && val >= v.Min && val <= v.Max)
                {
                    var dir = (int)((val - v.Min) * 8 / range); // 0=up … 7=up-left
                    s.Buttons |= dir switch
                    {
                        0 => XInput.DpadUp, 1 => (ushort)(XInput.DpadUp | XInput.DpadRight), 2 => XInput.DpadRight, 3 => (ushort)(XInput.DpadDown | XInput.DpadRight),
                        4 => XInput.DpadDown, 5 => (ushort)(XInput.DpadDown | XInput.DpadLeft), 6 => XInput.DpadLeft, 7 => (ushort)(XInput.DpadUp | XInput.DpadLeft), _ => 0
                    };
                }
                continue;
            }
            var norm = Normalize(raw, v); // 0..1
            switch (v.Usage)
            {
                case 0x30: s.LX = norm * 2 - 1; break;
                case 0x31: s.LY = -(norm * 2 - 1); break;  // HID Y grows downwards, XInput upwards
                case 0x32: s.RX = norm * 2 - 1; break;
                case 0x35: s.RY = -(norm * 2 - 1); break;
                case 0x33: s.LT = MathF.Max(s.LT, norm); break; // analog L2 (DualShock/DualSense)
                case 0x34: s.RT = MathF.Max(s.RT, norm); break;
            }
        }
        return s;
    }

    static float Normalize(uint raw, in ValueCap v)
    {
        long min = v.Min, max = v.Max, val = raw;
        if (v.Min < 0) val = Signed(raw, v.Bits);
        if (max <= min) { min = 0; max = v.Bits is > 0 and < 32 ? (1L << v.Bits) - 1 : 255; }
        return Math.Clamp((float)(val - min) / (max - min), 0f, 1f);
    }

    static long Signed(uint raw, ushort bits)
    {
        if (bits == 0 || bits >= 32) return (int)raw;
        long v = raw & ((1L << bits) - 1);
        return (v & (1L << (bits - 1))) != 0 ? v - (1L << bits) : v;
    }

    const ushort DpadMask = XInput.DpadUp | XInput.DpadDown | XInput.DpadLeft | XInput.DpadRight;

    // HID button numbers → XInput flags (index = button number - 1; 0 = unmapped). Index 6/7 double as digital triggers.
    static readonly ushort[] PsButtons = { XInput.X, XInput.A, XInput.B, XInput.Y, XInput.LeftShoulder, XInput.RightShoulder, 0, 0, XInput.Back, XInput.Start, XInput.LeftThumb, XInput.RightThumb, XInput.Guide, 0 };
    static readonly ushort[] SwitchButtons = { XInput.B, XInput.A, XInput.Y, XInput.X, XInput.LeftShoulder, XInput.RightShoulder, 0, 0, XInput.Back, XInput.Start, XInput.LeftThumb, XInput.RightThumb, XInput.Guide, 0 };
    static readonly ushort[] GenericButtons = { XInput.A, XInput.B, XInput.X, XInput.Y, XInput.LeftShoulder, XInput.RightShoulder, 0, 0, XInput.Back, XInput.Start, XInput.LeftThumb, XInput.RightThumb, XInput.Guide };

    public void Dispose()
    {
        if (_preparsed != nint.Zero) { Marshal.FreeHGlobal(_preparsed); _preparsed = nint.Zero; }
    }

    /* ---------------------------------------------------------- native */

    const uint RIDI_PREPARSEDDATA = 0x20000005;
    const int HidP_Input = 0;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(16)] public byte HasNull;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(56)] public ushort Usage;    // NotRange
        [FieldOffset(56)] public ushort UsageMin; // Range
        [FieldOffset(58)] public ushort UsageMax;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceInfoW(nint device, uint command, nint data, ref uint size);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(nint preparsed, out HIDP_CAPS caps);

    [DllImport("hid.dll")]
    static extern int HidP_GetValueCaps(int reportType, HIDP_VALUE_CAPS* caps, ref ushort length, nint preparsed);

    [DllImport("hid.dll")]
    static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, nint preparsed);

    [DllImport("hid.dll")]
    static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, ushort* usageList, ref uint usageLength, nint preparsed, byte* report, uint reportLength);

    [DllImport("hid.dll")]
    static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value, nint preparsed, byte* report, uint reportLength);
}
