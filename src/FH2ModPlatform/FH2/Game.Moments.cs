using System.Reflection;
using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;

namespace FH2;

// The pause menu opening, an event starting, and clearing the rewind (from FH2 ANNA's autodrive).
public static unsafe partial class Game
{
    private static readonly List<Action> s_pauseOpened = new(), s_eventStarted = new();
    private static int s_momentHooks;   // 0 not yet, 1 in
    private static void** s_pauseSlot, s_eventSlot;

    /// <summary>The pause menu is opening. On the game's thread, as it opens; keep it short.</summary>
    public static event Action PauseOpened
    {
        add { InstallMoments(); lock (s_lock) s_pauseOpened.Add(value); }
        remove { lock (s_lock) s_pauseOpened.Remove(value); }
    }

    /// <summary>An event (a race, a showcase, a bucket list...) is starting. On the game's thread; keep it short.</summary>
    public static event Action EventStarted
    {
        add { InstallMoments(); lock (s_lock) s_eventStarted.Add(value); }
        remove { lock (s_lock) s_eventStarted.Remove(value); }
    }

    internal static void InstallMoments()
    {
        if (Interlocked.Exchange(ref s_momentHooks, 1) != 0) return;
        new Thread(() =>
        {
            var b = ModPlatform.WaitForCode();
            var owner = ModPlatform.GameOwner;
            if (b == 0) return;
            if (Mismatch(b, [Addresses.PauseEnter, Addresses.EventEnter]) is { } bad)
            {
                Log(owner, $"Game.PauseOpened / EventStarted off: the game's code isn't as expected at {bad}");
                return;
            }
            s_pauseSlot = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
            s_eventSlot = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
            using var u = new Utf8Scope();
            var api = ModPlatform.Api;
            var p = api->HookFunction(owner, u.Str("FH2 pause menu opening"), (void*)(b + Addresses.PauseEnter.Offset),
                (delegate* unmanaged<nint, nint>)&OnPauseEnter, s_pauseSlot);
            var e = api->HookFunction(owner, u.Str("FH2 event starting"), (void*)(b + Addresses.EventEnter.Offset),
                (delegate* unmanaged<nint, nint>)&OnEventEnter, s_eventSlot);
            bool ok = p is not null && api->EnableHooks(owner, p) != 0;
            ok &= e is not null && api->EnableHooks(owner, e) != 0;
            if (!ok) Log(owner, "Game.PauseOpened / EventStarted: couldn't hook the pause or an event's start");
        }) { IsBackground = true, Name = "FH2 moments" }.Start();
    }

    [UnmanagedCallersOnly]
    private static nint OnPauseEnter(nint self)
    {
        Hud.PauseOpening();
        Raise(s_pauseOpened, "Game.PauseOpened");
        return ((delegate* unmanaged<nint, nint>)*s_pauseSlot)(self);
    }

    [UnmanagedCallersOnly]
    private static nint OnEventEnter(nint self)
    {
        Raise(s_eventStarted, "Game.EventStarted");
        return ((delegate* unmanaged<nint, nint>)*s_eventSlot)(self);
    }

    private static void Raise(List<Action> list, string what)
    {
        Action[] hs;
        lock (s_lock)
        {
            if (list.Count == 0) return;
            hs = list.ToArray();
        }
        foreach (var h in hs) Safe(h, what);
    }

