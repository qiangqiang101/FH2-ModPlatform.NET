using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using FH2ModPlatform;
using FH2ModPlatform.Interop;

namespace FH2;

/// <summary>
/// The game's database and string lookups while it runs: rewrite the SQL it sends to gamedb, and answer string keys
/// no string table has. With <c>Mod.AddSql</c> (new rows) these make a custom upgrade part, as FnFNitro's NOS kit and
/// FH2 Underglow's kit are made.
/// </summary>
/// <remarks>
/// Every query the game runs goes through your rewriter, often many a frame in the garage: keep it fast, and give back
/// null for the ones you leave alone. The hooks go in the first time you add a handler.
/// </remarks>
public static unsafe class GameDb
{
    private static readonly object s_lock = new();
    private static readonly List<Func<string, string?>> s_rewriters = new();
    private static readonly List<Func<int, int, string?>> s_answers = new();
    private static volatile int s_rewriterCount, s_answerCount;
    private static int s_queryState, s_lookupState;   // 0 not yet, 1 in
    private static void** s_querySlot, s_lookupSlot;
    private static readonly ConcurrentDictionary<string, nint> s_texts = new();   // UTF-16 answers, kept for the session

    /// <summary>
    /// Adds a rewriter for the SQL the game runs on gamedb: it gets each statement and gives back the one to run instead,
    /// or null to leave it as it is. Runs on whichever thread asked (often the game's). Any thread.
    /// </summary>
    public static void AddQueryRewriter(Func<string, string?> rewriter)
    {
        lock (s_lock)
        {
            s_rewriters.Add(rewriter);
            s_rewriterCount = s_rewriters.Count;
        }
        if (Interlocked.Exchange(ref s_queryState, 1) == 0) new Thread(InstallQuery) { IsBackground = true, Name = "FH2 database query" }.Start();
    }

    public static void RemoveQueryRewriter(Func<string, string?> rewriter)
    {
        lock (s_lock)
        {
            s_rewriters.Remove(rewriter);
            s_rewriterCount = s_rewriters.Count;
        }
    }

    /// <summary>
    /// Adds an answer for string keys the game looks up and no table has: it gets the table's number (as the game numbers
    /// them while it runs) and the key, and gives back the text, or null. Only asked when the game found nothing, so your
    /// keys should be ones the game doesn't use. Any thread.
    /// </summary>
    public static void AddStringAnswer(Func<int, int, string?> answer)
    {
        lock (s_lock)
        {
            s_answers.Add(answer);
            s_answerCount = s_answers.Count;
        }
        if (Interlocked.Exchange(ref s_lookupState, 1) == 0) new Thread(InstallLookup) { IsBackground = true, Name = "FH2 string lookup" }.Start();
    }

    public static void RemoveStringAnswer(Func<int, int, string?> answer)
    {
        lock (s_lock)
        {
            s_answers.Remove(answer);
            s_answerCount = s_answers.Count;
        }
    }

    // ---- The query: the database object's (game+0x34B3640) virtual slot 8, (self, out, const char* sql) ----

    private static void InstallQuery()
    {
        var owner = ModPlatform.GameOwner;
        var b = ModPlatform.WaitForCode();
        if (b == 0) return;
        nint target = 0;
        for (int tries = 0; tries < 1200 && target == 0; tries++)   // the database object comes a moment after the start
        {
            if (GameCode.TryRead<nint>(b + Addresses.DatabaseGlobal, out var db) && db != 0 &&
                GameCode.TryRead<nint>(db, out var vt) && GameCode.TryRead<nint>(vt + Addresses.DatabaseQuerySlot * 8, out var fn) &&
                fn > b + 0x1000 && fn < b + 0x1DCC000)
                target = fn;
            else Thread.Sleep(500);
        }
        if (target == 0)
        {
            Game.Log(owner, "GameDb query rewriting off: the game's database wasn't found");
            return;
        }
        s_querySlot = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        var h = ModPlatform.Api->HookFunction(owner, u.Str("FH2 database query (GameDb)"), (void*)target,
            (delegate* unmanaged<nint, nint, byte*, nint>)&OnQuery, s_querySlot);
        if (h is null || ModPlatform.Api->EnableHooks(owner, h) == 0) Game.Log(owner, "GameDb query rewriting off: couldn't hook the query");
    }

