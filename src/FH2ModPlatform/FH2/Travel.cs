using System.Numerics;
using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

public static unsafe partial class World
{
    private static void** s_satNavOriginal;
    private static volatile bool s_waypointReady;
    private static readonly object s_waypointLock = new();
    private static Vector3? s_waypoint;
    private static long s_waypointSeen;

    /// <summary>
    /// The waypoint you've set on the map, or null if there's none (or the HUD isn't up to say: in the garage, say).
    /// On the road where the sat-nav's route ends (with the ground's height); the map's pin when it has no route.
    /// Any thread.
    /// </summary>
    public static Vector3? Waypoint
    {
        get
        {
            Game.RequireBase("World.Waypoint");
            lock (s_waypointLock)
                return Environment.TickCount64 - s_waypointSeen < 2000 ? s_waypoint : null;
        }
    }

    internal static void InstallWaypoint(nint b, FH2Mod* owner)
    {
        if (*(nint*)(b + Addresses.SatNavUpdateSlot) != b + Addresses.SatNavUpdate.Offset || Game.Mismatch(b, Addresses.Waypoint) is not null)
        {
            Game.Log(owner, "World.Waypoint off: the sat-nav's code isn't as expected");
            return;
        }
        s_satNavOriginal = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        var hook = ModPlatform.Api->HookPointer(owner, u.Str("FH2 waypoint (HUDSatNavComponent update)"), (void**)(b + Addresses.SatNavUpdateSlot),
            (delegate* unmanaged<nint, nint, nint, nint, nint>)&OnSatNav, s_satNavOriginal);
        s_waypointReady = hook is not null && ModPlatform.Api->EnableHooks(owner, hook) != 0;
        if (!s_waypointReady) Game.Log(owner, "World.Waypoint off: couldn't hook the sat-nav");
    }

    // The component's update (this, and whatever it's given: passed on as they are).
    [UnmanagedCallersOnly]
    private static nint OnSatNav(nint h, nint a, nint b, nint c)
    {
        var r = ((delegate* unmanaged<nint, nint, nint, nint, nint>)*s_satNavOriginal)(h, a, b, c);
        try
        {
            if (h != 0 && *(int*)(h + Addresses.SatNavType) == 0)   // the player's own sat-nav
            {
                var w = ReadWaypoint(h);
                lock (s_waypointLock)
                {
                    s_waypoint = w;
                    s_waypointSeen = Environment.TickCount64;
                }
            }
        }
        catch { }
        return r;
    }

    private static Vector3? ReadWaypoint(nint h)
    {
        // The route's end, on the road with a real height, when the sat-nav has one.
        var ctrl = *(nint*)(h + Addresses.SatNavModuleCtrl);
        var module = *(nint*)(h + Addresses.SatNavModule);
        if (ctrl != 0 && module != 0 && *(int*)(ctrl + 8) > 0)
        {
            var system = *(nint*)(module + Addresses.SatNavSystem);
            var nav = system == 0 ? 0 : *(nint*)system;   // type 0's
            var route = nav == 0 ? 0 : *(nint*)(nav + Addresses.SatNavRoute);
            if (route != 0 && *(byte*)(route + Addresses.SatNavRouteValid) != 0)
            {
                nint begin = *(nint*)(nav + Addresses.SatNavNodes), end = *(nint*)(nav + Addresses.SatNavNodes + 8);
                if (begin != 0 && end - begin >= 8 && *(nint*)(end - 8) is var node and not 0)
                {
                    var p = (float*)(node + Addresses.SatNavNodePosition);
                    var v = new Vector3(p[0], p[1], p[2]);
                    if (float.IsFinite(v.X + v.Y + v.Z) && v != Vector3.Zero) return v;
                }
            }
        }
        // Else the map's pin (zero: none).
        var pin = (float*)(h + Addresses.SatNavPin);
        var pos = new Vector3(pin[0], pin[1], pin[2]);
        return pos != Vector3.Zero && float.IsFinite(pos.X + pos.Y + pos.Z) ? pos : null;
    }
}

public static unsafe partial class Player
{
    private static volatile bool s_teleportReady;
    private static long s_holdUntil;          // the car is held still (while the world loads around it) till then
    private static Vector3 s_replaceAt;
    private static long s_replaceWhen;        // and placed once more then, as the game's fast travel does

