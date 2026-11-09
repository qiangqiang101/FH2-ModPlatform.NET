// The FH2 classes, used like a script: no addresses, no reverse engineering.
//
// In ANNA's menu (D-pad down): Car > Lights > Headlights (ScriptDemo); ScriptDemo > Noon, Notify, Nearest car.
// In the console (~), while driving:
//   ScriptDemo status      credits, level, skill points, frames, cars around you
//   ScriptDemo car         your car: model, speed, gear, rpm, position; the nearest other car
//   ScriptDemo give 289    adds car 289 (a '69 Camaro SS) and makes it your current car
using FH2;
using FH2ModPlatform;

namespace ScriptDemo;

public sealed class ScriptDemo : Mod
{
    private int _ticks;

    public override ModInfo Info => new("ScriptDemo", "0.2.0") { Description = "Shows the FH2 classes." };

    protected override void OnInit()
    {
        // Once a frame, on the game's thread, while you drive.
        Game.Tick += () => _ticks++;

        // ANNA's menu. Entries run on the game's thread. One goes in her own Car > Lights list...
        var lights = ANNA.CarLights.Add("Headlights (ScriptDemo)", () =>
        {
            if (Player.Car is { } car) car.HeadlightsOn = !car.HeadlightsOn;
        }, staysOpen: true);
        lights.Visible = () => Player.Car is not null;

        // ...and two in a list of our own, on her top list.
        var menu = ANNA.Menu.AddList("ScriptDemo");
        menu.Add("Noon", () => World.TimeOfDay = TimeSpan.FromHours(12));
        menu.Add("Notify", () => UI.Notify("ScriptDemo", $"Hello! You're in a {Player.Car?.Model?.FullName ?? "car"}."));
        menu.Add("Nearest car", () =>
        {
            var near = World.GetNearestVehicle();
            Log(near is null ? "no other car around" : $"nearest: {near} at {Player.Car!.DistanceTo(near):F0} m");
        });

        AddCommand("status", "credits, level, skill points, cars around you", _ => OnGameThread(() =>
            $"credits {Player.Credits:N0}, level {Player.Level}, skill points {Player.SkillPoints}, " +
            $"current garage car {Player.CurrentGarageId}\n" +
            $"frames {Game.FrameCount} ({_ticks} seen by this mod), paused {Game.IsPaused}, time {World.TimeOfDay:hh\\:mm}\n" +
            $"cars last frame: {string.Join(", ", World.Vehicles.GroupBy(v => v.Kind).Select(g => $"{g.Count()} {g.Key}"))}"));

        AddCommand("car", "your car, and the nearest other one", _ => OnGameThread(() =>
        {
            if (Player.Car is not { } car) return "not driving";
            var near = World.GetNearestVehicle();
            return $"{car.Model}: {car.SpeedKmh:F0} km/h, gear {(car.Gear == 0 ? "R" : car.Gear.ToString())}, {car.Rpm:F0} rpm, " +
                   $"throttle {car.Throttle:P0}, lights {(car.HeadlightsOn ? "on" : "off")}\n" +
                   $"at {car.Position:F1}, heading {car.Forward:F2}, velocity {car.Velocity:F1}\n" +
                   $"nearest: {(near is null ? "none" : $"{near} at {car.DistanceTo(near):F0} m")}";
        }));

        AddCommand("give", "give <car>: adds it and makes it your current car", args =>
        {
            var found = CarCatalog.Find(args);
            if (found.Count != 1) return $"error: {found.Count} cars match \"{args}\"";
            var car = found[0];
            Game.Run(() => Log($"{car}: garage id {Garage.Give(car)}"));
            return $"giving {car} at the next frame";
        });

        Log($"{CarCatalog.All.Count} cars in the catalogue; ANNA {(ANNA.IsAvailable ? "found" : "not found (yet)")}");
    }

    // Commands run on the platform's command thread; the FH2 classes want the game's.
    private static string OnGameThread(Func<string> f)
    {
        try { return Game.RunAsync(f).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
        catch (TimeoutException) { return "error: the game didn't tick (paused, or not driving?)"; }
    }
}
