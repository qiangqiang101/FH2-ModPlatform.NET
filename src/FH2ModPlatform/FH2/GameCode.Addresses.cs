// Where the FH2 classes reach into forza_durango_release_final.exe (the 24 Nov 2015 build
// Azimuth runs). Offsets are from the game's base. Every one comes with the bytes it starts
// with, checked before anything is called or hooked: on any other build the classes stay off.
namespace FH2;

internal static partial class Addresses
{
    internal readonly record struct Code(string Name, int Offset, string Bytes);

    // ---- The game thread ---------------------------------------------------------------
    // ForzaCar's per-frame update (fh2game.h FH2_GAME_FORZACAR_UPDATE, 0x2A9400), its first
    // instruction after the prologue: cmp qword ptr [rbx+0x4F80], 0, with rbx = the car. Runs
    // for every car, every frame, on the thread the stateflow runs on (measured in game).
    public static readonly Code CarUpdate = new("ForzaCar update", 0x2A9436, "48 83 BB 80 4F 00 00 00");
    // The car's kind (fh2game.h FH2_GAME_FORZACAR_KIND): car+0x4D08, 0 = the local player's,
    // 1 a Drivatar, 2 traffic.
    public static readonly Code CarKind = new("ForzaCar kind", 0xF62280, "8B 81 08 4D 00 00 C3");
    public const int CarKindField = 0x4D08;

    // ---- The profile ---------------------------------------------------------------------
    // ProfileManager(): mov rax, [rip+0x318CBA1]; ret. The manager's vfunc 0x50 is the current
    // profile, the profile's vfunc 0xD8 its garage, and garage+8 its key/value store
    // ("Main/Credits", "Main/CareerCar"...).
    public static readonly Code ProfileManager = new("ProfileManager()", 0x327270, "48 8B 05 A1 CB 18 03 C3");
    public const int ProfileManagerGlobal = 0x327277 + 0x318CBA1;
    public const int CurrentProfileSlot = 0x50 / 8;
    public const int GarageSlot = 0xD8 / 8;
    public const int StoreField = 0x08;

    // Value* Store::Get(store, Value* out, const char* key); the value's int is at +8.
    public static readonly Code StoreGet = new("profile store get", 0x2E11F0, "48 8B C4 48 89 50 10 57 48 81 EC A0");
    // void Store::Set(store, const char* key, const Value* value)
    public static readonly Code StoreSet = new("profile store set", 0x2F7940, "48 8B C4 41 56 48 81 EC A0 00 00 00");
    // Value* Value::Value(this, int), and its destructor.
    public static readonly Code ValueFromInt = new("profile value (int)", 0x107CBF0, "40 53 48 83 EC 20 48 8D 05 C3 DF DF");
    public static readonly Code ValueFree = new("profile value free", 0x107D880, "48 8D 05 39 D3 DF 01 83 CA FF 48 89");
    public const int ValueSize = 0x40;   // generous: the game's own are on the stack, at most 0x38 apart
    public const int ValueIntField = 0x08;

    // ---- The garage --------------------------------------------------------------------------
    // vector<Object*> Garage::AddCar(int ordinal, int colour, int* garageIdOut, int kind, void* extra,
    // bool flag): rcx garage, rdx the vector to fill. What the stateflow's AwardCar calls.
    public static readonly Code AddCar = new("Garage::AddCar", 0x2CB240, "4C 8B DC 57 41 56 41 57 48 83 EC 60");
    public const int AddCarListReleaseSlot = 0x18 / 8;
    // The game's operator delete.
    public static readonly Code GameDelete = new("operator delete", 0x31C7B0, "48 85 C9 74 1D 53 48 83 EC 20 48 8B D9");
    // void Garage::SetCurrentCar(int garageId, const CarLoadOptions*): stores Main/CareerCar.
    public static readonly Code SetCurrentCar = new("Garage::SetCurrentCar", 0x2DE4E0, "4C 89 44 24 18 55 56 57 48 81 EC C0");
    // CarLoadOptions::CarLoadOptions(this): the default options, about 0x60 bytes.
    public static readonly Code LoadOptions = new("CarLoadOptions()", 0x215EC0, "48 89 5C 24 08 57 48 83 EC 20 33 FF");
    public const int LoadOptionsSize = 0x80;
    // Pause > My Cars, after SetCurrentCar: profile->vfunc[0x100]()->vfunc[0x1A8](sub, garage, garageId).
    public const int PlayerCarSettingsSlot = 0x100 / 8;
    public const int SetPlayerCarSlot = 0x1A8 / 8;

