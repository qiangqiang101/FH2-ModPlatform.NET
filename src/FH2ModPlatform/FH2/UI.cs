using System.Runtime.InteropServices;
using System.Text;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>
/// Things on the screen. <see cref="Notify"/> shows the game's own HUD notification (the panel
/// with a title and a line of text the game shows for skills and rewards), from any thread.
/// </summary>
/// <remarks>
/// Notifications show one at a time, about 5 seconds apart, while the HUD is up (driving);
/// ones that can't show within 2 minutes are dropped. The game's HUD font is ASCII: other
/// characters show as '?'. About 100 characters fit on a line.
/// </remarks>
public static unsafe class UI
{
    private const int ShowEveryMs = 5000;      // the panel stays up about 4.5 s
    private const int KeepForMs = 120_000;
    private const int MaxQueued = 8;
    private const int MaxText = 100;

    private sealed record Message(string Title, string Text, string Icon, long Queued);

    private static readonly object s_lock = new();
    private static readonly Queue<Message> s_queue = new();
    private static readonly nint[] s_live = new nint[4];   // live notification components, newest last
    private static long s_lastShown;
    private static volatile bool s_started, s_ready;

    /// <summary>
    /// Shows <paramref name="title"/> with <paramref name="text"/> under it, as the game's HUD
    /// notification. Any thread. If the game's code isn't as expected, nothing shows (see
    /// <see cref="IsReady"/>; the log says why).
    /// </summary>
    public static void Notify(string title, string text = "")
    {
        Start();
        lock (s_lock)
        {
            if (s_queue.Count >= MaxQueued) s_queue.Dequeue();
            s_queue.Enqueue(new Message(Ascii(title), Ascii(text), "", Environment.TickCount64));
        }
    }

    /// <summary>True once the notification hooks are in (the game's code checked).</summary>
    public static bool IsReady
    {
        get
        {
            Start();
            return s_ready;
        }
    }

    private static string Ascii(string s)
    {
        var sb = new StringBuilder();
        foreach (var r in s.EnumerateRunes())
            sb.Append(r.Value < 0x20 ? ' ' : r.Value < 0x80 ? (char)r.Value : '?');
        var t = sb.ToString();
        return t.Length > MaxText ? t[..(MaxText - 3)] + "..." : t;
    }

    // ---- Hooks: the component's life, and a tick on the game's thread ------------------------

    /// <summary>
    /// Puts the hooks in now, at startup: the HUD's notification panel is made once, when the game builds its
    /// HUD, and hooks put in after that never see it (a first Notify would then show nothing).
    /// </summary>
    internal static void Prepare() => Start();

    private static void Start()
    {
        if (s_started) return;
        lock (s_lock)
        {
            if (s_started) return;
            s_started = true;
        }
        new Thread(Install) { IsBackground = true, Name = "FH2.UI start" }.Start();
    }

    private static void Install()
    {
        var owner = ModPlatform.GameOwner;
        try
        {
            var b = ModPlatform.WaitForCode();
            if (b == 0) return;
            Addresses.Code[] code =
            [
                Addresses.NotificationCtorAt, Addresses.NotificationDtorAt, Addresses.AchievementsTickAt,
                Addresses.MakeDisplayString, Addresses.ShowNotification,
            ];
            if (Game.Mismatch(b, code) is { } bad)
            {
                Log(owner, $"UI.Notify off: the game's code isn't as expected at {bad}");
                return;
            }
            s_base = b;
            using var u = new Utf8Scope();
            var made = ModPlatform.Api->HookInstruction(owner, u.Str("FH2.UI notification made"), (void*)(b + Addresses.NotificationCtorAt.Offset), &OnMade, null);
            var gone = ModPlatform.Api->HookInstruction(owner, u.Str("FH2.UI notification gone"), (void*)(b + Addresses.NotificationDtorAt.Offset), &OnGone, null);
            var tick = ModPlatform.Api->HookInstruction(owner, u.Str("FH2.UI notifications (AchievementsModule tick)"), (void*)(b + Addresses.AchievementsTickAt.Offset), &OnTick, null);
            if (made is null || gone is null || tick is null ||
                ModPlatform.Api->EnableHooks(owner, made) == 0 || ModPlatform.Api->EnableHooks(owner, gone) == 0 ||
                ModPlatform.Api->EnableHooks(owner, tick) == 0)
            {
                Log(owner, "UI.Notify off: couldn't hook the HUD's notifications");
                return;
            }
            s_ready = true;
        }
        catch (Exception e)
        {
            Log(owner, "UI.Notify off: " + e);
        }
    }

    private static nint s_base;

    // The constructor and destructor have just written the vtable through rbx: a real component has ours.
    private static bool IsComponent(nint p) => p != 0 && *(nint*)p == s_base + Addresses.NotificationVtable;

    [UnmanagedCallersOnly]
    private static void OnMade(FH2Context* ctx, void* user)
    {
        var c = (nint)ctx->Rbx;
        if (!IsComponent(c)) return;
        lock (s_lock)
        {
            if (Array.IndexOf(s_live, c) >= 0) return;
            Array.Copy(s_live, 1, s_live, 0, s_live.Length - 1);
            s_live[^1] = c;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnGone(FH2Context* ctx, void* user)
    {
        var c = (nint)ctx->Rbx;
        lock (s_lock)
        {
            int i = Array.IndexOf(s_live, c);
            if (i < 0) return;
            Array.Copy(s_live, 0, s_live, 1, i);
            s_live[0] = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnTick(FH2Context* ctx, void* user)
    {
        long now = Environment.TickCount64;
        if (s_lastShown != 0 && now - s_lastShown < ShowEveryMs) return;
        Message m;
        nint component;
        lock (s_lock)
        {
            while (s_queue.Count > 0 && now - s_queue.Peek().Queued > KeepForMs) s_queue.Dequeue();
            component = s_live[^1];
            if (s_queue.Count == 0 || component == 0) return;
            m = s_queue.Dequeue();
        }
        try
        {
            var make = (delegate* unmanaged<nint*, byte*, int, nint*>)(s_base + Addresses.MakeDisplayString.Offset);
            var show = (delegate* unmanaged<nint, nint*, nint*, nint*, void>)(s_base + Addresses.ShowNotification.Offset);
            using var u = new Utf8Scope();
            nint title = 0, text = 0, icon = 0;
            make(&title, u.Str(m.Title), -1);
            make(&text, u.Str(m.Text), -1);
            make(&icon, u.Str(m.Icon), -1);
            show(component, &title, &text, &icon);   // takes the strings over
            s_lastShown = now;
        }
        catch (Exception e)
        {
            Log(ModPlatform.GameOwner, "UI.Notify failed: " + e.Message);
        }
    }

    private static void Log(FH2Mod* owner, string line)
    {
        if (owner is null || !ModPlatform.IsAttached) return;
        using var u = new Utf8Scope();
        ModPlatform.Api->Log(owner, u.Str(line));
    }
}
