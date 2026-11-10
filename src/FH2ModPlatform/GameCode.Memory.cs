using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace FH2ModPlatform;

// Reads that can't crash, the game's own std::string, and code checks that allow for another mod's hook.
public static unsafe partial class GameCode
{
    // ---- Reads that can't crash ------------------------------------------------------------------
    // .NET can't catch an access violation: reading a pointer that's gone ends the game. Game data that may
    // be freed under you (a streamed-out area, a car that's gone) is read with these instead.

    /// <summary>
    /// True if <paramref name="size"/> bytes at <paramref name="address"/> are mapped and readable right now.
    /// Costs a system call: for checking a pointer before a walk, not for every read in a hot loop.
    /// </summary>
    public static bool IsReadable(nint address, int size = 8)
    {
        if (address < 0x10000 || size <= 0) return false;
        nint at = address, end = address + size;
        while (at < end)
        {
            MemoryInfo mbi;
            if (VirtualQuery(at, &mbi, (nuint)sizeof(MemoryInfo)) == 0 || mbi.State != MemCommit) return false;
            if ((mbi.Protect & (PageNoAccess | PageGuard)) != 0 || (mbi.Protect & ReadableProtect) == 0) return false;
            at = mbi.BaseAddress + (nint)mbi.RegionSize;
        }
        return true;
    }

    /// <summary>Copies game memory into <paramref name="into"/>; false (and nothing read) if any of it isn't readable.</summary>
    public static bool TryRead(nint address, Span<byte> into)
    {
        if (address < 0x10000) return false;
        if (into.IsEmpty) return true;
        fixed (byte* p = into)
        {
            nuint got;
            return ReadProcessMemory(GetCurrentProcess(), address, p, (nuint)into.Length, &got) != 0 && got == (nuint)into.Length;
        }
    }

    /// <summary><see cref="TryRead(nint, Span{byte})"/> for languages without spans.</summary>
    public static bool TryRead(nint address, byte[] into) => TryRead(address, into.AsSpan());

