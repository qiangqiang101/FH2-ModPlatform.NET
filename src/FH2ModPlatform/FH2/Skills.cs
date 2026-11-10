using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>The skills whose timing <see cref="Skills"/> can change.</summary>
public enum Skill
{
    /// <summary>Wreckage: smashing scenery. The game waits 2.5 s before it awards another.</summary>
    Wreckage,
    /// <summary>Lumberjack: smashing log piles (and trees a mod made count as one).</summary>
    Lumberjack,
    /// <summary>Sideswipe: rubbing against another car.</summary>
    Sideswipe,
}

/// <summary>
/// The skills' timing: how long after awarding one the game waits before it can award it again (Skills.xml's
/// RetriggerDelay). Smashing through a forest would otherwise award Lumberjack after Lumberjack, or never again
/// for seconds.
/// </summary>
/// <remarks>
/// Set the delays in <c>OnInit</c>: the game reads its skills once, while it starts. A delay set later applies
/// straight away if that skill has been read already. Any thread. On another build of the game it does nothing,
/// and <c>platform.log</c> says so.
/// </remarks>
public static unsafe class Skills
{
    // Each skill's vtable; slot 14 loads its settings from Skills.xml, and SkillBase keeps RetriggerDelay at +0x24.
    private static readonly (int Vtable, int Load, string Name)[] s_skills =
    [
        (0x2D4BBF0, 0xA81770, "Destruction (Wreckage)"),
        (0x2D4BA28, 0xA81930, "Lumberjack"),
        (0x2D4B270, 0xA80D90, "SideSwipe"),
    ];
    private const int LoadSlot = 14, RetriggerField = 0x24;

    private static readonly float[] s_delay = [-1, -1, -1];        // -1: the game's own
    private static readonly nint[] s_object = new nint[3];         // the skill, once the game has read it
    private static readonly void**[] s_original = new void**[3];
    private static readonly object s_lock = new();
    private static bool s_started;

    /// <summary>
    /// Sets how many seconds after awarding <paramref name="skill"/> the game waits before it can award it again.
    /// A negative value keeps the game's own.
    /// </summary>
    public static void SetRetriggerDelay(Skill skill, float seconds)
    {
        int i = (int)skill;
        if ((uint)i >= 3) throw new ArgumentOutOfRangeException(nameof(skill));
        Volatile.Write(ref s_delay[i], seconds);
        Start();
        nint o = Volatile.Read(ref s_object[i]);
        if (o != 0 && seconds >= 0) Apply(i, o);
    }

    /// <summary>The delay the game uses for <paramref name="skill"/> now, in seconds; null before it has read its skills.</summary>
    public static float? RetriggerDelay(Skill skill)
    {
        int i = (int)skill;
        if ((uint)i >= 3) throw new ArgumentOutOfRangeException(nameof(skill));
        nint o = Volatile.Read(ref s_object[i]);
        return o != 0 && GameCode.TryRead<float>(o + RetriggerField, out var v) ? v : null;
    }

    private static void Start()
    {
        lock (s_lock)
        {
            if (s_started) return;
            s_started = true;
        }
        new Thread(Install) { IsBackground = true, Name = "FH2.Skills start" }.Start();
    }

    private static void Install()
    {
        var owner = ModPlatform.GameOwner;
        try
        {
            var b = ModPlatform.WaitForCode();
            if (b == 0) return;
            delegate* unmanaged<nint, nint, void>[] detours = [&LoadWreckage, &LoadLumberjack, &LoadSideswipe];
            for (int i = 0; i < 3; i++)
            {
                var (vt, load, name) = s_skills[i];
                nint slot = b + vt + LoadSlot * 8;
                if (!GameCode.SlotMatchesBuild(slot, b + load))
                {
                    Game.Log(owner, $"Skills: {name}'s settings aren't loaded where this build has them; its timing stays the game's");
                    continue;
                }
                // the slot is known before the hook goes on: the game may load that skill the moment it does
                var original = Hook.NewSlot();
                s_original[i] = original;
                using var u = new Utf8Scope();
                var h = ModPlatform.Api->HookPointer(owner, u.Str($"FH2.Skills {name} load"), (void**)slot, detours[i], original);
                if (h is null || ModPlatform.Api->EnableHooks(owner, h) == 0)
                    Game.Log(owner, $"Skills: couldn't hook {name}'s settings (platform.log says why); its timing stays the game's");
            }
        }
        catch (Exception e)
        {
            Game.Log(owner, "Skills off: " + e.Message);
        }
    }

    [UnmanagedCallersOnly] private static void LoadWreckage(nint skill, nint node) => Loaded(0, skill, node);
    [UnmanagedCallersOnly] private static void LoadLumberjack(nint skill, nint node) => Loaded(1, skill, node);
    [UnmanagedCallersOnly] private static void LoadSideswipe(nint skill, nint node) => Loaded(2, skill, node);

    private static void Loaded(int i, nint skill, nint node)
    {
        var original = s_original[i];
        if (original is not null && *original is not null) ((delegate* unmanaged<nint, nint, void>)*original)(skill, node);
        try
        {
            Volatile.Write(ref s_object[i], skill);
            Apply(i, skill);
        }
        catch (Exception e)
        {
            Game.Log(ModPlatform.GameOwner, "Skills: " + e.Message);   // never out into the game's code
        }
    }

    // Only over a delay the game has set to something sensible: the field is there and was read.
    private static void Apply(int i, nint skill)
    {
        float want = Volatile.Read(ref s_delay[i]);
        if (want < 0 || !GameCode.TryRead<float>(skill + RetriggerField, out var now)) return;
        if (now < 0 || now >= 60 || (i == 0 && now == 0)) return;
        *(float*)(skill + RetriggerField) = want;
    }
}
