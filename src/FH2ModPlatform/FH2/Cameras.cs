using System.Numerics;
using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>
/// Photo mode's camera, as it reads its controller each frame: <see cref="InputRead"/> lets you add to what it
/// read (move, turn, rise, zoom), as the sticks would. Starts with <see cref="Menus"/>.
/// </summary>
public static unsafe class PhotoCamera
{
    private static void** s_original;
    private static readonly object s_lock = new();
    private static readonly List<Action<PhotoCameraInput>> s_handlers = new();

    /// <summary>
    /// The photo camera has just read its controller (every frame in photo mode, on the game's thread; in the
    /// Autoshow two cameras read it, so it can come twice a frame): add yours to what it read.
    /// </summary>
    public static event Action<PhotoCameraInput> InputRead
    {
        add { lock (s_lock) s_handlers.Add(value); }
        remove { lock (s_lock) s_handlers.Remove(value); }
    }

    internal static void Install(nint b, FH2Mod* owner)
    {
        // The function, not CCamPhotoMode's slot: the Autoshow's photo camera has the function in a vtable of its own.
        if (*(nint*)(b + Addresses.PhotoInputSlot) != b + Addresses.PhotoInput.Offset || Game.Mismatch(b, [Addresses.PhotoInput]) is not null)
        {
            Game.Log(owner, "PhotoCamera off: the game's code isn't as expected");
            return;
        }
        s_original = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        var hook = ModPlatform.Api->HookFunction(owner, u.Str("FH2 photo mode camera input"), (void*)(b + Addresses.PhotoInput.Offset),
            (delegate* unmanaged<nint, void>)&OnInput, s_original);
        if (hook is null || ModPlatform.Api->EnableHooks(owner, hook) == 0) Game.Log(owner, "PhotoCamera off: couldn't hook it");
    }

    [UnmanagedCallersOnly]
    private static void OnInput(nint cam)
    {
        ((delegate* unmanaged<nint, void>)*s_original)(cam);
        Action<PhotoCameraInput>[] hs;
        lock (s_lock) hs = s_handlers.ToArray();
        if (hs.Length == 0) return;
        var input = new PhotoCameraInput(cam);
        foreach (var h in hs)
            try { h(input); }
            catch (Exception e) { Game.Log(ModPlatform.GameOwner, "PhotoCamera.InputRead: " + e.Message); }
    }

    internal static void Forget(System.Reflection.Assembly a)
    {
        lock (s_lock) s_handlers.RemoveAll(h => h.Method.DeclaringType?.Assembly == a);
    }
}

/// <summary>What the photo camera read from its controller this frame: stick values, -1..1, that you can add to.</summary>
public sealed unsafe class PhotoCameraInput
{
    private readonly nint _cam;
    internal PhotoCameraInput(nint cam) => _cam = cam;

    private float this[int offset]
    {
        get => *(float*)(_cam + offset);
        set => *(float*)(_cam + offset) = value;
    }

    /// <summary>Forward (+) and back (the left stick up and down).</summary>
    public float Forward { get => this[Addresses.PhotoForward]; set => this[Addresses.PhotoForward] = value; }
    /// <summary>Right (+) and left... as the camera has it: the left stick's sideways.</summary>
    public float Right { get => this[Addresses.PhotoRight]; set => this[Addresses.PhotoRight] = value; }
    /// <summary>Up (+) and down.</summary>
    public float Rise { get => this[Addresses.PhotoRise]; set => this[Addresses.PhotoRise] = value; }
    /// <summary>Turning sideways and up and down (the right stick).</summary>
    public float TurnX { get => this[Addresses.PhotoTurnX]; set => this[Addresses.PhotoTurnX] = value; }
    public float TurnY { get => this[Addresses.PhotoTurnY]; set => this[Addresses.PhotoTurnY] = value; }
    /// <summary>Zooming in (+) and out.</summary>
    public float Zoom { get => this[Addresses.PhotoZoom]; set => this[Addresses.PhotoZoom] = value; }
}

/// <summary>The garage's camera (and the other menus' car cameras that orbit the car): its distance, by the frame.</summary>
public static unsafe class GarageCamera
{
    private static void** s_original;
    private static long s_seen;
    private static float s_zoom;
    private static readonly object s_lock = new();

    /// <summary>True while the garage camera is updating (within the last 200 ms).</summary>
    public static bool IsActive => s_original is not null && Environment.TickCount64 - Interlocked.Read(ref s_seen) < 200;

    /// <summary>
    /// Moves the orbiting camera closer (+) or further (-) by <paramref name="metres"/> at its next update, kept
    /// between 1.5 and 8 m (the game's own steps: 2, 3.5, 5).
    /// </summary>
    public static void Zoom(float metres)
    {
        lock (s_lock) s_zoom += metres;
    }

