using System.Numerics;
using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>
/// The HUD's speedometer: whether it's up, and values for a speedometer of your own to show. Any thread.
/// </summary>
/// <remarks>
/// The speedometer's XAML (ui/Controls/HUDSpeedometerControl.xaml, replaced with <see cref="Mod.SetFile(string, byte[])"/>)
/// binds to the RPM gauge's number items, <c>RPMGaugeNumbers[n].XPos</c> and <c>.YPos</c>. Items 16 to 20 are
/// past the game's own numbers: set them with <see cref="SetGaugeItem"/> and bind your gauge (a bar's Ratio, a
/// glow's angle, an Opacity) to them. Values are applied every HUD frame, so set them whenever you like.
/// </remarks>
public static unsafe class Hud
{
    private static volatile bool s_ready;
    private static nint s_base;
    private static void** s_original;
    private static long s_lastTick;
    private static readonly float[] s_x = new float[Addresses.GaugeItemCount], s_y = new float[Addresses.GaugeItemCount];
    private static readonly bool[] s_set = new bool[Addresses.GaugeItemCount];
    private static readonly object s_lock = new();

    /// <summary>True if the HUD can be read and fed (the game's code is as expected).</summary>
    public static bool IsReady => Game.IsReady && s_ready;

    /// <summary>
    /// True while the speedometer is up: you're driving with the HUD showing (not in a menu, the garage, a car
    /// meet lobby, a race intro, a rewind or the photo mode).
    /// </summary>
    public static bool IsSpeedometerShown => IsReady && Environment.TickCount64 - Interlocked.Read(ref s_lastTick) < 200;

    /// <summary>
    /// Sets RPM gauge item <paramref name="index"/> (16..20 are free for your own) to <paramref name="x"/> (its XPos)
    /// and <paramref name="y"/> (its YPos), for the speedometer XAML to show.
    /// </summary>
    public static void SetGaugeItem(int index, float x, float y)
    {
        if ((uint)index >= Addresses.GaugeItemCount) throw new ArgumentOutOfRangeException(nameof(index), "0..20");
        lock (s_lock)
        {
            s_x[index] = x;
            s_y[index] = y;
            s_set[index] = true;
        }
    }

    /// <summary>Stops setting the gauge items (they keep their last values until the game sets them).</summary>
    public static void ClearGaugeItems()
    {
        lock (s_lock) Array.Clear(s_set);
    }

    internal static void Install(nint b, FH2Mod* owner)
    {
        if (Game.Mismatch(b, Addresses.Speedometer) is { } bad)
        {
            Game.Log(owner, $"Hud off: the game's code isn't as expected at {bad}");
            return;
        }
        s_base = b;
        s_original = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        var hook = ModPlatform.Api->HookCall(owner, u.Str("FH2 HUD (HUDStatusSpeedometer::UpdateGauges)"),
            (void*)(b + Addresses.SpeedometerCall.Offset), (delegate* unmanaged<nint, float, void>)&OnSpeedometer, s_original);
        if (hook is null || ModPlatform.Api->EnableHooks(owner, hook) == 0)
        {
            Game.Log(owner, "Hud off: couldn't hook the speedometer");
            return;
        }
        s_ready = true;
    }

    [UnmanagedCallersOnly]
    private static void OnSpeedometer(nint speedo, float rpm)
    {
        ((delegate* unmanaged<nint, float, void>)*s_original)(speedo, rpm);
        if (speedo == 0) return;
        Interlocked.Exchange(ref s_lastTick, Environment.TickCount64);
        try
        {
            Apply(speedo);
        }
        catch (Exception e)
        {
            Game.Log(ModPlatform.GameOwner, "Hud: " + e.Message);
        }
    }

    private static void Apply(nint speedo)
    {
        Span<float> xs = stackalloc float[Addresses.GaugeItemCount], ys = stackalloc float[Addresses.GaugeItemCount];
        Span<bool> set = stackalloc bool[Addresses.GaugeItemCount];
        bool any = false;
        lock (s_lock)
        {
            for (int i = 0; i < Addresses.GaugeItemCount; i++)
            {
                xs[i] = s_x[i];
                ys[i] = s_y[i];
                set[i] = s_set[i];
                any |= s_set[i];
            }
        }
        if (!any) return;

        var a = *(nint*)(speedo + Addresses.SpeedometerItems);
        var b = a == 0 ? 0 : *(nint*)(a + Addresses.GaugeItemsA);
        var c = b == 0 ? 0 : *(nint*)(b + Addresses.GaugeItemsB);
        var items = c == 0 ? null : *(nint**)(c + Addresses.GaugeItemsC);
        if (items is null) return;

        var xProp = ((delegate* unmanaged<nint>)(s_base + Addresses.GaugeXPosProperty.Offset))();
        var yProp = ((delegate* unmanaged<nint>)(s_base + Addresses.GaugeYPosProperty.Offset))();
        var raise = (delegate* unmanaged<nint, nint, void>)(s_base + Addresses.RaiseItemProperty.Offset);
        for (int i = 0; i < Addresses.GaugeItemCount; i++)
        {
            if (!set[i]) continue;
            var item = items[i];
            if (item == 0) continue;
            var px = (float*)(item + Addresses.GaugeItemX);
            if (MathF.Abs(*px - xs[i]) > 0.0005f)
            {
                *px = xs[i];
                if (xProp != 0) raise(item, xProp);
            }
            var py = (float*)(item + Addresses.GaugeItemY);
            if (MathF.Abs(*py - ys[i]) > 0.0005f)
            {
                *py = ys[i];
                if (yProp != 0) raise(item, yProp);
            }
        }
    }
}