    private static void ForgetMoments(Assembly assembly)
    {
        lock (s_lock)
        {
            s_pauseOpened.RemoveAll(h => h.Method.DeclaringType?.Assembly == assembly);
            s_eventStarted.RemoveAll(h => h.Method.DeclaringType?.Assembly == assembly);
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, nint> s_featureNames = new();

    /// <summary>
    /// True if your campaign has unlocked the feature <paramref name="name"/>, by the game's own names: "xbox_music",
    /// "xbox_music_playlists", "multiplayer", "festival", "map"... Game thread only. False for a name the game doesn't
    /// have, and on a game build the SDK doesn't know.
    /// </summary>
    public static bool IsFeatureUnlocked(string name)
    {
        RequireGameThread("Game.IsFeatureUnlocked");
        var b = Base;
        if (Mismatch(b, [Addresses.FeatureUnlocks, Addresses.IsFeatureUnlocked]) is not null) return false;
        var unlocks = ((delegate* unmanaged<nint>)(b + Addresses.FeatureUnlocks.Offset))();
        if (unlocks == 0) return false;
        var key = s_featureNames.GetOrAdd(name, GameCode.NewStdString);
        return (((delegate* unmanaged<nint, nint, int>)(b + Addresses.IsFeatureUnlocked.Offset))(unlocks, key) & 0xFF) != 0;
    }

    /// <summary>
    /// Forgets the rewind buffer: the player can't rewind to before now (as FH2 ANNA does when its autodrive stops, so
    /// a rewind doesn't take the car back into the AI's drive). Game thread only.
    /// </summary>
    public static void ClearRewind()
    {
        RequireGameThread("Game.ClearRewind");
        var b = Base;
        if (Mismatch(b, [Addresses.ClearRewindCode]) is { } bad)
        {
            Log(ModPlatform.GameOwner, $"Game.ClearRewind: the game's code isn't as expected at {bad}");
            return;
        }
        if (*(nint*)(b + Addresses.RewindRecorderGlobal) == 0) return;   // nothing recorded
        var rewind = *(nint*)(b + Addresses.RewindGlobal);
        if (rewind != 0) ((delegate* unmanaged<nint, int, int, void>)(b + Addresses.ClearRewindCode.Offset))(rewind, 1, 1);
    }
}

// Hiding all of the game's UI (ANNA-DroneMode's way).
public static unsafe partial class Hud
{
    private static volatile bool s_hideAll;
    private static int s_hideHooks;
    private static void** s_waitSlot, s_drawSlot, s_presentSlot;
    private static nint s_freeObject;

    /// <summary>
    /// Hides all of the game's UI: the HUD, the map, menus, everything drawn over the world (for screenshots, a
    /// cinematic camera). It comes back by itself when the pause menu opens, so the player is never stuck in a menu they
    /// can't see. Any thread.
    /// </summary>
    public static bool HideAll
    {
        get => s_hideAll;
        set
        {
            if (value)
            {
                Game.InstallMoments();   // to show it again as the pause opens
                if (Interlocked.Exchange(ref s_hideHooks, 1) == 0) InstallHide();
            }
            s_hideAll = value;
        }
    }

    internal static void PauseOpening() => s_hideAll = false;

    private static void InstallHide()
    {
        var owner = ModPlatform.GameOwner;
        var b = Game.Base;
        if (b == 0 || Game.Mismatch(b, Addresses.HideUi) is { } bad)
        {
            Game.Log(owner, "Hud.HideAll off: the game's code isn't as expected");
            s_hideHooks = 0;
            return;
        }
        s_freeObject = b + Addresses.FreeObject.Offset;
        s_waitSlot = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        s_drawSlot = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        s_presentSlot = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        var api = ModPlatform.Api;
        var w = api->HookFunction(owner, u.Str("FH2 UI render wait (Hud.HideAll)"), (void*)(b + Addresses.UiWait.Offset),
            (delegate* unmanaged<nint, nint*, nint, void>)&OnWait, s_waitSlot);
        var d = api->HookFunction(owner, u.Str("FH2 UI draw (Hud.HideAll)"), (void*)(b + Addresses.UiDraw.Offset),
            (delegate* unmanaged<nint, nint*, nint, void>)&OnDraw, s_drawSlot);
        var p = api->HookFunction(owner, u.Str("FH2 UI present (Hud.HideAll)"), (void*)(b + Addresses.UiPresent.Offset),
            (delegate* unmanaged<nint, nint, void>)&OnPresent, s_presentSlot);
        // All three or none: hiding the draw alone would leak what the wait hands it.
        if (w is null || d is null || p is null || api->EnableHooks(owner, w) == 0 || api->EnableHooks(owner, d) == 0 ||
            api->EnableHooks(owner, p) == 0)
        {
            if (w is not null) api->DisableHooks(owner, w);
            if (d is not null) api->DisableHooks(owner, d);
            if (p is not null) api->DisableHooks(owner, p);
            Game.Log(owner, "Hud.HideAll off: couldn't hook the UI's drawing");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnWait(nint renderer, nint* surface, nint parameters)
    {
        if (!s_hideAll) { ((delegate* unmanaged<nint, nint*, nint, void>)*s_waitSlot)(renderer, surface, parameters); return; }
        var owned = *surface;   // the graphics wrapper it hands over: released, as the wait would
        if (owned != 0) ((delegate* unmanaged<nint, void>)(*(nint*)(*(nint*)owned + 0x18)))(owned);
    }

    [UnmanagedCallersOnly]
    private static void OnDraw(nint app, nint* screen, nint transform)
    {
        if (!s_hideAll) { ((delegate* unmanaged<nint, nint*, nint, void>)*s_drawSlot)(app, screen, transform); return; }
        var root = *screen;   // the draw takes the screen it's handed even when it draws nothing
        if (root != 0 && Interlocked.Decrement(ref *(int*)(root + 0x10)) == 0) ((delegate* unmanaged<nint, void>)s_freeObject)(root);
    }

    [UnmanagedCallersOnly]
    private static void OnPresent(nint app, nint transform)
    {
        if (!s_hideAll) ((delegate* unmanaged<nint, nint, void>)*s_presentSlot)(app, transform);
    }
}
