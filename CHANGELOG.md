# Changelog

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
- **The SDK's assembly version is 1.1.0.0:** the API grew. A mod built against 0.10.0 needs Platform.NET 0.10.0 or later installed, and says so if it isn't.

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
