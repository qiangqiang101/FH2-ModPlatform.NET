# Changelog

## 0.12.0 (preview)

### New
- **Everything platform 2.9.0 (API 10) adds, on `Mod`:**
  - **Pop-ups:** `ShowPopup` with a picture, a sound, a key that replaces a waiting one, and urgent, reply and private pop-ups. `ClearPopup` takes them down. They go out under your mod's name.
  - **Tips:** `ShowTip`, shown once, with a button in the text (`Prompt.Pad`, `Prompt.Key`, `Prompt.ForHotkey`). `TipSeen` and `SetTipSeen` too.
  - **Message boxes:** `ShowMessageBox`, the game's own, with your choices, from a page's or setting's callback.
  - **Sounds and status:** `PlaySound` (the game's UI sounds), `UiStatus`, and `DeclareUi` for a "Pop-ups" row of your own.
  - **Page help:** `SetPageHelp`, `SetPageHints` and `ShowPageHint` for your pages' help bar and hint line.
  - **Text in the player's language:** `AddTranslations` (PO files embedded in your mod), `AddTranslation`, `Tr`, `TrPlural`, `SetStringTr`, `SetSettingText`, `SetDescription`. Text from `Tr` passed to a row, page or hotkey follows the game's language by itself.
  - **On `ModPlatform`:** `Language`, `GameLanguage`, `FormatNumber`, `FormatDate`.
  - On an older platform they do nothing, and `Tr` gives back your English, so a mod using them still runs there.
  - The wiki has two new pages: Pop-ups, tips and message boxes, and Translations.
- **More of the game, from the FH2 mods (FH2 ANNA, ANNA-DroneMode, FnFNitro, FH2 Underglow, NoPause, Livery Layer Patch, XboxMusicRadio, Garage Size, VirtualDashboard's findings):**
  - **`Vehicle`:** `Indicators` (left, right, hazards, held until you turn them off), `HeadlightsAutomatic`, part animations (`OpenPart`, `ClosePart`, `TogglePart`, `PlayAnimation`, `Animations`), `AiDriving` (the game's AI drives the route it's following), `Clutch`, `RedlineRpm`, `LimiterRpm`, `Boost`, `MediaName`, `Vehicle.FromHandle`.
  - **`Camera`:** `Kind` (which camera is on), `Position`, `Forward`, `FovScale`, `ViewUpdated` (move or turn the driving cameras), `Group`, `ShowReplayShot`, `Flash`.
  - **`Radio`** (new): the station, song and artist playing, `Changed`, `Next`, `Previous`.
  - **`GameDb`** (new): rewrite the SQL the game runs, and answer string keys no table has, for custom upgrade parts.
  - **`Livery`** (new): `SetLayerLimits`, up to 20,000 layers.
  - **`Garage.SetCapacity`** and `Capacity`: more than 550 cars.
  - **`World`:** `RouteToNearest` (an event, a bucket list, a road trip stop, a bonus board, a barn find; call `PrepareRoutes` from `OnInit` first), `LightLevel`.
  - **`Game`:** `PauseOpened`, `EventStarted`, `ClearRewind`, `IsFeatureUnlocked`.
  - **`Hud.HideAll`**: all of the game's UI, back by itself when the pause menu opens.
  - **`GameWindow.KeepRunningUnfocused`**: the game, and the pad, keep going behind other windows.
  - **`GameCode.NewStdString`**, for game functions that take a `std::string`.
  - Tested in the game with the Test012 script, except `GameDb`, `Garage.SetCapacity` and `Livery.SetLayerLimits` (their own mods do the same today).

### Changed
- **Built on FH2 Mod Platform 2.9.0** (it was 2.8.0). The SDK's bindings cover its API 10.
- **The console's `log` command:** `log off` hides the log, so only what you type and the replies show; `log <text>` shows only lines with that text; `log on` shows it all. It's remembered.
- **`Game.Language` asks the platform** on 2.9.0 or later, instead of reading the game's memory.
- **`Vehicle.HeadlightsOn`** reads what the lights are now, the game's own choice included (it read only the setting a mod made), and holds what you set against the game's automatic lights.
- **`UI.Notify` uses the platform's pop-ups on platform 2.9.0 or later:**
  - The platform shows them, in one queue for every mod, spaced around the game's own notifications. The SDK's own hooks on the game's notification panel aren't put in, and those were behind both of 0.10.0's crashes.
  - The title shows up to 28 characters and the line up to 45.
  - Up to 4 wait at once (hosted mods together).
  - The player can turn them off: a new **Pop-ups** row on Platform.NET's page, the platform's **Mod pop-ups**, or the game's Minor notifications.
  - On older platforms, `UI.Notify` works as in 0.10.0.
- **A mod built with 0.12.0 needs Platform.NET 0.12.0 or later** in the game, and says so in `platform.log` if it's older. Mods built with 0.9.0 to 0.11.0 keep working.

## 0.11.0 (preview)

### New
- **`Garage.Count` and `Garage.IsFull`:** how many cars your garage has, and whether the game will add another (its limit is 550; the game's own check, so it follows a mod that changes it).

### Changed
- **`Garage.Add` says what adding many cars costs:** the game saves your whole profile for every car, and quitting waits for every queued save. Hundreds at once leave minutes of saving after the window closes; add about one a second.
- **`PatchBytes` says it starts switched off,** like every hook: nothing is written until `EnableHooks`. The wiki's GameCode page explains it.
- **A mod built with 0.11.0 needs Platform.NET 0.11.0 or later** in the game, and says so in `platform.log` if it's older. Mods built with 0.9.0 and 0.10.0 keep working.

## 0.10.0 (preview)

### New
- **`Skills`:** how soon the game can award Wreckage, Lumberjack or Sideswipe again (`Skills.SetRetriggerDelay`), set as the game starts.
- **`GameCode`, for game data that may be gone:**
  - Reads that can't crash: `TryRead`, `ReadSafe`, `ReadPointerSafe`, `ReadInt32Safe`, `ReadSingleSafe`, `IsReadable`. .NET can't recover from an access violation, so one bad pointer read raw ends the game.
  - `ReadStdString`: the game's `std::string`.
  - `MatchesBuild` and `SlotMatchesBuild`: check code before hooking it, even where another mod has hooked it first (they read the game's exe on disk). `ShippedBytes` gives those bytes.
- **`Mod.Reloadable`:** a mod that can't be swapped while the game runs returns false. A reload (A on the Hosted .NET mods page, the reload key, `reload`, auto-reload) then leaves it running and says a restart is needed, instead of switching it off.
- **Update notices for Platform.NET:** the title screen says when a newer release is out.

### Fixed
- **`UI.Notify` crashed the game** when the HUD was rebuilt (a teleport, starting an event):
  - It could show on a notification panel that had gone, or on a new one the game was still building.
  - Notifications now wait until the panel has settled and you're driving.
  - `platform.log` says when one shows, waits or is dropped.
- **`UI.Notify` shows any language** (the text goes to the game as UTF-16). Characters outside ASCII showed as `?`.

### Changed
- **A mod built with 0.10.0 needs Platform.NET 0.10.0 or later** in the game, and says so in `platform.log` if it's older. Mods built with 0.9.0 keep working.

## 0.9.0 (preview)

The first release.

### The SDK and Platform.NET

- **The whole platform API (API 9, platform 2.8.0) in C#:** game text, title-screen notices and entries, files, the game database, hooks, byte patches and instruction hooks, settings, pages, hotkeys, commands, sessions and crash dumps.
- **Two ways into the game, from the same project:**
  - **Hosted:** `FH2DotNet.dll` runs .NET mods from `fh2-mods-dotnet\`, each in a load context of its own.
  - **NativeAOT:** one DLL in `fh2-mods\`, like any C++ mod.
- **VB.NET mods** in both modes. Hooks take delegates, and NativeAOT builds get the SDK's own entry point.
- **Scripts:** `.cs` and `.vb` files in `fh2-mods-dotnet\scripts\` are compiled when the game starts, with no project needed.
- **Reloading without restarting the game:** from a command, a key, or every build (auto-reload).
- **The console window** (`~`): `platform.log` as it's written, and a prompt for every mod's commands.
- **The Hosted .NET mods page** under MODS > Installed mods > Platform.NET: each mod's version, description and status. A on one reloads it.

### The FH2 classes: the game, ready to use

- **`Game`:** the frame (`Tick`), the game's thread (`Run`), every car's physics step (`PhysicsStep`), rewinds and relocations, `IsDriving`, `Language`.
- **`Player`, `Vehicle`, `World`:** your car and every other car, with repair, headlights, exhaust flames and backfires; the time of day; the map's waypoint; `Player.TeleportTo`, which uses the game's own fast-travel placement.
- **`Garage`, `CarCatalog`, `Profile`:** the cars you own, every car's names, the profile's values.
- **`ANNA`:** entries in FH2 ANNA's menu.
- **`UI`, `Hud`, `Camera`, `Audio`:** HUD notifications, the speedometer, camera shake, sounds through the game's audio.
- **`Controller`, `GameInput`, `GameWindow`:** the controller, feeding the game's controls (keys that drive and work the menus, playing without a controller), the window, mouse and keys.
- **`Menus`, `UiElement`, `MapView`, `PhotoCamera`, `GarageCamera`, `ButtonIcons`, `SettingsTile`:** what a mouse and keyboard mod needs.
- **`DebugOverlay`:** text over the game.

### Known issues

See [Known issues](README.md#known-issues) in the README.