    internal static void Install(nint b, FH2Mod* owner)
    {
        if (Game.Mismatch(b, [Addresses.GarageCamera]) is not null)
        {
            Game.Log(owner, "GarageCamera off: the game's code isn't as expected");
            return;
        }
        var original = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        var hook = ModPlatform.Api->HookFunction(owner, u.Str("FH2 garage camera"), (void*)(b + Addresses.GarageCamera.Offset),
            (delegate* unmanaged<nint, nint, void>)&OnUpdate, original);
        if (hook is null || ModPlatform.Api->EnableHooks(owner, hook) == 0)
        {
            Game.Log(owner, "GarageCamera off: couldn't hook it");
            return;
        }
        s_original = original;
    }

    [UnmanagedCallersOnly]
    private static void OnUpdate(nint cam, nint input)
    {
        Interlocked.Exchange(ref s_seen, Environment.TickCount64);
        float z;
        lock (s_lock)
        {
            z = s_zoom;
            s_zoom = 0;
        }
        if (z != 0 && *(int*)(cam + Addresses.GarageCameraMode) == 0)
        {
            var d = (float*)(cam + Addresses.GarageCameraDistance);   // negative: further is more negative
            *d = Math.Clamp(*d + z, -8f, -1.5f);
        }
        ((delegate* unmanaged<nint, nint, void>)*s_original)(cam, input);
    }
}

/// <summary>
/// The map, while it's open (<see cref="Current"/>): its cursor, which the stick moves over the map, and the camera
/// above it, which follows the cursor once it strays from the middle. Seen from straight above; X and Z are the
/// world's. UI thread (<see cref="Menus.Frame"/>).
/// </summary>
public sealed unsafe class MapView
{
    private readonly nint _c;
    private MapView(nint controller) => _c = controller;

    /// <summary>The map on screen (it has the focus), or null.</summary>
    public static MapView? Current
    {
        get
        {
            var f = Menus.Focused;
            if (f is null || *(nint*)f.Handle != Menus.Base + Addresses.MapSceneVtable) return null;
            var c = *(nint*)(f.Handle + Addresses.MapController);
            return c != 0 && *(nint*)c == Menus.Base + Addresses.MapControllerVtable ? new MapView(c) : null;
        }
    }

    private ref float F(int offset) => ref *(float*)(_c + offset);

    /// <summary>The map's cursor, in the world (X, Z): what A selects or puts a waypoint on.</summary>
    public Vector2 Cursor
    {
        get => new(F(Addresses.MapCursorX), F(Addresses.MapCursorZ));
        set { F(Addresses.MapCursorX) = value.X; F(Addresses.MapCursorZ) = value.Y; }
    }

    /// <summary>Where the camera is (X, Z), easing towards <see cref="Target"/>.</summary>
    public Vector2 Camera
    {
        get => new(F(Addresses.MapCameraX), F(Addresses.MapCameraZ));
        set { F(Addresses.MapCameraX) = value.X; F(Addresses.MapCameraZ) = value.Y; }
    }

    /// <summary>Where the camera is going (X, Z).</summary>
    public Vector2 Target
    {
        get => new(F(Addresses.MapTargetX), F(Addresses.MapTargetZ));
        set { F(Addresses.MapTargetX) = value.X; F(Addresses.MapTargetZ) = value.Y; }
    }

    /// <summary>The zoom, 0..1 as the game keeps it (kept within <see cref="ZoomMin"/>..<see cref="ZoomMax"/>).</summary>
    public float Zoom
    {
        get => F(Addresses.MapZoom);
        set
        {
            float lo = Math.Min(ZoomMin, ZoomMax), hi = Math.Max(ZoomMin, ZoomMax);
            F(Addresses.MapZoom) = Math.Clamp(value, lo, hi);
        }
    }

    public float ZoomMin => F(Addresses.MapZoomMin);
    public float ZoomMax => F(Addresses.MapZoomMax);

    /// <summary>How far the cursor goes from the middle before the camera follows it (world units).</summary>
    public float Leash
    {
        get => F(Addresses.MapLeash);
        set => F(Addresses.MapLeash) = value;
    }

    /// <summary>World units per UI pixel: the map shows its zoom's distance from the middle to the top edge.</summary>
    public float UnitsPerPixel => 2f * F(Addresses.MapZoomShown) / Menus.Size.Y;

    /// <summary>The spot under <paramref name="ui"/> (a point in the UI's pixels), in the world (X, Z).</summary>
    public Vector2 WorldAt(Vector2 ui)
    {
        var size = Menus.Size;
        float k = UnitsPerPixel;
        return new Vector2(F(Addresses.MapCameraX) + (ui.X - size.X / 2) * k, F(Addresses.MapCameraZ) - (ui.Y - size.Y / 2) * k);
    }

    /// <summary>Moves what's on the map by <paramref name="ui"/> UI pixels (a drag): the camera and where it's going.</summary>
    public void Pan(Vector2 ui)
    {
        float k = UnitsPerPixel;
        F(Addresses.MapTargetX) -= ui.X * k;
        F(Addresses.MapCameraX) -= ui.X * k;
        F(Addresses.MapTargetZ) += ui.Y * k;
        F(Addresses.MapCameraZ) += ui.Y * k;
    }

    /// <summary>The map's address (two MapViews of the same map have the same one).</summary>
    public nint Handle => _c;
}