    // ---- The car (ForzaCar) ------------------------------------------------------------------
    // Found by other FH2 mods (their sources say which); checked in game where noted.
    public const int CarIdField = 0x118;          // Data_Car.Id (NitroSystem; read in game: 2049 = Jeep Willys MB)
    public const int CarTargetField = 0x20;       // the car's "target" (DroneMode, FH2 ANNA)
    public const int CarBodyField = 0x4C08;       // the physics body's interface: CCarDynamics + 0x3A0 (NitroSystem, DroneMode)
    public const int BodyToDynamics = -0x3A0;
    public const int DynamicsPosition = 0x10;     // float x, y (up), z (NitroSystem FH3 notes; read in game, = body+0xEF0)
    public const int DynamicsForward = 0x40;      // float[3] (NitroSystem)
    public const int BodySpeed = 0xE28;           // float m/s (DroneMode; VirtualDashboard)
    public const int BodyGear = 0xBA4;            // int, 0 = reverse (VirtualDashboard's probing; R = 0 checked in game)
    public const int BodyRpm = 0x148;             // float rad/s (VirtualDashboard's probing; 800 rpm idle in game)
    public const int BodyThrottle = 0xDB4, BodyBrake = 0xDFC, BodySteer = 0xE00, BodyHandbrake = 0xE08;   // VirtualDashboard
    public const int BodyClutch = 0xA8C;          // 0..1 (VirtualDashboard)
    public const int BodyRedline = 0x1C8, BodyLimiter = 0x1CC;   // rad/s: where the red zone starts, the rev limiter (VirtualDashboard)
    public const int BodyBoost = 0x170;           // the turbo's boost, as the game keeps it (VirtualDashboard)
    public const int CarHeadlightsOn = 0x51A2;    // byte (FH2 ANNA)
    public const int CarHeadlightsByDriver = 0x51A1, CarHeadlightsAuto = 0x51B4;   // bytes: the driver's choice, else the game's (FH2 ANNA)
    // Indicators: bytes hazards, left, right (setters 0xF755C0 / 0xF76F10 / 0xF774E0 write them). Each car update turns them
    // into the lights' blink; the game resets them now and then, so held ones are written before every update (FH2 ANNA).
    public const int CarSignals = 0x51BB;
    // Parts: the animation graph (car +0x4D40 -> +0x1C8) has named events ("roof:open", "doorLF:close"), the clips
    // ForzaVista plays, kept in an unordered_map<std::string, ...> at graph +0x268 (its list's head node; count at +0x270).
    public const int CarAnimator = 0x4D40, AnimatorGraph = 0x1C8, GraphEvents = 0x268;
    public static readonly Code CarAnimEvent = new("ForzaCar animation event", 0xF5D300, "40 53 48 83 EC 20 48 8B 89 40 4D 00");
    public static readonly Code FindAnimEvent = new("animation graph event lookup", 0x16FD4D0, "48 89 5C 24 18 48 89 74 24 20 57 41");
    public static readonly Code AnimGraphDetail = new("animation graph detail", 0x16FBE90, "8B C2 C5 E8 57 D2 89 51 10 48 8B 15");
    public static readonly Code[] Parts = [CarAnimEvent, FindAnimEvent, AnimGraphDetail];
    // void SetAiDriver(car, bool ai): hands the car to the game's AI driver and back; the driver holder is car +0x4D10,
    // its mode +0x4D30 (1 the player's, 0 the AI's) (FH2 ANNA's autodrive).
    public static readonly Code SetAiDriver = new("ForzaCar SetAiDriver", 0xF79F10, "48 8B C1 48 8B 89 10 4D 00 00 48 85");
    public const int CarDriverHolder = 0x4D10, CarDriverMode = 0x4D30;
    public const int CarMediaName = 0x350;        // std::string, "ALF_8C_08" (VirtualDashboard)
    // void SetHeadlights(car, bool) and void SetHeadlightsByDriver(car, bool) (FH2 ANNA).
    public static readonly Code SetHeadlights = new("ForzaCar SetHeadlights", 0xF74EF0, "88 91 A2 51 00 00 C3");
    public static readonly Code SetHeadlightsByDriver = new("ForzaCar SetHeadlightsByDriver", 0xF74EE0, "88 91 A1 51 00 00 C3");

