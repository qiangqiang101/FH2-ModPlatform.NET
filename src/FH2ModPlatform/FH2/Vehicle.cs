using System.Numerics;

namespace FH2;

/// <summary>Who's driving a car (the game's ForzaCar kind, car+0x4D08).</summary>
public enum VehicleKind
{
    Player = 0,
    Drivatar = 1,
    Traffic = 2,
    Other = 3,
}

/// <summary>
/// A car in the world (the game's ForzaCar). Only valid during the frame you got it in: cars
/// come and go as the world streams, so don't keep one between ticks. Game thread only.
/// </summary>
/// <remarks>
/// Coordinates are the game's, in metres: X and Z across the map, Y up. Speeds in metres a
/// second (<see cref="SpeedKmh"/> and <see cref="SpeedMph"/> convert).
/// </remarks>
public sealed unsafe class Vehicle : IEquatable<Vehicle>
{
    internal Vehicle(nint handle) => Handle = handle;

    /// <summary>The ForzaCar's address, for code of your own.</summary>
    public nint Handle { get; }

    private nint Body
    {
        get
        {
            Game.RequireGameThread("Vehicle");
            return *(nint*)(Handle + Addresses.CarBodyField);
        }
    }

    private nint Dynamics => Body + Addresses.BodyToDynamics;

    /// <summary>Whose car it is.</summary>
    public VehicleKind Kind
    {
        get
        {
            Game.RequireGameThread("Vehicle.Kind");
            int k = *(int*)(Handle + Addresses.CarKindField);
            return k is >= 0 and <= 2 ? (VehicleKind)k : VehicleKind.Other;
        }
    }

    public bool IsPlayer => Kind == VehicleKind.Player;

    /// <summary>The car's Data_Car id (the one <see cref="CarCatalog"/> and <see cref="Garage"/> use).</summary>
    public int ModelId
    {
        get
        {
            Game.RequireGameThread("Vehicle.ModelId");
            return *(int*)(Handle + Addresses.CarIdField);
        }
    }

    /// <summary>Which car it is, with its names; null if the catalogue doesn't have it.</summary>
    public CarInfo? Model => CarCatalog.Get(ModelId);

    /// <summary>Where it is.</summary>
    public Vector3 Position
    {
        get
        {
            var p = (float*)(Dynamics + Addresses.DynamicsPosition);
            return new Vector3(p[0], p[1], p[2]);
        }
    }

    /// <summary>Which way it points (a unit vector).</summary>
    public Vector3 Forward
    {
        get
        {
            var p = (float*)(Dynamics + Addresses.DynamicsForward);
            return Vector3.Normalize(new Vector3(p[0], p[1], p[2]));
        }
    }

    /// <summary>
    /// How fast and which way it's moving, in m/s. Set it in <see cref="Game.PhysicsStep"/>, for this
    /// car, to push it: <c>car.Velocity += car.ForwardFlat * (10 * dt)</c> is 10 m/s² forwards.
    /// </summary>
    public Vector3 Velocity
    {
        get
        {
            Game.RequireGameThread("Vehicle.Velocity");
            var v = (float*)(Dynamics + Addresses.DynamicsVelocity);
            return new Vector3(v[0], v[1], v[2]);
        }
        set
        {
            if (Game.PhysicsCar != Handle)
                throw new InvalidOperationException("Set Vehicle.Velocity in Game.PhysicsStep, on the car it gives you: the physics owns it the rest of the time.");
            var v = (float*)(Dynamics + Addresses.DynamicsVelocity);
            v[0] = value.X;
            v[1] = value.Y;
            v[2] = value.Z;
        }
    }

    /// <summary>
    /// Which way it points, flattened to the ground (Y = 0) and of length 1: for pushing a car along
    /// the road without throwing it skyward. Zero when the nose points almost straight up or down.
    /// </summary>
    public Vector3 ForwardFlat
    {
        get
        {
            var f = Forward;
            var flat = new Vector3(f.X, 0, f.Z);
            var len = flat.Length();
            return len < 0.3f ? Vector3.Zero : flat / len;
        }
    }

    /// <summary>
    /// True while it's on the ground: false from when it starts to fall freely (a jump, a crest) until
    /// a moment after it lands, so a bump doesn't count as ground at once. Known from its physics
    /// steps; true until it has had a few.
    /// </summary>
    public bool IsOnGround
    {
        get
        {
            Game.RequireGameThread("Vehicle.IsOnGround");
            return Game.IsOnGround(Dynamics);
        }
    }

