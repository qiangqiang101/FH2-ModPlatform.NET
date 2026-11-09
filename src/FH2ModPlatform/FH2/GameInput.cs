using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>The driving controls the game asks its input for (context 0): what keys drive.</summary>
public enum DrivingControl
{
    Gas = 0, Brake = 1,
    /// <summary>-1 left .. 1 right.</summary>
    Steer = 2,
    Handbrake = 3, ShiftUp = 4, ShiftDown = 5, Clutch = 6,
    /// <summary>The right stick, for the driving cameras: up (ahead), back, left, right.</summary>
    LookAhead = 9, LookBack = 10, LookLeft = 11, LookRight = 12,
    PhotoMode = 15, Camera = 17, Rewind = 20,
    /// <summary>The HUD's prompts ("Enter garage"): X with an automatic gearbox, View with a manual one.</summary>
    Interact = 21,
    Map = 22,
}

/// <summary>Analog controls the menus ask for (context 7).</summary>
public enum MenuControl
{
    /// <summary>Design editor > Edit layer: move, size, spin or skew the layer (right +, up +).</summary>
    LayerRight = 111, LayerUp = 112,
    /// <summary>The garage's camera, turned by the right stick (right +, up +).</summary>
    CameraRight = 113, CameraUp = 114,
}

/// <summary>The controller's buttons, as the game keeps them (the d-pad's carry bits of the game's own with them).</summary>
[Flags]
public enum PadButtons : ulong
{
    None = 0,
    DPadUp = 0x140000001, DPadDown = 0x140000002, DPadLeft = 0xC0000004, DPadRight = 0xC0000008,
    Start = 0x10, View = 0x20, LeftStick = 0x40, RightStick = 0x80, LB = 0x100, RB = 0x200,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
}

/// <summary>
/// The game's input, fed by a mod: keys that drive (<see cref="SetDriving"/>, <see cref="PressDriving"/>), menu
/// sticks (<see cref="SetMenu"/>), the controller's buttons held (<see cref="HeldButtons"/>), and playing without a
/// controller at all (<see cref="PlayWithoutController"/>). Any thread. Starts with <see cref="Menus"/>.
/// </summary>
/// <remarks>
/// What's fed goes straight to the game's own controls, as a controller would: driving values count only while
/// the game takes driving input (not in photo mode or a menu), the menu sticks only in the menus.
/// </remarks>
public static unsafe class GameInput
{
    private const int Driving = 0, MenuContext = 7, Controls = 32;

    private static nint s_base;
    private static FH2Mod* s_owner;
    private static void** s_wasPressed, s_getDigital, s_getAnalog, s_consume, s_inputUpdate, s_showPairing, s_profileInput,
        s_padLostPause, s_uiSetInput, s_buttonState;
    private static FH2Hook* s_signIn;
    private static volatile bool s_driving, s_menus, s_pads;

    private static readonly float[] s_add = new float[Controls], s_floor = new float[Controls];
    private static readonly long[] s_asked = new long[Controls];
    private static readonly float[] s_menuAdd = new float[128];
    private static nint s_player;          // the input that last asked about a control: the player's
    private static uint s_inputFrame;
    private static long s_held;            // PadButtons, as a long
    private static int s_xinputHeld;
    private static volatile bool s_withoutPad;

    /// <summary>True once the driving controls can be fed (the game's input code is as expected).</summary>
    public static bool CanDrive => s_driving;

    private static volatile bool s_drivingOn;
    private static long s_drivingOnAt;

    /// <summary>
    /// True while the game takes driving input (its driving controls are on): not on a race's results, in a cutscene,
    /// photo mode or a menu, even where the driving HUD's window keeps the focus. As of the last input frame. Any thread.
    /// </summary>
    public static bool DrivingControlsOn => s_drivingOn && Environment.TickCount64 - Interlocked.Read(ref s_drivingOnAt) < 500;

    // ---- Driving ---------------------------------------------------------------------------------

    /// <summary>
    /// Adds <paramref name="value"/> to a driving control, as long as you leave it set (0 to stop): 1 is a pedal
    /// pressed fully, a stick pushed all the way; the controller's own is added to it, kept within -1..1.
    /// </summary>
    public static void SetDriving(DrivingControl control, float value) => Volatile.Write(ref s_add[(int)control], value);