    // ---- Car physics (from the Nitro System mod, FnFNitro) --------------------------------------
    // bool CCarDynamics::UpdateDynamics(this, rdx, r8, uint32 r9, float dt): every simulated car, every
    // physics step, on the physics thread (CCarDynamics vtables 0x2EB4B08 and 0x2EB5D68, slot 16).
    public static readonly Code UpdateDynamics = new("CCarDynamics::UpdateDynamics", 0x12C1EA0, "48 89 5C 24 08 48 89 6C 24 10 48 89");
    public const int DynamicsVtable = 0x2EB4B08, DynamicsVtableRefCounted = 0x2EB5D68;
    public const int DynamicsSimulatedField = 0x178;   // int: 1 for every simulated car (the player's, AI, traffic)
    public const int DynamicsVelocity = 0x50;          // float[3], m/s: written within the physics step to push the car

    // ---- Exhaust flames (from FnFNitro) ----------------------------------------------------------------
    // A car's presentation: ForzaCar+0x4D38, a CCarPresentationDeferred holding the CarPresentation11 at +8.
    public const int CarPresentationField = 0x4D38, DeferredPresentationInner = 0x8;
    public const int DeferredPresentationVtable = 0x2EBEF68;
    public const int PresentationVtable = 0x2EC1450, PresentationVtableRefCounted = 0x2EC2B70;
    // uint64 CarPresentation11::UpdateBackfire(pres, message, 0, 0) (its vtable slot 76): the game's backfire
    // routine. The message starts with 8 exhaust tips of 0x60 bytes: a 4x4 matrix, then a flag byte at +0x40 for
    // each tip to fire. It runs on the thread of the game logic's frame step below.
    public static readonly Code UpdateBackfire = new("CarPresentation11::UpdateBackfire", 0x13B0D70, "48 8B C4 48 89 50 10 55 41 54 41 55");
    public static readonly Code LogicFrame = new("GameLogic frame step", 0xFA78A0, "40 56 57 41 54 41 56 41 57 48 83 EC");
    public const int BackfireTips = 8, BackfireTipSize = 0x60, BackfireTipFlag = 0x40, BackfireMessageSize = 0x400;
    // The car's exhaust locators: 8 weak pointers at ForzaCar+0x88 (object, then control block with the use count
    // at +8); a locator's vfunc 2 gives its matrix. Empty until the car resolves them (ForzaCar vfunc 58).
    public const int CarExhaustLocators = 0x88;
    public const int ForzaCarVtable = 0x2C5CB80;
    public static readonly Code ResolveLocatorsEntry = new("ForzaCar vtable: resolve locators", 0x2C5CB80 + 58 * 8, "40 DB F7 40 01 00 00 00");
    public const int ResolveLocatorsSlot = 58, ResolveLocatorsFn = 0xF7DB40;
    public const int ImageSize = 0x3A18000;

