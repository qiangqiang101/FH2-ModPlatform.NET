using System.Runtime.InteropServices;

namespace FH2ModPlatform;

/// <summary>
/// A hook whose detour is a delegate, with the code it replaced as the same
/// delegate type. For VB.NET (which has no function pointers), and C# code that
/// would rather not use them.
/// </summary>
/// <remarks>
/// Declare the delegate with the game function's signature (pointers as IntPtr).
/// x64 Windows has one calling convention, so no attributes are needed.
/// A delegate costs a little more per call than an [UnmanagedCallersOnly]
/// function pointer: fine for most hooks, not for code that runs thousands of
/// times a frame.
/// </remarks>
public sealed class Hook<T> where T : Delegate
{
    private readonly T _detour;   // kept alive: the game holds a pointer to it
    private T? _original;

    internal Hook(Hook hook, T detour)
    {
        Raw = hook;
        _detour = detour;
    }

    /// <summary>The underlying hook.</summary>
    public Hook Raw { get; }

    /// <summary>The code your detour replaced: call it to carry on as the game would.</summary>
    public T Original => _original ??= Marshal.GetDelegateForFunctionPointer<T>(Raw.Original);

    public bool Enable() => Raw.Enable();
    public bool Disable() => Raw.Disable();
}

public abstract unsafe partial class Mod
{
    /// <summary>
    /// An inline hook at <paramref name="target"/> whose detour is a delegate (VB.NET: <c>AddressOf MyDetour</c>
    /// in a <c>New MyDelegate(...)</c>). Null if refused (the log says why). Starts switched off.
    /// </summary>
    public Hook<T>? HookFunction<T>(string? name, nint target, T detour) where T : Delegate
    {
        var h = HookFunction(name, target, Marshal.GetFunctionPointerForDelegate(detour));
        if (h is null) return null;
        Root(detour);
        return new Hook<T>(h, detour);
    }

    /// <summary>The same for one CALL rel32 or JMP rel32 at <paramref name="callSite"/>.</summary>
    public Hook<T>? HookCall<T>(string? name, nint callSite, T detour) where T : Delegate
    {
        var h = HookCall(name, callSite, Marshal.GetFunctionPointerForDelegate(detour));
        if (h is null) return null;
        Root(detour);
        return new Hook<T>(h, detour);
    }

    /// <summary>The same for a function pointer in memory, like a vtable slot.</summary>
    public Hook<T>? HookPointer<T>(string? name, nint pointerSlot, T detour) where T : Delegate
    {
        var h = HookPointer(name, pointerSlot, Marshal.GetFunctionPointerForDelegate(detour));
        if (h is null) return null;
        Root(detour);
        return new Hook<T>(h, detour);
    }

    /// <summary><see cref="SetFile(string, ReadOnlySpan{byte})"/> for languages without spans.</summary>
    public bool SetFile(string mediaPath, byte[] data) => SetFile(mediaPath, (ReadOnlySpan<byte>)data);

    /// <summary><see cref="PatchBytes(string?, nint, ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> for languages without spans.</summary>
    public Hook? PatchBytes(string? name, nint at, byte[] bytes, byte[]? expect = null) =>
        PatchBytes(name, at, (ReadOnlySpan<byte>)bytes, expect is null ? default : (ReadOnlySpan<byte>)expect);
}

/// <summary>
/// Reading the game's memory and checking its code, without pointers. Addresses
/// are absolute: the game's base (<see cref="ModPlatform.WaitForCode"/>) plus an offset.
/// </summary>
public static partial class GameCode
{
    /// <summary>
    /// True if the bytes at <paramref name="address"/> are <paramref name="expected"/>. Check a
    /// function's first bytes before hooking or calling it, so a different game build is refused
    /// instead of crashing.
    /// </summary>
    public static bool Matches(nint address, byte[] expected)
    {
        if (address == 0) return false;
        for (int i = 0; i < expected.Length; i++)
            if (Marshal.ReadByte(address, i) != expected[i]) return false;
        return true;
    }

    /// <summary>"4C 8B DC 57" into bytes, for <see cref="Matches"/>.</summary>
    public static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "").Replace("-", ""));

    public static nint ReadPointer(nint address, int offset = 0) => Marshal.ReadIntPtr(address, offset);
    public static int ReadInt32(nint address, int offset = 0) => Marshal.ReadInt32(address, offset);

    /// <summary>Entry <paramref name="slot"/> (0, 1, 2...) of the vtable of the object at <paramref name="obj"/>.</summary>
    public static nint VirtualFunction(nint obj, int slot) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);

    /// <summary>A callable delegate for game code at <paramref name="address"/>.</summary>
    public static T Function<T>(nint address) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(address);
}
