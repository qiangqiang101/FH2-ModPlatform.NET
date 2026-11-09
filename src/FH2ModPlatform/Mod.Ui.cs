using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2ModPlatform;

public abstract unsafe partial class Mod
{
    // ---- Settings on the Mods page (API 4, 5, 6, 9) -----------------------
    // Add them from OnInit: the page is written once every mod has started.

    /// <summary>Adds a row to Pause &gt; Settings &gt; MODS. False if refused (the log says why).</summary>
    public bool AddSetting(SettingBase setting)
    {
        ModPlatform.Require(ApiLevel.Settings, nameof(AddSetting));
        if (setting is not LinkSetting)
        {
            SettingRows++;
            SettingLabels.Add((setting.Key, setting.Label));
        }
        object callback = setting switch
        {
            ToggleSetting { Changed: { } c } => (Action<int>)(v => c(v != 0)),
            SliderSetting { Changed: { } c } => c,
            ChoiceSetting { Changed: { } c } => c,
            _ => setting,   // no callback: the trampolines do nothing
        };
        var user = Bind("setting", setting.Key, callback, out bool rebound);
        if (rebound)
        {
            // Reloaded: the platform has the row; show the value the new copy starts with.
            switch (setting)
            {
                case ToggleSetting t: SetSetting(t.Key, t.Value); break;
                case ChoiceSetting c: SetSetting(c.Key, c.Value); break;
                case SliderSetting sl when ModPlatform.Supports(ApiLevel.Sliders): SetSlider(sl.Key, sl.Value); break;
            }
            return true;
        }

        using var u = new Utf8Scope();
        var s = new FH2Setting
        {
            Size = (uint)sizeof(FH2Setting),
            Key = u.Str(setting.Key),
            Label = u.Str(setting.Label),
            Description = u.Str(setting.Description),
            NeedsRestart = setting.NeedsRestart ? 1 : 0,
            // Always set, so a reload can add a handler the first copy didn't have.
            OnChange = &Callbacks.SettingChanged,
            User = user,
        };
        switch (setting)
        {
            case ToggleSetting t:
                s.Value = t.Value ? 1 : 0;
                break;
            case SliderSetting sl:
                ModPlatform.Require(ApiLevel.Sliders, "A slider");
                s.Kind = 1;
                s.Min = sl.Min;
                s.Max = sl.Max;
                s.Step = sl.Step;
                s.SliderValue = sl.Value;
                s.DisplayScale = sl.DisplayScale;
                s.Decimals = sl.Decimals;
                s.OnSlide = &Callbacks.SliderChanged;
                break;
            case LinkSetting l:
                ModPlatform.Require(ApiLevel.Links, "A link row");
                s.Kind = 2;
                s.Url = u.Str(l.Url);
                break;
            case ChoiceSetting c:
                ModPlatform.Require(ApiLevel.TitleEntries, "A row of choices");
                s.Kind = 3;
                s.Value = c.Value;
                s.Choices = u.StrArray(c.Choices);
                s.ChoiceCount = (uint)c.Choices.Count;
                break;
            default:
                throw new ArgumentException($"Unknown setting type {setting.GetType().Name}", nameof(setting));
        }
        return Api->AddSetting(H, &s) != 0;
    }

    /// <summary>How many rows this mod has on the Mods page (Platform.NET's page says where they are).</summary>
    internal int SettingRows { get; private set; }

    /// <summary>This mod's rows on the Mods page: key and label (for <see cref="FH2.SettingsTile"/>).</summary>
    internal List<(string Key, string Label)> SettingLabels { get; } = new();

    /// <summary>An On/Off row's value (0/1) or a choice row's index; -1 if you have no row called <paramref name="key"/>.</summary>
    public int GetSetting(string key)
    {
        ModPlatform.Require(ApiLevel.Settings, nameof(GetSetting));
        using var u = new Utf8Scope();
        return Api->GetSetting(H, u.Str(key));
    }

