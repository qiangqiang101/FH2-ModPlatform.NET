using System.Numerics;
using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>A controller button (or trigger, counted as pressed past the usual threshold).</summary>
public enum Button
{
    A, B, X, Y,
    LB, RB,
    /// <summary>The left trigger.</summary>
    LT,
    /// <summary>The right trigger.</summary>
    RT,
    /// <summary>The left stick, clicked.</summary>
    LS,
    /// <summary>The right stick, clicked.</summary>
    RS,
    DPadUp, DPadDown, DPadLeft, DPadRight,
    Back, Start,
}

/// <summary>
/// The controllers (any connected one, as the game reads them): buttons, triggers, sticks, and the
/// rumble motors. Any thread. Keys aren't here: the platform's hotkeys are for those.
/// </summary>
/// <remarks>
/// The game still gets every button: pick ones that do nothing harmful where your mod uses them.
/// Rumble you start runs over the game's own until you stop it (<see cref="StopRumble"/>).
/// </remarks>
public static unsafe partial class Controller
{
    private const byte TriggerThreshold = 30;   // XINPUT_GAMEPAD_TRIGGER_THRESHOLD

    /// <summary>True if any connected controller has <paramref name="button"/> held.</summary>
    public static bool IsPressed(Button button)
    {
        if ((Volatile.Read(ref s_simulated) & (1 << (int)button)) != 0) return true;
        for (uint i = 0; i < 4; i++)
            if (XInputGetState(i, out var s) == 0 && Pressed(s.Gamepad, button)) return true;
        return false;
    }

    /// <summary>True if a controller is connected.</summary>
    public static bool IsConnected
    {
        get
        {
            for (uint i = 0; i < 4; i++)
                if (XInputGetState(i, out _) == 0) return true;
            return false;
        }
    }

    /// <summary>The left and right triggers of the first connected controller, 0..1.</summary>
    public static float LeftTrigger => First(out var g) ? g.LeftTrigger / 255f : 0;
    public static float RightTrigger => First(out var g) ? g.RightTrigger / 255f : 0;

    /// <summary>The sticks of the first connected controller, -1..1 (up is +Y).</summary>
    public static Vector2 LeftStick => First(out var g) ? Stick(g.ThumbLX, g.ThumbLY) : Vector2.Zero;
    public static Vector2 RightStick => First(out var g) ? Stick(g.ThumbRX, g.ThumbRY) : Vector2.Zero;

    /// <summary>
    /// Runs the rumble motors of every connected controller: <paramref name="left"/> the heavy one,
    /// <paramref name="right"/> the light one, each 0..1. Stays on until you change or stop it.
    /// </summary>
    public static void Rumble(float left, float right)
    {
        var v = new XInputVibration
        {
            LeftMotorSpeed = (ushort)(Math.Clamp(left, 0, 1) * 65535),
            RightMotorSpeed = (ushort)(Math.Clamp(right, 0, 1) * 65535),
        };
        for (uint i = 0; i < 4; i++) XInputSetState(i, ref v);
    }

    /// <summary>Stops the rumble you started (the game's own rumble carries on from its next change).</summary>
    public static void StopRumble() => Rumble(0, 0);

    private static bool Pressed(in XInputGamepad g, Button b) => b switch
    {
        Button.LT => g.LeftTrigger > TriggerThreshold,
        Button.RT => g.RightTrigger > TriggerThreshold,
        _ => (g.Buttons & Mask(b)) != 0,
    };

    private static ushort Mask(Button b) => b switch
    {
        Button.DPadUp => 0x0001, Button.DPadDown => 0x0002, Button.DPadLeft => 0x0004, Button.DPadRight => 0x0008,
        Button.Start => 0x0010, Button.Back => 0x0020, Button.LS => 0x0040, Button.RS => 0x0080,
        Button.LB => 0x0100, Button.RB => 0x0200,
        Button.A => 0x1000, Button.B => 0x2000, Button.X => 0x4000, Button.Y => 0x8000,
        _ => 0,
    };

    private static bool First(out XInputGamepad pad)
    {
        for (uint i = 0; i < 4; i++)
            if (XInputGetState(i, out var s) == 0)
            {
                pad = s.Gamepad;
                return true;
            }
        pad = default;
        return false;
    }

    private static Vector2 Stick(short x, short y) => new(Math.Max(-1f, x / 32767f), Math.Max(-1f, y / 32767f));

    // ---- XInput (xinput1_4.dll, as Windows has it and FnFNitro uses it) ---------------------------

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

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed, RightMotorSpeed;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint user, out XInputState state);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputSetState(uint user, ref XInputVibration vibration);
}
