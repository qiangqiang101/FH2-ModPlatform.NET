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
/// <para>
/// From platform 2.9.0 (API 10), notifications are the platform's pop-ups: one queue for every mod,
/// spaced around the game's own notifications, waiting while you're in a menu, loading or in photo
/// mode, and dropped if they can't show within 2 minutes. The title shows up to 28 characters (in
/// capitals) and the line up to 45; longer text is cut with "...". Up to 4 wait at once (for
/// hosted mods, all of them together): a 5th drops the oldest. The player can turn them off: the
/// "Pop-ups" row on Platform.NET's page (or a NativeAOT mod's own page), the platform's "Mod pop-ups",
/// or the game's Minor notifications option.
/// </para>
/// <para>
/// On older platforms the SDK shows them itself, one at a time, about 5 seconds apart, while you
/// drive, with up to about 100 characters on each line. Either way the text can be any language,
/// if the HUD's font for the game's language has its characters (Chinese with the game in Chinese, say).
/// </para>
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
    // When each was made. The constructor hook fires as soon as the vtable is written, partway through building it,
    // and a new HUD (after a teleport, entering an event) is still being put together: showing on a panel that new
    // ended the game. A panel is only used once it's this old.
    private static readonly Dictionary<nint, long> s_madeAt = [];
    private const int SettleMs = 3000;
    private static long s_lastShown;
    private static bool s_toldWaiting;
    private static int s_shown;
    private static volatile bool s_started, s_ready;
    // The platform's pop-ups (API 10): declared at startup, and then none of the hooks below go in.
    private static volatile bool s_toasts;
    private const uint UsesToasts = 0x0001;    // FH2_UI_USES_TOASTS
    private const uint StatusToasts = 0x0001;  // FH2_UI_TOASTS

    /// <summary>
    /// Shows <paramref name="title"/> with <paramref name="text"/> under it, as the game's HUD
    /// notification. Any thread. If the game's code isn't as expected, nothing shows (see
    /// <see cref="IsReady"/>; the log says why).
    /// </summary>
    public static void Notify(string title, string text = "")
    {
        if (s_toasts)
        {
            Toast(title, text);
            return;
        }
        Start();
        lock (s_lock)
        {
            if (s_queue.Count >= MaxQueued) s_queue.Dequeue();
            s_queue.Enqueue(new Message(Clean(title), Clean(text), "", Environment.TickCount64));
        }
    }

    /// <summary>True once the notification hooks are in (the game's code checked).</summary>
    public static bool IsReady
    {
        get
        {
            if (s_toasts)
                return ModPlatform.Api->UiStatus is null || (ModPlatform.Api->UiStatus(ModPlatform.GameOwner) & StatusToasts) != 0;
            Start();
            return s_ready;
        }
    }

    // One line: control characters become spaces, and a long one is cut (never inside a character).
    private static string Clean(string? s)
    {
        var runes = (s ?? "").EnumerateRunes().Select(r => r.Value < 0x20 ? new Rune(' ') : r).ToList();
        if (runes.Count > MaxText) runes = [.. runes.Take(MaxText - 3), new('.'), new('.'), new('.')];
        return string.Concat(runes);
    }

    // ---- Hooks: the component's life, and a tick on the game's thread ------------------------

    /// <summary>
    /// Puts the hooks in now, at startup: the HUD's notification panel is made once, when the game builds its
    /// HUD, and hooks put in after that never see it (a first Notify would then show nothing).
    /// </summary>
    /// <remarks>
    /// On platform 2.9.0 or later this declares the platform's pop-ups instead, for whoever owns the FH2
    /// classes' hooks (Platform.NET, or the NativeAOT mod itself): that has to happen during fh2mod_init,
    /// which is when this runs.
    /// </remarks>
    internal static void Prepare()
    {
        var owner = ModPlatform.GameOwner;
        if (owner is not null && ModPlatform.IsAttached && ModPlatform.Supports(ApiLevel.UiTemplates) &&
            ModPlatform.Api->UiUses is not null && ModPlatform.Api->ShowToast is not null &&
            ModPlatform.Api->UiUses(owner, UsesToasts) != 0)
        {
            s_toasts = true;
            s_started = true;   // no hooks of our own
            return;
        }
        Start();
    }

    // The platform's pop-up: it queues it, copies the text and logs what it shows (or why it can't).
    private static void Toast(string title, string text)
    {
        title = Clean(title);
        text = Clean(text);
        if (title.Trim().Length == 0) (title, text) = (text, "");   // a pop-up needs a title
        if (title.Trim().Length == 0) return;
        using var u = new Utf8Scope();
        var t = new FH2Toast
        {
            Size = (uint)sizeof(FH2Toast),
            Title = u.Str(title),
            Subtitle = text.Length == 0 ? null : u.Str(text),
        };
        ModPlatform.Api->ShowToast(ModPlatform.GameOwner, &t);
    }

    private static void Start()
    {
        if (s_started || s_toasts) return;
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
                Addresses.MakeDisplayString, Addresses.SetDisplayStringWide, Addresses.ShowNotification,
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
            s_madeAt[c] = Environment.TickCount64;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnGone(FH2Context* ctx, void* user)
    {
        var c = (nint)ctx->Rbx;
        lock (s_lock)
        {
            s_madeAt.Remove(c);
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
            while (s_queue.Count > 0 && now - s_queue.Peek().Queued > KeepForMs)
            {
                var old = s_queue.Dequeue();
                Log(ModPlatform.GameOwner, $"UI.Notify: \"{old.Title}\" dropped: no notification panel to show it on within {KeepForMs / 1000} s");
            }
            if (s_queue.Count == 0) return;
            // The newest panel still alive. The HUD can go (an event starting, say) without its panel's destructor
            // running, so each is checked before use: still mapped, still a notification panel. Showing on one
            // that's gone would end the game.
            while (true)
            {
                component = s_live[^1];
                if (component == 0)
                {
                    if (!s_toldWaiting) Log(ModPlatform.GameOwner, "UI.Notify: waiting for the HUD's notification panel (it's made with the HUD)");
                    s_toldWaiting = true;
                    return;
                }
                if (GameCode.TryRead<nint>(component, out var vt) && vt == s_base + Addresses.NotificationVtable) break;
                s_madeAt.Remove(component);
                Array.Copy(s_live, 0, s_live, 1, s_live.Length - 1);   // forget it
                s_live[0] = 0;
                Log(ModPlatform.GameOwner, "UI.Notify: a notification panel went away without its destructor; using the one before it, if any");
            }
            if (s_madeAt.TryGetValue(component, out var made) && now - made < SettleMs) return;   // the HUD is still being built
            if (Game.IsReady && !Game.IsDriving) return;   // loading, a menu, an intro: shown once you drive
            s_toldWaiting = false;
            m = s_queue.Dequeue();
        }
        try
        {
            var make = (delegate* unmanaged<nint*, byte*, int, nint*>)(s_base + Addresses.MakeDisplayString.Offset);
            var setWide = (delegate* unmanaged<nint, char*, int, void>)(s_base + Addresses.SetDisplayStringWide.Offset);
            var show = (delegate* unmanaged<nint, nint*, nint*, nint*, void>)(s_base + Addresses.ShowNotification.Offset);
            using var u = new Utf8Scope();
            nint title = 0, text = 0, icon = 0;
            // MakeDisplayString reads char* text as ANSI (CP_ACP), so the title and text are made empty and then
            // set as UTF-16, which is what the string holds: every language comes through.
            byte empty = 0;
            make(&title, &empty, -1);
            make(&text, &empty, -1);
            fixed (char* t = m.Title) if (title != 0) setWide(title, t, -1);
            fixed (char* t = m.Text) if (text != 0) setWide(text, t, -1);
            make(&icon, u.Str(m.Icon), -1);   // an icon's name: ASCII
            show(component, &title, &text, &icon);   // takes the strings over
            s_lastShown = now;
            if (s_shown++ < 5) Log(ModPlatform.GameOwner, $"UI.Notify: showed \"{m.Title}\"");
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
