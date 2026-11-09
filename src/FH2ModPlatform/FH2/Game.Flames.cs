using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

// Exhaust flames on demand, the way FnFNitro makes them: the game only runs its backfire routine when the
// engine sim sends a backfire (now and then), so for a car that should be burning the SDK runs that routine
// itself every 40 ms, with exhaust tips built from the car's own exhaust locators. The tips are built on the
// cars' update thread (where the game builds its own) and sent from the thread the game runs the routine on
// (its logic frame step).
public static unsafe partial class Game
{
    private const long FlameEveryMs = 40, TipsFreshMs = 250, ResolveEveryMs = 500, FlameCarGoneMs = 2000;

    // The cars that should burn: until when (long.MaxValue for "until told otherwise"), and when last seen.
    private sealed class FlameCar
    {
        public long Until, Seen, Resolved;
        public nint Presentation;
    }
    private static readonly ConcurrentDictionary<nint, FlameCar> s_flames = new();

    // The tips to send, per presentation, from the cars' update.
    private sealed class FlameTips
    {
        public readonly byte[] Tips = new byte[Addresses.BackfireTips * Addresses.BackfireTipSize];
        public long Built;
    }
    private static readonly ConcurrentDictionary<nint, FlameTips> s_flameTips = new();

    private static volatile bool s_flamesReady;
    private static void** s_backfireOriginal, s_frameOriginal;
    private static volatile uint s_backfireThread;
    private static long s_lastFlame;
    private static byte* s_message;

    private static void InstallFlames(nint b, FH2Mod* owner)
    {
        if (Mismatch(b, Addresses.Flames) is { } bad)
        {
            Log(owner, $"Vehicle.ExhaustFlames off: the game's code isn't as expected at {bad}");
            return;
        }
        s_backfireOriginal = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        s_frameOriginal = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        s_message = (byte*)NativeMemory.AlignedAlloc(Addresses.BackfireMessageSize, 16);
        using var u = new Utf8Scope();
        var api = ModPlatform.Api;
        var bf = api->HookFunction(owner, u.Str("FH2 flames (CarPresentation11::UpdateBackfire)"),
            (void*)(b + Addresses.UpdateBackfire.Offset), (delegate* unmanaged<nint, nint, nint, nint, ulong>)&OnBackfire, s_backfireOriginal);
        var frame = api->HookFunction(owner, u.Str("FH2 flames (GameLogic frame step)"),
            (void*)(b + Addresses.LogicFrame.Offset), (delegate* unmanaged<nint, nint, ulong>)&OnLogicFrame, s_frameOriginal);
        if (bf is null || frame is null || api->EnableHooks(owner, bf) == 0 || api->EnableHooks(owner, frame) == 0)
        {
            Log(owner, "Vehicle.ExhaustFlames off: couldn't hook the backfire routine");
            return;
        }
        s_flamesReady = true;
    }

    internal static bool FlamesReady => s_flamesReady;

    /// <summary>Makes the car burn until <paramref name="until"/> (TickCount64; long.MaxValue: until stopped), or stops it (0).</summary>
    internal static void SetFlames(nint car, long until)
    {
        if (until <= Environment.TickCount64)
        {
            StopFlames(car);
            return;
        }
        var f = s_flames.GetOrAdd(car, _ => new FlameCar());
        f.Until = until;
        f.Seen = Environment.TickCount64;
    }

    internal static long FlamesUntil(nint car) => s_flames.TryGetValue(car, out var f) ? f.Until : 0;

    internal static bool HasFlames(nint car) => s_flames.TryGetValue(car, out var f) && f.Until > Environment.TickCount64;

    private static void StopFlames(nint car)
    {
        if (s_flames.TryRemove(car, out var f) && f.Presentation != 0) s_flameTips.TryRemove(f.Presentation, out _);
    }

    private static void StopAllFlames()
    {
        s_flames.Clear();
        s_flameTips.Clear();
    }

    // From OnCarUpdate (the cars' update thread): a burning car's tips, from its exhaust locators.
    private static void NoteFlames(nint car)
    {
        if (!s_flamesReady || s_flames.IsEmpty) return;
        long now = Environment.TickCount64;
        foreach (var kv in s_flames)
            if (now - kv.Value.Seen > FlameCarGoneMs || kv.Value.Until <= now) StopFlames(kv.Key);
        if (!s_flames.TryGetValue(car, out var flame)) return;
        flame.Seen = now;

        var pres = Presentation(car);
        if (pres == 0) return;
        flame.Presentation = pres;
        Span<byte> tips = stackalloc byte[Addresses.BackfireTips * Addresses.BackfireTipSize];
        uint mask = ReadLocatorTips(car, tips);
        if (mask == 0 && now - flame.Resolved >= ResolveEveryMs)
        {
            // Not resolved yet (they stay empty until the car's first backfire): have the car resolve them, as
            // the game does before its own backfire message, if its method is the one expected.
            flame.Resolved = now;
            var vt = *(nint**)car;
            if ((nint)vt == s_base + Addresses.ForzaCarVtable &&
                vt[Addresses.ResolveLocatorsSlot] == s_base + Addresses.ResolveLocatorsFn)
            {
                ((delegate* unmanaged<nint, void>)vt[Addresses.ResolveLocatorsSlot])(car);
                mask = ReadLocatorTips(car, tips);
            }
        }
        if (mask == 0) return;   // the game empties them now and then: keep the last good ones until they're back
        var entry = s_flameTips.GetOrAdd(pres, _ => new FlameTips());
        lock (entry)
        {
            tips.CopyTo(entry.Tips);
            entry.Built = now;
        }
    }