    /// <summary>
    /// The control is at least <paramref name="value"/> (0 to stop): for the look controls, where the stick let go
    /// means "ahead" and pushing up doesn't.
    /// </summary>
    public static void SetDrivingAtLeast(DrivingControl control, float value) => Volatile.Write(ref s_floor[(int)control], value);

    /// <summary>Stops feeding every driving control.</summary>
    public static void ClearDriving()
    {
        Array.Clear(s_add);
        Array.Clear(s_floor);
    }

    /// <summary>
    /// A press of a button control (shift up, rewind, map...), as the controller's: "pressed" once to each place in
    /// the game that asks within a moment of it, for the frame it asks in, unless one takes it for itself.
    /// </summary>
    public static void PressDriving(DrivingControl control)
    {
        lock (s_edges)
        {
            ref var e = ref s_edges[(int)control];
            e.Id++;
            e.At = Environment.TickCount64;
        }
    }

    /// <summary>When the game last asked for the control (<see cref="Environment.TickCount64"/>): the look
    /// controls are asked for while a driving camera looks where the stick points.</summary>
    public static long LastAsked(DrivingControl control) => Volatile.Read(ref s_asked[(int)control]);

    // ---- The menus ---------------------------------------------------------------------------------

    /// <summary>Adds <paramref name="value"/> (-1..1) to a menu stick, as long as you leave it set.</summary>
    public static void SetMenu(MenuControl control, float value) => Volatile.Write(ref s_menuAdd[(int)control], value);

    // ---- The controller's own buttons --------------------------------------------------------------

    /// <summary>
    /// Controller buttons held for the game, as if on the controller, until you change it: what reads the buttons
    /// themselves (the radio's d-pad, the tile grids, FH2 ANNA) sees them held, pressed and let go.
    /// </summary>
    public static PadButtons HeldButtons
    {
        get => (PadButtons)Interlocked.Read(ref s_held);
        set => Interlocked.Exchange(ref s_held, (long)value);
    }

    /// <summary>
    /// XInput buttons (0x1 d-pad up ... 0x8000 Y) held only where the controller's raw state is read, not for the
    /// game's own buttons: Extra Options and its plugins read them there.
    /// </summary>
    public static ushort HeldXInputButtons
    {
        get => (ushort)Volatile.Read(ref s_xinputHeld);
        set => Volatile.Write(ref s_xinputHeld, value);
    }

    /// <summary>True if a controller is connected to the game.</summary>
    public static bool AnyControllerConnected
    {
        get
        {
            if (s_base == 0) return false;
            var inputs = *(nint*)(s_base + Addresses.Inputs);
            if (inputs == 0) return false;
            for (int i = 0; i < 8; i++)
                if (*(nint*)(inputs + i * Addresses.InputsStride + Addresses.InputsDevice) != 0) return true;
            return false;
        }
    }

    /// <summary>
    /// True: the game plays on without a controller, for a mod that's the controller (the keyboard). The player's
    /// input stays connected; with no controller there, the title screen signs in without one, "Controller
    /// disconnected" isn't shown, the game doesn't pause for it, and photo mode starts. Plugging a controller in
    /// pairs it again as usual.
    /// </summary>
    public static bool PlayWithoutController
    {
        get => s_withoutPad;
        set
        {
            s_withoutPad = value;
            if (s_signIn is not null)
            {
                if (value) ModPlatform.Api->EnableHooks(s_owner, s_signIn);
                else ModPlatform.Api->DisableHooks(s_owner, s_signIn);
            }
        }
    }

    /// <summary>
    /// True (before the game starts): platform.log gets each input check that answers anything but 0 (which context,
    /// which control, the answer, and where in the game it was asked from), at most every 400 ms each. For finding
    /// what a screen reads. Slows the game a little.
    /// </summary>
    public static bool Trace { get; set; }

    private static readonly Dictionary<(int, int, int), long> s_traced = new();

    private static void TraceCheck(int kind, int context, int control, float value)
    {
        if (!Trace || value == 0) return;
        long now = Environment.TickCount64;
        lock (s_traced)
        {
            if (s_traced.TryGetValue((kind, context, control), out var t) && now - t < 400) return;
            s_traced[(kind, context, control)] = now;
        }
        var from = CallerInGame();
        var what = kind switch { 0 => "digital", 1 => "analog", 2 => "pressed", _ => "state" };
        Game.Log(s_owner, $"input trace: {what} context {context} control {control} = {value:0.##}, asked from game+0x{(from == 0 ? 0 : from - s_base):X}");
    }

