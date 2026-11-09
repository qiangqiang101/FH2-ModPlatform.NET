using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>
/// The game itself, for scripts: its thread, its frame and readiness. Like ScriptHookVDotNet's
/// Script.Tick, <see cref="Tick"/> runs your code on the game's thread once a frame; the other
/// FH2 classes (Player, Garage, Profile...) are meant to be used from there, or from
/// <see cref="Run"/>.
/// </summary>
/// <remarks>
/// Frames are counted while you drive: the tick comes from your own car's per-frame update, so
/// nothing ticks on the title screen or while the game is paused. Starts the first time a mod
/// touches it: the game's code is checked (the log says what doesn't match, and
/// <see cref="IsReady"/> stays false), then one hook goes in, shared by every hosted mod.
/// </remarks>
public static unsafe partial class Game
{
    private static readonly object s_lock = new();
    private static readonly List<Action> s_tick = new();
    private static readonly ConcurrentQueue<Action> s_queue = new();
    private static volatile bool s_started, s_ready;
    private static nint s_base;
    private static volatile uint s_thread;
    private static long s_frame;
    private static nint s_playerCar;
    private static readonly List<nint> s_cars = new(), s_carsLast = new();
    [ThreadStatic] private static bool t_inTick;

    /// <summary>True once the game's code has been checked and the frame hook is in.</summary>
    public static bool IsReady
    {
        get
        {
            Start();
            return s_ready;
        }
    }

    /// <summary>The game's base address (0 until the game is mapped).</summary>
    public static nint Base => s_base;

    /// <summary>True while the game is paused (the pause menu). Any thread.</summary>
    public static bool IsPaused => IsReady && World.IsPaused;

    /// <summary>Frames ticked so far (while driving).</summary>
    public static long FrameCount => Interlocked.Read(ref s_frame);

    /// <summary>True on the game's thread (inside <see cref="Tick"/> or <see cref="Run"/>).</summary>
    public static bool IsGameThread => s_thread != 0 && GetCurrentThreadId() == s_thread;

    /// <summary>
    /// Once a frame, on the game's thread, while you drive. Keep it short: the game waits for it.
    /// An exception is logged and the handler stays on.
    /// </summary>
    public static event Action Tick
    {
        add
        {
            Start();
            lock (s_lock) s_tick.Add(value);
        }
        remove
        {
            lock (s_lock) s_tick.Remove(value);
        }
    }

    /// <summary>Runs <paramref name="action"/> on the game's thread at the next frame (straight away if already on it).</summary>
    public static void Run(Action action)
    {
        Start();
        if (IsGameThread && t_inTick)
        {
            Safe(action, "Game.Run");
            return;
        }
        s_queue.Enqueue(action);
    }

    /// <summary>Runs <paramref name="func"/> on the game's thread and gives its result back. Never await it from the game's thread.</summary>
    public static Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    /// <inheritdoc cref="RunAsync{T}(Func{T})"/>
    public static Task RunAsync(Action action) => RunAsync(() => { action(); return true; });

    internal static void RequireGameThread(string what)
    {
        if (!IsGameThread && !InPhysicsStep)
            throw new InvalidOperationException(
                $"{what} has to run on the game's thread: call it from Game.Tick, or wrap it in Game.Run(...).");
    }

    internal static nint RequireBase(string what)
    {
        if (!IsReady || s_base == 0)
            throw new InvalidOperationException($"{what}: the game isn't ready (still loading, or a different game build; platform.log says).");
        return s_base;
    }

    /// <summary>The local player's car this frame, or 0.</summary>
    internal static nint PlayerCar => s_playerCar;

    /// <summary>Every car the game updated last frame.</summary>
    internal static IReadOnlyList<nint> CarsLastFrame
    {
        get
        {
            lock (s_carsLast) return s_carsLast.ToArray();
        }
    }

    // ---- Starting ----------------------------------------------------------------------------

    private static void Start()
    {
        if (s_started) return;
        lock (s_lock)
        {
            if (s_started) return;
            s_started = true;
        }
        new Thread(Install) { IsBackground = true, Name = "FH2.Game start" }.Start();
    }

