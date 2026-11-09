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
