// The game's menus (AVUI, a C++ take on WPF), its input, and the cameras the mouse turns: what the
// Menus, GameInput, MapView, PhotoCamera, GarageCamera, ButtonIcons and SettingsTile classes use.
// From FH2 Mouse & Keyboard (FH2Mouse), which found them; each checked against the 24 Nov 2015 build.
namespace FH2;

internal static partial class Addresses
{
    // ---- The UI's frame and its objects ----------------------------------------------------------
    // UserInterface vtable slot 15: Update(this, float dt), once a UI frame on the UI thread.
    public const int UiUpdateSlot = 0x2ECFBF8;
    public static readonly Code UiUpdate = new("UserInterface::Update", 0x142C5E0, "40 53 48 83 EC 30 C5 FA 10 05");
    public const int Application = 0x36B0418;                 // AVUI::Application*: render size floats at +0x50/+0x54
    public static readonly Code GetWindowManager = new("WindowManager()", 0x157D110, "48 8B 0D 01 33 13 02 48 85 C9 74 07");
    public static readonly Code IsTypeOf = new("Type::IsTypeOf", 0x154F960, "40 57 48 83 EC 50 48 C7 44 24 20 FE");
    public static readonly Code KeyboardFocus = new("Keyboard::Focus", 0x14D01D0, "48 89 5C 24 08 57 48 83 EC 30 48 8B");
    public static readonly Code FocusedElement = new("Keyboard::FocusedElement", 0x1519FD0, "48 83 EC 28 E8 37 8E FB FF 48 8B 88");
    public static readonly Code KeyCode = new("window manager key code", 0x1514530, "FF C9 81 F9 AA 00 00 00 0F 87 0C 04");
    public static readonly Code ObjectFree = new("AVUI object free", 0x257730, "40 53 48 83 EC 20 48 8B D9 E8 B2 A2");
    public static readonly Code TransformToAncestor = new("Visual::TransformToAncestor", 0x1576BB0, "4C 89 4C 24 20 44 88 44 24 18 48 89");
    public static readonly Code MatrixTransform = new("Matrix::Transform", 0x146E570, "40 53 48 83 EC 20 49 8B 00 4C 8D 42");
    public static readonly Code StoredValue = new("value store lookup", 0x1548310, "48 89 5C 24 08 48 89 74 24 10 57 48");
    public static readonly Code UnsetValue = new("DependencyProperty::UnsetValue", 0x160C340, "48 8D 05 B1 E1 0A 02 C3 CC CC CC CC");
    public static readonly Code DpGetValue = new("DependencyObject::GetValue", 0x15E0F10, "48 8B C4 48 89 50 10 55 56 57 48 83");
    public static readonly Code DpSetValue = new("DependencyObject::SetValue", 0x15F8A70, "48 89 5C 24 08 48 89 74 24 10 57 48");
    public static readonly Code BoxFloat = new("box a float", 0x3F5990, "40 53 48 83 EC 30 48 8B D9 C7 44 24");
    public static readonly Code MakeAvuiString = new("AVUI String", 0x1663220, "48 89 4C 24 08 57 48 83 EC 30");
    public const int OpacityStored = 0x36A61D0, OpacityDefault = 0x36A61C8;
    public const int IsFocusedDp = 0x36A48B8, FocusableDp = 0x36A76F8, DataContextDp = 0x36C7128, SelectedItemDp = 0x36CF118;
    public const int ZPanelTransformDp = 0x36A5460, HelpButtonDp = 0x34E35F0, UriSourceDp = 0x36B1178;
    public const int WidthDp = 0x36C6958, HeightDp = 0x36C5C18;
    public const int WmActiveWindow = 0x68, WmKeySlot = 0xD8;
    public const int ObjType = 0x18, ObjRefCount = 0x10, VisualParent = 0x70, UiElementInput = 0xB8, VisualFlags = 0xA8;
    public const int VisualOffset = 0x90, RenderSize = 0x134, VtChildCount = 0x98, VtChild = 0xA0, VtGtTransform = 0xE0;
    public const int BoxedValue = 0x28;
    // AVUI's static Type objects.
    public const int TypeVisual = 0x36A25B0, TypeUiElement = 0x36A3520, TypeButtonBase = 0x36ABE70, TypeRepeatButton = 0x36D0F10;
    public const int TypeListBoxItem = 0x3689AE0, TypeComboItem = 0x36B79D0, TypeTabItem = 0x36DABC0, TypeTreeItem = 0x36DB0C0;
    public const int TypeMenuItem = 0x3686B00, TypeSlider = 0x36D0070, TypeSelector = 0x36CE8F0, TypeSpinner = 0x3681F40;
    public const int TypeHelpButton = 0x34E3D00;

