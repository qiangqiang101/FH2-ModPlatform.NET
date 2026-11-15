using System.Collections.Concurrent;
using System.Text;
using FH2ModPlatform;

namespace FH2;

/// <summary>A car's indicators (<see cref="Vehicle.Indicators"/>).</summary>
public enum Indicators
{
    Off,
    Left,
    Right,
    Hazards,
}

/// <summary>A part of a car that the game can animate (<see cref="Vehicle.OpenPart"/>).</summary>
public enum CarPart
{
    Roof,
    Hood,
    Trunk,
    DoorFrontLeft,
    DoorFrontRight,
    DoorRearLeft,
    DoorRearRight,
    /// <summary>A storage compartment (a frunk, a pickup's tonneau...).</summary>
    Storage,
    /// <summary>A second storage compartment, in the middle.</summary>
    StorageMiddle,
    /// <summary>Pop-up headlights (both sides together).</summary>
    PopUpLights,
    /// <summary>An active spoiler or wing.</summary>
    ActiveAero,
}

// Indicators, headlight modes, part animations and more telemetry (from FH2 ANNA and VirtualDashboard's findings).
public sealed unsafe partial class Vehicle
{
    // ---- Telemetry ----------------------------------------------------------------------------

    /// <summary>The clutch pedal, 0 (out) to 1 (in).</summary>
    public float Clutch => *(float*)(Body + Addresses.BodyClutch);

    /// <summary>Where the red zone starts on the rev counter, in rpm.</summary>
    public float RedlineRpm => *(float*)(Body + Addresses.BodyRedline) * 9.5492966f;

    /// <summary>The rev limiter, in rpm.</summary>
    public float LimiterRpm => *(float*)(Body + Addresses.BodyLimiter) * 9.5492966f;

    /// <summary>The turbo's boost, as the game keeps it (0 without a turbo, or off boost). Its unit isn't known for sure.</summary>
    public float Boost => *(float*)(Body + Addresses.BodyBoost);

    /// <summary>The car's media name, as the game's files call it ("ALF_8C_08"), or "". Any thread.</summary>
    public string MediaName => GameCode.ReadStdString(Handle + Addresses.CarMediaName);

    // ---- Headlights ---------------------------------------------------------------------------

    // Cars whose headlights a mod set, and to what: the game sets them from its own choice now and then, so they're
    // written again before each of the car's updates (the car update hook), as FH2 ANNA does.
    private static readonly ConcurrentDictionary<nint, bool> s_lights = new();
    private static readonly ConcurrentDictionary<nint, Indicators> s_signals = new();
    private static readonly ConcurrentDictionary<nint, long> s_seen = new();

    /// <summary>
    /// True while the game chooses the headlights itself (at dusk, in tunnels). Setting <see cref="HeadlightsOn"/>
    /// takes them over until you set this back to true.
    /// </summary>
    public bool HeadlightsAutomatic
    {
        get
        {
            Game.RequireGameThread("Vehicle.HeadlightsAutomatic");
            return !s_lights.ContainsKey(Handle) && *(byte*)(Handle + Addresses.CarHeadlightsByDriver) == 0;
        }
        set
        {
            Game.RequireGameThread("Vehicle.HeadlightsAutomatic");
            if (!value) { HeadlightsOn = HeadlightsOn; return; }   // hold them as they are
            s_lights.TryRemove(Handle, out _);
            *(byte*)(Handle + Addresses.CarHeadlightsByDriver) = 0;
        }
    }

    // ---- Indicators ---------------------------------------------------------------------------

    /// <summary>
    /// The indicators: left, right, the hazards, or off. Ones you set stay on until you set <see cref="FH2.Indicators.Off"/>
    /// (the game would otherwise reset them now and then). Game thread only.
    /// </summary>
    public Indicators Indicators
    {
        get
        {
            Game.RequireGameThread("Vehicle.Indicators");
            if (s_signals.TryGetValue(Handle, out var held)) return held;
            var s = (byte*)(Handle + Addresses.CarSignals);
            return s[0] != 0 ? Indicators.Hazards : s[1] != 0 ? Indicators.Left : s[2] != 0 ? Indicators.Right : Indicators.Off;
        }
        set
        {
            Game.RequireGameThread("Vehicle.Indicators");
            WriteSignals(Handle, value);
            if (value == Indicators.Off) s_signals.TryRemove(Handle, out _);
            else
            {
                s_signals[Handle] = value;
                s_seen[Handle] = Environment.TickCount64;
            }
        }
    }

    private static void WriteSignals(nint car, Indicators s)
    {
        var p = (byte*)(car + Addresses.CarSignals);
        p[0] = s == Indicators.Hazards ? (byte)1 : (byte)0;
        p[1] = s == Indicators.Left ? (byte)1 : (byte)0;
        p[2] = s == Indicators.Right ? (byte)1 : (byte)0;
    }