    public bool IsInAir => !IsOnGround;

    /// <summary>Speed in m/s, as the car's own physics has it.</summary>
    public float Speed => *(float*)(Body + Addresses.BodySpeed);

    public float SpeedKmh => Speed * 3.6f;
    public float SpeedMph => Speed * 2.2369363f;

    /// <summary>The gear: 0 is reverse, 1 first, and so on.</summary>
    public int Gear => *(int*)(Body + Addresses.BodyGear);

    /// <summary>Engine speed in revolutions a minute.</summary>
    public float Rpm => *(float*)(Body + Addresses.BodyRpm) * 9.5492966f;

    /// <summary>The pedals and wheel as the car has them this frame: 0..1 (steering -1..1).</summary>
    public float Throttle => *(float*)(Body + Addresses.BodyThrottle);
    public float BrakePedal => *(float*)(Body + Addresses.BodyBrake);
    public float Steering => *(float*)(Body + Addresses.BodySteer);
    public float Handbrake => *(float*)(Body + Addresses.BodyHandbrake);

    /// <summary>The headlights. Setting them turns the game's automatic lights off for this car, as ANNA does.</summary>
    public bool HeadlightsOn
    {
        get
        {
            Game.RequireGameThread("Vehicle.HeadlightsOn");
            return *(byte*)(Handle + Addresses.CarHeadlightsOn) != 0;
        }
        set
        {
            Game.RequireGameThread("Vehicle.HeadlightsOn");
            ((delegate* unmanaged<nint, byte, void>)(Game.Base + Addresses.SetHeadlightsByDriver.Offset))(Handle, 1);
            ((delegate* unmanaged<nint, byte, void>)(Game.Base + Addresses.SetHeadlights.Offset))(Handle, value ? (byte)1 : (byte)0);
        }
    }

    /// <summary>
    /// Flames out of the exhausts, a burst every 40 ms, until you set it back to false (or the car leaves
    /// the world). The game's own backfire effect, from the car's real exhaust tips: any car, any kind.
    /// Your mod reloading puts them out. Does nothing on a game build the SDK doesn't know (platform.log says so once).
    /// </summary>
    public bool ExhaustFlames
    {
        get => Game.HasFlames(Handle);
        set
        {
            Game.RequireGameThread("Vehicle.ExhaustFlames");
            if (!RequireFlames()) return;
            Game.SetFlames(Handle, value ? long.MaxValue : 0);
        }
    }

    /// <summary>A backfire: flames out of the exhausts for <paramref name="seconds"/> (one pop at the default).</summary>
    public void Backfire(float seconds = 0.05f)
    {
        Game.RequireGameThread("Vehicle.Backfire");
        if (!RequireFlames()) return;
        if (Game.HasFlames(Handle) && !ExhaustFlamesTimed) return;   // already burning for good
        Game.SetFlames(Handle, Environment.TickCount64 + Math.Max(1, (long)(seconds * 1000)));
    }

    private bool ExhaustFlamesTimed => Game.FlamesUntil(Handle) != long.MaxValue;

    // Without the flames (another game build), setting them does nothing: they're a nicety, not worth stopping a mod for.
    private static bool RequireFlames() => Game.FlamesReady;

    /// <summary>
    /// Repairs the car, as the game does when you enter the garage or start a road trip (the
    /// stateflow's ResetCarDamage): the damage you see goes, and with <paramref name="simulated"/> the
    /// simulated damage too (what Damage &amp; Tire Wear: Simulation does to the handling).
    /// </summary>
    public void Repair(bool simulated = true)
    {
        Game.RequireGameThread("Vehicle.Repair");
        var vtable = *(nint*)Handle;
        ((delegate* unmanaged<nint, void>)(*(nint*)(vtable + Addresses.ResetDamageSlot * sizeof(nint))))(Handle);
        ((delegate* unmanaged<nint, void>)(Game.Base + Addresses.ClearDamageTotals.Offset))(Handle);
        if (simulated)
            ((delegate* unmanaged<nint, void>)(*(nint*)(vtable + Addresses.ResetSimulatedDamageSlot * sizeof(nint))))(Handle);
    }