    // ---- The game's input ----------------------------------------------------------------------------
    // Controls are asked for by (context, control): context 0 is driving, 7 the menus.
    public static readonly Code ContextActive = new("input IsContextActive", 0x1072890, "40 53 48 83 EC 40 48 83 79 58 00 8B");
    public static readonly Code GetDigital = new("input GetDigital", 0x106E3E0, "40 53 41 55 41 56 41 57 48 83 EC 58");
    public static readonly Code GetAnalog = new("input GetAnalog", 0x106F040, "40 53 41 55 41 56 41 57 48 83 EC 68");
    public static readonly Code WasPressed = new("input WasPressed", 0x1072510, "44 88 4C 24 20 55 57 41 54 41 57 48");
    public static readonly Code Consume = new("input Consume", 0x106D640, "48 89 5C 24 08 48 89 6C 24 10 48 89");
    // int ButtonState(input, context, control, bool): another way the game asks about a button (for tracing only).
    public static readonly Code ButtonState = new("input ButtonState", 0x106E690, "40 56 41 55 41 56 41 57 48 83 EC 48");
    // The input update, once a frame: each player's pad into their input. FH2 ANNA hooks it too.
    public static readonly Code InputUpdate = new("input update", 0x1075490, "48 8B C4 48 89 48 08 55 56 57 41 54");
    public const int PadKeys = 0x2254390;     // 24 x {pressed control, released control, key}: the pad-to-key pump's table
    public const int Inputs = 0x365D7C8;      // the players' inputs, 0x1300 apart; +0x70 each one's pad (or null)
    public const int InputsStride = 0x1300, InputsDevice = 0x70, InputsCurrent = 0xAB28;
    public const int InputConnected = 0x4, InputButtons = 0x78, InputState = 0x70, InputLastState = 0x128;
    public const int StateButtons = 0x8, StateDpad = 0x10, StateXInputButtons = 0x50;
    // The buttons of the last frames they were held in (seen with a controller, not in the code): 10 entries of 8
    // bytes (the XInput bits), then the next one's index and how many frames they've been held; all 0 when let go.
    // Some menus move by it (the race's start screen goes up and down by it, left and right without).
    public const int StateHistory = 0x60, StateHistoryIndex = 0xB0, StateHistoryCount = 0xB4, HistoryLength = 10;
    // Without a pad: the pairing page, the profile's input, the HUD's pause, the UI's input, signing in.
    public static readonly Code ShowPairing = new("show controller pairing", 0xB620C0, "48 8B C4 55 41 56 41 57 48 8D 68 C8");
    public static readonly Code ProfileInput = new("profile's input", 0x124A9F0, "40 57 48 83 EC 30 48 C7 44 24 20 FE FF FF FF 48 89 5C 24 48 0F B6 DA");
    public static readonly Code PadLostPause = new("HUD: pause while the pad is gone", 0x9648FA - 13, "80 7B 04 00 75 0C 48 8D 57 28 48 8B CF E8");
    public const int PadLostPauseCall = 0x9648FA;
    public const int UiSetInputSlot = 0x2ECFC68;
    public static readonly Code UiSetInput = new("UserInterface::SetInput", 0x14237E0, "48 89 91 88 00 00 00 C3");
    public static readonly Code SignInGiveUp = new("sign in: give up without a pad", 0xE7ECC4, "0F 84 9B 01 00 00");

    // ---- The cameras ---------------------------------------------------------------------------
    // The map: MapScene (the focused page) and its MapViewControllerInteractive.
    public const int MapSceneVtable = 0x2DFED48, MapControllerVtable = 0x2D9CD48, MapController = 0x410;
    public const int MapCursorX = 0x2C0, MapCursorZ = 0x2C8, MapTargetX = 0x2D0, MapTargetZ = 0x2D8, MapCameraX = 0x27C, MapCameraZ = 0x290;
    public const int MapZoom = 0x2E0, MapZoomShown = 0x2E4, MapLeash = 0x3B0, MapZoomMin = 0x384, MapZoomMax = 0x388;
    // Photo mode's camera reads its input (CCamPhotoMode slot 7; the Autoshow's camera has the function too).
    public const int PhotoInputSlot = 0x2C6C5B0;
    public static readonly Code PhotoInput = new("photo mode camera input", 0x103F660, "40 53 48 83 EC 40 80 B9 2C 02 00 00");
    public const int PhotoForward = 0x1F0, PhotoRight = 0x1F4, PhotoRise = 0x1F8, PhotoTurnX = 0x1FC, PhotoTurnY = 0x200, PhotoZoom = 0x204;
    // The garage camera's Update(camera, input): mode 0 orbits, +0xA8 its distance (-2, -3.5, -5...).
    public static readonly Code GarageCamera = new("garage camera", 0xAA02E0, "48 89 5C 24 08 48 89 74 24 10 48 89");
    public const int GarageCameraMode = 0x15C, GarageCameraDistance = 0xA8;

