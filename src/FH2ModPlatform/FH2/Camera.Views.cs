using System.Numerics;
using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;

namespace FH2;

/// <summary>Which camera is showing the game (<see cref="Camera.Kind"/>).</summary>
public enum CameraKind
{
    /// <summary>None known: a menu, a cutscene, or a camera the SDK doesn't know.</summary>
    Unknown,
    Chase,
    ChaseNear,
    ChaseFar,
    /// <summary>Inside the car.</summary>
    Driver,
    Hood,
    Bumper,
    /// <summary>A free camera (photo mode's and the like).</summary>
    Free,
    /// <summary>A replay's or a cutscene's animated camera.</summary>
    Animated,
    /// <summary>The replay's cameras around the car.</summary>
    Replay,
}

/// <summary>
/// The view a driving camera is drawn from this frame (<see cref="Camera.ViewUpdated"/>): its axes and translation, as
/// the game's view matrix has them. Change them to move or turn the camera.
/// </summary>
public sealed unsafe class CameraView
{
    private float* _m;
    internal void Use(float* m, CameraKind kind) { _m = m; Kind = kind; }

    public CameraKind Kind { get; private set; }

    private Vector3 Row(int r) => new(_m[r * 4], _m[r * 4 + 1], _m[r * 4 + 2]);
    private void SetRow(int r, Vector3 v) { _m[r * 4] = v.X; _m[r * 4 + 1] = v.Y; _m[r * 4 + 2] = v.Z; }

    public Vector3 Right { get => Row(0); set => SetRow(0, value); }
    public Vector3 Up { get => Row(1); set => SetRow(1, value); }
    public Vector3 Forward { get => Row(2); set => SetRow(2, value); }

    /// <summary>The matrix's translation column, as the game keeps it.</summary>
    public Vector3 Translation
    {
        get => new(_m[3], _m[7], _m[11]);
        set { _m[3] = value.X; _m[7] = value.Y; _m[11] = value.Z; }
    }
}

// Which camera is on, its field of view, where it is, and the director's modes, flash and replay shots
// (FnFNitro's FOV boost, FH2 ANNA's cinematic camera; vtables from Deuquan's FH2-CameraMods' findings).
public static unsafe partial class Camera
{
    private static readonly Dictionary<int, CameraKind> s_kinds = new()
    {
        [0x2E783A0] = CameraKind.Chase,
        [0x2E78610] = CameraKind.ChaseNear,
        [0x2E78880] = CameraKind.ChaseFar,
        [0x2E78120] = CameraKind.Driver,
        [0x2E77EA8] = CameraKind.Hood,
        [0x2E703B0] = CameraKind.Bumper,
        [0x2E78AF0] = CameraKind.Free,
        [0x2E78D98] = CameraKind.Free,
        [0x2E79040] = CameraKind.Free,
        [0x2E779D8] = CameraKind.Animated,
        [0x2C6CB70] = CameraKind.Replay,
    };

    private static CameraKind KindOf(nint camera)
    {
        if (camera == 0 || s_base == 0 || !GameCode.TryRead<nint>(camera, out var vt)) return CameraKind.Unknown;
        return s_kinds.TryGetValue((int)(vt - s_base), out var k) ? k : CameraKind.Unknown;
    }

    // ---- The director -------------------------------------------------------------------------

    private static nint PrimaryView()
    {
        var b = Game.Base;
        if (!GameCode.TryRead<nint>(b + Addresses.PlayerManagerGlobal, out var mgr) || mgr == 0) return 0;
        if (!GameCode.TryRead<nint>(mgr + Addresses.ViewsChain1, out var sim) || sim == 0) return 0;
        if (!GameCode.TryRead<nint>(sim + Addresses.ViewsChain2, out var m) || m == 0) return 0;
        if (!GameCode.TryRead<nint>(m + Addresses.ViewsChain3, out var views) || views == 0) return 0;
        return GameCode.TryRead<nint>(views, out var view) ? view : 0;
    }

    // A virtual function of the game's, if it's in the exe's code.
    private static nint Method(nint obj, int slot)
    {
        if (!GameCode.TryRead<nint>(obj, out var vt) || !GameCode.TryRead<nint>(vt + slot, out var fn)) return 0;
        var b = Game.Base;
        return fn > b + 0x1000 && fn < b + 0x1DCC000 ? fn : 0;
    }

    private static nint Director()
    {
        var view = PrimaryView();
        var get = view != 0 ? Method(view, Addresses.ViewCameraSlot) : 0;
        if (get == 0) return 0;
        var camera = ((delegate* unmanaged<nint, nint>)get)(view);
        return GameCode.TryRead<nint>(camera, out var vt) && vt == Game.Base + Addresses.DirectorVtable ? camera : 0;
    }

