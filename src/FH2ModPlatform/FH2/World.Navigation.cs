using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;

namespace FH2;

/// <summary>Where <see cref="World.RouteToNearest"/> sets the GPS route to.</summary>
public enum Destination
{
    /// <summary>The nearest championship or race event.</summary>
    Event,
    /// <summary>The nearest bucket list challenge.</summary>
    BucketList,
    /// <summary>The nearest road trip location (the festival's next stop).</summary>
    RoadTrip,
    /// <summary>The nearest bonus board you've found and not yet smashed.</summary>
    BonusBoard,
    /// <summary>The nearest barn find rumour's hint.</summary>
    BarnFind,
}

// Routes to the nearest event, bucket list, road trip stop, bonus board or barn find, through the game's own
// CareerNavigationModule: the module ANNA's voice commands used on the Xbox (FH2 ANNA's routes).
public static unsafe partial class World
{
    private static nint s_nav;   // the CareerNavigationModule, caught as it's made
    private static void** s_navCtorSlot, s_navDtorSlot;
    private static int s_navState;   // 0 not yet, 1 hooked, -1 off

    /// <summary>
    /// Puts in the navigation module's hooks at startup: the game makes the module once, as it loads your career, and
    /// hooks put in after that never see it.
    /// </summary>
    internal static void PrepareNavigation()
    {
        if (Interlocked.CompareExchange(ref s_navState, 1, 0) != 0) return;
        new Thread(() =>
        {
            var owner = ModPlatform.GameOwner;
            var b = ModPlatform.WaitForCode();
            if (b == 0) return;
            if (Game.Mismatch(b, Addresses.Navigation) is { } bad)
            {
                s_navState = -1;
                Game.Log(owner, $"World.RouteToNearest off: the game's code isn't as expected at {bad}");
                return;
            }
            s_navCtorSlot = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
            s_navDtorSlot = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
            using var u = new Utf8Scope();
            var api = ModPlatform.Api;
            var c = api->HookFunction(owner, u.Str("FH2 career navigation (made)"), (void*)(b + Addresses.NavCtor.Offset),
                (delegate* unmanaged<nint, nint>)&OnNavMade, s_navCtorSlot);
            var d = api->HookFunction(owner, u.Str("FH2 career navigation (gone)"), (void*)(b + Addresses.NavDtor.Offset),
                (delegate* unmanaged<nint, nint>)&OnNavGone, s_navDtorSlot);
            if (c is null || d is null || api->EnableHooks(owner, c) == 0 || api->EnableHooks(owner, d) == 0)
            {
                s_navState = -1;
                Game.Log(owner, "World.RouteToNearest off: couldn't hook the career navigation");
            }
        }) { IsBackground = true, Name = "FH2 navigation" }.Start();
    }

    [UnmanagedCallersOnly]
    private static nint OnNavMade(nint self)
    {
        var r = ((delegate* unmanaged<nint, nint>)*s_navCtorSlot)(self);
        s_nav = self;
        return r;
    }

    [UnmanagedCallersOnly]
    private static nint OnNavGone(nint self)
    {
        Interlocked.CompareExchange(ref s_nav, 0, self);
        return ((delegate* unmanaged<nint, nint>)*s_navDtorSlot)(self);
    }

    /// <summary>
    /// How much light there is where you're driving, as the game's lighting has it: it drops to a fifth or less in a
    /// tunnel and comes back as you come out (FH2 ANNA turns the headlights on with it). Compare it with what it was a
    /// moment ago rather than with a fixed number: it follows the time of day too. -1 if it can't be read. Any thread.
    /// </summary>
    public static float LightLevel =>
        Game.IsReady && GameCode.TryRead<float>(Game.Base + Addresses.ZoneLightGlobal, out var v) && float.IsFinite(v) ? v : -1;

    /// <summary>
    /// Sets the GPS route to the nearest <paramref name="destination"/>, as ANNA's voice commands did. False if there's
    /// none (no bonus board left), or the game's navigation isn't there (still loading). Game thread only.
    /// </summary>
    public static bool RouteToNearest(Destination destination)
    {
        Game.RequireGameThread("World.RouteToNearest");
        var nav = s_nav;
        if (s_navState != 1 || nav == 0) return false;
        var b = Game.Base;
        switch (destination)
        {
            case Destination.Event: ((delegate* unmanaged<nint, void>)(b + Addresses.NearestEvent.Offset))(nav); return true;
            case Destination.BucketList: ((delegate* unmanaged<nint, void>)(b + Addresses.NearestBucketList.Offset))(nav); return true;
            case Destination.RoadTrip: ((delegate* unmanaged<nint, void>)(b + Addresses.NearestRoadTrip.Offset))(nav); return true;
            case Destination.BonusBoard:
            {
                // Result {Entity*, its shared count*, bool found at +0x10}: the reference is ours to let go of.
                var result = stackalloc nint[4];
                result[0] = result[1] = result[2] = result[3] = 0;
                ((delegate* unmanaged<nint, nint*, nint>)(b + Addresses.NearestBoard.Offset))(nav, result);
                bool found = ((byte*)result)[0x10] != 0;
                ReleaseShared(result[1]);
                return found;
            }
            case Destination.BarnFind:
            {
                // The nav module's world, as a shared reference the function takes over.
                var ctx = stackalloc nint[2];
                ctx[0] = ctx[1] = 0;
                ((delegate* unmanaged<nint, nint*, nint>)(b + Addresses.LockWeakWorld.Offset))(nav + 8, ctx);
                if (ctx[0] == 0) return false;
                ((delegate* unmanaged<nint, nint*, void>)(b + Addresses.NearestBarnFind.Offset))(0, ctx);
                return true;
            }
        }
        return false;
    }

    // A std::shared_ptr's control block: the strong count at +8; at 0 the object goes (vtable[0]) and its weak count
    // (+0xC) with it, and at 0 the block (vtable[1]).
    internal static void ReleaseShared(nint counter)
    {
        if (counter == 0) return;
        var vt = *(nint**)counter;
        if (Interlocked.Decrement(ref *(int*)(counter + 8)) != 0) return;
        ((delegate* unmanaged<nint, void>)vt[0])(counter);
        if (Interlocked.Decrement(ref *(int*)(counter + 0xC)) == 0) ((delegate* unmanaged<nint, void>)vt[1])(counter);
    }
}