    // ---- Button pictures ---------------------------------------------------------------------------
    // A ControllerButtonControl shows its button with IconUpdate(control, type), which calls FindResource(element,
    // String* name) for "A_BitmapImage_ControllerButton_<button>" at two places; an ActivateButtonControl (the
    // HUD's prompts) the same in ActivateUpdate. The BitmapImageTypeConverter makes images from a path.
    public static readonly Code FindResource = new("FindResource", 0x164F840, "48 89 5C 24 08 57 48 83 EC 50 33 FF");
    public static readonly Code IconUpdate = new("ControllerButtonControl icon update", 0xB61C20, "89 54 24 10 56 57 41 56 48 83 EC 40");
    public static readonly Code ActivateUpdate = new("ActivateButtonControl update", 0xB6A560, "40 55 56 57 41 56 41 57 48 8D 6C 24");
    public static readonly int[] IconFindCalls = [0xB61FFE, 0xB62055];
    public const int ActivateFindCall = 0xB6B054;
    public const int ActivateShown = 0x1F8, HelpIcon = 0x1D8, HelpIconShadow = 0x1E0;
    public const int BitmapConverterVtable = 0x2D5F628;
    public static readonly Code BitmapCanConvert = new("BitmapImageTypeConverter vtable +0", BitmapConverterVtable, "00 2C 53 41 01 00 00 00");
    public static readonly Code BitmapConvertFrom = new("BitmapImageTypeConverter vtable +8", BitmapConverterVtable + 8, "F0 99 59 41 01 00 00 00");

    // ---- Pause > Settings: a tile of a mod's own -----------------------------------------------------
    public static readonly Code GetResource = new("XAML reader data", 0xAABEC0, "48 83 EC 18 48 8B 41 38 C7 04 24 00");
    public const int PageXamlReturn = 0x16EBB3C;   // after the page loader's call to it
    public static readonly Code GameAllocate = new("game allocate", 0x3229C0, "48 89 5C 24 08 57 48 83 EC 20 8B 15");
    public static readonly Code ObjectInit = new("AVUI object init", 0x143EB80, "48 89 5C 24 08 57 48 83 EC 20 48 8D");
    public static readonly Code ObjectCleanup = new("AVUI object cleanup", 0x1442050, "40 53 48 83 EC 20 48 8D 05 B3 D0 9F");
    public static readonly Code PauseWireTiles = new("PauseOptionsMenu: wire the tiles", 0xDC8A60, "48 8B C4 55 48 8D 68 D8 48 81 EC 20");
    public static readonly Code WireButton = new("page: wire a button", 0xD9FC10, "48 8B C4 57 48 81 EC 90 00 00 00 48");
    public static readonly Code HudTile = new("PauseOptionsMenu::OnHud", 0xDC6C40, "40 53 48 83 EC 30 48 8B D9 48 8B 89");
    public static readonly Code HudSceneRecord = new("HUD page scene record", 0xB3A3E0, "48 8B C4 48 89 48 08 57 48 83 EC 70");
    public static readonly Code FindProperty = new("Type::FindProperty", 0x157FF60, "48 89 5C 24 08 48 89 74 24 10 57 48");
    public static readonly Code NotifyProperty = new("raise PropertyChanged", 0xCF9C60, "40 57 48 83 EC 60 48 C7 44 24 20 FE");
    public const int DescriptorSetter = 0x30;

    public static readonly Code[] MenusCore =
    [
        UiUpdate, GetWindowManager, IsTypeOf, KeyboardFocus, FocusedElement, KeyCode, ObjectFree, TransformToAncestor,
        MatrixTransform, StoredValue, UnsetValue, DpGetValue,
    ];
}
