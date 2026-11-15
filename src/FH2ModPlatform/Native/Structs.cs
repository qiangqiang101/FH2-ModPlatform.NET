// Exact C# mirrors of the structs in platform/include/fh2mod.h (API 9).
// Field order, types and padding match the C header on x64 Windows; the
// LayoutCheck test compares every offset against the C compiler's.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FH2ModPlatform.Native;

/// <summary>Opaque handle for one registered mod (<c>FH2Mod*</c>).</summary>
public struct FH2Mod;
/// <summary>Opaque handle for one hook (<c>FH2Hook*</c>).</summary>
public struct FH2Hook;

/// <summary>One XMM register at an instruction hook.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct FH2Xmm
{
    [FieldOffset(0)] public Float4 F;
    [FieldOffset(0)] public Double2 D;
    [FieldOffset(0)] public UInt4 U32;
    [FieldOffset(0)] public ULong2 U64;

    [InlineArray(4)] public struct Float4 { private float _e; }
    [InlineArray(2)] public struct Double2 { private double _e; }
    [InlineArray(4)] public struct UInt4 { private uint _e; }
    [InlineArray(2)] public struct ULong2 { private ulong _e; }
}

/// <summary>The CPU's registers at an instruction hook (API 7). Changing <see cref="Rsp"/> has no effect.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FH2Context
{
    public ulong Rax, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi;
    public ulong R8, R9, R10, R11, R12, R13, R14, R15;
    public ulong RFlags;
    public ulong Reserved;
    public XmmRegisters Xmm;

    [InlineArray(16)] public struct XmmRegisters { private FH2Xmm _e; }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FH2ModInfo
{
    public uint Size;
    public byte* Name;
    public byte* Version;
    public byte* GithubRepo;
    public byte* PlatformVersion;
    public int CheckUpdates;
    public byte* Description;   // API 6
    public int OfflineOnly;     // API 9
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FH2Setting
{
    public uint Size;
    public byte* Key;
    public byte* Label;
    public byte* Description;
    public int Value;
    public int NeedsRestart;
    public delegate* unmanaged<void*, byte*, int, void> OnChange;
    public void* User;
    // API 5
    public int Kind;
    public float Min, Max;
    public float Step;
    public float SliderValue;
    public float DisplayScale;
    public int Decimals;
    public delegate* unmanaged<void*, byte*, float, void> OnSlide;
    // API 6
    public byte* Url;
    // API 9
    public byte** Choices;
    public uint ChoiceCount;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FH2Page
{
    public uint Size;
    public byte* Key;
    public byte* Title;
    public byte* Label;
    public byte* Description;
    public byte* Tile;
    public delegate* unmanaged<void*, byte*, void> OnOpen;
    public delegate* unmanaged<void*, byte*, byte*, void> OnSelect;
    public void* User;
    public delegate* unmanaged<void*, byte*, int, int> OnButton;
    public delegate* unmanaged<void*, byte*, int, int, void> OnSlot;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FH2PageRow
{
    public uint Size;
    public byte* Key;
    public byte* Label;
    public byte* Value;
    public byte* Description;
    public byte* Footer;
    public int Style;
    public int ValueAlert;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FH2TitleEntry
{
    public uint Size;
    public byte* Key;
    public byte* Label;
    public delegate* unmanaged<void*, byte*, int> OnPress;
    public void* User;
    public int Button;
    public byte** Choices;
    public uint ChoiceCount;
    public delegate* unmanaged<void*, byte*, uint, int> OnChoose;
    public byte* ListTitle;
    public byte* ListMessage;
}

/// <summary>A pop-up (API 10): the game's HUD notification, queued by the platform.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FH2Toast
{
    public uint Size;
    public byte* Key;        // letters, digits, '_'; replaces your waiting one with the same key; null = never
    public byte* Title;      // UTF-8, 28 characters, shown in capitals
    public byte* Subtitle;   // UTF-8, 45 characters; may be null
    public uint Icon;        // FH2_ICON_
    public uint Sound;       // FH2_SOUND_; 0 = none
    public uint Flags;       // FH2_TOAST_FLAG_
    public uint ExpireS;     // 0 = 120 s
}

/// <summary>One button prompt (API 10). Frozen: it never grows, and <c>Reserved</c> must be zero.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FH2Prompt
{
    public uint Glyph;       // FH2_GLYPH_
    public uint Vk;          // a Windows virtual-key code; 0 = the glyph always
    public uint Modifiers;   // FH2_KEY_
    public uint Word;        // FH2_WORD_: the game's own word instead of Label
    public byte* Hotkey;     // or one of your hotkeys' keys: its binding, whatever it is now
    public byte* Label;      // UTF-8
    public int Hold;         // nonzero: "Hold" before it (the HUD layer only)
    public fixed uint Reserved[5];
}

/// <summary>A tip (API 10).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FH2Tip
{
    public uint Size;
    public byte* Key;        // the seen key; null = no seen state
    public byte* Text;       // UTF-8 with "{button}"; 45 characters
    public FH2Prompt Button; // glyph 0, vk 0, no hotkey = none
    public uint DelayS;      // 0 = 60
    public uint MaxSessions; // FH2_TIP_FLAG_UNTIL_DONE only; 0 = 1
    public uint Flags;       // FH2_TIP_FLAG_
}

/// <summary>The game's message box (API 10).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FH2MessageBox
{
    public uint Size;
    public byte* Key;
    public byte* Title;      // 30 characters
    public byte* Message;    // 200 characters; may be null
    public byte** Choices;
    public uint ChoiceCount; // 1 to 99 (the platform adds Back)
    public uint Focus;
    public delegate* unmanaged<void*, byte*, uint, void> OnChoose;   // FH2_BOX_BACK = 0xFFFFFFFF
    public void* User;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FH2Hotkey
{
    public uint Size;
    public byte* Key;
    public byte* Label;
    public uint Vk;
    public uint Modifiers;
    public uint PadChord;
    public delegate* unmanaged<void*, byte*, void> OnPress;
    public delegate* unmanaged<void*, byte*, uint, uint, uint, void> OnRebind;
    public void* User;
}

/// <summary>The function table the platform passes to <c>fh2mod_init</c>. Members are in header order.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FH2ModApi
{
    public uint Size;
    public uint ApiVersion;
    public byte* PlatformVersion;
    public char* AzimuthDir;
    public char* GameDir;

    public delegate* unmanaged<FH2ModInfo*, FH2Mod*> RegisterMod;
    public delegate* unmanaged<FH2Mod*, byte*, void> Log;

    public delegate* unmanaged<FH2Mod*, byte*, ushort, char*, void> SetString;
    public delegate* unmanaged<FH2Mod*, byte*, char*, char*, void> ReplaceString;
    public delegate* unmanaged<FH2Mod*, byte*, void> ClearStrings;
    public delegate* unmanaged<FH2Mod*, void> ApplyStrings;

    public delegate* unmanaged<FH2Mod*, byte*, void> SetNotice;

    public delegate* unmanaged<void*> GameBase;
    public delegate* unmanaged<uint, void*> WaitForGame;

    // API 2
    public delegate* unmanaged<FH2Mod*, byte*, void*, nuint, int> SetFile;
    public delegate* unmanaged<FH2Mod*, byte*, void> ClearFile;
    public delegate* unmanaged<FH2Mod*, byte*, void> AddSql;
    public delegate* unmanaged<FH2Mod*, void> ClearSql;
    public delegate* unmanaged<FH2Mod*, void> ApplyDatabase;
    public delegate* unmanaged<FH2Mod*, byte*, uint, void*, void> PublishInterface;
    public delegate* unmanaged<byte*, uint, void*> GetInterface;
    public delegate* unmanaged<uint, int> WaitForMods;
    public delegate* unmanaged<byte*, byte*> ModVersion;

    // API 3
    public delegate* unmanaged<FH2Mod*, byte*, void*, void*, void**, FH2Hook*> HookFunction;
    public delegate* unmanaged<FH2Mod*, byte*, void*, void*, void**, FH2Hook*> HookCall;
    public delegate* unmanaged<FH2Mod*, byte*, void**, void*, void**, FH2Hook*> HookPointer;
    public delegate* unmanaged<FH2Mod*, FH2Hook*, int> EnableHooks;
    public delegate* unmanaged<FH2Mod*, FH2Hook*, int> DisableHooks;
    public delegate* unmanaged<uint, void*> WaitForCode;

    // API 4
    public delegate* unmanaged<FH2Mod*, FH2Setting*, int> AddSetting;
    public delegate* unmanaged<FH2Mod*, byte*, int> GetSetting;
    public delegate* unmanaged<FH2Mod*, byte*, int, void> SetSetting;

    // API 5
    public delegate* unmanaged<FH2Mod*, byte*, float> GetSlider;
    public delegate* unmanaged<FH2Mod*, byte*, float, void> SetSlider;

    // API 7
    public delegate* unmanaged<FH2Mod*, byte*, void*, void*, void*, uint, FH2Hook*> PatchBytes;
    public delegate* unmanaged<FH2Mod*, byte*, void*, delegate* unmanaged<FH2Context*, void*, void>, void*, FH2Hook*> HookInstruction;

    // API 8
    public delegate* unmanaged<FH2Mod*, FH2Page*, int> AddPage;
    public delegate* unmanaged<FH2Mod*, byte*, FH2PageRow*, uint, int> SetPageRows;
    public delegate* unmanaged<FH2Mod*, byte*, byte*, byte*, byte*, int> SetPageXaml;
    public delegate* unmanaged<FH2Mod*, byte*, int, int> GetPageSlot;
    public delegate* unmanaged<FH2Mod*, byte*, int, int, int> SetPageSlot;
    public delegate* unmanaged<FH2Mod*, byte*, byte*, int> FocusPageElement;
    public delegate* unmanaged<FH2Mod*, byte*, byte*, int> SetPageTileText;
    public delegate* unmanaged<FH2Mod*, byte*, uint, uint, int> SetPageTileColors;

    // API 9
    public delegate* unmanaged<FH2Mod*, FH2TitleEntry*, int> AddTitleEntry;
    public delegate* unmanaged<FH2Mod*, byte*> TitleStartedBy;
    public delegate* unmanaged<int> PlayingOnline;
    public delegate* unmanaged<byte*> SessionId;
    public delegate* unmanaged<FH2Mod*, char*, uint, char*> SessionLogPath;
    public delegate* unmanaged<FH2Mod*, byte*, uint, int> RequestSaveSnapshot;
    public delegate* unmanaged<FH2Mod*, void*, nuint, void> AddDumpMemory;
    public delegate* unmanaged<FH2Mod*, FH2Hotkey*, int> AddHotkey;
    public delegate* unmanaged<FH2Mod*, byte*, byte*, delegate* unmanaged<void*, byte*, byte*, byte*, nuint, void>, void*, int> AddCommand;

    // API 10 (platform 2.9.0): text in the game's language, translations, UI templates.
    public delegate* unmanaged<byte*, nuint, int> GameLanguage;
    public delegate* unmanaged<FH2Mod*, byte*, byte*, byte*, byte**, uint, int> SetSettingText;
    public delegate* unmanaged<FH2Mod*, byte*, void> SetModDescription;
    public delegate* unmanaged<byte*> Language;
    public delegate* unmanaged<FH2Mod*, byte*, byte*, nuint, int> AddTranslation;
    public delegate* unmanaged<FH2Mod*, byte*, byte*, byte*> Tr;
    public delegate* unmanaged<FH2Mod*, byte*, byte*, byte*, long, byte*> TrPlural;
    public delegate* unmanaged<FH2Mod*, byte*, ushort, byte*, char*, void> SetStringTr;
    public delegate* unmanaged<double, int, byte*, nuint, int> FormatNumber;
    public delegate* unmanaged<int, int, int, byte*, nuint, int> FormatDate;
    public delegate* unmanaged<FH2Mod*, uint> UiStatus;
    public delegate* unmanaged<FH2Mod*, uint, int> UiUses;
    public delegate* unmanaged<FH2Mod*, uint, int> PlayUiSound;
    public delegate* unmanaged<FH2Mod*, FH2Toast*, int> ShowToast;
    public delegate* unmanaged<FH2Mod*, byte*, void> ClearToast;
    public delegate* unmanaged<FH2Mod*, FH2Tip*, int> ShowTip;
    public delegate* unmanaged<FH2Mod*, byte*, int> TipSeen;
    public delegate* unmanaged<FH2Mod*, byte*, int, void> SetTipSeen;
    public delegate* unmanaged<FH2Mod*, FH2MessageBox*, int> ShowMessageBox;
    public delegate* unmanaged<FH2Mod*, byte*, FH2Prompt*, uint, int> SetPageHelp;
    public delegate* unmanaged<FH2Mod*, byte*, byte**, uint, int> SetPageHints;
    public delegate* unmanaged<FH2Mod*, byte*, int, uint, int> ShowPageHint;
}
