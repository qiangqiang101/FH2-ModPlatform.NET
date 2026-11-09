using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

public static unsafe partial class Game
{
    private static readonly object s_physicsLock = new();
    private static readonly List<Action<Vehicle, float>> s_physics = new();
    private static void** s_dynamicsOriginal;
    // CCarDynamics -> its ForzaCar, from the cars' per-frame update (game thread), read on the physics thread.
    // Trusted only if seen within FreshMs (as FnFNitro does): cars come and go as the world streams.
    private static readonly ConcurrentDictionary<nint, (nint Car, long Seen)> s_carOfDynamics = new();
    private const long FreshMs = 500;
    private static long s_lastPrune;
    private static readonly ConcurrentDictionary<nint, GroundState> s_ground = new();
    [ThreadStatic] private static nint t_physicsCar;

    /// <summary>
    /// Every car's physics step (yours, Drivatars', traffic), on the game's physics thread, with the
    /// step's length in seconds. This is where to push a car: set <see cref="Vehicle.Velocity"/> here.
    /// Runs often (every car, 60+ times a second): keep it short, and use only the car you're given.
    /// </summary>
    public static event Action<Vehicle, float> PhysicsStep
    {
        add
        {
            Start();
            lock (s_physicsLock) s_physics.Add(value);
        }
        remove
        {
            lock (s_physicsLock) s_physics.Remove(value);
        }
    }

    /// <summary>True inside <see cref="PhysicsStep"/> (on the physics thread).</summary>
    internal static bool InPhysicsStep => t_physicsCar != 0;

    /// <summary>The car whose physics step is running on this thread, or 0.</summary>
    internal static nint PhysicsCar => t_physicsCar;

    private static void InstallPhysics(nint b, FH2Mod* owner)
    {
        if (Mismatch(b, Addresses.Physics) is { } bad)
        {
            Log(owner, $"Game.PhysicsStep off: the game's code isn't as expected at {bad}");
            return;
        }
        s_dynamicsOriginal = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        var hook = ModPlatform.Api->HookFunction(owner, u.Str("FH2.Game physics (CCarDynamics::UpdateDynamics)"),
            (void*)(b + Addresses.UpdateDynamics.Offset), (delegate* unmanaged<nint, nint, nint, uint, float, byte>)&OnDynamics, s_dynamicsOriginal);
        if (hook is null || ModPlatform.Api->EnableHooks(owner, hook) == 0)
            Log(owner, "Game.PhysicsStep off: couldn't hook the cars' physics");
    }

    // From OnCarUpdate (game thread): which CCarDynamics is this car's.
    private static void NoteDynamics(nint car)
    {
        var body = *(nint*)(car + Addresses.CarBodyField);
        if (body == 0) return;   // not simulated (yet)
        var dyn = body + Addresses.BodyToDynamics;
        var vt = *(nint*)dyn - s_base;
        if (vt != Addresses.DynamicsVtable && vt != Addresses.DynamicsVtableRefCounted) return;
        long now = Environment.TickCount64;
        s_carOfDynamics[dyn] = (car, now);
        if (now - s_lastPrune > 5000)
        {
            s_lastPrune = now;
            foreach (var kv in s_carOfDynamics)
                if (now - kv.Value.Seen > 5000)
                {
                    s_carOfDynamics.TryRemove(kv.Key, out _);
                    s_ground.TryRemove(kv.Key, out _);
                }
        }
    }

    [UnmanagedCallersOnly]
    private static byte OnDynamics(nint self, nint rdx, nint r8, uint r9, float dt)
    {
        var original = (delegate* unmanaged<nint, nint, nint, uint, float, byte>)*s_dynamicsOriginal;
        byte result = original(self, rdx, r8, r9, dt);
        if (self == 0 || *(int*)(self + Addresses.DynamicsSimulatedField) != 1) return result;
        if (!s_carOfDynamics.TryGetValue(self, out var link) || Environment.TickCount64 - link.Seen > FreshMs) return result;
        var car = link.Car;

        UpdateGround(self, dt);
        if (*(int*)(car + Addresses.CarKindField) == 0) Player.HoldStill(self);   // just teleported: still while the world loads
        Action<Vehicle, float>[] handlers;
        lock (s_physicsLock)
        {
            if (s_physics.Count == 0) return result;
            handlers = s_physics.ToArray();
        }
        var vehicle = new Vehicle(car);
        t_physicsCar = car;
        try
        {
            foreach (var h in handlers)
            {
                try { h(vehicle, dt); }
                catch (Exception e)
                {
                    Log(ModPlatform.GameOwner, $"Game.PhysicsStep handler {h.Method.DeclaringType?.FullName}.{h.Method.Name} failed: {e}");
                }
            }
        }
        finally
        {
            t_physicsCar = 0;
        }
        return result;
    }

    // ---- On the ground: from how the car falls (the way FnFNitro tells) -----------------------------
    // Airborne = vertical acceleration near -g (below -6 m/s^2) for two steps in a row; after landing it
    // stays "in the air" another 6 steps, so a bump doesn't count as ground straight away.
    private sealed class GroundState
    {
        public float PrevVy;
        public long PrevTicks;
        public int FallSteps, Grace;
        public volatile bool OnGround = true;
    }

    private static void UpdateGround(nint dyn, float dt)
    {
        var state = s_ground.GetOrAdd(dyn, _ => new GroundState());
        float vy = ((float*)(dyn + Addresses.DynamicsVelocity))[1];
        float step = dt is > 0.001f and < 0.1f ? dt : 1f / 60;
        long now = Environment.TickCount64;
        if (now - state.PrevTicks > 100)
        {
            state.FallSteps = 0;
            state.Grace = 0;
        }
        else
        {
            state.FallSteps = (vy - state.PrevVy) / step < -6f ? state.FallSteps + 1 : 0;
        }
        state.PrevVy = vy;
        state.PrevTicks = now;
        if (state.FallSteps >= 2) state.Grace = 6;
        else if (state.Grace > 0) state.Grace--;
        state.OnGround = state.FallSteps < 2 && state.Grace == 0;
    }

    /// <summary>Whether the car with this CCarDynamics was on the ground at its last physics step (true if unknown).</summary>
    internal static bool IsOnGround(nint dyn) => !s_ground.TryGetValue(dyn, out var s) || s.OnGround;
}