    /// <summary>A hosted mod is being replaced: whatever it fed stops (the new copy feeds its own).</summary>
    internal static void Forget()
    {
        ClearDriving();
        Array.Clear(s_menuAdd);
        HeldButtons = PadButtons.None;
        HeldXInputButtons = 0;
        ReleaseMenuPad();
    }

    /// <summary>Counts the game's input updates (once a frame); 0 until the input hook is in.</summary>
    public static uint InputFrame => s_inputFrame;

    // ---- Installing ----------------------------------------------------------------------------------

    private static void** Slot() => (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));

    private static bool Hook(string name, Addresses.Code code, void* detour, void** original, bool call = false)
    {
        if (Game.Mismatch(s_base, [code]) is { } bad)
        {
            Game.Log(s_owner, $"{name}: the game's code isn't as expected at {bad}");
            return false;
        }
        using var u = new Utf8Scope();
        var at = (void*)(s_base + code.Offset);
        var hook = call ? ModPlatform.Api->HookCall(s_owner, u.Str(name), at, detour, original)
                        : ModPlatform.Api->HookFunction(s_owner, u.Str(name), at, detour, original);
        if (hook is null || ModPlatform.Api->EnableHooks(s_owner, hook) == 0)
        {
            Game.Log(s_owner, $"{name}: couldn't hook it");
            return false;
        }
        return true;
    }

    private static bool HookSlot(string name, int slot, Addresses.Code expect, void* detour, void** original)
    {
        if (*(nint*)(s_base + slot) != s_base + expect.Offset || Game.Mismatch(s_base, [expect]) is not null)
        {
            Game.Log(s_owner, $"{name}: the game's code isn't as expected");
            return false;
        }
        using var u = new Utf8Scope();
        var hook = ModPlatform.Api->HookPointer(s_owner, u.Str(name), (void**)(s_base + slot), detour, original);
        return hook is not null && ModPlatform.Api->EnableHooks(s_owner, hook) != 0;
    }

    internal static void Install(nint b, FH2Mod* owner)
    {
        s_base = b;
        s_owner = owner;
        s_wasPressed = Slot(); s_getDigital = Slot(); s_getAnalog = Slot(); s_consume = Slot(); s_inputUpdate = Slot();
        s_showPairing = Slot(); s_profileInput = Slot(); s_padLostPause = Slot(); s_uiSetInput = Slot();

        bool pressed = Hook("FH2 input: WasPressed", Addresses.WasPressed, (delegate* unmanaged<nint, int, int, byte, byte>)&OnWasPressed, s_wasPressed);
        s_menus = Hook("FH2 input: GetAnalog", Addresses.GetAnalog, (delegate* unmanaged<nint, int, int, float>)&OnGetAnalog, s_getAnalog);
        s_driving = pressed && s_menus && Game.Mismatch(b, [Addresses.ContextActive]) is null &&
                    Hook("FH2 input: GetDigital", Addresses.GetDigital, (delegate* unmanaged<nint, int, int, float>)&OnGetDigital, s_getDigital);
        if (!s_driving) Game.Log(owner, "GameInput: no driving controls (the game's input isn't as expected)");
        if (s_driving) Hook("FH2 input: Consume", Addresses.Consume, (delegate* unmanaged<nint, int, int, void>)&OnConsume, s_consume);
        if (Trace)
        {
            s_buttonState = Slot();
            Hook("FH2 input: ButtonState (trace)", Addresses.ButtonState, (delegate* unmanaged<nint, int, int, byte, int>)&OnButtonState, s_buttonState);
            Game.Log(owner, "GameInput: tracing the input checks");
        }
        // Not checked by bytes: FH2 ANNA may have hooked it first (the platform chains them).
        using (var u = new Utf8Scope())
        {
            var hook = ModPlatform.Api->HookFunction(owner, u.Str("FH2 input: update"), (void*)(b + Addresses.InputUpdate.Offset),
                (delegate* unmanaged<nint, float, nint>)&OnInputUpdate, s_inputUpdate);
            s_pads = hook is not null && ModPlatform.Api->EnableHooks(owner, hook) != 0;
            if (!s_pads) Game.Log(owner, "GameInput: no held buttons (couldn't hook the input update)");
        }
        // Without a controller (each optional).
        if (Hook("FH2 input: controller pairing", Addresses.ShowPairing, (delegate* unmanaged<nint, byte, byte, void>)&OnShowPairing, s_showPairing))
        {
            Hook("FH2 input: profile's input", Addresses.ProfileInput, (delegate* unmanaged<nint, byte, nint>)&OnProfileInput, s_profileInput);
            if (Game.Mismatch(b, [Addresses.PadLostPause]) is null)
            {
                using var u = new Utf8Scope();
                var hook = ModPlatform.Api->HookCall(owner, u.Str("FH2 input: HUD pause without a pad"), (void*)(b + Addresses.PadLostPauseCall),
                    (delegate* unmanaged<nint, nint, nint>)&OnPadLostPause, s_padLostPause);
                if (hook is not null) ModPlatform.Api->EnableHooks(owner, hook);
            }
            HookSlot("FH2 input: UserInterface::SetInput", Addresses.UiSetInputSlot, Addresses.UiSetInput,
                (delegate* unmanaged<nint, nint, void>)&OnUiSetInput, s_uiSetInput);
        }
        if (Game.Mismatch(b, [Addresses.SignInGiveUp]) is null)
        {
            using var u = new Utf8Scope();
            byte* giveUp = stackalloc byte[] { 0x0F, 0x84, 0x9B, 0x01, 0x00, 0x00 };
            byte* ask = stackalloc byte[] { 0x66, 0x0F, 0x1F, 0x44, 0x00, 0x00 };   // nop: ask the account picker
            s_signIn = ModPlatform.Api->PatchBytes(owner, u.Str("FH2 input: sign in without a pad"), (void*)(b + Addresses.SignInGiveUp.Offset), giveUp, ask, 6);
            if (s_withoutPad && s_signIn is not null) ModPlatform.Api->EnableHooks(owner, s_signIn);
        }
        PhotoCamera.Install(b, owner);
        GarageCamera.Install(b, owner);
    }

    private static bool ContextOn(nint input, int context) =>
        ((delegate* unmanaged<nint, int, byte>)(s_base + Addresses.ContextActive.Offset))(input, context) != 0;

    // ---- A press, to each place that asks ---------------------------------------------------------------

    private struct Edge
    {
        public uint Id, Used;
        public long At;
        public Askers Askers;
    }

    [System.Runtime.CompilerServices.InlineArray(8)]
    private struct Askers { private (nint From, uint Told, uint Frame) _e; }

    private static readonly Edge[] s_edges = new Edge[Controls];

    private static bool KeyPressed(nint input, int context, int control)
    {
        if (context != Driving || (uint)control >= Controls) return false;
        lock (s_edges)
        {
            ref var e = ref s_edges[control];
            if (e.Id == 0 || e.Used == e.Id || Environment.TickCount64 - e.At > 250) return false;
            if (!ContextOn(input, Driving)) return false;
            var from = CallerInGame();
            uint frame = s_inputFrame;
            for (int i = 0; i < 8; i++)
            {
                ref var a = ref e.Askers[i];
                if (a.From != 0 && a.From != from) continue;
                a.From = from;
                if (a.Told == e.Id) return frame != 0 && s_consume is not null && *s_consume is not null && a.Frame == frame;
                a.Told = e.Id;
                a.Frame = frame;
                return true;
            }
            return false;
        }
    }

    // The first return address in the game's code on the stack: which place in the game called.
    internal static nint CallerInGame() => CallerInGame(s_base);

    // The place in the game that called: the first return address into the game's code on our stack, above our own
    // frames (a stack backtrace stops where .NET's frames begin). A return address is in the game's .text, right
    // after a call instruction (E8 rel32, FF 15 [rip+disp32], or FF /2 through a register or memory).
    internal static nint CallerInGame(nint b)
    {
        if (b == 0) return 0;
        if (s_textStart == 0)
        {
            var nt = b + *(int*)(b + 0x3C);
            int sections = *(ushort*)(nt + 6), optional = *(ushort*)(nt + 20);
            for (int i = 0; i < sections; i++)
            {
                var sec = nt + 24 + optional + 40 * i;
                if (*(ulong*)sec != 0x747865742E) continue;   // ".text"
                s_textEnd = b + *(int*)(sec + 12) + *(int*)(sec + 8);
                s_textStart = b + *(int*)(sec + 12);
                break;
            }
            if (s_textStart == 0) return 0;
        }
        nuint low, high;
        GetCurrentThreadStackLimits(&low, &high);
        nint marker = 0;
        var p = (nint*)&marker;
        for (int i = 0; i < 2048 && (nuint)(p + i) < high; i++)
        {
            var v = p[i];
            if (v < s_textStart + 8 || v >= s_textEnd) continue;
            var c = (byte*)v;
            if (c[-5] == 0xE8 || (c[-6] == 0xFF && c[-5] == 0x15) || (c[-2] == 0xFF && (c[-1] & 0xF8) == 0xD0) ||
                (c[-3] == 0xFF && (c[-2] & 0xF8) == 0x50) || (c[-6] == 0xFF && (c[-5] & 0xF8) == 0x90))
                return v;
        }
        return 0;
    }

    private static nint s_textStart, s_textEnd;

    [DllImport("kernel32.dll")]
    private static extern void GetCurrentThreadStackLimits(nuint* low, nuint* high);

    // ---- The menus' own presses (context 7): "pressed" once, then "released" once ------------------------

    private static int s_pressCtl = -1, s_releaseCtl = -1, s_phase;   // 1 pressed, 2 released
    private static long s_phaseUntil;
    private static volatile bool s_pressHeld, s_pressTold, s_releaseTold;

    internal static bool PressMenuPad(int key, bool hold)
    {
        if (s_wasPressed is null || *s_wasPressed is null || Volatile.Read(ref s_phase) != 0) return false;
        for (int i = 0; i < 24; i++)
        {
            var e = s_base + Addresses.PadKeys + 12 * i;
            if (*(int*)(e + 8) != key) continue;
            s_pressCtl = *(int*)e;
            s_releaseCtl = *(int*)(e + 4);
            s_pressHeld = hold;
            s_pressTold = s_releaseTold = false;
            s_phaseUntil = Environment.TickCount64 + 80;
            Volatile.Write(ref s_phase, 1);
            return true;
        }
        return false;
    }

    internal static void ReleaseMenuPad() => s_pressHeld = false;

    // Once a UI frame: down while held (at least 80 ms), then released for 40 ms, in real time.
    internal static void MenuPadFrame()
    {
        int phase = Volatile.Read(ref s_phase);
        if (phase == 0 || s_pressHeld || Environment.TickCount64 < s_phaseUntil) return;
        if (phase == 1)
        {
            s_phaseUntil = Environment.TickCount64 + 40;
            Volatile.Write(ref s_phase, 2);
        }
        else Volatile.Write(ref s_phase, 0);
    }

    // ---- The menus' d-pad checks -----------------------------------------------------------------------
    // The game decides "pressed" per button, and the first control that takes a press consumes it for the others.
    // With a controller, a screen's own d-pad checks (menu controls 121 up, 123 down, 125/127 left/right; the race's
    // start screen moves by them) come first; with held buttons, the menus' key pump's (92...) does, and they never
    // see the press. So for a d-pad press of ours, each is told "pressed" once if the game didn't say so itself.
    private static readonly int[] s_dpadControls = [121, 123, 125, 127];
    private static readonly ulong[] s_dpadBits = [1, 2, 4, 8];   // up, down, left, right (by trace: 121 up, 123 down)
    private static readonly long[] s_dpadAt = new long[4];
    private static readonly bool[] s_dpadTold = new bool[4];

    private static void DpadTold(int control, nint input)
    {
        int i = Array.IndexOf(s_dpadControls, control);
        if (i < 0) return;
        // The game said so itself: not again, for this press (it's seen).
        s_dpadTold[i] = true;
        s_dpadAt[i] = Environment.TickCount64;
        s_dpadVisible[i] = input != 0 && (*(ulong*)(input + Addresses.InputButtons) & s_dpadBits[i]) != 0;
    }

    private static bool DpadPressedPeek(int control)
    {
        int i = Array.IndexOf(s_dpadControls, control);
        return i >= 0 && !s_dpadTold[i] && Environment.TickCount64 - s_dpadAt[i] <= 250;
    }

    // Every menu d-pad check in the 300 ms after a d-pad press appears in the input (anyone's), when tracing.
    private static long s_dpadSeenAt;

    private static void TraceDpadAsk(int context, int control, byte consume, int game, int ours)
    {
        if (context != MenuContext || control is < 85 or > 130 || Environment.TickCount64 - s_dpadSeenAt > 300) return;
        Game.Log(s_owner, $"dpad trace: frame {s_inputFrame} input 0x{s_player:X} ctl {control} consume {consume} game {game} ours {ours} " +
                          $"bits {*(ulong*)(s_player + Addresses.InputButtons):X} last {*(ulong*)(s_player + Addresses.InputLastState + Addresses.StateButtons):X}");
    }

    // A press starts when our held direction becomes visible in the input that's asked: not when the key went down,
    // as something may hide it a while (FH2 ANNA keeps a d-pad-down tap from the game for 250 ms, as its open button).
    private static readonly bool[] s_dpadVisible = new bool[4];

    private static bool DpadPressed(int control, nint input)
    {
        int i = Array.IndexOf(s_dpadControls, control);
        if (i < 0) return false;
        ulong bit = s_dpadBits[i];
        bool ours = ((ulong)Interlocked.Read(ref s_held) & bit) != 0;
        bool visible = input != 0 && (*(ulong*)(input + Addresses.InputButtons) & bit) != 0;
        if (ours && visible && !s_dpadVisible[i])
        {
            s_dpadAt[i] = Environment.TickCount64;
            s_dpadTold[i] = false;
        }
        s_dpadVisible[i] = visible;
        if (!ours || !visible || s_dpadTold[i] || Environment.TickCount64 - s_dpadAt[i] > 250) return false;
        s_dpadTold[i] = true;
        return true;
    }

    // ---- The hooks -----------------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static byte OnWasPressed(nint input, int context, int control, byte a)
    {
        s_player = input;
        if (((delegate* unmanaged<nint, int, int, byte, byte>)*s_wasPressed)(input, context, control, a) != 0)
        {
            if (Trace) TraceCheck(2, context, control, 1);
            if (context == MenuContext) DpadTold(control, input);
            if (Trace) TraceDpadAsk(context, control, a, 1, 1);
            return 1;
        }
        if (Trace && context == MenuContext && control is >= 85 and <= 130)
        {
            // What we answer, in the moment after a d-pad press (keys' or the controller's).
            byte ours = 0;
            try { ours = DpadPressedPeek(control) ? (byte)1 : (byte)0; } catch { }
            TraceDpadAsk(context, control, a, 0, ours);
        }
        try
        {
            if (s_driving && KeyPressed(input, context, control)) return 1;
            if (context != MenuContext) return 0;
            if (DpadPressed(control, input)) return 1;
            int phase = Volatile.Read(ref s_phase);
            // Like a real press: "pressed" to the first that asks, once; then "released" once.
            if (phase == 1 && control == s_pressCtl && !s_pressTold)
            {
                s_pressTold = true;
                return 1;
            }
            if (phase == 2 && control == s_releaseCtl && !s_releaseTold)
            {
                s_releaseTold = true;
                return 1;
            }
        }
        catch { }
        return 0;
    }

    private static float Fed(nint input, int context, int control, float v, bool floor)
    {
        if (context == Driving && (uint)control < Controls)
        {
            Volatile.Write(ref s_asked[control], Environment.TickCount64);
            float add = Volatile.Read(ref s_add[control]), atLeast = floor ? Volatile.Read(ref s_floor[control]) : 0;
            if ((add != 0 || atLeast != 0) && ContextOn(input, Driving))
            {
                if (add != 0) v = Math.Clamp(v + add, -1, 1);
                if (atLeast != 0) v = Math.Max(v, atLeast);   // only when set: else steering left (-1) would become 0
            }
        }
        else if (context == MenuContext && (uint)control < 128)
        {
            float add = Volatile.Read(ref s_menuAdd[control]);
            if (add != 0) v = Math.Clamp(v + add, -1, 1);
        }
        return v;
    }

    [UnmanagedCallersOnly]
    private static float OnGetDigital(nint input, int context, int control)
    {
        var v = ((delegate* unmanaged<nint, int, int, float>)*s_getDigital)(input, context, control);
        if (Trace) TraceCheck(0, context, control, v);
        return Fed(input, context, control, v, true);
    }

    [UnmanagedCallersOnly]
    private static float OnGetAnalog(nint input, int context, int control)
    {
        var v = ((delegate* unmanaged<nint, int, int, float>)*s_getAnalog)(input, context, control);
        if (Trace) TraceCheck(1, context, control, v);
        return Fed(input, context, control, v, false);
    }

    [UnmanagedCallersOnly]
    private static int OnButtonState(nint input, int context, int control, byte a)
    {
        var v = ((delegate* unmanaged<nint, int, int, byte, int>)*s_buttonState)(input, context, control, a);
        if (Trace) TraceCheck(3, context, control, v);
        return v;
    }

    [UnmanagedCallersOnly]
    private static void OnConsume(nint input, int context, int control)
    {
        if (context == Driving && (uint)control < Controls)
            lock (s_edges) s_edges[control].Used = s_edges[control].Id;
        ((delegate* unmanaged<nint, int, int, void>)*s_consume)(input, context, control);
    }

    // Each input the held buttons go into, with what we held there last frame and its history's count as we left it.
    private sealed class FedInput
    {
        public ulong LastBits;
        public int HistoryCount;
        public bool HistoryOurs;
        public long Seen;
    }

    private static readonly Dictionary<nint, FedInput> s_fed = new();
    private static ulong s_lastBits;

    [UnmanagedCallersOnly]
    private static nint OnInputUpdate(nint inputs, float dt)
    {
        var r = ((delegate* unmanaged<nint, float, nint>)*s_inputUpdate)(inputs, dt);
        s_inputFrame++;
        var bits = (ulong)Interlocked.Read(ref s_held);
        if (s_driving && Current is var player and not 0)
        {
            s_drivingOn = ContextOn(player, Driving);
            Interlocked.Exchange(ref s_drivingOnAt, Environment.TickCount64);
        }
        // Into the input that last asked and the current player's (both handed over by the game each frame). Not a
        // kept pointer to the UI's input: the game swaps it (a restart, a replay), and writing into the old one
        // corrupted memory.
        nint a = s_player, c = Current;
        long now = Environment.TickCount64;
        if (a != 0) Feed(a, bits, now);
        if (c != 0 && c != a) Feed(c, bits, now);
        if (s_fed.Count > 8)
            foreach (var key in s_fed.Where(kv => now - kv.Value.Seen > 5000).Select(kv => kv.Key).ToList()) s_fed.Remove(key);
        s_lastBits = bits;
        var xinput = (ushort)Volatile.Read(ref s_xinputHeld);
        if (xinput != 0 && c != 0) *(ushort*)(c + Addresses.InputState + Addresses.StateXInputButtons) |= xinput;
        return r;
    }

    private static void Feed(nint input, ulong bits, long now)
    {
        if (!s_fed.TryGetValue(input, out var f)) s_fed[input] = f = new FedInput();
        f.Seen = now;
        if (s_withoutPad) *(byte*)(input + Addresses.InputConnected) = 1;   // the car and the HUD stop for a gone pad
        *(ulong*)(input + Addresses.InputButtons) |= bits;
        if ((bits & 0xF) != 0) SetDpad(input + Addresses.InputState, bits);
        // Last frame's state is the pad's alone: what was held then goes in too, else a held button is a new press each frame.
        *(ulong*)(input + Addresses.InputLastState + Addresses.StateButtons) |= f.LastBits;
        if ((f.LastBits & 0xF) != 0) SetDpad(input + Addresses.InputLastState, f.LastBits);
        if (Trace)
        {
            var on = *(ulong*)(input + Addresses.InputButtons) & 0xF;
            var before = *(ulong*)(input + Addresses.InputLastState + Addresses.StateButtons) & 0xF;
            if ((on & ~before) != 0)
            {
                s_dpadSeenAt = now;
                Game.Log(s_owner, $"dpad trace: frame {s_inputFrame} input 0x{input:X} press {on:X} (ours {bits & 0xF:X})");
            }
        }
        f.LastBits = bits;
        // As a controller's: its raw XInput buttons too, and the history of the frames they've been held.
        if ((bits & 0xFFFF) != 0) *(ushort*)(input + Addresses.InputState + Addresses.StateXInputButtons) |= (ushort)(bits & 0xFFFF);
        History(input + Addresses.InputState, (ushort)(bits & 0xFFFF), f);
    }

    // The held buttons into the state's history, as a controller's go in: each frame they're held, the next entry
    // and one more frame; let go, it's cleared. If the controller wrote this frame's entry, ours join it.
    private static void History(nint state, ushort buttons, FedInput f)
    {
        var entries = (ulong*)(state + Addresses.StateHistory);
        ref int index = ref *(int*)(state + Addresses.StateHistoryIndex);
        ref int count = ref *(int*)(state + Addresses.StateHistoryCount);
        if ((uint)index >= Addresses.HistoryLength) return;   // not as expected: leave it alone
        bool gameWrote = count != f.HistoryCount;
        if (buttons != 0)
        {
            if (gameWrote && count > 0) entries[(index + Addresses.HistoryLength - 1) % Addresses.HistoryLength] |= buttons;
            else
            {
                entries[index] = buttons;
                index = (index + 1) % Addresses.HistoryLength;
                count++;
            }
            f.HistoryOurs = true;
        }
        else if (f.HistoryOurs)
        {
            f.HistoryOurs = false;
            if (!gameWrote)   // ours alone: let go, as the controller's are
            {
                for (int i = 0; i < Addresses.HistoryLength; i++) entries[i] = 0;
                index = 0;
                count = 0;
            }
        }
        f.HistoryCount = count;
    }

    // The current player's input (the inputs' +0xAB28), or 0.
    private static nint Current
    {
        get
        {
            var inputs = *(nint*)(s_base + Addresses.Inputs);
            return inputs == 0 ? 0 : *(nint*)(inputs + Addresses.InputsCurrent);
        }
    }

    // Extra Options' plugins read the current player's raw buttons from the UI's update, after it's cleared them for
    // a gone pad: just before it, the held buttons go there again (and it stays connected without a pad).
    internal static void BeforeUiUpdate()
    {
        if (!s_pads) return;
        var cur = Current;
        if (cur == 0) return;
        if (s_withoutPad) *(byte*)(cur + Addresses.InputConnected) = 1;
        var bits = (ushort)((ulong)Interlocked.Read(ref s_held) & 0xFFFF) | (ushort)Volatile.Read(ref s_xinputHeld);
        if (bits != 0) *(ushort*)(cur + Addresses.InputState + Addresses.StateXInputButtons) |= (ushort)bits;
    }

    // The d-pad as a direction, as the pad gives it with its bits (the tile grids go by it): 0 up, 2 left, 4 down,
    // 6 right (8 none), the angle from up, the length, x, y. Left alone when the pad's own is pushed.
    private static void SetDpad(nint state, ulong bits)
    {
        var d = (int*)(state + Addresses.StateDpad);
        if (*d != 8) return;
        var f = (float*)(d + 1);
        if ((bits & 1) != 0) { *d = 0; f[0] = 0; f[1] = 1; f[2] = 0; f[3] = 1; }
        else if ((bits & 4) != 0) { *d = 2; f[0] = 1.5707964f; f[1] = 1; f[2] = -1; f[3] = 0; }
        else if ((bits & 2) != 0) { *d = 4; f[0] = 3.1415927f; f[1] = 1; f[2] = 0; f[3] = -1; }
        else if ((bits & 8) != 0) { *d = 6; f[0] = -1.5707964f; f[1] = 1; f[2] = 1; f[3] = 0; }
    }

    [UnmanagedCallersOnly]
    private static void OnShowPairing(nint manager, byte show, byte flag)
    {
        if (show != 0 && s_withoutPad && !AnyControllerConnected) return;   // the keyboard plays on
        ((delegate* unmanaged<nint, byte, byte, void>)*s_showPairing)(manager, show, flag);
    }

    [UnmanagedCallersOnly]
    private static nint OnProfileInput(nint profile, byte flag)
    {
        var input = ((delegate* unmanaged<nint, byte, nint>)*s_profileInput)(profile, flag);
        if (input != 0 || !s_withoutPad || AnyControllerConnected) return input;
        return Current;
    }

    [UnmanagedCallersOnly]
    private static nint OnPadLostPause(nint self, nint ev) =>
        s_withoutPad && !AnyControllerConnected ? 0 : ((delegate* unmanaged<nint, nint, nint>)*s_padLostPause)(self, ev);

    private static nint s_uiInput;

    [UnmanagedCallersOnly]
    private static void OnUiSetInput(nint ui, nint input)
    {
        if (input != 0) s_uiInput = input;
        else if (s_withoutPad) input = s_uiInput;
        ((delegate* unmanaged<nint, nint, void>)*s_uiSetInput)(ui, input);
    }

    [DllImport("kernel32.dll")]
    private static extern ushort RtlCaptureStackBackTrace(uint skip, uint count, nint* frames, uint* hash);
}
