using System.Diagnostics;
using System.Runtime.InteropServices;
using PadHotkey.Core;

namespace PadHotkey.Windows;

/// <summary>Reads one explicitly selected XInput slot; never guesses a replacement slot.</summary>
public static class WindowsInput
{
    public static InputSnapshot Read(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 3);
        var foreground = GetForegroundProcess();
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (XInputGetState((uint)index, out var state) != 0)
            return new InputSnapshot(false, values, foreground);

        var buttons = state.Gamepad.Buttons;
        foreach (var (name, mask) in ButtonMasks)
            values[name] = (buttons & mask) != 0 ? 1 : 0;
        values["LT"] = state.Gamepad.LeftTrigger / 255d;
        values["RT"] = state.Gamepad.RightTrigger / 255d;
        AddStick(values, "LS", state.Gamepad.ThumbLX, state.Gamepad.ThumbLY);
        AddStick(values, "RS", state.Gamepad.ThumbRX, state.Gamepad.ThumbRY);
        return new InputSnapshot(true, values, foreground);
    }

    // Return the executable's name without .exe. A window in an inaccessible
    // process gives no match, so scoped bindings fail closed.
    public static string GetForegroundProcess()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return "";
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return "";
        lock (ForegroundLock)
        {
            var now = Environment.TickCount64;
            if (_lastProcessId == processId && now - _lastRead < 250)
                return _lastProcessName;
            _lastProcessId = processId;
            _lastRead = now;
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                _lastProcessName = process.ProcessName;
            }
            catch (ArgumentException) { _lastProcessName = ""; }
            catch (InvalidOperationException) { _lastProcessName = ""; }
            catch (System.ComponentModel.Win32Exception) { _lastProcessName = ""; }
            return _lastProcessName;
        }
    }

    private static void AddStick(Dictionary<string, double> values, string stick, short rawX, short rawY)
    {
        var x = Normalize(rawX);
        var y = Normalize(rawY);
        // Preserve both raw axes for the engine's dominant-direction selection
        // and hysteresis. Suppressing an axis here would falsely rearm it when
        // a diagonal changes dominance without actually returning to neutral.
        values[$"{stick}.Up"] = Math.Max(0, y);
        values[$"{stick}.Down"] = Math.Max(0, -y);
        values[$"{stick}.Right"] = Math.Max(0, x);
        values[$"{stick}.Left"] = Math.Max(0, -x);
    }

    private static double Normalize(short value) => value >= 0 ? value / 32767d : value / 32768d;

    private static readonly (string Name, ushort Mask)[] ButtonMasks =
    [
        ("DPad.Up", 0x0001), ("DPad.Down", 0x0002), ("DPad.Left", 0x0004), ("DPad.Right", 0x0008),
        ("Start", 0x0010), ("Back", 0x0020), ("LS.Click", 0x0040), ("RS.Click", 0x0080),
        ("LB", 0x0100), ("RB", 0x0200), ("A", 0x1000), ("B", 0x2000), ("X", 0x4000), ("Y", 0x8000)
    ];

    private static readonly object ForegroundLock = new();
    private static uint _lastProcessId;
    private static long _lastRead;
    private static string _lastProcessName = "";

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetState(uint index, out XInputState state);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
