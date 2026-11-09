using System.Diagnostics;
using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>The game's camera. Any thread.</summary>
public static unsafe class Camera
{
    private static volatile float s_shake;
    private static volatile bool s_ready;
    private static nint s_base;
    private static void** s_mountedOriginal, s_chaseOriginal;
    // The shake's own state: only the thread the game draws from touches it.
    private static long s_last;
    private static float s_envelope, s_time;

    /// <summary>
    /// Shakes the camera (driver, hood, bumper and chase views; not the others): 0 still, 1 a good rumble
    /// (peaks about half a degree), more for more. It fades in and back out by itself, so just set it and
    /// set it back to 0.
    /// </summary>
    public static float Shake
    {
        get => s_shake;
        set
        {
            Game.RequireBase("Camera.Shake");
            s_shake = float.IsFinite(value) ? Math.Clamp(value, 0, 10) : 0;
        }
    }

    /// <summary>True if the camera can be shaken (the game's code is as expected).</summary>
    public static bool IsReady => Game.IsReady && s_ready;

    internal static void Install(nint b, FH2Mod* owner)
    {
        if (Game.Mismatch(b, Addresses.Camera) is { } bad)
        {
            Game.Log(owner, $"Camera.Shake off: the game's code isn't as expected at {bad}");
            return;
        }
        s_base = b;
        s_mountedOriginal = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        s_chaseOriginal = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        var api = ModPlatform.Api;
        // The functions are hooked rather than the vtable slots, so camera mods that hook the slots still call through.
        var mounted = api->HookFunction(owner, u.Str("FH2 camera (mounted view matrix)"),
            (void*)(b + Addresses.ViewMatrixMounted.Offset), (delegate* unmanaged<nint, float*, nint>)&OnMounted, s_mountedOriginal);
        var chase = api->HookFunction(owner, u.Str("FH2 camera (chase view matrix)"),
            (void*)(b + Addresses.ViewMatrixChase.Offset), (delegate* unmanaged<nint, float*, nint>)&OnChase, s_chaseOriginal);
        bool ok = mounted is not null && api->EnableHooks(owner, mounted) != 0;
        ok &= chase is not null && api->EnableHooks(owner, chase) != 0;
        if (!ok) Game.Log(owner, "Camera.Shake: couldn't hook the cameras (it may work in some views only)");
        s_ready = ok;
    }

    /// <summary>A hosted mod is being replaced: it can't stop the shake any more, so it stops now.</summary>
    internal static void Forget() => s_shake = 0;

    [UnmanagedCallersOnly]
    private static nint OnMounted(nint camera, float* m)
    {
        var r = ((delegate* unmanaged<nint, float*, nint>)*s_mountedOriginal)(camera, m);
        ShakeMatrix(camera, m);
        return r;
    }

    [UnmanagedCallersOnly]
    private static nint OnChase(nint camera, float* m)
    {
        var r = ((delegate* unmanaged<nint, float*, nint>)*s_chaseOriginal)(camera, m);
        ShakeMatrix(camera, m);
        return r;
    }

    private static void ShakeMatrix(nint camera, float* m)
    {
        if (camera == 0 || m is null) return;
        float strength = s_shake;
        if (strength <= 0 && s_envelope < 0.001f) return;
        var vt = (int)(*(nint*)camera - s_base);
        if (Array.IndexOf(Addresses.ShakeCameraVtables, vt) < 0) return;

        // Fades in over about 0.2 s and out over 0.4 s. A function of the time, so a matrix asked for twice in one
        // frame comes out the same.
        long now = Stopwatch.GetTimestamp();
        float dt = s_last == 0 ? 0 : (float)((now - s_last) / (double)Stopwatch.Frequency);
        s_last = now;
        if (dt is < 0 or > 0.1f) dt = 0;
        float target = strength > 0 ? 1 : 0;
        s_envelope += (target - s_envelope) * Math.Min(1, dt * (target > s_envelope ? 5f : 2.5f));
        s_time = (s_time + dt) % 1000;
        if (s_envelope < 0.001f) return;

        for (int i = 0; i < 12; i++)
            if (!float.IsFinite(m[i])) return;
        float* r0 = m, r1 = m + 4, r2 = m + 8;
        float d02 = r0[0] * r2[0] + r0[1] * r2[1] + r0[2] * r2[2];
        float d12 = r1[0] * r2[0] + r1[1] * r2[1] + r1[2] * r2[2];
        float l2 = r2[0] * r2[0] + r2[1] * r2[1] + r2[2] * r2[2];
        if (MathF.Abs(d02) > 0.05f || MathF.Abs(d12) > 0.05f || l2 < 0.9f || l2 > 1.1f) return;   // not a rotation: leave it

        // A slower wobble under a fast buzz, at frequencies that don't line up.
        const float Tau = MathF.Tau;
        float t = s_time;
        float a = s_envelope * Math.Max(strength, 0.0001f) * 0.008f;
        if (strength <= 0) a = s_envelope * s_fadeFrom * 0.008f;
        else s_fadeFrom = strength;
        float pitch = a * (0.55f * MathF.Sin(Tau * 4.3f * t) + 0.30f * MathF.Sin(Tau * 13.7f * t + 1.1f) + 0.15f * MathF.Sin(Tau * 23.1f * t + 2.3f));
        float yaw = a * (0.35f * MathF.Sin(Tau * 3.1f * t + 0.7f) + 0.20f * MathF.Sin(Tau * 17.3f * t + 0.4f));
        float roll = a * (0.60f * MathF.Sin(Tau * 5.9f * t + 1.9f) + 0.35f * MathF.Sin(Tau * 19.7f * t + 0.2f));
        Turn(r2, r0, yaw);
        Turn(r2, r1, pitch);
        Turn(r0, r1, roll);
    }

    private static float s_fadeFrom;   // the strength it's fading out from

    // Rotates row x towards row y.
    private static void Turn(float* x, float* y, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        for (int k = 0; k < 4; k++)
        {
            float nx = c * x[k] + s * y[k], ny = c * y[k] - s * x[k];
            x[k] = nx;
            y[k] = ny;
        }
    }
}