    private static nint ActiveCamera()
    {
        var d = Director();
        return d != 0 && GameCode.TryRead<nint>(d + Addresses.DirectorActive, out var cam) ? cam : 0;
    }

    private static int s_directorState;   // 0 not checked, 1 ready, -1 the game's code isn't as expected

    private static bool DirectorReady()
    {
        if (s_directorState == 0)
        {
            var bad = Game.Mismatch(Game.Base, Addresses.CameraDirector);
            s_directorState = bad is null ? 1 : -1;
            if (bad is not null) Game.Log(ModPlatform.GameOwner, $"Camera's director (Kind, Group, Flash) off: the game's code isn't as expected at {bad}");
        }
        return s_directorState == 1;
    }

    /// <summary>Which camera is showing the game now. Game thread only.</summary>
    public static CameraKind Kind
    {
        get
        {
            Game.RequireGameThread("Camera.Kind");
            s_base = Game.Base;
            return DirectorReady() ? KindOf(ActiveCamera()) : CameraKind.Unknown;
        }
    }

    /// <summary>Where the camera showing the game is, in world coordinates, or null if it isn't known. Game thread only.</summary>
    public static Vector3? Position
    {
        get
        {
            Game.RequireGameThread("Camera.Position");
            if (!DirectorReady()) return null;
            var cam = ActiveCamera();
            var get = cam != 0 ? Method(cam, Addresses.CameraPositionSlot) : 0;
            if (get == 0) return null;
            double* p = stackalloc double[4];
            ((delegate* unmanaged<nint, double*, double*>)get)(cam, p);
            var v = new Vector3((float)p[0], (float)p[1], (float)p[2]);
            return float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? v : null;
        }
    }

    /// <summary>
    /// Which way the driving camera looks, from the last frame drawn with one of the chase, driver, hood or bumper
    /// cameras; null before there's been one. Any thread.
    /// </summary>
    public static Vector3? Forward => s_haveForward ? s_forward : null;

    private static Vector3 s_forward;
    private static volatile bool s_haveForward;

    /// <summary>
    /// The director's camera mode: the game's own number for the group of cameras it's using (13 is the replay's
    /// cameras around the car); -1 if it isn't known. Setting it switches, as the game's camera button does.
    /// Game thread only.
    /// </summary>
    public static int Group
    {
        get
        {
            Game.RequireGameThread("Camera.Group");
            if (!DirectorReady()) return -1;
            var d = Director();
            if (d == 0 || !GameCode.TryRead<nint>(d + Addresses.DirectorGroups, out var head) || head == 0) return -1;
            if (!GameCode.TryRead<nint>(d + Addresses.DirectorGroupCount, out var count) || count == 0) return -1;
            var current = ((delegate* unmanaged<nint, int*>)(Game.Base + Addresses.CurrentCameraGroup.Offset))(d + Addresses.DirectorGroups);
            return GameCode.TryRead<int>((nint)current, out var g) ? g : -1;
        }
        set
        {
            Game.RequireGameThread("Camera.Group");
            if (!DirectorReady() || value < 0) return;
            var d = Director();
            if (d != 0) ((delegate* unmanaged<nint, int, void>)(Game.Base + Addresses.SetCameraGroup.Offset))(d, value);
        }
    }

    /// <summary>
    /// Switches to the replay's camera shot <paramref name="label"/> around the car (17 and 19 are slow orbits), as
    /// FH2 ANNA's cinematic camera does. False if there's no director now. Game thread only.
    /// </summary>
    public static bool ShowReplayShot(byte label)
    {
        Game.RequireGameThread("Camera.ShowReplayShot");
        if (!DirectorReady()) return false;
        var d = Director();
        if (d == 0) return false;
        ((delegate* unmanaged<nint, byte, void>)(Game.Base + Addresses.ReplayShot.Offset))(d, label);
        return true;
    }

    /// <summary>The game's white flash between cameras. False if it can't flash now (one is already on). Game thread only.</summary>
    public static bool Flash()
    {
        Game.RequireGameThread("Camera.Flash");
        if (!DirectorReady()) return false;
        var b = Game.Base;
        var view = PrimaryView();
        if (view == 0 || !GameCode.TryRead<nint>(view, out var vt) || (vt != b + Addresses.ViewVtableA && vt != b + Addresses.ViewVtableB)) return false;
        if (!GameCode.TryRead<byte>(view + Addresses.ViewBusy, out var busy) || busy != 0) return false;
        ((delegate* unmanaged<nint, int, float, void>)(b + Addresses.CameraFlash.Offset))(view, 3, 0.6f);
        return true;
    }

    // ---- Field of view ------------------------------------------------------------------------

