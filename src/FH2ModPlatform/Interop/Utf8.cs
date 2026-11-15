using System.Runtime.InteropServices;
using System.Text;

namespace FH2ModPlatform.Interop;

/// <summary>
/// Native UTF-8 strings that live until the scope is disposed. The platform
/// copies every string it's given, so they only have to outlast the call.
/// </summary>
internal unsafe struct Utf8Scope : IDisposable
{
    private List<nint>? _blocks;

    /// <summary>A NUL-terminated UTF-8 copy of <paramref name="s"/>, or null for null.</summary>
    public byte* Str(string? s)
    {
        if (s is null) return null;
        // Text from Mod.Tr goes back as the platform's own pointer: that's how the platform knows which
        // translation it is, and puts it in the game's language each time it writes a page.
        if (TranslatedText.TryGet(s, out var translated)) return translated;
        int n = Encoding.UTF8.GetByteCount(s);
        var p = (byte*)Alloc((nuint)n + 1);
        Encoding.UTF8.GetBytes(s, new Span<byte>(p, n));
        p[n] = 0;
        return p;
    }

    /// <summary>A <c>const char* const*</c> array of UTF-8 strings, or null for null.</summary>
    public byte** StrArray(IReadOnlyList<string>? items)
    {
        if (items is null) return null;
        var arr = (byte**)Alloc((nuint)(sizeof(byte*) * Math.Max(items.Count, 1)));
        for (int i = 0; i < items.Count; i++) arr[i] = Str(items[i]);
        return arr;
    }

    public void* Alloc(nuint size)
    {
        void* p = NativeMemory.Alloc(size);
        (_blocks ??= new()).Add((nint)p);
        return p;
    }

    public void Dispose()
    {
        if (_blocks is null) return;
        foreach (var p in _blocks) NativeMemory.Free((void*)p);
        _blocks = null;
    }
}

/// <summary>Every text <c>tr</c> and <c>tr_plural</c> handed out, by its text: valid for the session.</summary>
internal static unsafe class TranslatedText
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, nint> s_texts = new(StringComparer.Ordinal);

    public static string Remember(byte* p)
    {
        var s = NativeString.FromUtf8(p) ?? "";
        if (p is not null) s_texts.TryAdd(s, (nint)p);
        return s;
    }

    public static bool TryGet(string s, out byte* p)
    {
        bool found = s_texts.TryGetValue(s, out var v);
        p = (byte*)v;
        return found;
    }
}

internal static unsafe class NativeString
{
    public static string? FromUtf8(byte* p) => p is null ? null : Marshal.PtrToStringUTF8((nint)p);
    public static string? FromUtf16(char* p) => p is null ? null : new string(p);

    /// <summary>Writes <paramref name="s"/> into a fixed buffer as UTF-8, cut to fit without splitting a character.</summary>
    public static void WriteUtf8(string s, byte* buffer, nuint size)
    {
        if (buffer is null || size == 0) return;
        var dest = new Span<byte>(buffer, (int)Math.Min(size - 1, int.MaxValue));
        // Stops at the last whole character that fits (DestinationTooSmall), never throws.
        System.Text.Unicode.Utf8.FromUtf16(s, dest, out _, out int written);
        buffer[written] = 0;
    }
}
