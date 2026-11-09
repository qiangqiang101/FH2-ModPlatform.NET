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