    // ---- Sound: the game's own FMOD (FnFNitro, after HonkHonk) ------------------------------------------
    // Each FMOD function names itself in FMOD's error-trace string ("System::createSound"...). Every call is
    // made from inside the game's Studio::System::update, on the thread it drives FMOD from.
    public static readonly Code FmodStudioUpdate = new("FMOD Studio::System::update", 0x1A9C7B0, "48 89 5C 24 10 57 48 81 EC 50 01 00");
    public static readonly Code FmodGetLowLevel = new("FMOD Studio::System::getLowLevelSystem", 0x1A98080, "48 89 5C 24 18 48 89 74 24 20 57 48");
    public static readonly Code FmodCreateSound = new("FMOD System::createSound", 0x1997740, "40 53 55 56 57 41 56 41 57 48 81 EC");
    public static readonly Code FmodPlaySound = new("FMOD System::playSound", 0x199B0F0, "40 53 55 56 57 41 56 41 57 48 81 EC");
    public static readonly Code FmodGetListener = new("FMOD System::get3DListenerAttributes", 0x1997C00, "40 53 55 56 57 41 54 41 56 41 57 48");
    public static readonly Code FmodGetSoftwareFormat = new("FMOD System::getSoftwareFormat", 0x1999F70, "40 53 55 56 57 41 56 41 57 48 81 EC");
    public static readonly Code FmodSet3D = new("FMOD ChannelControl::set3DAttributes", 0x19A3F50, "40 53 55 56 57 41 56 41 57 48 81 EC");
    public static readonly Code FmodSet3DMinMax = new("FMOD ChannelControl::set3DMinMaxDistance", 0x19A4BC0, "48 8B C4 55 56 57 48 81 EC 90 01 00");
    public static readonly Code FmodSetVolume = new("FMOD ChannelControl::setVolume", 0x19A6470, "48 8B C4 57 48 81 EC 80 01 00 00 48");
    public static readonly Code FmodSetPaused = new("FMOD ChannelControl::setPaused", 0x19A5E50, "48 8B C4 57 48 81 EC 70 01 00 00 48");
    public static readonly Code FmodStop = new("FMOD ChannelControl::stop", 0x19A6730, "40 57 48 81 EC 70 01 00 00 48 C7 44");
    public static readonly Code FmodSetPriority = new("FMOD Channel::setPriority", 0x19AD070, "48 8B C4 57 48 81 EC 70 01 00 00 48");
    public static readonly Code FmodSetVolumeRamp = new("FMOD ChannelControl::setVolumeRamp", 0x19A6610, "48 8B C4 57 48 81 EC 60 01 00 00 48");
    public const uint FmodLoopOff = 0x1, FmodLoopNormal = 0x2, Fmod3D = 0x10, FmodCreateSample = 0x100,
                      FmodOpenMemory = 0x800, Fmod3DLinearSquareRolloff = 0x400000;
    public const int FmodExInfoSize = 0xD8;
    public const int PresentationPosition = 0x50;   // float[3]: the car as drawn (moves in step with the listener)

