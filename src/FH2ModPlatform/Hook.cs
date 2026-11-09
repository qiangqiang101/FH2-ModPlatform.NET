using System.Runtime.InteropServices;
using FH2ModPlatform.Native;

namespace FH2ModPlatform;

/// <summary>
/// A hook made through the platform. It starts switched off: call <see cref="Enable"/>.
/// </summary>
public sealed unsafe class Hook
{
    private readonly Mod _mod;
    private readonly void** _original;

    internal Hook(Mod mod, FH2Hook* handle, string? name, void** originalSlot)
    {
        _mod = mod;
        Handle = handle;
        Name = name;
        _original = originalSlot;
    }

    public FH2Hook* Handle { get; }
    public string? Name { get; }

    /// <summary>
    /// The code your detour replaced (and any other mod's detour behind yours). Call it from the detour:
    /// <c>((delegate* unmanaged&lt;nint, int, int&gt;)hook.Original)(a, b)</c>. 0 for patches and instruction hooks.
    /// </summary>
    public nint Original => _original is null ? 0 : (nint)Volatile.Read(ref *(nint*)_original);

    /// <summary>The address of the slot holding <see cref="Original"/>; it stays valid for the life of the process.</summary>
    public void** OriginalSlot => _original;

    public bool Enable() => _mod.EnableHooks(this);
    public bool Disable() => _mod.DisableHooks(this);

    /// <summary>A pointer-sized slot that never moves or gets freed, for the platform to write <c>*original</c> into.</summary>
    internal static void** NewSlot() => (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
}

/// <summary>Called at an instruction hook with the CPU's registers, on the game's thread. Keep it short.</summary>
public delegate void InstructionHandler(ref FH2Context context);