    /// <summary>True if <see cref="TeleportTo"/> can be used (the game's code is as expected).</summary>
    public static bool CanTeleport => Game.IsReady && s_teleportReady;

    internal static void InstallTeleport(nint b, FH2Mod* owner)
    {
        if (Game.Mismatch(b, Addresses.Teleport) is { } bad)
        {
            Game.Log(owner, $"Player.TeleportTo off: the game's code isn't as expected at {bad}");
            return;
        }
        s_teleportReady = true;
    }

    /// <summary>
    /// Puts your car on the road nearest <paramref name="position"/> (only X and Z need to be right), facing along
    /// it, as the game's fast travel does: on the ground, stopped. The world loads around it for a moment, during
    /// which it's held still and then placed again. Game thread (Game.Tick, Game.Run, an ANNA entry).
    /// </summary>
    public static void TeleportTo(Vector3 position)
    {
        Game.RequireGameThread("Player.TeleportTo");
        if (!CanTeleport) throw new InvalidOperationException("Teleporting isn't available (a different game build; platform.log says).");
        Place(position);
        Interlocked.Exchange(ref s_holdUntil, Environment.TickCount64 + 2500);
        s_replaceAt = position;
        Interlocked.Exchange(ref s_replaceWhen, Environment.TickCount64 + 1500);
    }

    private static void Place(Vector3 position)
    {
        var b = Game.Base;
        var sp = stackalloc nint[2] { 0, 0 };   // shared_ptr<FastTravelModule>: object, control block
        ((delegate* unmanaged<nint, nint*, nint*>)(b + Addresses.GetFastTravelModule.Offset))(*(nint*)(b + Addresses.EventBusGlobal), sp);
        var module = sp[0];
        if (module == 0) throw new InvalidOperationException("The game's fast travel isn't there (not in free roam?).");
        try
        {
            var buffer = stackalloc float[8];
            var pos = (float*)(((nint)buffer + 15) & ~(nint)15);   // __m128: 16-byte aligned
            pos[0] = position.X; pos[1] = position.Y; pos[2] = position.Z; pos[3] = 0;
            ((delegate* unmanaged<nint, float*, void>)(b + Addresses.DebugFastTravel.Offset))(module, pos);
            // The world streams in around the new place.
            var player = ((delegate* unmanaged<nint, int, nint>)(b + Addresses.LocalPlayer.Offset))(*(nint*)(b + Addresses.PlayerManagerGlobal), 0);
            if (player != 0)
                ((delegate* unmanaged<nint, float*, void>)(b + Addresses.StreamingFocus.Offset))(player, (float*)(module + Addresses.FastTravelNode));
        }
        finally
        {
            Release(sp[1]);
        }
    }

    // A shared_ptr's control block let go: uses at +8 (then its vfunc 0), weak references at +0xC (then vfunc 1).
    private static void Release(nint ctrl)
    {
        if (ctrl == 0) return;
        if (Interlocked.Decrement(ref *(int*)(ctrl + 8)) == 0)
        {
            ((delegate* unmanaged<nint, void>)(*(nint*)*(nint*)ctrl))(ctrl);
            if (Interlocked.Decrement(ref *(int*)(ctrl + 0xC)) == 0)
                ((delegate* unmanaged<nint, void>)(*(nint*)(*(nint*)ctrl + 8)))(ctrl);
        }
    }

    // From the player's car update (game thread): the second placement, once the world has loaded.
    internal static void TeleportFrame()
    {
        long when = Interlocked.Read(ref s_replaceWhen);
        if (when == 0 || Environment.TickCount64 < when) return;
        Interlocked.Exchange(ref s_replaceWhen, 0);
        try { Place(s_replaceAt); }
        catch (Exception e) { Game.Log(ModPlatform.GameOwner, "Player.TeleportTo: " + e.Message); }
    }

    // From the player's physics step: held still while the world loads (it may not have ground yet).
    internal static void HoldStill(nint dynamics)
    {
        if (Environment.TickCount64 >= Interlocked.Read(ref s_holdUntil)) return;
        var v = (float*)(dynamics + Addresses.DynamicsVelocity);
        var w = (float*)(dynamics + Addresses.DynamicsAngularVelocity);
        v[0] = v[1] = v[2] = 0;
        w[0] = w[1] = w[2] = 0;
    }
}