    // ---- The camera (FnFNitro; found through Deuquan's FH2-CameraMods) ------------------------------------
    // void* ViewMatrix(camera, float out[12]): each camera's view matrix (rows right/up/forward, the translation in
    // the 4th column), its vtable slot +0x50: one function for the driver, hood and bumper cameras, one for the
    // three chase cameras. Other cameras share them: the vtables say which is which.
    public static readonly Code ViewMatrixMounted = new("CameraMounted view matrix", 0x1034360, "48 89 5C 24 08 57 48 83 EC 70 C5 F8");
    public static readonly Code ViewMatrixChase = new("CameraFollow view matrix", 0x1034430, "48 8B C4 48 89 58 18 57 48 81 EC 00");
    public static readonly int[] ShakeCameraVtables = [0x2E78120, 0x2E77EA8, 0x2E703B0, 0x2E783A0, 0x2E78610, 0x2E78880];
    // float GetFov(camera), vtable slot +0x70: an angle in radians, what the view is drawn with. One for the mounted
    // cameras, one for the chase cameras (FnFNitro's FOV boost; the slot from Extra Options' notes).
    public static readonly Code FovMounted = new("CameraMounted::GetFov", 0x10340F0, "80 B9 18 02 00 00 00 74 11 C5 FA 10");
    public static readonly Code FovChase = new("CameraFollow::GetFov", 0x1034120, "80 B9 88 01 00 00 00 48 8B 81 80 01");
    public static readonly Code[] CameraFov = [FovMounted, FovChase];
    // The camera director (FH2 ANNA's cinematic camera): the player manager +0x48 +0x98 +0x2D0 is the views, the first
    // one's vtable +0x38 gives its camera, a director (vtable 0x2C6C840). Its active camera is at +0x208; its groups
    // (camera modes) at +0x1B0, the current one from CurrentCameraGroup(director + 0x1B0), changed with SetCameraGroup.
    public const int ViewsChain1 = 0x48, ViewsChain2 = 0x98, ViewsChain3 = 0x2D0, ViewCameraSlot = 0x38;
    public const int DirectorVtable = 0x2C6C840, DirectorActive = 0x208, DirectorGroups = 0x1B0, DirectorGroupCount = 0x1D0;
    public const int CameraPositionSlot = 0x158;   // void(camera, double out[4]): where it is (any camera)
    public static readonly Code CurrentCameraGroup = new("camera director current group", 0x399FF0, "48 83 EC 58 48 8D 54 24 20 E8 D2 32");
    public static readonly Code SetCameraGroup = new("camera director set group", 0xFE5780, "40 53 57 41 56 48 83 EC 20 48 8B 81");
    public static readonly Code ReplayShot = new("camera director replay shot", 0xFE5E30, "48 89 5C 24 08 48 89 6C 24 10 48 89");
    // void Flash(view, 3, 0.6f): the white flash between cameras; the view's vtable 0x2E6E890 or 0x2C66188, not while +0xB1.
    public static readonly Code CameraFlash = new("camera flash", 0xFDDAE0, "48 89 5C 24 08 57 48 81 EC D0 00 00");
    public const int ViewVtableA = 0x2E6E890, ViewVtableB = 0x2C66188, ViewBusy = 0xB1;
    public static readonly Code[] CameraDirector = [CurrentCameraGroup, SetCameraGroup, ReplayShot, CameraFlash];

    // ---- The HUD speedometer (FnFNitro) -------------------------------------------------------------------
    // void HUDStatusSpeedometer::UpdateGauges(speedo, float rpm): each HUD frame while the speedometer is up. Hooked
    // at its only call (other mods patch the function itself). The gauge's data items ("RPMGaugeNumbers"):
    // speedo+0x150 -> +0x50 -> +0x28 -> +0x28, an array of items; each has XPos (+0x38) and YPos (+0x3C), which the
    // speedometer XAML binds to, and tells the UI of a change through RaiseItemProperty(item, property).
    public static readonly Code SpeedometerCall = new("HUDStatusSpeedometer::UpdateGauges call", 0x783E7E, "E8");
    public static readonly Code SpeedometerUpdate = new("HUDStatusSpeedometer::UpdateGauges", 0xAD6070, "40 53 48 83 EC 20 C5 F2 5E 81 60 01");
    public static readonly Code GaugeXPosProperty = new("gauge item XPos property", 0xADD560, "40 57 48 83 EC 30 48 C7 44 24 20 FE");
    public static readonly Code GaugeYPosProperty = new("gauge item YPos property", 0xADD620, "40 57 48 83 EC 30 48 C7 44 24 20 FE");
    public static readonly Code RaiseItemProperty = new("gauge RaiseItemProperty", 0xAD1A80, "40 57 48 83 EC 60 48 C7 44 24 20 FE");
    public const int SpeedometerItems = 0x150, GaugeItemsA = 0x50, GaugeItemsB = 0x28, GaugeItemsC = 0x28;
    public const int GaugeItemX = 0x38, GaugeItemY = 0x3C, GaugeItemCount = 21;

    // ---- The game's text language (FH2 ANNA, Extra Options) ------------------------------------------------
    // The string tables' manager, [game+0x3662990]: its code ("EN", "DE", "CHT"...) is an MSVC std::string at +0x30.
    public const int StringsManagerGlobal = 0x3662990, LanguageCodeField = 0x30;

