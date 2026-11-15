namespace FH2ModPlatform;

/// <summary>Who your mod is, for <c>register_mod</c>.</summary>
public sealed record ModInfo(string Name, string Version)
{
    /// <summary>"Owner/Repo" to check for new releases, or null.</summary>
    public string? GithubRepo { get; init; }
    /// <summary>The platform version your mod ships with. Players with an older one are told to reinstall.</summary>
    public string PlatformVersion { get; init; } = ModPlatform.BuiltAgainstVersion;
    /// <summary>False: never go online for this mod.</summary>
    public bool CheckUpdates { get; init; } = true;
    /// <summary>A sentence or two for MODS &gt; Installed mods (API 6).</summary>
    public string? Description { get; init; }
    /// <summary>The mod shouldn't run in online play (FH2MP) because it changes driving or what other players see (API 9).</summary>
    public bool OfflineOnly { get; init; }
}

/// <summary>Common to every row on the Mods page.</summary>
public abstract record SettingBase(string Key, string Label)
{
    /// <summary>Shown beside the list while the row is selected.</summary>
    public string? Description { get; init; }
    /// <summary>A change only takes effect the next time the game starts.</summary>
    public bool NeedsRestart { get; init; }
}

/// <summary>An On/Off row. <see cref="Changed"/> runs on the game's UI thread: keep it short.</summary>
public sealed record ToggleSetting(string Key, string Label, bool Value) : SettingBase(Key, Label)
{
    public Action<bool>? Changed { get; init; }
}

/// <summary>A slider (API 5).</summary>
public sealed record SliderSetting(string Key, string Label, float Value, float Min, float Max) : SettingBase(Key, Label)
{
    /// <summary>One press of left or right; 0 = a hundredth of the range.</summary>
    public float Step { get; init; }
    /// <summary>The number shown is the value times this (100 shows 0.12 as 12); 0 = 1.</summary>
    public float DisplayScale { get; init; }
    /// <summary>Digits after the decimal point, 0 to 3.</summary>
    public int Decimals { get; init; }
    public Action<float>? Changed { get; init; }
}

/// <summary>A row of named choices (API 9); <see cref="Value"/> is the chosen index.</summary>
public sealed record ChoiceSetting(string Key, string Label, IReadOnlyList<string> Choices, int Value) : SettingBase(Key, Label)
{
    public Action<int>? Changed { get; init; }
}

/// <summary>A row that opens a web page in the player's browser (API 6). Shown on your mod's own page under MODS &gt; Installed mods.</summary>
public sealed record LinkSetting(string Key, string Label, string Url) : SettingBase(Key, Label);

/// <summary>A page of your own (API 8). Every callback runs on the game's UI thread.</summary>
public sealed record PageOptions(string Key, string Title)
{
    /// <summary>The row on your mod's page that opens it; null = the title.</summary>
    public string? Label { get; init; }
    public string? Description { get; init; }
    /// <summary>A pause-menu tile to take over, like "Achievements", or null.</summary>
    public string? Tile { get; init; }
    /// <summary>The page is about to be shown: set its rows now.</summary>
    public Action? Opened { get; init; }
    /// <summary>A was pressed on the row with this key.</summary>
    public Action<string>? Selected { get; init; }
    /// <summary>A page of your own design: a button was pressed.</summary>
    public Func<PadButton, PageButtonResult>? ButtonPressed { get; init; }
    /// <summary>A page of your own design: a binding changed slot (first) to value (second).</summary>
    public Action<int, int>? SlotChanged { get; init; }
}

/// <summary>One row on a page of yours.</summary>
public sealed record PageRow(string Label, string? Value = null)
{
    /// <summary>Passed to <see cref="PageOptions.Selected"/> when A is pressed on it; null: A does nothing.</summary>
    public string? Key { get; init; }
    public string? Description { get; init; }
    /// <summary>A line at the bottom of the description panel.</summary>
    public string? Footer { get; init; }
    public RowStyle Style { get; init; }
    /// <summary>The value in red.</summary>
    public bool Alert { get; init; }

    public static PageRow Heading(string label) => new(label) { Style = RowStyle.Heading };
}

/// <summary>A title-screen entry (API 9), like "(Y) START ON STORM ISLAND". Callbacks run on the game's UI thread.</summary>
public sealed record TitleEntryOptions(string Key, string Label)
{
    /// <summary>Called when its button is pressed (not used for an entry with <see cref="Choices"/>).</summary>
    public Func<TitleResult>? Pressed { get; init; }
    /// <summary>Y, X, RB or LB to ask for that button; None = the next free one.</summary>
    public PadButton Button { get; init; }
    /// <summary>A list instead of an action: the press shows a message box with these.</summary>
    public IReadOnlyList<string>? Choices { get; init; }
    /// <summary>The player picked a choice (its index).</summary>
    public Func<int, TitleResult>? Chosen { get; init; }
    public string? ListTitle { get; init; }
    public string? ListMessage { get; init; }
}

/// <summary>A hotkey binding, as passed in and as rebound by the player.</summary>
public readonly record struct HotkeyBinding(uint VirtualKey, KeyModifiers Modifiers, PadChord Chord);

/// <summary>A hotkey (API 9). <see cref="Pressed"/> runs on a platform thread, <see cref="Rebound"/> on the UI thread.</summary>
public sealed record HotkeyOptions(string Key, string Label, HotkeyBinding Binding)
{
    public Action? Pressed { get; init; }
    /// <summary>The player picked another binding on the Mods page: save it and pass it in next time.</summary>
    public Action<HotkeyBinding>? Rebound { get; init; }
}

