namespace FH2ModPlatform;

/// <summary>API versions of the platform (<c>FH2MOD_API_VERSION</c>), with the platform release that brought each.</summary>
public enum ApiLevel : uint
{
    V1 = 1,
    /// <summary>Platform 2.1.0: game files, database, other mods.</summary>
    Files = 2,
    /// <summary>Platform 2.2.0: hooks.</summary>
    Hooks = 3,
    /// <summary>Platform 2.3.0: On/Off settings.</summary>
    Settings = 4,
    /// <summary>Platform 2.4.0: sliders.</summary>
    Sliders = 5,
    /// <summary>Platform 2.5.0: descriptions, links.</summary>
    Links = 6,
    /// <summary>Platform 2.6.0: byte patches, instruction hooks.</summary>
    PreciseHooks = 7,
    /// <summary>Platform 2.7.0: pages.</summary>
    Pages = 8,
    /// <summary>Platform 2.8.0: title entries, choices, online, sessions, hotkeys, commands.</summary>
    TitleEntries = 9,
    /// <summary>Platform 2.9.0: pop-ups, tips, message boxes, translations.</summary>
    UiTemplates = 10,
}

/// <summary>Controller buttons on pages and title entries (<c>FH2_BUTTON_*</c>).</summary>
public enum PadButton
{
    None = 0,
    A = 1,
    B = 2,
    X = 3,
    Y = 4,
    LB = 5,
    RB = 6,
}

/// <summary>What a page's button handler did (<c>FH2_PAGE_*</c>).</summary>
public enum PageButtonResult
{
    /// <summary>Do what the button usually does.</summary>
    Ignored = 0,
    Handled = 1,
    /// <summary>Handled, and the menus' select sound plays.</summary>
    HandledSelect = 2,
    /// <summary>Handled, and the menus' back sound plays.</summary>
    HandledBack = 3,
}

/// <summary>What a title-screen entry did (<c>FH2_TITLE_*</c>).</summary>
public enum TitleResult
{
    /// <summary>Stay on the title screen.</summary>
    Handled = 0,
    /// <summary>The platform presses A for the player: the game starts.</summary>
    Start = 1,
}

/// <summary>A page row's look (<c>FH2_ROW_*</c>).</summary>
public enum RowStyle
{
    Normal = 0,
    /// <summary>Label and value in grey: locked, not available yet.</summary>
    Dim = 1,
    /// <summary>Label in the game's orange: a heading over the rows after it.</summary>
    Heading = 2,
}

/// <summary>Keyboard modifiers held with a hotkey (<c>FH2_KEY_*</c>).</summary>
[Flags]
public enum KeyModifiers : uint
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>Controller buttons held together for a hotkey chord (<c>FH2_PAD_*</c>, XInput's bits).</summary>
[Flags]
public enum PadChord : uint
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LB = 0x0100,
    RB = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

// ---- UI templates (API 10, platform 2.9.0) ---------------------------------------------------

/// <summary>The game's own notification pictures, for a pop-up (<c>FH2_ICON_*</c>).</summary>
public enum PopupIcon : uint
{
    None = 0,
    Trophy = 1,
    Roads = 2,
    Photo = 3,
    Boards = 4,
    Bucket = 5,
    CarMeet = 6,
    Club = 7,
    Payout = 8,
    BarnFind = 9,
}

/// <summary>The game's UI sounds (<c>FH2_SOUND_*</c>).</summary>
public enum UiSound : uint
{
    None = 0,
    Select = 1,
    Back = 2,
    Open = 3,
    Close = 4,
    /// <summary>The menus' "can't do that".</summary>
    Error = 5,
    /// <summary>The game's notification chime, for a milestone. Only over the driving view.</summary>
    Notify = 6,
    /// <summary>A pop-up opening (very faint over the driving view).</summary>
    Info = 7,
    /// <summary>The countdown beeps. Only over the driving view.</summary>
    Tick = 8,
    TickLow = 9,
    TickEnd = 10,
    /// <summary>The photo shutter.</summary>
    Shutter = 11,
}

/// <summary>The game's controller pictures, for button prompts (<c>FH2_GLYPH_*</c>).</summary>
public enum PadGlyph : uint
{
    None = 0,
    A = 1, B = 2, X = 3, Y = 4, LB = 5, RB = 6, LT = 7, RT = 8,
    Menu = 9, View = 10,
    DPadUp = 11, DPadDown = 12, DPadLeft = 13, DPadRight = 14, DPadUpDown = 15, DPadLeftRight = 16,
    L3 = 17, R3 = 18,
    LeftStick = 19, LeftStickUpDown = 20, LeftStickLeftRight = 21,
    RightStick = 22, RightStickUpDown = 23, RightStickLeftRight = 24,
}

/// <summary>The game's own words, in the player's language, for a prompt instead of your label (<c>FH2_WORD_*</c>).</summary>
public enum UiWord : uint
{
    None = 0,
    Select = 1,
    Back = 2,
    Accept = 3,
    Toggle = 4,
    On = 5,
    Off = 6,
    Previous = 7,
    Next = 8,
}

/// <summary>How a page's hint line shows a hint (<c>FH2_HINT_*</c>).</summary>
public enum HintKind : uint
{
    /// <summary>In the colour of the platform's own hints.</summary>
    Info = 0,
    /// <summary>Orange, with the game's info icon.</summary>
    Attention = 1,
    /// <summary>Red, with its alert icon.</summary>
    Fault = 2,
}

/// <summary>What works now (<c>ui_status</c>, <c>FH2_UI_*</c>).</summary>
[Flags]
public enum UiStatus : uint
{
    None = 0,
    /// <summary>Pop-ups and tips can show this session.</summary>
    Popups = 0x0001,
    /// <summary>The player turned mod pop-ups off (yours, or every mod's).</summary>
    PopupsOff = 0x0002,
    /// <summary>The player turned tips off.</summary>
    TipsOff = 0x0004,
    /// <summary>The free-roam HUD is on screen now.</summary>
    OnScreen = 0x0008,
    /// <summary>The game's HUD is suspended (a car meet, a Rivals event): pop-ups and tips wait.</summary>
    GameHudHidden = 0x0010,
    /// <summary>Buttons are written as keys: the keyboard or mouse was used last.</summary>
    Keyboard = 0x0020,
}

/// <summary>What a mod will use, declared at startup (<c>ui_uses</c>, <c>FH2_UI_USES_*</c>).</summary>
[Flags]
public enum UiUses : uint
{
    None = 0,
    /// <summary>Pop-ups: the platform adds a "Pop-ups" row to your mod's page.</summary>
    Popups = 0x0001,
    /// <summary>Tips: the platform watches which device the player used last, for the button.</summary>
    Tips = 0x0002,
    /// <summary>You have a pop-ups row of your own: the platform adds none.</summary>
    OwnSwitch = 0x0004,
}
