using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;

namespace FH2;

/// <summary>
/// The game's car radio: what's playing, and changing the station as the D-pad does (ANNA-DroneMode's findings).
/// </summary>
public static unsafe class Radio
{
    private static int s_state;   // 0 not yet, 1 hooked, -1 off
    private static void** s_eventSlot;
    private static volatile string s_station = "", s_song = "", s_artist = "";
    private static readonly object s_lock = new();
    private static readonly List<Action> s_changed = new();

    /// <summary>The station playing, as the radio's banner names it ("Horizon Bass Arena"), or "" while it's off or not known yet. Any thread.</summary>
    public static string Station { get { Start(); return s_station; } }

    /// <summary>The song playing, or "". Any thread.</summary>
    public static string Song { get { Start(); return s_song; } }

    /// <summary>Who it's by, or "". Any thread.</summary>
    public static string Artist { get { Start(); return s_artist; } }

    /// <summary>The station, the song or the artist changed. On the game's thread; keep it short.</summary>
    public static event Action Changed
    {
        add { Start(); lock (s_lock) s_changed.Add(value); }
        remove { lock (s_lock) s_changed.Remove(value); }
    }

    /// <summary>The next station, as the game's D-pad right does. Game thread only.</summary>
    public static bool Next() => Change(true);

    /// <summary>The station before, as the game's D-pad left does. Game thread only.</summary>
    public static bool Previous() => Change(false);

    private static bool Change(bool next)
    {
        Game.RequireGameThread("Radio.Next / Previous");
        var b = Game.Base;
        if (Game.Mismatch(b, [Addresses.GetRadioUi, Addresses.ChangeStation]) is not null) return false;
        var uis = *(nint*)(b + Addresses.RadioUisGlobal);
        if (uis == 0) return false;
        var radio = stackalloc nint[2];   // a shared_ptr: the UI, its count
        radio[0] = radio[1] = 0;
        ((delegate* unmanaged<nint, nint*, nint>)(b + Addresses.GetRadioUi.Offset))(uis, radio);
        if (radio[0] != 0) ((delegate* unmanaged<nint, int, ulong>)(b + Addresses.ChangeStation.Offset))(radio[0], next ? 1 : 0);
        World.ReleaseShared(radio[1]);
        return radio[0] != 0;
    }

    private static void Start()
    {
        if (Interlocked.CompareExchange(ref s_state, 1, 0) != 0) return;
        new Thread(() =>
        {
            var owner = ModPlatform.GameOwner;
            var b = ModPlatform.WaitForCode();
            if (b == 0) return;
            if (Game.Mismatch(b, [Addresses.HudRadioEvent]) is { } bad)
            {
                s_state = -1;
                Game.Log(owner, $"Radio.Station / Song off: the game's code isn't as expected at {bad}");
                return;
            }
            s_eventSlot = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
            using var u = new Utf8Scope();
            var h = ModPlatform.Api->HookFunction(owner, u.Str("FH2 radio banner"), (void*)(b + Addresses.HudRadioEvent.Offset),
                (delegate* unmanaged<nint, int, nint, nint, void>)&OnEvent, s_eventSlot);
            if (h is null || ModPlatform.Api->EnableHooks(owner, h) == 0)
            {
                s_state = -1;
                Game.Log(owner, "Radio.Station / Song off: couldn't hook the radio's banner");
            }
        }) { IsBackground = true, Name = "FH2 radio" }.Start();
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(nint manager, int evt, nint code, nint payload)
    {
        ((delegate* unmanaged<nint, int, nint, nint, void>)*s_eventSlot)(manager, evt, code, payload);
        if (evt != 0x10 || manager == 0) return;
        var station = GameCode.ReadStdString(manager + 0x48);
        var song = GameCode.ReadStdString(manager + 0x08);
        var artist = GameCode.ReadStdString(manager + 0x28);
        if (station == s_station && song == s_song && artist == s_artist) return;
        s_station = station;
        s_song = song;
        s_artist = artist;
        Action[] hs;
        lock (s_lock)
        {
            if (s_changed.Count == 0) return;
            hs = s_changed.ToArray();
        }
        foreach (var h in hs)
            try { h(); }
            catch (Exception e) { Game.Log(ModPlatform.GameOwner, "Radio.Changed: " + e.Message); }
    }

    internal static void Forget(System.Reflection.Assembly a)
    {
        lock (s_lock) s_changed.RemoveAll(h => h.Method.DeclaringType?.Assembly == a);
    }
}