// ---- UI templates (API 10, platform 2.9.0) ---------------------------------------------------

/// <summary>
/// A pop-up: the game's HUD notification, with your title and line (<see cref="Mod.ShowPopup(PopupOptions)"/>).
/// About 5 seconds on screen, bottom centre over the driving view.
/// </summary>
/// <param name="Title">Shown in capitals; 28 characters. Pass sentence case.</param>
public sealed record PopupOptions(string Title)
{
    /// <summary>The line under the title; 45 characters.</summary>
    public string? Text { get; init; }
    /// <summary>Up to 32 letters, digits and '_': a new pop-up with the same key replaces your waiting one. Null: never replaced.</summary>
    public string? Key { get; init; }
    public PopupIcon Icon { get; init; }
    /// <summary>A sound as it appears; none by default, as the game's own pop-ups.</summary>
    public UiSound Sound { get; init; }
    /// <summary>A fault the player must know about and act on: first in the queue (2 a session; later ones are normal).</summary>
    public bool Urgent { get; init; }
    /// <summary>Answers the player's own press (a hotkey, a command): honoured within 1 s of it, dropped if it can't show within 6 s.</summary>
    public bool Reply { get; init; }
    /// <summary>The text may name people: the log gets only your mod and the key.</summary>
    public bool Private { get; init; }
    /// <summary>Dropped once it could have shown for this long and didn't; 1 to 600, 0 = 120.</summary>
    public int ExpireSeconds { get; init; }
}

/// <summary>
/// One button in a prompt: the game's picture of it when the pad was used last, the key when the
/// keyboard was. Use <see cref="Pad"/>, <see cref="Key"/> or <see cref="ForHotkey"/>.
/// </summary>
public sealed record Prompt
{
    public PadGlyph Glyph { get; init; }
    /// <summary>The key, a Windows virtual-key code (0x20 for Space, 'R'); 0 = the glyph always.</summary>
    public uint Vk { get; init; }
    public KeyModifiers Modifiers { get; init; }
    /// <summary>The key of one of your hotkeys: shows whatever it's bound to now (Glyph, Vk and Modifiers are ignored).</summary>
    public string? Hotkey { get; init; }
    /// <summary>What the button does ("Keep it"); ignored when <see cref="Word"/> is set.</summary>
    public string? Label { get; init; }
    /// <summary>The game's own word instead of <see cref="Label"/>, in the player's language.</summary>
    public UiWord Word { get; init; }
    /// <summary>"Hold" before it, with the hold fill (the HUD layer only).</summary>
    public bool Hold { get; init; }

    /// <summary>A controller button, with the key to show when the keyboard was used last (0: the button always).</summary>
    public static Prompt Pad(PadGlyph glyph, string? label = null, uint vk = 0) => new() { Glyph = glyph, Vk = vk, Label = label };
    /// <summary>A key, with the controller button to show when the pad was used last.</summary>
    public static Prompt Key(uint vk, string? label = null, PadGlyph glyph = PadGlyph.None, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { Glyph = glyph, Vk = vk, Modifiers = modifiers, Label = label };
    /// <summary>One of your hotkeys (<see cref="HotkeyOptions.Key"/>): whatever the player bound it to.</summary>
    public static Prompt ForHotkey(string hotkeyKey, string? label = null) => new() { Hotkey = hotkeyKey, Label = label };
}

/// <summary>
/// A tip: a pop-up with your mod's name as its title and a button in its line ("Press {button} to
/// keep it"), shown once rather than every session (<see cref="Mod.ShowTip"/>).
/// </summary>
/// <param name="Key">Its seen key, up to 32 letters, digits and '_' ("tip_boost"): once seen, it isn't shown again (see <see cref="Mod.TipSeen"/>).</param>
/// <param name="Text">With "{button}" where the button goes; 45 characters in all.</param>
public sealed record TipOptions(string Key, string Text)
{
    /// <summary>The button in the text; null for none (then the text needs no "{button}").</summary>
    public Prompt? Button { get; init; }
    /// <summary>Seconds of driving, with nothing else showing, before it shows; 0 = 60.</summary>
    public int DelaySeconds { get; init; }
    /// <summary>Seen only when you call <see cref="Mod.SetTipSeen"/> (the player did the thing), not when it's drawn.</summary>
    public bool UntilDone { get; init; }
    /// <summary>With <see cref="UntilDone"/>: shown in at most this many sessions; 0 = 1.</summary>
    public int MaxSessions { get; init; }
}

/// <summary>
/// The game's own message box over the page the player is on (<see cref="Mod.ShowMessageBox"/>):
/// a title, a message, your choices, and Back, which the platform adds last.
/// </summary>
/// <param name="Key">Yours, to tell boxes apart.</param>
/// <param name="Title">Shown in capitals; 30 characters.</param>
/// <param name="Choices">1 to 99; put the safe one first, and no Back of your own.</param>
public sealed record MessageBoxOptions(string Key, string Title, IReadOnlyList<string> Choices)
{
    /// <summary>Up to 200 characters, wrapped.</summary>
    public string? Message { get; init; }
    /// <summary>The choice the focus starts on.</summary>
    public int Focus { get; init; }
    /// <summary>
    /// The choice picked (from 0), or <see cref="Back"/>, on the game's UI thread: keep it short. It may
    /// never be called if the player leaves the box another way.
    /// </summary>
    public Action<int>? Chosen { get; init; }

    /// <summary>What <see cref="Chosen"/> gets for Back (the item, or B).</summary>
    public const int Back = -1;
}