    private static volatile float s_fovScale = 1;
    private static void** s_fovMountedOriginal, s_fovChaseOriginal;

    /// <summary>
    /// The driving cameras' field of view (chase, driver, hood, bumper) as a multiple of the game's: 1.1 is 10% wider.
    /// 0.3 to 3. Any thread.
    /// </summary>
    public static float FovScale
    {
        get => s_fovScale;
        set => s_fovScale = float.IsFinite(value) ? Math.Clamp(value, 0.3f, 3f) : 1;
    }

    private static void InstallFov(nint b, FH2ModPlatform.Native.FH2Mod* owner)
    {
        if (Game.Mismatch(b, Addresses.CameraFov) is { } bad)
        {
            Game.Log(owner, $"Camera.FovScale off: the game's code isn't as expected at {bad}");
            return;
        }
        s_fovMountedOriginal = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        s_fovChaseOriginal = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        var api = ModPlatform.Api;
        var mounted = api->HookFunction(owner, u.Str("FH2 camera (mounted FOV)"), (void*)(b + Addresses.FovMounted.Offset),
            (delegate* unmanaged<nint, float>)&OnFovMounted, s_fovMountedOriginal);
        var chase = api->HookFunction(owner, u.Str("FH2 camera (chase FOV)"), (void*)(b + Addresses.FovChase.Offset),
            (delegate* unmanaged<nint, float>)&OnFovChase, s_fovChaseOriginal);
        bool ok = mounted is not null && api->EnableHooks(owner, mounted) != 0;
        ok &= chase is not null && api->EnableHooks(owner, chase) != 0;
        if (!ok) Game.Log(owner, "Camera.FovScale: couldn't hook the cameras' field of view (it may work in some views only)");
    }

    [UnmanagedCallersOnly]
    private static float OnFovMounted(nint camera) => Widen(camera, ((delegate* unmanaged<nint, float>)*s_fovMountedOriginal)(camera));

    [UnmanagedCallersOnly]
    private static float OnFovChase(nint camera) => Widen(camera, ((delegate* unmanaged<nint, float>)*s_fovChaseOriginal)(camera));

    private static float Widen(nint camera, float fov)
    {
        float scale = s_fovScale;
        if (scale == 1 || !float.IsFinite(fov) || fov <= 0 || camera == 0) return fov;
        var vt = (int)(*(nint*)camera - s_base);
        return Array.IndexOf(Addresses.ShakeCameraVtables, vt) >= 0 ? fov * scale : fov;
    }

    // ---- The view, each frame -----------------------------------------------------------------

    private static readonly object s_viewLock = new();
    private static readonly List<Action<CameraView>> s_viewHandlers = new();
    private static readonly CameraView s_view = new();

    /// <summary>
    /// Each time a driving camera (chase, driver, hood, bumper) works out its view, on the thread the game draws from:
    /// change the <see cref="CameraView"/> to move or turn the camera. Keep it short and allocation-free; an exception
    /// is logged and the view is left as it was.
    /// </summary>
    public static event Action<CameraView> ViewUpdated
    {
        add { lock (s_viewLock) s_viewHandlers.Add(value); }
        remove { lock (s_viewLock) s_viewHandlers.Remove(value); }
    }

    // From the view-matrix hooks, after the shake: note where it looks, then let mods change it.
    private static void ViewHandlers(nint camera, float* m)
    {
        if (camera == 0 || m is null) return;
        var vt = (int)(*(nint*)camera - s_base);
        if (Array.IndexOf(Addresses.ShakeCameraVtables, vt) < 0) return;
        var f = new Vector3(m[8], m[9], m[10]);
        if (float.IsFinite(f.X) && float.IsFinite(f.Y) && float.IsFinite(f.Z) && f.LengthSquared() > 0.5f)
        {
            s_forward = f;
            s_haveForward = true;
        }
        Action<CameraView>[] hs;
        lock (s_viewLock)
        {
            if (s_viewHandlers.Count == 0) return;
            hs = s_viewHandlers.ToArray();
        }
        Span<float> saved = stackalloc float[12];
        new Span<float>(m, 12).CopyTo(saved);
        s_view.Use(m, s_kinds.TryGetValue(vt, out var k) ? k : CameraKind.Unknown);
        foreach (var h in hs)
        {
            try { h(s_view); }
            catch (Exception e)
            {
                saved.CopyTo(new Span<float>(m, 12));
                Game.Log(ModPlatform.GameOwner, "Camera.ViewUpdated: " + e.Message);
            }
        }
    }

    internal static void ForgetViews(System.Reflection.Assembly a)
    {
        lock (s_viewLock) s_viewHandlers.RemoveAll(h => h.Method.DeclaringType?.Assembly == a);
    }
}