    /// <summary>Straight-line distance to <paramref name="point"/>, in metres.</summary>
    public float DistanceTo(Vector3 point) => Vector3.Distance(Position, point);
    public float DistanceTo(Vehicle other) => Vector3.Distance(Position, other.Position);

    public bool Equals(Vehicle? other) => other is not null && other.Handle == Handle;
    public override bool Equals(object? obj) => Equals(obj as Vehicle);
    public override int GetHashCode() => Handle.GetHashCode();
    public override string ToString() => $"{Kind} {Model?.ShortName ?? "car"} (0x{Handle:X})";
}

/// <summary>The world around you. Game thread only.</summary>
public static unsafe partial class World
{
    /// <summary>Every car the game updated last frame: yours, Drivatars, traffic.</summary>
    public static IReadOnlyList<Vehicle> Vehicles
    {
        get
        {
            Game.RequireGameThread("World.Vehicles");
            return Game.CarsLastFrame.Distinct().Select(c => new Vehicle(c)).ToList();
        }
    }

    /// <summary>
    /// The car nearest <paramref name="from"/> (your car's position if null), leaving out your own;
    /// only cars of <paramref name="kind"/> if given. Null if there's none.
    /// </summary>
    public static Vehicle? GetNearestVehicle(Vector3? from = null, VehicleKind? kind = null)
    {
        var at = from ?? Player.Car?.Position;
        if (at is null) return null;
        return Vehicles.Where(v => !v.IsPlayer && (kind is null || v.Kind == kind))
                       .OrderBy(v => Vector3.DistanceSquared(v.Position, at.Value))
                       .FirstOrDefault();
    }

    /// <summary>Cars within <paramref name="radius"/> metres of <paramref name="from"/> (your car if null), nearest first, leaving out yours.</summary>
    public static IReadOnlyList<Vehicle> GetVehiclesInRange(float radius, Vector3? from = null)
    {
        var at = from ?? Player.Car?.Position;
        if (at is null) return [];
        float r2 = radius * radius;
        return Vehicles.Where(v => !v.IsPlayer)
                       .Select(v => (v, d: Vector3.DistanceSquared(v.Position, at.Value)))
                       .Where(x => x.d <= r2).OrderBy(x => x.d).Select(x => x.v).ToList();
    }

    private static nint Sim
    {
        get
        {
            var b = Game.RequireBase("World");
            var root = *(nint*)(b + Addresses.PlayerManagerGlobal);
            return root == 0 ? 0 : *(nint*)(root + Addresses.SimField);
        }
    }

    private static nint Clock
    {
        get
        {
            var sim = Sim;
            var world = sim == 0 ? 0 : *(nint*)(sim + Addresses.WorldField);
            var tod = world == 0 ? 0 : *(nint*)(world + Addresses.TimeOfDayField);
            if (tod == 0) throw new InvalidOperationException("The world isn't loaded.");
            return tod;
        }
    }

    /// <summary>The time of day, as the world's clock has it. Setting it moves the sun straight away.</summary>
    public static TimeSpan TimeOfDay
    {
        get
        {
            Game.RequireGameThread("World.TimeOfDay");
            var s = ((delegate* unmanaged<nint, int, float>)(Game.Base + Addresses.GetTime.Offset))(Clock, 1);
            return TimeSpan.FromSeconds(s);
        }
        set
        {
            Game.RequireGameThread("World.TimeOfDay");
            var s = (float)(((value.TotalSeconds % 86400) + 86400) % 86400);
            ((delegate* unmanaged<nint, float, int, void>)(Game.Base + Addresses.SetTime.Offset))(Clock, s, 1);
        }
    }

    /// <summary>How fast the clock runs: 1 is the game's own pace, 0 stops it.</summary>
    public static float TimeScale
    {
        get
        {
            Game.RequireGameThread("World.TimeScale");
            return *(float*)(Clock + Addresses.TimeOfDaySpeedField);
        }
        set
        {
            Game.RequireGameThread("World.TimeScale");
            *(float*)(Clock + Addresses.TimeOfDaySpeedField) = Math.Max(0, value);
        }
    }

    /// <summary>True while the game is paused (the pause menu, a menu over the world).</summary>
    internal static bool IsPaused
    {
        get
        {
            var sim = Sim;
            return sim != 0 && *(byte*)(sim + Addresses.SimPausedField) != 0;
        }
    }
}
