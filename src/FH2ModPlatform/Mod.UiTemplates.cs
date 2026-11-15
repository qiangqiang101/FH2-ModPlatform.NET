using System.Reflection;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2ModPlatform;

// Platform 2.9.0 (API 10): pop-ups, tips, message boxes, UI sounds, page help, text in the game's
// language and translations. On an older platform every one of these does nothing (or gives back
// your English) instead of throwing, so a mod can use them and still run there.
public abstract unsafe partial class Mod
{
    private bool HasUi => _handle is not null && ModPlatform.IsAttached && ModPlatform.Supports(ApiLevel.UiTemplates);

    // ---- What you use, and what works ---------------------------------------------------------

    /// <summary>
    /// Says which UI templates you use: with <see cref="UiUses.Popups"/> your mod's page gets a "Pop-ups"
    /// row, so the player can turn yours off. Only honoured while the game starts (a mod that starts
    /// with the game, from <see cref="OnInit"/>); a reload keeps what the first copy said.
    /// </summary>
    /// <remarks>
    /// Pop-ups and tips work without it: Platform.NET declares them for every .NET mod. Without it you
    /// just have no row of your own (the platform's "Mod pop-ups" still turns yours off).
    /// </remarks>
    public bool DeclareUi(UiUses uses)
    {
        if (!HasUi) return false;
        if (Slot.IsReload) return true;
        return Api->UiUses(H, (uint)uses) != 0;
    }

    /// <summary>What works now: whether pop-ups can show, the player turned them off, the HUD is on screen...</summary>
    public UiStatus UiStatus => HasUi ? (UiStatus)Api->UiStatus(H) : UiStatus.None;

    /// <summary>
    /// Plays one of the game's UI sounds. From one of your page, setting, title or box callbacks it plays
    /// at once; from anywhere else, at the game's next tick. Never make a sound the only cue.
    /// False for a repeat within 100 ms, or Notify and the countdown beeps under a menu.
    /// </summary>
    public bool PlaySound(UiSound sound) => HasUi && Api->PlayUiSound(H, (uint)sound) != 0;

    // ---- Pop-ups ------------------------------------------------------------------------------

    /// <summary>
    /// Queues a pop-up: the game's HUD notification, in one queue for every mod, spaced around the game's
    /// own. Up to 4 of yours wait at once; a 5th drops your oldest waiting one. Any thread.
    /// True if queued (it can still expire or be dropped); false if not (pop-ups are off, no title; the log says which).
    /// </summary>
    public bool ShowPopup(PopupOptions popup)
    {
        if (!HasUi || string.IsNullOrWhiteSpace(popup.Title)) return false;
        using var u = new Utf8Scope();
        var t = new FH2Toast
        {
            Size = (uint)sizeof(FH2Toast),
            Key = u.Str(popup.Key),
            Title = u.Str(popup.Title),
            Subtitle = u.Str(string.IsNullOrEmpty(popup.Text) ? null : popup.Text),
            Icon = (uint)popup.Icon,
            Sound = (uint)popup.Sound,
            Flags = (popup.Urgent ? 1u : 0) | (popup.Reply ? 2u : 0) | (popup.Private ? 4u : 0),
            ExpireS = (uint)Math.Clamp(popup.ExpireSeconds, 0, 600),
        };
        return Api->ShowToast(H, &t) != 0;
    }

    /// <summary>A pop-up with a title and a line.</summary>
    public bool ShowPopup(string title, string? text = null) => ShowPopup(new PopupOptions(title) { Text = text });

    /// <summary>Takes down your waiting pop-up <paramref name="key"/>, or all your waiting ones for null. One on screen runs its 5 seconds.</summary>
    public void ClearPopup(string? key = null)
    {
        if (!HasUi) return;
        using var u = new Utf8Scope();
        Api->ClearToast(H, u.Str(key));
    }

    // ---- Tips ---------------------------------------------------------------------------------

    /// <summary>
    /// Queues a tip, shown once: after a while of driving, at least 3 minutes after any other mod's tip.
    /// False if it was seen, tips are off, or a button is set but the text has no "{button}".
    /// </summary>
    public bool ShowTip(TipOptions tip)
    {
        if (!HasUi) return false;
        var u = new Utf8Scope();
        try
        {
            var t = new FH2Tip
            {
                Size = (uint)sizeof(FH2Tip),
                Key = u.Str(tip.Key),
                Text = u.Str(tip.Text),
                Button = ToNative(tip.Button, ref u),
                DelayS = (uint)Math.Max(0, tip.DelaySeconds),
                MaxSessions = (uint)Math.Max(0, tip.MaxSessions),
                Flags = tip.UntilDone ? 1u : 0,
            };
            return Api->ShowTip(H, &t) != 0;
        }
        finally { u.Dispose(); }
    }

