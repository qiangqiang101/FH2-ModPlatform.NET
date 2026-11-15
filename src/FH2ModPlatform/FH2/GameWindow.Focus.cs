using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;

namespace FH2;

// Keeping the game running when its window isn't in front (FH2 NoPause's way): the window doesn't hear it lost the
// focus, GetForegroundWindow / GetActiveWindow / GetFocus answer with the game's window, and the pad, which Azimuth
// reads through Windows.Gaming.Input (it stops giving readings to a window behind others), is read from XInput instead.
public static unsafe partial class GameWindow
{
    private static volatile bool s_keepRunning;
    private static int s_focusHooks;   // 0 not yet, 1 in
    // The platform's slots for the originals: filled before a hook goes live, so a detour never finds them empty.
    private static void** s_foregroundSlot, s_activeSlot, s_focusSlot;
    private static delegate* unmanaged<nint> s_origForeground => s_foregroundSlot is null ? null : (delegate* unmanaged<nint>)*s_foregroundSlot;

    /// <summary>
    /// Keeps the game running, and the controller working, while its window isn't the one in front (alt-tab, a second
    /// screen). Any thread. The keyboard checks here (<see cref="IsKeyDown"/>, <see cref="IsForeground"/>) still tell
    /// the truth, so a mod's keys don't fire while the player types somewhere else.
    /// </summary>
    public static bool KeepRunningUnfocused
    {
        get => s_keepRunning;
        set
        {
            s_keepRunning = value;
            if (!value) return;
            Start();
            if (Interlocked.Exchange(ref s_focusHooks, 1) == 0) InstallFocusHooks();
        }
    }

    private static bool IsLosingFocus(uint msg, nint w) => msg switch
    {
        0x1C => w == 0,                    // WM_ACTIVATEAPP, deactivated
        0x06 => (w & 0xFFFF) == 0,         // WM_ACTIVATE, WA_INACTIVE
        0x08 => true,                      // WM_KILLFOCUS
        _ => false,
    };

    // The real foreground window, even while ours answers for it.
    private static nint RealForeground() => s_origForeground is not null ? s_origForeground() : GetForegroundWindow();

    private static void InstallFocusHooks()
    {
        var owner = ModPlatform.GameOwner;
        var user32 = NativeLibrary.Load("user32.dll");
        s_foregroundSlot = NewSlot();
        s_activeSlot = NewSlot();
        s_focusSlot = NewSlot();
        Hook(owner, user32, "GetForegroundWindow", &OnForeground, s_foregroundSlot);
        Hook(owner, user32, "GetActiveWindow", &OnActive, s_activeSlot);
        Hook(owner, user32, "GetFocus", &OnFocus, s_focusSlot);
        new Thread(PadHookLoop) { IsBackground = true, Name = "FH2 pad unfocused" }.Start();
        Log("GameWindow: keeps running unfocused");
    }