    /// <summary>From the car update hook, before the car's update reads them: what mods hold goes back in.</summary>
    internal static void NoteCar(nint car)
    {
        if (s_lights.IsEmpty && s_signals.IsEmpty) return;
        bool held = false;
        if (s_lights.TryGetValue(car, out var on))
        {
            *(byte*)(car + Addresses.CarHeadlightsByDriver) = 1;
            *(byte*)(car + Addresses.CarHeadlightsOn) = on ? (byte)1 : (byte)0;
            held = true;
        }
        if (s_signals.TryGetValue(car, out var s))
        {
            WriteSignals(car, s);
            held = true;
        }
        if (held) s_seen[car] = Environment.TickCount64;
    }

    /// <summary>Once a frame: forget cars that left the world (their memory may be another car's by now).</summary>
    internal static void ForgetGoneCars()
    {
        if (s_seen.IsEmpty) return;
        long now = Environment.TickCount64;
        foreach (var kv in s_seen)
        {
            if (now - kv.Value < 5000) continue;
            s_seen.TryRemove(kv.Key, out _);
            s_lights.TryRemove(kv.Key, out _);
            s_signals.TryRemove(kv.Key, out _);
        }
    }

    // ---- Who drives ---------------------------------------------------------------------------

    /// <summary>
    /// The car's <see cref="Vehicle"/>, from its address (a ForzaCar* your own hook got), or null if it isn't a car
    /// in the world now. Game thread only.
    /// </summary>
    public static Vehicle? FromHandle(nint handle)
    {
        if (handle == 0) return null;
        foreach (var v in World.Vehicles)
            if (v.Handle == handle) return v;
        return null;
    }

    private int DriverMode
    {
        get
        {
            if (!GameCode.TryRead<nint>(Handle + Addresses.CarDriverHolder, out var holder) || holder == 0) return -1;
            if (!GameCode.TryRead<nint>(holder, out var driver) || driver == 0) return -1;
            return *(int*)(Handle + Addresses.CarDriverMode);
        }
    }

    /// <summary>
    /// True while the game's AI drives the car. Setting it on hands your car to the AI driver, as FH2 ANNA's autodrive
    /// does: it drives the route the game is following (an event's, a race's, a road trip's). In free roam, a GPS
    /// route needs the sat-nav's route tracking started too, which this doesn't do. Setting it off gives the car back.
    /// Game thread only; does nothing on a game build the SDK doesn't know.
    /// </summary>
    public bool AiDriving
    {
        get
        {
            Game.RequireGameThread("Vehicle.AiDriving");
            return DriverMode == 0;
        }
        set
        {
            Game.RequireGameThread("Vehicle.AiDriving");
            if (s_aiState == 0)
            {
                var bad = Game.Mismatch(Game.Base, [Addresses.SetAiDriver]);
                s_aiState = bad is null ? 1 : -1;
                if (bad is not null) Game.Log(ModPlatform.GameOwner, $"Vehicle.AiDriving off: the game's code isn't as expected at {bad}");
            }
            if (s_aiState != 1 || DriverMode != (value ? 1 : 0)) return;   // no driver, or already that way
            ((delegate* unmanaged<nint, byte, void>)(Game.Base + Addresses.SetAiDriver.Offset))(Handle, value ? (byte)1 : (byte)0);
        }
    }

    private static int s_aiState;

    // ---- Part animations ----------------------------------------------------------------------

    private static int s_partsState;   // 0 not checked, 1 ready, -1 the game's code isn't as expected

    private static bool PartsReady()
    {
        if (s_partsState == 0)
        {
            var bad = Game.Mismatch(Game.Base, Addresses.Parts);
            s_partsState = bad is null ? 1 : -1;
            if (bad is not null) Game.Log(ModPlatform.GameOwner, $"Vehicle part animations off: the game's code isn't as expected at {bad}");
        }
        return s_partsState == 1;
    }

    private nint Graph
    {
        get
        {
            if (!GameCode.TryRead<nint>(Handle + Addresses.CarAnimator, out var animator) || animator == 0) return 0;
            return GameCode.TryRead<nint>(animator + Addresses.AnimatorGraph, out var graph) ? graph : 0;
        }
    }