    /// <summary>True if the tip with <paramref name="key"/> has been seen (in any session).</summary>
    public bool TipSeen(string key)
    {
        if (!HasUi) return false;
        using var u = new Utf8Scope();
        return Api->TipSeen(H, u.Str(key)) != 0;
    }

    /// <summary>
    /// Marks a tip seen (which also drops it if it's waiting): for a <see cref="TipOptions.UntilDone"/> tip,
    /// once the player has done the thing. Or not seen, to show it again.
    /// </summary>
    public void SetTipSeen(string key, bool seen = true)
    {
        if (!HasUi) return;
        using var u = new Utf8Scope();
        Api->SetTipSeen(H, u.Str(key), seen ? 1 : 0);
    }

    // ---- Message boxes ------------------------------------------------------------------------

    /// <summary>
    /// Shows the game's message box over the page the player is on. Only from a callback of one of your
    /// pages, settings or title entries (a page's Opened or Selected, a setting's Changed...): from
    /// anywhere else it returns false. One box at a time; nothing is queued.
    /// </summary>
    public bool ShowMessageBox(MessageBoxOptions box)
    {
        if (!HasUi || box.Choices.Count == 0) return false;
        using var u = new Utf8Scope();
        var b = new FH2MessageBox
        {
            Size = (uint)sizeof(FH2MessageBox),
            Key = u.Str(box.Key),
            Title = u.Str(box.Title),
            Message = u.Str(box.Message),
            Choices = u.StrArray(box.Choices),
            ChoiceCount = (uint)box.Choices.Count,
            Focus = (uint)Math.Clamp(box.Focus, 0, box.Choices.Count - 1),
            OnChoose = box.Chosen is null ? null : &Callbacks.BoxChosen,
            User = box.Chosen is null ? null : Root(box.Chosen),
        };
        return Api->ShowMessageBox(H, &b) != 0;
    }

    // ---- Your rows, your description, your pages' help ------------------------------------------

    /// <summary>
    /// Changes a Mods-page row's label, description and (a choice row) its choices' names; null leaves
    /// that part as it is. Shows the next time the player opens the page. Any time, any thread.
    /// </summary>
    public bool SetSettingText(string key, string? label = null, string? description = null, IReadOnlyList<string>? choices = null)
    {
        if (!HasUi) return false;
        using var u = new Utf8Scope();
        return Api->SetSettingText(H, u.Str(key), u.Str(label), u.Str(description), u.StrArray(choices), (uint)(choices?.Count ?? 0)) != 0;
    }

    /// <summary>Your <see cref="ModInfo.Description"/>, changed: on the YOUR MODS list and your mod's page.</summary>
    public void SetDescription(string description)
    {
        if (!HasUi) return;
        using var u = new Utf8Scope();
        Api->SetModDescription(H, u.Str(description));
    }

    /// <summary>
    /// Your page's help bar: these prompts, beside the platform's own Back (and Select, when the page has
    /// <see cref="PageOptions.Selected"/>). Only buttons the page can handle are taken: A and B on a page of
    /// rows; A, B, X, Y, LB and RB on a page of your own design. Returns how many were taken.
    /// </summary>
    public int SetPageHelp(string pageKey, params Prompt[] prompts)
    {
        if (!HasUi) return 0;
        var u = new Utf8Scope();
        try
        {
            var p = (FH2Prompt*)u.Alloc((nuint)(sizeof(FH2Prompt) * Math.Max(prompts.Length, 1)));
            for (int i = 0; i < prompts.Length; i++) p[i] = ToNative(prompts[i], ref u);
            return Api->SetPageHelp(H, u.Str(pageKey), p, (uint)prompts.Length);
        }
        finally { u.Dispose(); }
    }

    /// <summary>The texts your page's hint line can show (up to 8, 80 characters each). From OnInit or the page's Opened.</summary>
    public bool SetPageHints(string pageKey, params string[] hints)
    {
        if (!HasUi) return false;
        using var u = new Utf8Scope();
        return Api->SetPageHints(H, u.Str(pageKey), u.StrArray(hints), (uint)hints.Length) != 0;
    }