    private static void** NewSlot() => (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));

    private static void Hook(FH2ModPlatform.Native.FH2Mod* owner, nint module, string name, delegate* unmanaged<nint> detour, void** slot)
    {
        if (!NativeLibrary.TryGetExport(module, name, out var target)) return;
        using var u = new Utf8Scope();
        var h = ModPlatform.Api->HookFunction(owner, u.Str("FH2 window " + name), (void*)target, detour, slot);
        if (h is null || ModPlatform.Api->EnableHooks(owner, h) == 0)
        {
            Log($"GameWindow.KeepRunningUnfocused: couldn't hook {name}");
            *slot = null;
        }
    }

    [UnmanagedCallersOnly] private static nint OnForeground() => s_keepRunning && s_hwnd != 0 ? s_hwnd : ((delegate* unmanaged<nint>)*s_foregroundSlot)();
    [UnmanagedCallersOnly] private static nint OnActive() => s_keepRunning && s_hwnd != 0 ? s_hwnd : ((delegate* unmanaged<nint>)*s_activeSlot)();
    [UnmanagedCallersOnly] private static nint OnFocus() => s_keepRunning && s_hwnd != 0 ? s_hwnd : ((delegate* unmanaged<nint>)*s_focusSlot)();

    // ---- The pad ------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct GamepadReading   // Windows.Gaming.Input.GamepadReading
    {
        public ulong Timestamp;
        public uint Buttons;
        public double LeftTrigger, RightTrigger, LeftX, LeftY, RightX, RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    private static void** s_readingSlot;
    private static delegate* unmanaged<uint, XInputState*, uint> s_xinputGetState;

    // IGamepad::GetCurrentReading is found through the Gamepad statics (it needs one pad connected; the code is shared).
    private static void PadHookLoop()
    {
        if (!NativeLibrary.TryLoad("xinput1_4.dll", out var xi) && !NativeLibrary.TryLoad("xinput9_1_0.dll", out xi)) return;
        if (!NativeLibrary.TryGetExport(xi, "XInputGetState", out var get)) return;
        s_xinputGetState = (delegate* unmanaged<uint, XInputState*, uint>)get;
        var iid = new Guid(0x8BBCE529, 0xD49C, 0x39E9, 0x95, 0x60, 0xE4, 0x7D, 0xDE, 0x96, 0xB7, 0xC8);   // IGamepadStatics
        const string name = "Windows.Gaming.Input.Gamepad";
        for (int tries = 0; tries < 600; tries++, Thread.Sleep(1000))
        {
            nint hs = 0;
            fixed (char* n = name)
                if (WindowsCreateString(n, (uint)name.Length, &hs) != 0) return;
            try
            {
                void* statics = null;
                if (RoGetActivationFactory(hs, &iid, &statics) != 0 || statics is null) continue;
                void* vec = null, pad = null;
                uint size = 0;
                var vt = *(void***)statics;
                if (((delegate* unmanaged<void*, void**, int>)vt[10])(statics, &vec) == 0 && vec is not null)   // get_Gamepads
                {
                    var vv = *(void***)vec;
                    ((delegate* unmanaged<void*, uint*, int>)vv[7])(vec, &size);                             // get_Size
                    if (size > 0) ((delegate* unmanaged<void*, uint, void**, int>)vv[6])(vec, 0, &pad);      // GetAt
                }
                if (pad is null) continue;
                var target = (*(void***)pad)[8];   // IGamepad::GetCurrentReading
                s_readingSlot = NewSlot();
                using var u = new Utf8Scope();
                var owner = ModPlatform.GameOwner;
                var h = ModPlatform.Api->HookFunction(owner, u.Str("FH2 window: pad reading"), target,
                    (delegate* unmanaged<void*, GamepadReading*, int>)&OnReading, s_readingSlot);
                if (h is null || ModPlatform.Api->EnableHooks(owner, h) == 0) Log("GameWindow.KeepRunningUnfocused: couldn't hook the pad's readings (the pad works only in front)");
                return;
            }
            finally { WindowsDeleteString(hs); }
        }
    }

    [UnmanagedCallersOnly]
    private static int OnReading(void* self, GamepadReading* r)
    {
        int hr = ((delegate* unmanaged<void*, GamepadReading*, int>)*s_readingSlot)(self, r);
        if (!s_keepRunning || r is null || s_hwnd == 0 || RealForeground() == s_hwnd || s_xinputGetState is null) return hr;
        for (uint i = 0; i < 4; i++)
        {
            XInputState st;
            if (s_xinputGetState(i, &st) != 0) continue;
            r->Buttons = MapButtons(st.Buttons);
            r->LeftTrigger = st.LeftTrigger / 255.0;
            r->RightTrigger = st.RightTrigger / 255.0;
            r->LeftX = Axis(st.LeftX);
            r->LeftY = Axis(st.LeftY);
            r->RightX = Axis(st.RightX);
            r->RightY = Axis(st.RightY);
            return 0;
        }
        return hr;
    }

    private static double Axis(short v) => v < 0 ? v / 32768.0 : v / 32767.0;

    // XInput's button bits, in GamepadButtons' order (Menu, View, A, B, X, Y, the d-pad, the shoulders, the sticks).
    private static uint MapButtons(ushort b)
    {
        ReadOnlySpan<ushort> x = [0x0010, 0x0020, 0x1000, 0x2000, 0x4000, 0x8000, 0x0001, 0x0002, 0x0004, 0x0008, 0x0100, 0x0200, 0x0040, 0x0080];
        uint r = 0;
        for (int i = 0; i < x.Length; i++)
            if ((b & x[i]) != 0) r |= 1u << i;
        return r;
    }

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint activatableClassId, Guid* iid, void** factory);
    [DllImport("combase.dll")] private static extern int WindowsCreateString(char* source, uint length, nint* hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
}