    // The car's CarPresentation11, or 0.
    internal static nint Presentation(nint car)
    {
        var pres = *(nint*)(car + Addresses.CarPresentationField);
        if (pres == 0) return 0;
        if (*(nint*)pres - s_base == Addresses.DeferredPresentationVtable)
            pres = *(nint*)(pres + Addresses.DeferredPresentationInner);
        if (pres == 0) return 0;
        var vt = *(nint*)pres - s_base;
        return vt == Addresses.PresentationVtable || vt == Addresses.PresentationVtableRefCounted ? pres : 0;
    }

    // The car's live exhaust locators as the game's tips (matrix, then the flag); which of the 8 there are.
    private static uint ReadLocatorTips(nint car, Span<byte> tips)
    {
        tips.Clear();
        uint mask = 0;
        for (int i = 0; i < Addresses.BackfireTips; i++)
        {
            var slot = car + Addresses.CarExhaustLocators + 0x10 * i;
            var obj = *(nint*)slot;
            var ctrl = *(nint*)(slot + 8);
            if (obj == 0 || ctrl == 0 || Volatile.Read(ref *(int*)(ctrl + 8)) <= 0) continue;
            var vt = *(nint**)obj;
            if ((nint)vt < s_base || (nint)vt >= s_base + Addresses.ImageSize) continue;
            var m = ((delegate* unmanaged<nint, float*>)vt[2])(obj);
            if (m is null) continue;
            var tip = tips.Slice(i * Addresses.BackfireTipSize, Addresses.BackfireTipSize);
            new ReadOnlySpan<byte>(m, 0x40).CopyTo(tip);
            tip[Addresses.BackfireTipFlag] = 1;
            mask |= 1u << i;
        }
        return mask;
    }

    // The game's backfire routine: notes which thread it runs on.
    [UnmanagedCallersOnly]
    private static ulong OnBackfire(nint pres, nint message, nint c, nint d)
    {
        s_backfireThread = GetCurrentThreadId();
        return ((delegate* unmanaged<nint, nint, nint, nint, ulong>)*s_backfireOriginal)(pres, message, c, d);
    }

    // The game logic's frame step: on the backfire routine's thread, every 40 ms, a flame for each burning car.
    [UnmanagedCallersOnly]
    private static ulong OnLogicFrame(nint self, nint flag)
    {
        var r = ((delegate* unmanaged<nint, nint, ulong>)*s_frameOriginal)(self, flag);
        // The frame step is where the game's backfires come from (FnFNitro traced the call chain); once the game has
        // run one, only that thread is trusted.
        var bfThread = s_backfireThread;
        if (s_flameTips.IsEmpty || (bfThread != 0 && bfThread != GetCurrentThreadId())) return r;
        long now = Environment.TickCount64;
        if (now - s_lastFlame < FlameEveryMs) return r;
        s_lastFlame = now;
        try
        {
            var send = (delegate* unmanaged<nint, nint, nint, nint, ulong>)*s_backfireOriginal;
            var size = Addresses.BackfireTips * Addresses.BackfireTipSize;
            foreach (var kv in s_flameTips)
            {
                var entry = kv.Value;
                lock (entry)
                {
                    if (now - entry.Built > TipsFreshMs)
                    {
                        if (now - entry.Built > FlameCarGoneMs) s_flameTips.TryRemove(kv.Key, out _);
                        continue;
                    }
                    // The routine copies the tips into the presentation, spawns a flame for each flagged one, then
                    // destroys the message: no ref-counted pointers in these, so nothing to take or give back.
                    NativeMemory.Clear(s_message, Addresses.BackfireMessageSize);
                    entry.Tips.CopyTo(new Span<byte>(s_message, size));
                }
                send(kv.Key, (nint)s_message, 0, 0);
            }
        }
        catch (Exception e)
        {
            Log(ModPlatform.GameOwner, "Vehicle.ExhaustFlames: " + e.Message);
        }
        return r;
    }
}