    // Repairing: what the stateflow's ResetCarDamage behavior (global.reset_car_damage, used by the
    // garage, road trips and car meets; on-enter 0x8FD920) does to a car:
    //     car->vfunc[0x120]();  ClearDamageTotals(car);  if (reset_simulated_damage) car->vfunc[0x128]();
    // In ForzaCar's vtable those are 0x29D3E0 (the damage) and 0x29D6E0 (the simulated damage).
    public const int ResetDamageSlot = 0x120 / 8, ResetSimulatedDamageSlot = 0x128 / 8;
    public static readonly Code ResetDamageEntry = new("ForzaCar vtable: reset damage", 0x2C5CB80 + 0x120, "E0 D3 29 40 01 00 00 00");
    public static readonly Code ResetSimulatedDamageEntry = new("ForzaCar vtable: reset simulated damage", 0x2C5CB80 + 0x128, "E0 D6 29 40 01 00 00 00");
    // void ClearDamageTotals(car): zeroes car+0x5AE8 (8 bytes) and car+0x5AF0 (4).
    public static readonly Code ClearDamageTotals = new("ForzaCar clear damage totals", 0x29D480, "33 C0 48 89 81 E8 5A 00 00 89 81 F0");

    // ---- HUD notifications (fh2game.h; the Achievements and Community Garage mods use them) ----------
    // The HUD's notification component (HUDMinorNotificationComponent, vtable 0x2CD7B70): an
    // instruction in its constructor and one in its destructor, with rbx = the component.
    public const int NotificationVtable = 0x2CD7B70;
    public static readonly Code NotificationCtorAt = new("HUD notification constructor", 0x73E0B1, "48 8D 8B A0 01 00 00");
    public static readonly Code NotificationDtorAt = new("HUD notification destructor", 0x73F046, "48 89 41 28 48 81 C1 A0 01 00 00");
    // An instruction in the AchievementsModule's per-frame tick, on the game's thread.
    public static readonly Code AchievementsTickAt = new("AchievementsModule tick", 0x3FCD84, "8B 81 A8 00 00 00");
    // void** MakeDisplayString(void** out, const char* text, int len = -1): a ref-counted display string
    // (allocated from the calling thread's game heap: game thread only).
    public static readonly Code MakeDisplayString = new("MakeDisplayString", 0x323310, "40 57 48 83 EC 30 48 C7 44 24 28 FE");
    // The display string is UTF-16 inside ({vtable, wchar_t* buffer, uint capacity}), but MakeDisplayString's
    // char* text goes through the char* setter (0x1246700): MultiByteToWideChar(CP_ACP), ANSI, not UTF-8.
    // void SetDisplayStringWide(string, const wchar_t* text, int len = -1): its wchar_t* twin (wcsncpy_s), what
    // the wchar_t* constructor (0x1239840) calls. It regrows the buffer from the game heap: game thread only.
    public static readonly Code SetDisplayStringWide = new("SetDisplayStringWide", 0x12467B0, "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20");
    // void ShowNotification(component, title*, text*, icon*): display strings by value (it releases them).
    public static readonly Code ShowNotification = new("ShowNotification", 0x755330, "4C 89 4C 24 20 4C 89 44 24 18 48 89");