    /// <summary>A value of type <typeparamref name="T"/> at <paramref name="address"/>, if it's readable.</summary>
    public static bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        T v = default;
        bool ok = TryRead(address, new Span<byte>(&v, sizeof(T)));
        value = ok ? v : default;
        return ok;
    }

    /// <summary>The value at <paramref name="address"/>, or the default (0, null) if it isn't readable.</summary>
    public static T ReadSafe<T>(nint address) where T : unmanaged => TryRead<T>(address, out var v) ? v : default;

    /// <summary>A pointer at <paramref name="address"/> + <paramref name="offset"/>, or 0 if it isn't readable.</summary>
    public static nint ReadPointerSafe(nint address, int offset = 0) => ReadSafe<nint>(address + offset);

    /// <summary>An Int32 at <paramref name="address"/> + <paramref name="offset"/>, or 0 if it isn't readable.</summary>
    public static int ReadInt32Safe(nint address, int offset = 0) => ReadSafe<int>(address + offset);

    /// <summary>A Single at <paramref name="address"/> + <paramref name="offset"/>, or 0 if it isn't readable.</summary>
    public static float ReadSingleSafe(nint address, int offset = 0) => ReadSafe<float>(address + offset);

    // ---- The game's std::string ------------------------------------------------------------------

    /// <summary>
    /// The text of a <c>std::string</c> in the game's data (Visual C++ 2012's: 0x20 bytes, the text inside it
    /// up to 15 characters, else a pointer to it). "" if it isn't readable.
    /// </summary>
    public static string ReadStdString(nint address)
    {
        if (!TryRead<StdString>(address, out var s) || s.Size == 0 || s.Size > 0x10000) return "";
        if (s.Capacity < 16) return Encoding.UTF8.GetString(s.Inline, (int)Math.Min(s.Size, 15));
        var bytes = new byte[(int)s.Size];
        return TryRead(s.Pointer, bytes) ? Encoding.UTF8.GetString(bytes) : "";
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    private struct StdString
    {
        [FieldOffset(0)] public fixed byte Inline[16];
        [FieldOffset(0)] public nint Pointer;
        [FieldOffset(16)] public ulong Size;
        [FieldOffset(24)] public ulong Capacity;
    }

    // ---- Is this still the build I was written for? -----------------------------------------------
    // Another mod hooking the same function through the platform puts a jump over its first bytes, and the
    // platform chains both hooks, so memory alone can't tell the build any more. The game's exe on disk can:
    // the platform compares with it too, and refuses to hook code that differs from it.

    /// <summary>The 24 November 2015 build's image size.</summary>
    private const uint KnownImageSize = 0x3A18000;

    /// <summary>
    /// Like <see cref="Matches"/>, but still true where another mod has hooked that code: then the bytes are read
    /// from the game's exe on disk (or, if it can't be read, a jump out of the game followed by the expected bytes
    /// is taken as the platform's hook). Use it before hooking code other mods may hook as well.
    /// </summary>
    public static bool MatchesBuild(nint address, byte[] expected)
    {
        if (address == 0 || expected.Length == 0 || !IsReadable(address, expected.Length)) return false;
        var live = new ReadOnlySpan<byte>((void*)address, expected.Length);
        if (live.SequenceEqual(expected)) return true;
        Span<byte> disk = stackalloc byte[expected.Length];
        if (ShippedBytes(address, disk)) return disk.SequenceEqual(expected);
        if (expected.Length <= 5 || live[0] != 0xE9) return false;   // jmp rel32
        nint b = ModPlatform.GameBase, to = address + 5 + *(int*)(address + 1);
        if (to >= b && to < b + (nint)KnownImageSize) return false;  // a jump inside the game: other code
        for (int i = 5; i < expected.Length; i++)
            if (live[i] != expected[i] && live[i] != 0xCC) return false;   // an instruction hook pads with int3
        return true;
    }

    /// <summary>
    /// True if the function pointer at <paramref name="slot"/> (a vtable entry) is <paramref name="function"/>, or
    /// another mod has hooked it and the exe on disk has <paramref name="function"/> there. Both are absolute addresses.
    /// </summary>
    public static bool SlotMatchesBuild(nint slot, nint function)
    {
        if (!TryRead<nint>(slot, out var live)) return false;
        if (live == function) return true;
        nint b = ModPlatform.GameBase;
        Span<byte> disk = stackalloc byte[8];
        if (ShippedBytes(slot, disk)) return BitConverter.ToInt64(disk) - (long)s_exeImageBase == (long)(function - b);
        // without the exe: a pointer to code outside the game, the platform's
        if (live >= b && live < b + (nint)KnownImageSize) return false;
        MemoryInfo mbi;
        return VirtualQuery(live, &mbi, (nuint)sizeof(MemoryInfo)) != 0 && mbi.State == MemCommit && (mbi.Protect & ExecutableProtect) != 0;
    }

    /// <summary>
    /// The bytes at <paramref name="address"/> as the game's exe on disk has them (the code before any mod changed
    /// it). False if the exe can't be read, isn't this build, or the address isn't in it.
    /// </summary>
    public static bool ShippedBytes(nint address, Span<byte> into)
    {
        var sections = ExeSections();
        if (sections is null) return false;
        long rva = (long)address - (long)ModPlatform.GameBase;
        foreach (var (va, size, raw) in sections)
        {
            if (rva < va || rva + into.Length > (long)va + size) continue;
            try
            {
                using var f = new FileStream(s_exePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                f.Position = raw + (rva - va);
                return f.ReadAtLeast(into, into.Length, throwOnEndOfStream: false) == into.Length;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        return false;
    }

    private static readonly object s_exeLock = new();
    private static bool s_exeTried;
    private static string? s_exePath;
    private static ulong s_exeImageBase;
    private static (int Va, int Size, int Raw)[]? s_exeSections;

    private static (int Va, int Size, int Raw)[]? ExeSections()
    {
        lock (s_exeLock)
        {
            if (s_exeTried) return s_exeSections;
            s_exeTried = true;
            foreach (var dir in new[] { ModPlatform.GameDir, ModPlatform.AzimuthDir })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var path = Path.Combine(dir, "forza_durango_release_final.exe");
                try
                {
                    if (!File.Exists(path)) continue;
                    using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var pe = new PEReader(f);
                    var h = pe.PEHeaders;
                    if (h.PEHeader is null || h.PEHeader.SizeOfImage != KnownImageSize) continue;
                    s_exeSections = h.SectionHeaders.Select(s => (s.VirtualAddress, s.SizeOfRawData, s.PointerToRawData)).ToArray();
                    s_exeImageBase = h.PEHeader.ImageBase;
                    s_exePath = path;
                    return s_exeSections;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException) { }
            }
            return null;
        }
    }

    // ---- kernel32 ----------------------------------------------------------------------------------

    private const uint MemCommit = 0x1000, PageNoAccess = 0x01, PageGuard = 0x100;
    private const uint ReadableProtect = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;   // READONLY, READWRITE, WRITECOPY, EXECUTE_READ(_WRITE/_WRITECOPY)
    private const uint ExecutableProtect = 0x10 | 0x20 | 0x40 | 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")] private static extern nuint VirtualQuery(nint address, MemoryInfo* info, nuint length);
    [DllImport("kernel32.dll")] private static extern int ReadProcessMemory(nint process, nint address, void* buffer, nuint size, nuint* read);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
}