    /// <summary>
    /// The animation events the car has, the names <see cref="PlayAnimation"/> takes: "roof:open", "doorLF:toggle",
    /// "hood:close"... Empty for a car without any (or on a game build the SDK doesn't know). Game thread only.
    /// </summary>
    public IReadOnlyList<string> Animations
    {
        get
        {
            Game.RequireGameThread("Vehicle.Animations");
            var names = new List<string>();
            if (!PartsReady()) return names;
            var graph = Graph;
            if (graph == 0 || !GameCode.TryRead<nint>(graph + Addresses.GraphEvents, out var head) || head == 0) return names;
            // The map's entries are a std::list: nodes {next, prev, value}, the value's std::string key first.
            GameCode.TryRead<nint>(head, out var node);
            for (int guard = 0; node != 0 && node != head && guard < 2000; guard++)
            {
                var key = GameCode.ReadStdString(node + 0x10);
                if (key.Length > 0) names.Add(key);
                if (!GameCode.TryRead(node, out node)) break;
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }

    /// <summary>True if the car has the animation event <paramref name="name"/> ("roof:open"). Game thread only.</summary>
    public bool HasAnimation(string name)
    {
        Game.RequireGameThread("Vehicle.HasAnimation");
        if (!PartsReady()) return false;
        var graph = Graph;
        return graph != 0 && FindEvent(graph, name);
    }

    /// <summary>
    /// Plays the car's animation event <paramref name="name"/> ("roof:open", "doorLF:toggle"), as ForzaVista does: the part
    /// moves, wherever the camera is. False if the car has no such event. Game thread only.
    /// </summary>
    public bool PlayAnimation(string name)
    {
        Game.RequireGameThread("Vehicle.PlayAnimation");
        if (!PartsReady()) return false;
        var graph = Graph;
        if (graph == 0 || !FindEvent(graph, name)) return false;
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        ((delegate* unmanaged<nint, int, void>)(Game.Base + Addresses.AnimGraphDetail.Offset))(graph, 0);   // full detail: a part far off moves too
        fixed (byte* p = bytes) ((delegate* unmanaged<nint, byte*, void>)(Game.Base + Addresses.CarAnimEvent.Offset))(Handle, p);
        return true;
    }

    /// <summary>True if the car has <paramref name="part"/> to animate. Game thread only.</summary>
    public bool HasPart(CarPart part)
    {
        foreach (var n in PartNames(part))
            if (!HasAnimation(n + ":toggle") && !(HasAnimation(n + ":open") && HasAnimation(n + ":close"))) return false;
        return true;
    }

    /// <summary>Opens <paramref name="part"/> (a door, the roof, pop-up lights up). False if the car hasn't got it.</summary>
    public bool OpenPart(CarPart part) => PartEvent(part, "open");

    /// <summary>Closes <paramref name="part"/>. False if the car hasn't got it.</summary>
    public bool ClosePart(CarPart part) => PartEvent(part, "close");

    /// <summary>Opens <paramref name="part"/> if it's closed, closes it if it's open. False if the car hasn't got it.</summary>
    public bool TogglePart(CarPart part) => PartEvent(part, "toggle");

    private bool PartEvent(CarPart part, string action)
    {
        bool any = false;
        foreach (var n in PartNames(part)) any |= PlayAnimation(n + ":" + action);
        return any;
    }

    private static string[] PartNames(CarPart part) => part switch
    {
        CarPart.Roof => ["roof"],
        CarPart.Hood => ["hood"],
        CarPart.Trunk => ["trunk"],
        CarPart.DoorFrontLeft => ["doorLF"],
        CarPart.DoorFrontRight => ["doorRF"],
        CarPart.DoorRearLeft => ["doorLR"],
        CarPart.DoorRearRight => ["doorRR"],
        CarPart.Storage => ["storage"],
        CarPart.StorageMiddle => ["storageM"],
        CarPart.PopUpLights => ["headlightL", "headlightR"],
        CarPart.ActiveAero => ["active_aero"],
        _ => [],
    };

    // The graph's event map, looked up with the game's own find: the key is an MSVC 2012 std::string.
    private static bool FindEvent(nint graph, string name)
    {
        var map = graph + Addresses.GraphEvents;
        if (!GameCode.TryRead<nint>(map, out var head) || head == 0) return false;
        if (!GameCode.TryRead<ulong>(map + 8, out var count) || count > 65536) return false;
        var text = Encoding.ASCII.GetBytes(name);
        byte* key = stackalloc byte[32 + text.Length + 1];
        new Span<byte>(key, 32 + text.Length + 1).Clear();
        if (text.Length < 16)
        {
            text.CopyTo(new Span<byte>(key, 16));
            *(ulong*)(key + 24) = 15;
        }
        else
        {
            text.CopyTo(new Span<byte>(key + 32, text.Length));
            *(byte**)key = key + 32;
            *(ulong*)(key + 24) = (ulong)text.Length;
        }
        *(ulong*)(key + 16) = (ulong)text.Length;
        nint node = 0;
        ((delegate* unmanaged<nint, nint*, byte*, void>)(Game.Base + Addresses.FindAnimEvent.Offset))(map, &node, key);
        return node != 0 && node != head;
    }
}
