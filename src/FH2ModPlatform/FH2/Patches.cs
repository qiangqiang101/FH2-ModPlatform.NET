using FH2ModPlatform;
using FH2ModPlatform.Interop;

namespace FH2;

/// <summary>
/// Changes to numbers in the game's code, made for the FH2 classes (the garage's size, livery layer limits): all of a
/// set or none, after checking each against the exe, on a thread of their own once the game's code is there.
/// </summary>
internal static unsafe class Patches
{
    internal readonly record struct Edit(string Name, int Offset, byte[] Before, byte[] After);

    /// <summary>Queues <paramref name="edits"/>; <paramref name="done"/> gets whether they went in.</summary>
    public static void Apply(string what, Edit[] edits, Action<bool>? done = null)
    {
        new Thread(() =>
        {
            bool ok = false;
            try { ok = ApplyNow(what, edits); }
            catch (Exception e) { Game.Log(ModPlatform.GameOwner, $"{what}: {e.Message}"); }
            done?.Invoke(ok);
        }) { IsBackground = true, Name = "FH2 " + what }.Start();
    }

    private static bool ApplyNow(string what, Edit[] edits)
    {
        var owner = ModPlatform.GameOwner;
        var b = ModPlatform.WaitForCode();
        if (b == 0) return false;
        foreach (var e in edits)
        {
            if (GameCode.MatchesBuild(b + e.Offset, e.Before)) continue;
            Game.Log(owner, $"{what} off: the game's code isn't as expected at {e.Name} (another game build, or another mod changed it)");
            return false;
        }
        var made = new List<nint>();
        bool ok = true;
        using (var u = new Utf8Scope())
        {
            foreach (var e in edits)
            {
                fixed (byte* before = e.Before)
                fixed (byte* after = e.After)
                {
                    var h = ModPlatform.Api->PatchBytes(owner, u.Str($"FH2 {what}: {e.Name}"), (void*)(b + e.Offset), before, after, (uint)e.After.Length);
                    if (h is null) { ok = false; break; }
                    made.Add((nint)h);
                }
            }
            if (ok)
                foreach (var h in made)
                    ok &= ModPlatform.Api->EnableHooks(owner, (FH2ModPlatform.Native.FH2Hook*)h) != 0;
            if (!ok)
                foreach (var h in made)
                    ModPlatform.Api->DisableHooks(owner, (FH2ModPlatform.Native.FH2Hook*)h);
        }
        if (!ok) Game.Log(owner, $"{what} off: couldn't change the game's code (platform.log says why above)");
        return ok;
    }

    /// <summary>The bytes of <paramref name="prefix"/>, then <paramref name="value"/> little-endian (int or ushort), then <paramref name="suffix"/>.</summary>
    public static byte[] With(string prefix, int value, string suffix = "") =>
        [.. GameCode.Hex(prefix), .. BitConverter.GetBytes(value), .. suffix.Length > 0 ? GameCode.Hex(suffix) : []];

    public static byte[] Repeat(ushort value, int count)
    {
        var r = new byte[count * 2];
        for (int i = 0; i < count; i++) BitConverter.TryWriteBytes(r.AsSpan(i * 2), value);
        return r;
    }
}

/// <summary>Livery editor limits.</summary>
public static unsafe class Livery
{
    /// <summary>The game's own layer limits.</summary>
    public const int GameHoodAndSidesLimit = 3000, GameBumpersAndWingLimit = 1000;
    /// <summary>The most tested in the game (what FH2 Layer Patcher shipped). The limits are 16-bit.</summary>
    public const int MaxLayerLimit = 20000;

    private static int s_set;

    /// <summary>
    /// Raises the livery editor's layer limits: the hood and the two sides (the game's is 3,000 each), and the bumpers and
    /// the wing (1,000), up to <see cref="MaxLayerLimit"/>. Call it once, from OnInit; it applies as the game's code comes
    /// in, and not at all if any part of it isn't as expected (platform.log says so). Any thread.
    /// </summary>
    /// <remarks>A livery with more layers than the game's limits may not open on a game without this.</remarks>
    public static void SetLayerLimits(int hoodAndSides, int bumpersAndWing)
    {
        if (Interlocked.Exchange(ref s_set, 1) != 0)
        {
            Game.Log(ModPlatform.GameOwner, "Livery.SetLayerLimits: already set this session (a change needs a restart)");
            return;
        }
        var hood = (ushort)Math.Clamp(hoodAndSides, GameHoodAndSidesLimit, MaxLayerLimit);
        var bumpers = (ushort)Math.Clamp(bumpersAndWing, GameBumpersAndWingLimit, MaxLayerLimit);
        // FH2LiveryLayerPatch's edits: 16-bit limits held as immediates in the livery editor's code.
        Patches.Edit[] edits =
        [
            new("bumpers and wing (1)", 0x2C01EF, Patches.Repeat(GameBumpersAndWingLimit, 1), Patches.Repeat(bumpers, 1)),
            new("bumpers and wing (2)", 0x2C01F9, Patches.Repeat(GameBumpersAndWingLimit, 2), Patches.Repeat(bumpers, 2)),
            new("hood and sides (1)", 0x2C01FE, Patches.Repeat(GameHoodAndSidesLimit, 1), Patches.Repeat(hood, 1)),
            new("hood and sides (2)", 0x2C0208, Patches.Repeat(GameHoodAndSidesLimit, 2), Patches.Repeat(hood, 2)),
        ];
        Patches.Apply("livery layer limits", edits, ok =>
        {
            if (ok) Game.Log(ModPlatform.GameOwner, $"Livery: layer limits {hood} (hood, sides) and {bumpers} (bumpers, wing)");
        });
    }
}