    /// <summary>You changed the value yourself: the page shows it next time. The change callback isn't called.</summary>
    public void SetSetting(string key, int value)
    {
        ModPlatform.Require(ApiLevel.Settings, nameof(SetSetting));
        using var u = new Utf8Scope();
        Api->SetSetting(H, u.Str(key), value);
    }

    public void SetSetting(string key, bool value) => SetSetting(key, value ? 1 : 0);

    public float GetSlider(string key)
    {
        ModPlatform.Require(ApiLevel.Sliders, nameof(GetSlider));
        using var u = new Utf8Scope();
        return Api->GetSlider(H, u.Str(key));
    }

    public void SetSlider(string key, float value)
    {
        ModPlatform.Require(ApiLevel.Sliders, nameof(SetSlider));
        using var u = new Utf8Scope();
        Api->SetSlider(H, u.Str(key), value);
    }

    // ---- Pages (API 8) ----------------------------------------------------

    /// <summary>Adds a page of your own (from OnInit). Give it rows with <see cref="SetPageRows"/>.</summary>
    public bool AddPage(PageOptions page)
    {
        ModPlatform.Require(ApiLevel.Pages, nameof(AddPage));
        var user = Bind("page", page.Key, page, out bool rebound);
        if (rebound) return true;
        using var u = new Utf8Scope();
        var p = new FH2Page
        {
            Size = (uint)sizeof(FH2Page),
            Key = u.Str(page.Key),
            Title = u.Str(page.Title),
            Label = u.Str(page.Label),
            Description = u.Str(page.Description),
            Tile = u.Str(page.Tile),
            User = user,
            OnOpen = &Callbacks.PageOpened,
            OnSelect = &Callbacks.PageSelected,
            OnButton = &Callbacks.PageButton,
            OnSlot = &Callbacks.PageSlot,
        };
        return Api->AddPage(H, &p) != 0;
    }

