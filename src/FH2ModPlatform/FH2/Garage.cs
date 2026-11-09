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