    // ---- The world ----------------------------------------------------------------------------
    // The player manager global (FH2 ANNA, Extra Options): +0x48 the simulation (its +0x28: paused,
    // byte), whose +0x98 is the world, whose +0x480 is the time of day (getter 0x2E1F90 below).
    public const int PlayerManagerGlobal = 0x34ABA60;
    // ---- Moments (FH2 ANNA's autodrive) and hiding the UI (ANNA-DroneMode) -----------------------------
    // uintptr_t PauseEnter(state): the pause menu opens. uintptr_t EventEnter(context): an event starts.
    public static readonly Code PauseEnter = new("pause menu enter", 0x8E98E0, "48 89 5C 24 08 57 48 83 EC 20 48 8B");
    public static readonly Code EventEnter = new("event enter", 0x912860, "40 57 48 83 EC 40 48 C7 44 24 20 FE");
    // void ClearRewind(rewind, 1, 1): forgets the rewind buffer; the rewind at [0x34D5710], while the recorder [0x34D5700] is.
    public static readonly Code ClearRewindCode = new("rewind clear", 0x99C4C0, "48 89 5C 24 10 56 48 83 EC 20 41 0F");
    public const int RewindGlobal = 0x34D5710, RewindRecorderGlobal = 0x34D5700;
    // Application::Render's UI wait (renderer, surface, parameters), draw (app, screen, transform) and present (app,
    // transform): with the UI hidden they let go of what they're handed (the surface's Release at +0x18; the screen's
    // root refcount at +0x10, freed with the game's FreeObject) and draw nothing.
    public static readonly Code UiWait = new("UI render wait", 0x1140ED0, "48 89 54 24 10 55 56 57 48 83 EC 30");
    public static readonly Code UiDraw = new("UI draw", 0x15B51A0, "48 89 54 24 10 57 48 83 EC 30 48 C7");
    public static readonly Code UiPresent = new("UI present", 0x15B5110, "40 53 48 83 EC 20 48 8B D9 48 8B 0D");
    public static readonly Code FreeObject = new("FreeObject", 0x257730, "40 53 48 83 EC 20 48 8B D9 E8 B2 A2");
    public static readonly Code[] HideUi = [UiWait, UiDraw, UiPresent, FreeObject];
    // CareerNavigationModule (FH2 ANNA's routes; the module ANNA's voice commands used on the Xbox): caught as it's made.
    public static readonly Code NavCtor = new("CareerNavigationModule()", 0x462930, "48 89 5C 24 08 48 89 74 24 10 57 48");
    public static readonly Code NavDtor = new("~CareerNavigationModule()", 0x4685D0, "48 89 5C 24 08 48 89 74 24 10 48 89");
    public static readonly Code NearestEvent = new("SetDestinationToNearestCareerEvent", 0x4825B0, "33 D2 E9 09 00 00 00 CC");
    public static readonly Code NearestBucketList = new("SetDestinationToNearestBucketList", 0x4823E0, "40 55 53 56 57 48 8D 6C 24 C1 48 81");
    public static readonly Code NearestRoadTrip = new("SetDestinationToNearestRoadtripLocation", 0x4829E0, "40 53 48 83 EC 40 48 8D 54 24 20 48");
    public static readonly Code NearestBoard = new("route to the nearest bonus board", 0x482740, "40 55 53 56 57 41 56 48 8D 6C 24 C9");
    public static readonly Code NearestBarnFind = new("route to the nearest barn find", 0x715A70, "48 8B C4 48 89 50 10 57 48 81 EC 90");
    public static readonly Code LockWeakWorld = new("lock the world (weak to shared)", 0x1908670, "40 53 48 83 EC 30 48 83 C1 28 48 8B");
    // The campaign's feature unlocks (XboxMusicRadio): FeatureUnlocks() gives the table, IsFeatureUnlocked(table,
    // const std::string* name) one byte per name ("xbox_music", "multiplayer", "festival", "map"...).
    public static readonly Code FeatureUnlocks = new("feature unlocks", 0xA65CA0, "40 53 48 83 EC 70 48 C7 44 24 30 FE");
    public static readonly Code IsFeatureUnlocked = new("feature unlocked", 0xA67500, "40 53 55 56 57 41 56 48 83 EC 20 4C");
    // The radio (ANNA-DroneMode): the radio UI is a shared_ptr from GetRadioUi([0x34C0F40], out); ChangeStation(ui, 1 the
    // next / 0 the one before), as the game's own D-pad does. HudStatusRadioManager::OnSystemEvent(manager, event, code,
    // payload): event 0x10, about every frame, has the station at manager +0x48, the song +0x08, the artist +0x28.
    public const int RadioUisGlobal = 0x34C0F40;
    public static readonly Code GetRadioUi = new("get the radio UI", 0x766C70, "40 53 48 83 EC 30 48 8B 49 08 48 8B");
    public static readonly Code ChangeStation = new("radio change station", 0x660E50, "48 8B C4 57 48 83 EC 70 48 C7 40 A8");
    public static readonly Code HudRadioEvent = new("HudStatusRadioManager::OnSystemEvent", 0x7835B0, "48 89 74 24 10 57 48 83 EC 30 49 8B");
    // The game's database object (FnFNitro's and FH2 Underglow's upgrade kits): its virtual slot 8 runs a statement,
    // (self, out, const char* sql). The string lookup (manager, table, key) -> const wchar_t*, "" for a key no table has.
    public const int DatabaseGlobal = 0x34B3640, DatabaseQuerySlot = 8;
    // The light where the player's car is: a scale in the game's global lighting, beside the sun's direction and the
    // sky's colour, that drops to a fifth or less in a tunnel (FH2 ANNA's tunnel headlights).
    public const int ZoneLightGlobal = 0x366101C;
    public static readonly Code StringLookup = new("string lookup", 0x123D780, "48 83 EC 38 B8 FF FF 00 00 45 8B C8 66 3B D0");
    public static readonly Code[] Navigation = [NavCtor, NavDtor, NearestEvent, NearestBucketList, NearestRoadTrip, NearestBoard, NearestBarnFind, LockWeakWorld];
    public const int SimField = 0x48, SimPausedField = 0x28, WorldField = 0x98, TimeOfDayField = 0x480;
    public static readonly Code WorldTimeOfDay = new("World time of day getter", 0x2E1F90, "48 8B 81 80 04 00 00 C3");
    // float TimeOfDay::GetTime(tod, int remap) / void SetTime(tod, float seconds, int remap); remap 1
    // is the game's own path. Seconds since midnight (time-of-day facts from Extra Options' weather control).
    public static readonly Code GetTime = new("TimeOfDay::GetTime", 0x197A880, "85 D2 75 06 C5 FA 10 41 10 C3");
    public static readonly Code SetTime = new("TimeOfDay::SetTime", 0x197AEC0, "40 53 48 83 EC 20 80 79 25 00 48 8B");
    public const int TimeOfDaySpeedField = 0x18;   // float, 1 = the game's own pace
    public const int TimeOfDayStoppedField = 0x24; // byte
    // bool Garage::IsFull(this): cars added this session (the vector below) + SavedCarCount >= 550.
    public static readonly Code GarageIsFull = new("Garage::IsFull", 0x2E77F0, "40 53 48 83 EC 20 48 8B 59 68 48 2B");
    // uint Garage::SavedCarCount(this): SELECT COUNT() FROM the garage's table (its name at +0x10).
    public static readonly Code GarageSavedCount = new("Garage saved car count", 0x2E0210, "40 53 48 81 EC B0 00 00 00 48 C7 44");
    public const int GarageNewCarsBegin = 0x60;   // std::vector<Car*>, begin and end
    public const int GarageNewCarsEnd = 0x68;