    [ThreadStatic] private static byte[]? t_sqlBuffer;

    [UnmanagedCallersOnly]
    private static nint OnQuery(nint self, nint output, byte* sql)
    {
        var original = (delegate* unmanaged<nint, nint, byte*, nint>)*s_querySlot;
        if (s_rewriterCount == 0 || sql is null) return original(self, output, sql);
        string? rewritten = null;
        try
        {
            var text = NativeString.FromUtf8(sql) ?? "";
            Func<string, string?>[] rs;
            lock (s_lock) rs = s_rewriters.ToArray();
            foreach (var r in rs)
            {
                var next = r(rewritten ?? text);
                if (next is not null) rewritten = next;
            }
        }
        catch (Exception e)
        {
            Game.Log(ModPlatform.GameOwner, "GameDb query rewriter: " + e.Message);
            rewritten = null;
        }
        if (rewritten is null) return original(self, output, sql);
        // The statement only has to last the call: a buffer of the thread's own, grown as needed.
        int n = Encoding.UTF8.GetByteCount(rewritten);
        var buf = t_sqlBuffer;
        if (buf is null || buf.Length < n + 1) t_sqlBuffer = buf = new byte[Math.Max(n + 1, 4096)];
        Encoding.UTF8.GetBytes(rewritten, buf);
        buf[n] = 0;
        fixed (byte* p = buf) return original(self, output, p);
    }

    // ---- The string lookup: (manager, table, key) -> const wchar_t* ----

    private static void InstallLookup()
    {
        var owner = ModPlatform.GameOwner;
        var b = ModPlatform.WaitForCode();
        if (b == 0) return;
        if (Game.Mismatch(b, [Addresses.StringLookup]) is { } bad)
        {
            Game.Log(owner, $"GameDb string answers off: the game's code isn't as expected at {bad}");
            return;
        }
        s_lookupSlot = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        var h = ModPlatform.Api->HookFunction(owner, u.Str("FH2 string lookup (GameDb)"), (void*)(b + Addresses.StringLookup.Offset),
            (delegate* unmanaged<nint, uint, uint, char*>)&OnLookup, s_lookupSlot);
        if (h is null || ModPlatform.Api->EnableHooks(owner, h) == 0) Game.Log(owner, "GameDb string answers off: couldn't hook the lookup");
    }

    [UnmanagedCallersOnly]
    private static char* OnLookup(nint manager, uint table, uint key)
    {
        var r = ((delegate* unmanaged<nint, uint, uint, char*>)*s_lookupSlot)(manager, table, key);
        if (s_answerCount == 0 || (r is not null && r[0] != 0)) return r;
        try
        {
            Func<int, int, string?>[] answers;
            lock (s_lock) answers = s_answers.ToArray();
            foreach (var a in answers)
                if (a((int)table, (int)(key & 0xFFFF)) is { Length: > 0 } text)
                    return (char*)s_texts.GetOrAdd(text, t =>
                    {
                        var p = (char*)NativeMemory.Alloc((nuint)((t.Length + 1) * 2));
                        t.AsSpan().CopyTo(new Span<char>(p, t.Length));
                        p[t.Length] = '\0';
                        return (nint)p;
                    });
        }
        catch (Exception e) { Game.Log(ModPlatform.GameOwner, "GameDb string answer: " + e.Message); }
        return r;
    }

    internal static void Forget(System.Reflection.Assembly a)
    {
        lock (s_lock)
        {
            s_rewriters.RemoveAll(h => h.Method.DeclaringType?.Assembly == a);
            s_answers.RemoveAll(h => h.Method.DeclaringType?.Assembly == a);
            s_rewriterCount = s_rewriters.Count;
            s_answerCount = s_answers.Count;
        }
    }
}
