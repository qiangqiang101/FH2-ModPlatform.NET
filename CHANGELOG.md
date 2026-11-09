# Changelog

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
