using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>What a native mod's controller reads get added (<see cref="Controller.FeedNativeMod"/>).</summary>
public readonly record struct PadFeed(ushort Buttons, byte LeftTrigger = 0, byte RightTrigger = 0);

public static unsafe partial class Controller
{
    private static int s_simulated;

    /// <summary>
    /// Holds a button for other .NET mods (what <see cref="IsPressed"/> says), with or without a controller: a
    /// keyboard mod pressing another mod's button. The game doesn't see it.
    /// </summary>
    public static void Simulate(Button button, bool held)
    {
        int bit = 1 << (int)button, cur;
        do cur = Volatile.Read(ref s_simulated);
        while (Interlocked.CompareExchange(ref s_simulated, held ? cur | bit : cur & ~bit, cur) != cur);
    }

    /// <summary>Lets go of every simulated button.</summary>
    public static void StopSimulating() => Volatile.Write(ref s_simulated, 0);

    // ---- A native mod's own controller reads -----------------------------------------------------------

    private const int MaxFeeds = 4;
    private static readonly Func<PadFeed>?[] s_feeds = new Func<PadFeed>?[MaxFeeds];
    private static readonly nint[] s_feedOriginal = new nint[MaxFeeds];
    private static int s_feedCount;

    /// <summary>
    /// Adds to what a native mod in fh2-mods (<paramref name="dll"/>, "FnFNitro.dll") reads from the controller with
    /// its own XInputGetState: controller 0 has <paramref name="feed"/>'s buttons and triggers too (with no
    /// controller there, it's one). Only that mod sees them, not the game. False if the mod isn't loaded or doesn't
    /// read the controller so.
    /// </summary>
    public static bool FeedNativeMod(string dll, Func<PadFeed> feed)
    {
        var module = GetModuleHandleW(dll);
        if (module == 0) return false;
        var slot = XInputSlot(module);
        if (slot == null) return false;
        int i = Interlocked.Increment(ref s_feedCount) - 1;
        if (i >= MaxFeeds) return false;
        s_feeds[i] = feed;
        var detour = i switch
        {
            0 => (nint)(delegate* unmanaged<uint, byte*, uint>)&Feed0,
            1 => (nint)(delegate* unmanaged<uint, byte*, uint>)&Feed1,
            2 => (nint)(delegate* unmanaged<uint, byte*, uint>)&Feed2,
            _ => (nint)(delegate* unmanaged<uint, byte*, uint>)&Feed3,
        };
        var original = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        var hook = ModPlatform.Api->HookPointer(ModPlatform.GameOwner, u.Str(dll + ": XInputGetState"), slot, (void*)detour, original);
        if (hook is null || ModPlatform.Api->EnableHooks(ModPlatform.GameOwner, hook) == 0) return false;
        s_feedOriginal[i] = (nint)(*original);
        return true;
    }

    [UnmanagedCallersOnly] private static uint Feed0(uint port, byte* state) => Fed(0, port, state);
    [UnmanagedCallersOnly] private static uint Feed1(uint port, byte* state) => Fed(1, port, state);
    [UnmanagedCallersOnly] private static uint Feed2(uint port, byte* state) => Fed(2, port, state);
    [UnmanagedCallersOnly] private static uint Feed3(uint port, byte* state) => Fed(3, port, state);

    // XINPUT_STATE: dwPacketNumber, then the gamepad: wButtons (+4), bLeftTrigger (+6), bRightTrigger (+7), sticks.
    private static uint Fed(int i, uint port, byte* state)
    {
        uint r = ((delegate* unmanaged<uint, byte*, uint>)s_feedOriginal[i])(port, state);
        if (port != 0 || state == null || s_feeds[i] is not { } feed) return r;
        PadFeed f;
        try { f = feed(); }
        catch { return r; }
        if (f.Buttons == 0 && f.LeftTrigger == 0 && f.RightTrigger == 0) return r;
        if (r != 0)
        {
            new Span<byte>(state, 16).Clear();
            r = 0;
        }
        *(ushort*)(state + 4) |= f.Buttons;
        state[6] = Math.Max(state[6], f.LeftTrigger);
        state[7] = Math.Max(state[7], f.RightTrigger);
        return r;
    }

    // A module's import of XInputGetState (by name, or xinput's ordinal 2), or null.
    private static void** XInputSlot(nint m)
    {
        var nt = m + *(int*)(m + 0x3C);
        int dir = *(int*)(nt + 0x18 + 0x70 + 8);   // OptionalHeader.DataDirectory[IMPORT].VirtualAddress (PE32+)
        if (dir == 0) return null;
        for (var d = m + dir; *(int*)(d + 12) != 0; d += 20)
        {
            var name = Marshal.PtrToStringAnsi(m + *(int*)(d + 12)) ?? "";
            if (!name.StartsWith("xinput", StringComparison.OrdinalIgnoreCase)) continue;
            int originalThunk = *(int*)d, firstThunk = *(int*)(d + 16);
            var names = (ulong*)(m + (originalThunk != 0 ? originalThunk : firstThunk));
            var slots = (nint*)(m + firstThunk);
            for (; *names != 0; names++, slots++)
            {
                bool byOrdinal = (*names & 0x8000000000000000) != 0;
                if ((byOrdinal && (*names & 0xFFFF) == 2) ||
                    (!byOrdinal && Marshal.PtrToStringAnsi(m + (nint)(*names & 0x7FFFFFFF) + 2) == "XInputGetState"))
                    return (void**)slots;
            }
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);
}
