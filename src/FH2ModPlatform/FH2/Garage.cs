using FH2ModPlatform;
using System.Runtime.InteropServices;

namespace FH2;

/// <summary>
/// Your cars: give one, and make one your current car, the way the game's own AwardCar
/// (Storm Island's gift cars) and Pause > My Cars do. Game thread only.
/// </summary>
/// <remarks>
/// <see cref="SetCurrent"/> makes the car current, as My Cars does, but doesn't swap the car
/// you're driving there and then: that happens the next time the game loads your car (a house,
/// a race, fast travel; or at once with Extra Options' "change car anywhere").
/// </remarks>
public static unsafe class Garage
{
    /// <summary>
    /// Adds <paramref name="car"/> to your garage (free, as a gift is) and returns its garage id,
    /// or 0 if the game didn't add it.
    /// </summary>
    /// <remarks>
    /// The game saves your whole profile for every car added, one save at a time, and quitting
    /// waits for every queued save. Adding many cars at once leaves minutes of saving after the window
    /// closes (killing the game then can break the save): add about one a second.
    /// </remarks>
    public static int Add(CarInfo car, int colour = 0) => Add(car.Id, colour);

    /// <inheritdoc cref="Add(CarInfo, int)"/>
    public static int Add(int carId, int colour = 0)
    {
        Game.RequireGameThread("Garage.Add");
        var b = Game.Base;
        var garage = RequireGarage();
        var list = (nint*)NativeMemory.AllocZeroed(3 * (nuint)sizeof(nint));   // the std::vector it fills
        var garageId = (int*)NativeMemory.AllocZeroed(8);
        var extra = NativeMemory.AllocZeroed(8);
        try
        {
            var add = (delegate* unmanaged<nint, nint*, int, int, int*, int, void*, byte, nint*>)(b + Addresses.AddCar.Offset);
            add(garage, list, carId, colour, garageId, 2, extra, 0);
            // What it hands back is released, and the list freed with the game's own delete, as AwardCar does.
            for (var p = list[0]; p != 0 && p < list[1]; p += sizeof(nint))
            {
                var obj = *(nint*)p;
                if (obj == 0) continue;
                var release = *(nint*)(*(nint*)obj + Addresses.AddCarListReleaseSlot * sizeof(nint));
                ((delegate* unmanaged<nint, void>)release)(obj);
            }
            if (list[0] != 0) ((delegate* unmanaged<nint, void>)(b + Addresses.GameDelete.Offset))(list[0]);
            return *garageId;
        }
        finally
        {
            NativeMemory.Free(list);
            NativeMemory.Free(garageId);
            NativeMemory.Free(extra);
        }
    }

    /// <summary>
    /// How many cars are in your garage: the number the game compares with its limit (550) before
    /// it adds one. Game thread only.
    /// </summary>
    public static int Count
    {
        get
        {
            Game.RequireGameThread("Garage.Count");
            var garage = RequireGarage();
            var saved = ((delegate* unmanaged<nint, uint>)(Game.Base + Addresses.GarageSavedCount.Offset))(garage);
            var added = (*(nint*)(garage + Addresses.GarageNewCarsEnd) - *(nint*)(garage + Addresses.GarageNewCarsBegin)) / sizeof(nint);
            return (int)(saved + added);
        }
    }

    /// <summary>True when the game won't add another car: the game's own check, so it follows a mod that changes the limit.</summary>
    public static bool IsFull
    {
        get
        {
            Game.RequireGameThread("Garage.IsFull");
            var garage = RequireGarage();
            return ((delegate* unmanaged<nint, byte>)(Game.Base + Addresses.GarageIsFull.Offset))(garage) != 0;
        }
    }

    /// <summary>The game's own garage size: it's full at this many cars.</summary>
    public const int GameCapacity = 550;

    private static volatile int s_capacity = GameCapacity;
    private static int s_capacitySet;

    /// <summary>How many cars the garage holds: <see cref="GameCapacity"/>, or what <see cref="SetCapacity"/> made it. Any thread.</summary>
    public static int Capacity => s_capacity;

    /// <summary>
    /// Makes the garage hold <paramref name="cars"/> (550 to 100,000) instead of the game's 550: the game's own check
    /// (<see cref="IsFull"/>), adding a car, and the menus. Call it once, from OnInit; it applies as the game's code comes
    /// in, all three places or none (platform.log says why). Any thread.
    /// </summary>
    /// <remarks>
    /// The garage is a table in your profile, so only the number changes. Each car added saves the profile, and quitting
    /// waits for every save (see <see cref="Add(CarInfo, int)"/>). With more than 550 cars and the change gone (another
    /// session without it), you keep every car but can't add more.
    /// </remarks>
    public static void SetCapacity(int cars)
    {
        cars = Math.Clamp(cars, GameCapacity, 100_000);
        if (cars == GameCapacity) return;
        if (Interlocked.Exchange(ref s_capacitySet, 1) != 0)
        {
            Game.Log(ModPlatform.GameOwner, "Garage.SetCapacity: already set this session (a change needs a restart)");
            return;
        }
        // Where the game has 550 (0x226): Garage::IsFull, the same check in AddCar, and the menus' list of limits.
        // Each with the instruction before it, so a change can only land on this game build.
        Patches.Edit[] edits =
        [
            new("Garage::IsFull", 0x2E7809, Patches.With("48 03 CB 48 81 F9", GameCapacity), Patches.With("48 03 CB 48 81 F9", cars)),
            new("Garage::AddCar", 0x2CB6CE, Patches.With("48 03 C3 48 3D", GameCapacity), Patches.With("48 03 C3 48 3D", cars)),
            new("the menus' limit", 0xD2A610, Patches.With("C3 B8", GameCapacity, "C3"), Patches.With("C3 B8", cars, "C3")),
        ];
        Patches.Apply("garage size", edits, ok =>
        {
            if (!ok) return;
            s_capacity = cars;
            Game.Log(ModPlatform.GameOwner, $"Garage: holds {cars} cars (the game's is {GameCapacity})");
        });
    }

    /// <summary>Makes the car with <paramref name="garageId"/> your current car, as Pause > My Cars does.</summary>
    public static void SetCurrent(int garageId)
    {
        Game.RequireGameThread("Garage.SetCurrent");
        var b = Game.Base;
        var garage = RequireGarage();
        // Default load options, as AwardCar makes them; left allocated (a few bytes; the game may keep reading them).
        var options = NativeMemory.AllocZeroed(Addresses.LoadOptionsSize);
        ((delegate* unmanaged<void*, void*>)(b + Addresses.LoadOptions.Offset))(options);
        ((delegate* unmanaged<nint, int, void*, void>)(b + Addresses.SetCurrentCar.Offset))(garage, garageId, options);
        // What My Cars does next: tells the player car's loader.
        var profile = Profile.Current;
        if (profile == 0) return;
        var settings = Profile.VCall(profile, Addresses.PlayerCarSettingsSlot);
        if (settings == 0) return;
        var fn = *(nint*)(*(nint*)settings + Addresses.SetPlayerCarSlot * sizeof(nint));
        ((delegate* unmanaged<nint, nint, int, void>)fn)(settings, garage, garageId);
    }

    /// <summary>Adds <paramref name="car"/> and makes it your current car. Returns its garage id, or 0.</summary>
    public static int Give(CarInfo car, int colour = 0)
    {
        var id = Add(car, colour);
        if (id > 0) SetCurrent(id);
        return id;
    }

    private static nint RequireGarage()
    {
        var g = Profile.Garage;
        if (g == 0) throw new InvalidOperationException("No profile is loaded yet (still on the title screen?).");
        return g;
    }
}
