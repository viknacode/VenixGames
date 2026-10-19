using System.Runtime.InteropServices;

namespace Venix.Core;

/// <summary>
/// XInput 1.4 imports (xinput1_4.dll ships with Windows 8+). Used to read Xbox-class controllers
/// for launcher navigation and to probe which of the four XInput slots are occupied.
/// </summary>
internal static partial class XInput
{
    public const int MaxControllers = 4;
    public const int ErrorSuccess = 0, ErrorDeviceNotConnected = 0x48F;

    // XINPUT_GAMEPAD.wButtons — also the layout of PadState.Buttons and of the ViGEm XUSB report
    public const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008;
    public const ushort Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400;
    public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    public const byte BatteryTypeDisconnected = 0x00, BatteryTypeWired = 0x01, BatteryTypeAlkaline = 0x02, BatteryTypeNimh = 0x03, BatteryTypeUnknown = 0xFF;
    public const byte BatteryLevelEmpty = 0, BatteryLevelLow = 1, BatteryLevelMedium = 2, BatteryLevelFull = 3;
    public const byte BatteryDevTypeGamepad = 0;
    public const ushort CapsWireless = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vibration { public ushort LeftMotorSpeed, RightMotorSpeed; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Capabilities
    {
        public byte Type, SubType;
        public ushort Flags;
        public Gamepad Gamepad;
        public Vibration Vibration;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BatteryInformation { public byte BatteryType, BatteryLevel; }

    [LibraryImport("xinput1_4.dll")]
    private static partial int XInputGetState(int userIndex, out State state);

    [LibraryImport("xinput1_4.dll")]
    private static partial int XInputGetCapabilities(int userIndex, uint flags, out Capabilities caps);

    [LibraryImport("xinput1_4.dll")]
    private static partial int XInputGetBatteryInformation(int userIndex, byte devType, out BatteryInformation info);

    static bool _available = true;
    /// <summary>False once xinput1_4.dll turned out to be missing (very old Windows); every call then reports "not connected".</summary>
    public static bool Available => _available;

    public static bool TryGetState(int slot, out State state)
    {
        state = default;
        if (!_available || slot < 0 || slot >= MaxControllers) return false;
        try { return XInputGetState(slot, out state) == ErrorSuccess; }
        catch (DllNotFoundException) { _available = false; return false; }
        catch (EntryPointNotFoundException) { _available = false; return false; }
    }

    public static bool TryGetCapabilities(int slot, out Capabilities caps)
    {
        caps = default;
        if (!_available || slot < 0 || slot >= MaxControllers) return false;
        try { return XInputGetCapabilities(slot, 0, out caps) == ErrorSuccess; }
        catch (Exception) { return false; }
    }

    public static bool TryGetBattery(int slot, out BatteryInformation info)
    {
        info = default;
        if (!_available || slot < 0 || slot >= MaxControllers) return false;
        try { return XInputGetBatteryInformation(slot, BatteryDevTypeGamepad, out info) == ErrorSuccess; }
        catch (Exception) { return false; }
    }

    /// <summary>Normalizes an XInput reading to the shared <see cref="PadState"/> layout.</summary>
    public static PadState ToPadState(in Gamepad g) => new()
    {
        Buttons = g.Buttons,
        LX = Axis(g.ThumbLX), LY = Axis(g.ThumbLY), RX = Axis(g.ThumbRX), RY = Axis(g.ThumbRY),
        LT = g.LeftTrigger / 255f, RT = g.RightTrigger / 255f
    };

    static float Axis(short v) => Math.Clamp(v / 32767f, -1f, 1f);
}

/// <summary>
/// Controller state normalized to the XInput layout: <see cref="Buttons"/> uses the <see cref="XInput"/> flags,
/// sticks are -1..1 (Y up is positive, like XInput) and triggers 0..1. Both XInput pads and HID pads
/// (DualSense, DualShock, Switch Pro, generic) are translated into this so navigation and XInput emulation
/// share one code path.
/// </summary>
public struct PadState
{
    public ushort Buttons;
    public float LX, LY, RX, RY, LT, RT;

    public bool Has(ushort button) => (Buttons & button) != 0;
    public bool Any => Buttons != 0 || MathF.Abs(LX) > 0.3f || MathF.Abs(LY) > 0.3f || MathF.Abs(RX) > 0.3f || MathF.Abs(RY) > 0.3f || LT > 0.5f || RT > 0.5f;
}