    public static readonly Code[] All =
    [
        CarUpdate, CarKind, ProfileManager, StoreGet, StoreSet, ValueFromInt, ValueFree,
        AddCar, GameDelete, SetCurrentCar, LoadOptions, GarageIsFull, GarageSavedCount,
        SetHeadlights, SetHeadlightsByDriver, WorldTimeOfDay, GetTime, SetTime,
        ResetDamageEntry, ResetSimulatedDamageEntry, ClearDamageTotals,
    ];

    // Checked when their feature starts, each turning off only that feature. A function another mod has
    // hooked (a jump at its start) is fine: the platform chains hooks on the same function.
    public static readonly Code[] Physics = [UpdateDynamics];
    public static readonly Code[] Flames = [UpdateBackfire, LogicFrame, ResolveLocatorsEntry];
    public static readonly Code[] Camera = [ViewMatrixMounted, ViewMatrixChase];
    public static readonly Code[] Speedometer = [SpeedometerCall, SpeedometerUpdate, GaugeXPosProperty, GaugeYPosProperty, RaiseItemProperty];
    public static readonly Code[] Sound =
    [
        FmodStudioUpdate, FmodGetLowLevel, FmodCreateSound, FmodPlaySound, FmodGetListener, FmodGetSoftwareFormat,
        FmodSet3D, FmodSet3DMinMax, FmodSetVolume, FmodSetPaused, FmodStop, FmodSetPriority, FmodSetVolumeRamp,
    ];
}