    /// <summary>
    /// Shows hint <paramref name="index"/> (from <see cref="SetPageHints"/>) on your page's hint line, or
    /// none for -1. From the page's own callbacks it changes at once; otherwise the next time it opens.
    /// </summary>
    public bool ShowPageHint(string pageKey, int index, HintKind kind = HintKind.Info)
    {
        if (!HasUi) return false;
        using var u = new Utf8Scope();
        return Api->ShowPageHint(H, u.Str(pageKey), index, (uint)kind) != 0;
    }

    // ---- Translations -------------------------------------------------------------------------

    /// <summary>
    /// One language's translations, as a gettext PO file's text. <paramref name="language"/> is its tag
    /// ("pl", "pt-BR", "zh-Hant"). Call it from OnInit, once per language. A file
    /// fh2-mods\lang\&lt;your mod's name&gt;\&lt;language&gt;.po is used instead, if there is one, so
    /// translators can try theirs without a build. Returns how many entries were taken.
    /// </summary>
    public int AddTranslation(string language, string po)
    {
        if (!HasUi) return 0;
        using var u = new Utf8Scope();
        var bytes = System.Text.Encoding.UTF8.GetBytes(po);
        fixed (byte* b = bytes) return Api->AddTranslation(H, u.Str(language), b, (nuint)bytes.Length);
    }

    /// <summary>
    /// Every PO file built into your mod as an embedded resource, each named after its language:
    /// "pl.po", "Lang\pt-BR.po". Returns how many entries were taken in all.
    /// </summary>
    public int AddTranslations(Assembly? from = null)
    {
        if (!HasUi) return 0;
        from ??= GetType().Assembly;
        int taken = 0;
        foreach (var name in from.GetManifestResourceNames())
        {
            if (!name.EndsWith(".po", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = name[..^3];
            var language = stem[(stem.LastIndexOf('.') + 1)..];   // "MyMod.Lang.pt-BR" -> "pt-BR"
            using var s = from.GetManifestResourceStream(name);
            if (s is null) continue;
            using var r = new StreamReader(s, System.Text.Encoding.UTF8);
            taken += AddTranslation(language, r.ReadToEnd());
        }
        return taken;
    }

    /// <summary>
    /// <paramref name="english"/> in the player's language, or the English when there's no translation.
    /// <paramref name="key"/> is the PO file's msgctxt ("row.Trees.label"). Pass the result straight to a
    /// row, page, hotkey or notice and it follows the game's language by itself. Any thread.
    /// </summary>
    public string Tr(string? key, string english)
    {
        if (!HasUi) return english;
        using var u = new Utf8Scope();
        var p = Api->Tr(H, u.Str(key), u.Str(english));
        return p is null ? english : TranslatedText.Remember(p);
    }

    /// <summary>Text without a key (PO entries without a msgctxt).</summary>
    public string Tr(string english) => Tr(null, english);

    /// <summary>
    /// Text with a count: <paramref name="one"/> and <paramref name="other"/> are the English forms, with
    /// "{n}" where the number goes, and the language's own rules pick the form (Polish has three). The
    /// number is filled in, written the language's way (<see cref="ModPlatform.FormatNumber"/>).
    /// </summary>
    public string TrPlural(string? key, string one, string other, long n)
    {
        string form;
        if (HasUi)
        {
            using var u = new Utf8Scope();
            var p = Api->TrPlural(H, u.Str(key), u.Str(one), u.Str(other), n);
            form = p is null ? (n == 1 ? one : other) : NativeString.FromUtf8(p) ?? other;
        }
        else form = n == 1 ? one : other;
        return form.Replace("{n}", ModPlatform.FormatNumber(n));
    }

    /// <summary>
    /// <see cref="SetString(string, ushort, string)"/> in every language: each of the game's string tables
    /// gets the text in its own language, so the game shows the right one whatever language it picks.
    /// From OnInit. On an older platform, the English goes in as SetString would.
    /// </summary>
    public void SetStringTr(string table, ushort key, string msgKey, string english)
    {
        if (!HasUi)
        {
            SetString(table, key, english);
            return;
        }
        using var u = new Utf8Scope();
        fixed (char* e = english) Api->SetStringTr(H, u.Str(table), key, u.Str(msgKey), e);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static FH2Prompt ToNative(Prompt? p, ref Utf8Scope u)
    {
        var n = new FH2Prompt();
        if (p is null) return n;
        n.Glyph = (uint)p.Glyph;
        n.Vk = p.Vk;
        n.Modifiers = (uint)p.Modifiers;
        n.Word = (uint)p.Word;
        n.Hotkey = u.Str(p.Hotkey);
        n.Label = u.Str(p.Label);
        n.Hold = p.Hold ? 1 : 0;
        return n;
    }
}