    /// <summary>The page's rows from now on (copied); it shows them the next time it opens.</summary>
    public bool SetPageRows(string pageKey, IReadOnlyList<PageRow> rows)
    {
        ModPlatform.Require(ApiLevel.Pages, nameof(SetPageRows));
        using var u = new Utf8Scope();
        var native = (FH2PageRow*)u.Alloc((nuint)(sizeof(FH2PageRow) * Math.Max(rows.Count, 1)));
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            native[i] = new FH2PageRow
            {
                Size = (uint)sizeof(FH2PageRow),
                Key = u.Str(r.Key),
                Label = u.Str(r.Label),
                Value = u.Str(r.Value),
                Description = u.Str(r.Description),
                Footer = u.Str(r.Footer),
                Style = (int)r.Style,
                ValueAlert = r.Alert ? 1 : 0,
            };
        }
        return Api->SetPageRows(H, u.Str(pageKey), native, (uint)rows.Count) != 0;
    }

    /// <summary>XAML of your own for a page on a tile. <paramref name="content"/> must name its first focus x:Name="FirstItem".</summary>
    public bool SetPageXaml(string pageKey, string content, string? resources = null, string? help = null)
    {
        ModPlatform.Require(ApiLevel.Pages, nameof(SetPageXaml));
        using var u = new Utf8Scope();
        return Api->SetPageXaml(H, u.Str(pageKey), u.Str(resources), u.Str(content), u.Str(help)) != 0;
    }

    /// <summary>Slot <paramref name="slot"/> (0..15), bound in XAML as FH2Mod_Slot_&lt;n&gt;.</summary>
    public int GetPageSlot(string pageKey, int slot)
    {
        ModPlatform.Require(ApiLevel.Pages, nameof(GetPageSlot));
        using var u = new Utf8Scope();
        return Api->GetPageSlot(H, u.Str(pageKey), slot);
    }

    public bool SetPageSlot(string pageKey, int slot, int value)
    {
        ModPlatform.Require(ApiLevel.Pages, nameof(SetPageSlot));
        using var u = new Utf8Scope();
        return Api->SetPageSlot(H, u.Str(pageKey), slot, value) != 0;
    }

    /// <summary>Moves the focus to the named element, from a button handler.</summary>
    public bool FocusPageElement(string pageKey, string elementName)
    {
        ModPlatform.Require(ApiLevel.Pages, nameof(FocusPageElement));
        using var u = new Utf8Scope();
        return Api->FocusPageElement(H, u.Str(pageKey), u.Str(elementName)) != 0;
    }

    /// <summary>A line of plain ASCII (up to 40 characters) on the tile your page took over.</summary>
    public bool SetPageTileText(string pageKey, string? text)
    {
        ModPlatform.Require(ApiLevel.Pages, nameof(SetPageTileText));
        using var u = new Utf8Scope();
        return Api->SetPageTileText(H, u.Str(pageKey), u.Str(text)) != 0;
    }

    /// <summary>The tile's colours as 0xAARRGGBB; 0 keeps the game's own.</summary>
    public bool SetPageTileColors(string pageKey, uint background, uint text)
    {
        ModPlatform.Require(ApiLevel.Pages, nameof(SetPageTileColors));
        using var u = new Utf8Scope();
        return Api->SetPageTileColors(H, u.Str(pageKey), background, text) != 0;
    }

    // ---- Title-screen entries (API 9) -------------------------------------

    public bool AddTitleEntry(TitleEntryOptions entry)
    {
        ModPlatform.Require(ApiLevel.TitleEntries, nameof(AddTitleEntry));
        var user = Bind("title", entry.Key, entry, out bool rebound);
        if (rebound) return true;
        using var u = new Utf8Scope();
        var e = new FH2TitleEntry
        {
            Size = (uint)sizeof(FH2TitleEntry),
            Key = u.Str(entry.Key),
            Label = u.Str(entry.Label),
            User = user,
            Button = (int)entry.Button,
            Choices = u.StrArray(entry.Choices),
            ChoiceCount = (uint)(entry.Choices?.Count ?? 0),
            ListTitle = u.Str(entry.ListTitle),
            ListMessage = u.Str(entry.ListMessage),
        };
        // Set as the platform checks them, so its log says what's missing.
        if (entry.Pressed is not null || entry.Choices is null) e.OnPress = &Callbacks.TitlePressed;
        if (entry.Chosen is not null) e.OnChoose = &Callbacks.TitleChosen;
        return Api->AddTitleEntry(H, &e) != 0;
    }

    // ---- Hotkeys (API 9) --------------------------------------------------

    public bool AddHotkey(HotkeyOptions hotkey)
    {
        ModPlatform.Require(ApiLevel.TitleEntries, nameof(AddHotkey));
        var user = Bind("hotkey", hotkey.Key, hotkey, out bool rebound);
        if (rebound) return true;
        using var u = new Utf8Scope();
        var k = new FH2Hotkey
        {
            Size = (uint)sizeof(FH2Hotkey),
            Key = u.Str(hotkey.Key),
            Label = u.Str(hotkey.Label),
            Vk = hotkey.Binding.VirtualKey,
            Modifiers = (uint)hotkey.Binding.Modifiers,
            PadChord = (uint)hotkey.Binding.Chord,
            User = user,
            OnPress = &Callbacks.HotkeyPressed,
            OnRebind = &Callbacks.HotkeyRebound,
        };
        return Api->AddHotkey(H, &k) != 0;
    }

    // ---- Commands (API 9) -------------------------------------------------

    /// <summary>
    /// A command other programs can run (<c>fh2mod MyMod name args</c>). <paramref name="handler"/> gets the
    /// arguments and returns the reply; start it with "error: " for a failure. Exceptions become "error: ...".
    /// Runs on the platform's command thread.
    /// </summary>
    public bool AddCommand(string name, string? help, Func<string, string> handler)
    {
        ModPlatform.Require(ApiLevel.TitleEntries, nameof(AddCommand));
        var user = Bind("command", name, handler, out bool rebound);
        if (rebound) return true;
        using var u = new Utf8Scope();
        return Api->AddCommand(H, u.Str(name), u.Str(help), &Callbacks.Command, user) != 0;
    }
}