    private static void Install()
    {
        try
        {
            var b = ModPlatform.WaitForCode();
            if (b == 0) return;
            var owner = ModPlatform.GameOwner;
            if (Mismatch(b, Addresses.All) is { } bad)
            {
                Log(owner, $"FH2 classes off: the game's code isn't as expected at {bad}");
                return;
            }
            s_base = b;
            using var u = new Utf8Scope();
            var hook = ModPlatform.Api->HookInstruction(owner, u.Str("FH2.Game frame (ForzaCar update)"),
                (void*)(b + Addresses.CarUpdate.Offset), &OnCarUpdate, null);
            if (hook is null || ModPlatform.Api->EnableHooks(owner, hook) == 0)
            {
                Log(owner, "FH2 classes off: couldn't hook the cars' update");
                return;
            }
            InstallPhysics(b, owner);
            InstallFlames(b, owner);
            Audio.Install(b, owner);
            Camera.Install(b, owner);
            Hud.Install(b, owner);
            World.InstallWaypoint(b, owner);
            Player.InstallTeleport(b, owner);
            s_ready = true;
            Log(owner, "FH2 classes ready");
        }
        catch (Exception e)
        {
            Log(ModPlatform.GameOwner, "FH2 classes off: " + e);
        }
    }

    // The first of `codes` that isn't what this build has, as "name (game+0x...)"; null if they all are.
    internal static string? Mismatch(nint b, Addresses.Code[] codes)
    {
        foreach (var c in codes)
            if (!Matches(b + c.Offset, c.Bytes)) return $"{c.Name} (game+0x{c.Offset:X})";
        return null;
    }

    // The bytes, or a jump where another mod has hooked it (E9 rel32: the platform chains hooks on the same place).
    private static bool Matches(nint at, string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        if (Marshal.ReadByte(at) == 0xE9 && bytes[0] != 0xE9) return true;
        for (int i = 0; i < bytes.Length; i++)
            if (Marshal.ReadByte(at, i) != bytes[i]) return false;
        return true;
    }

    // ---- Every car, every frame ------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static void OnCarUpdate(FH2Context* ctx, void* user)
    {
        var car = (nint)ctx->Rbx;
        if (car == 0) return;
        int kind = *(int*)(car + Addresses.CarKindField);
        try
        {
            NoteDynamics(car);
            NoteFlames(car);
            Audio.NoteCar(car);
        }
        catch (Exception e)
        {
            Log(ModPlatform.GameOwner, "FH2 car update: " + e.Message);   // never out of the game's code
        }
        if (kind != 0)
        {
            lock (s_cars) s_cars.Add(car);
            return;
        }
        // The local player's car: once a frame. Last frame's cars become the snapshot.
        lock (s_cars)
        {
            s_cars.Add(car);
            lock (s_carsLast)
            {
                s_carsLast.Clear();
                s_carsLast.AddRange(s_cars);
            }
            s_cars.Clear();
        }
        s_playerCar = car;
        s_thread = GetCurrentThreadId();
        Interlocked.Increment(ref s_frame);
        t_inTick = true;
        try
        {
            try
            {
                PlayerFrame(car);
                Player.TeleportFrame();
            }
            catch (Exception e) { Log(ModPlatform.GameOwner, "Game events: " + e.Message); }
            while (s_queue.TryDequeue(out var a)) Safe(a, "Game.Run");
            Action[] handlers;
            lock (s_lock) handlers = s_tick.ToArray();
            foreach (var h in handlers) Safe(h, "Game.Tick");
        }
        finally
        {
            t_inTick = false;
        }
    }

    private static void Safe(Action a, string what)
    {
        try { a(); }
        catch (Exception e)
        {
            Log(ModPlatform.GameOwner, $"{what} handler {a.Method.DeclaringType?.FullName}.{a.Method.Name} failed: {e}");
        }
    }

    /// <summary>A hosted mod is being replaced: its Tick handlers and queued work go.</summary>
    internal static void Forget(Assembly assembly)
    {
        ANNA.Forget(assembly);
        lock (s_lock) s_tick.RemoveAll(h => h.Method.DeclaringType?.Assembly == assembly);
        lock (s_physicsLock) s_physics.RemoveAll(h => h.Method.DeclaringType?.Assembly == assembly);
        StopAllFlames();   // whichever mod lit them: a reloaded one lights its own again
        Audio.Forget(assembly);
        Camera.Forget();
        Hud.ClearGaugeItems();
        ForgetEvents(assembly);
        DebugOverlay.Forget();
        Menus.Forget(assembly);
        PhotoCamera.Forget(assembly);
        GameInput.Forget();
        Controller.StopSimulating();
        var keep = new List<Action>();
        while (s_queue.TryDequeue(out var a))
            if (a.Method.DeclaringType?.Assembly != assembly) keep.Add(a);
        foreach (var a in keep) s_queue.Enqueue(a);
    }

    internal static void Log(FH2Mod* owner, string line)
    {
        if (owner is null || !ModPlatform.IsAttached) return;
        using var u = new Utf8Scope();
        foreach (var l in line.Split('\n'))
            ModPlatform.Api->Log(owner, u.Str(l.TrimEnd('\r')));
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
