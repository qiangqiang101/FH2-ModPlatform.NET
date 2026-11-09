using System.Runtime.InteropServices;
using FH2ModPlatform.Interop;

namespace FH2;

/// <summary>
/// The current profile's values, by the game's own keys: "Main/Credits", "Main/Level",
/// "Main/NumSkillTokens", "Main/CareerCar", "Main/TractionControl", "Main/TopSpeed"... (the exe
/// names about 200). Game thread only (<see cref="Game.Tick"/> or <see cref="Game.Run"/>).
/// </summary>
/// <remarks>
/// These are integer values; a key the profile doesn't have reads as 0. Writing one changes
/// the save the next time the game saves it. Some keys the game keeps in step with other data:
/// change those through the classes that know them (Player, Garage) rather than here.
/// </remarks>
public static unsafe class Profile
{
    /// <summary>The current profile, or 0 if none is loaded (the title screen).</summary>
    internal static nint Current
    {
        get
        {
            var b = Game.RequireBase("Profile");
            var manager = *(nint*)(b + Addresses.ProfileManagerGlobal);
            if (manager == 0) return 0;
            return VCall(manager, Addresses.CurrentProfileSlot);
        }
    }

    /// <summary>The current profile's garage, or 0.</summary>
    internal static nint Garage
    {
        get
        {
            var p = Current;
            return p == 0 ? 0 : VCall(p, Addresses.GarageSlot);
        }
    }

    private static nint Store
    {
        get
        {
            var g = Garage;
            if (g == 0) throw new InvalidOperationException("No profile is loaded yet (still on the title screen?).");
            return *(nint*)(g + Addresses.StoreField);
        }
    }

    /// <summary>True when a profile is loaded (you're past the title screen).</summary>
    public static bool IsLoaded
    {
        get
        {
            Game.RequireGameThread("Profile.IsLoaded");
            return Garage != 0;
        }
    }

    /// <summary>The integer under <paramref name="key"/> ("Main/Credits"); 0 if there's none.</summary>
    public static int GetInt(string key)
    {
        Game.RequireGameThread("Profile.GetInt");
        var b = Game.Base;
        var store = Store;
        var value = (byte*)NativeMemory.AllocZeroed(Addresses.ValueSize);
        using var u = new Utf8Scope();
        try
        {
            var get = (delegate* unmanaged<nint, byte*, byte*, byte*>)(b + Addresses.StoreGet.Offset);
            var got = get(store, value, u.Str(key));
            return *(int*)((got is null ? value : got) + Addresses.ValueIntField);
        }
        finally
        {
            ((delegate* unmanaged<byte*, void>)(b + Addresses.ValueFree.Offset))(value);
            NativeMemory.Free(value);
        }
    }

    /// <summary>Sets the integer under <paramref name="key"/>.</summary>
    public static void SetInt(string key, int number)
    {
        Game.RequireGameThread("Profile.SetInt");
        var b = Game.Base;
        var store = Store;
        var value = (byte*)NativeMemory.AllocZeroed(Addresses.ValueSize);
        using var u = new Utf8Scope();
        try
        {
            ((delegate* unmanaged<byte*, int, byte*>)(b + Addresses.ValueFromInt.Offset))(value, number);
            ((delegate* unmanaged<nint, byte*, byte*, void>)(b + Addresses.StoreSet.Offset))(store, u.Str(key), value);
        }
        finally
        {
            ((delegate* unmanaged<byte*, void>)(b + Addresses.ValueFree.Offset))(value);
            NativeMemory.Free(value);
        }
    }

    internal static nint VCall(nint obj, int slot)
    {
        var fn = *(nint*)(*(nint*)obj + slot * sizeof(nint));
        return ((delegate* unmanaged<nint, nint>)fn)(obj);
    }
}

/// <summary>You: money, level, skill points and the car you're driving. Game thread only.</summary>
public static partial class Player
{
    /// <summary>Credits (CR), as the HUD shows them.</summary>
    public static int Credits
    {
        get => Profile.GetInt("Main/Credits");
        set => Profile.SetInt("Main/Credits", Math.Max(0, value));
    }

    /// <summary>Your driver level.</summary>
    public static int Level => Profile.GetInt("Main/Level");

    /// <summary>Skill points to spend in the skill tree.</summary>
    public static int SkillPoints
    {
        get => Profile.GetInt("Main/NumSkillTokens");
        set => Profile.SetInt("Main/NumSkillTokens", Math.Max(0, value));
    }

    /// <summary>The garage id of your current car (Garage > Change car).</summary>
    public static int CurrentGarageId => Profile.GetInt("Main/CareerCar");

    /// <summary>The car you're driving this frame, or null (in a menu, on the title screen).</summary>
    public static Vehicle? Car
    {
        get
        {
            Game.RequireGameThread("Player.Car");
            var c = Game.PlayerCar;
            return c == 0 ? null : new Vehicle(c);
        }
    }
}
