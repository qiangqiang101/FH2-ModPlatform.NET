// HelloMod in C#: the platform's examples/hello_mod, plus a setting, a command and the FH2 classes.
//
//   Visual Studio: Debug/Release + Build -> a hosted mod: fh2-mods-dotnet\HelloMod\
//                  Aot + Build           -> a native DLL of its own: fh2-mods\HelloMod.dll
//
// Then launch the game: fh2-mods\platform.log gets its lines, the "Fast Traveling to
// Destination" loading message gets a greeting, Pause > Settings > MODS gets its switch,
// and ANNA's menu (D-pad down) gets "Say hello".
using FH2;
using FH2ModPlatform;

namespace HelloMod;

public sealed class HelloMod : Mod
{
    private volatile bool _greeting = true;

    public override ModInfo Info => new("HelloMod.NET", "1.1.0")
    {
        GithubRepo = null,   // "You/YourRepo" to get update notices
        // PlatformVersion is left at its default, the platform version this mod was built with:
        // players with an older platform then get told to update it. (ModPlatform.PlatformVersion
        // is the installed one, which always matches, so the warning could never show.)
        Description = "Says hello from C#.",
        CheckUpdates = false,
        OfflineOnly = false,
    };

    protected override void OnInit()
    {
        Log($"hello from C# ({(ModPlatform.IsHosted ? "hosted by Platform.NET" : "NativeAOT")}), " +
            $"platform {ModPlatform.PlatformVersion}, API {(uint)ModPlatform.ApiVersion}");

        // LoadingMessages.str, key 0x6875 (IDS_FastTravel): shown while fast travelling.
        // Edits made in OnInit are written for you before the game reads its strings.
        SetString("LoadingMessages.str", 0x6875, "Fast Traveling to Destination (hello from C#!)");

        if (ModPlatform.Supports(ApiLevel.Settings))
        {
            AddSetting(new ToggleSetting("Greeting", "Loading-screen greeting", _greeting)
            {
                Description = "Adds a hello to the fast-travel loading message.",
                NeedsRestart = true,   // the game reads its text once, at startup
                // On the game's UI thread: flip the flag, and write the text off it.
                Changed = on =>
                {
                    _greeting = on;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        if (on) SetString("LoadingMessages.str", 0x6875, "Fast Traveling to Destination (hello from C#!)");
                        else ClearStrings("LoadingMessages.str");
                        ApplyStrings();
                    });
                },
            });
        }

        if (ModPlatform.Supports(ApiLevel.TitleEntries))
            AddCommand("hello", "says hello back", args => $"hello, {(args.Length > 0 ? args : "you")}");

        // ANNA's menu (needs the FH2 ANNA mod). Entries run on the game's thread, so the FH2
        // classes can be used straight away.
        ANNA.Menu.Add("Say hello", () =>
        {
            var car = Player.Car;
            Log(car is null
                ? "hello from C#!"
                : $"hello from C#! That's a {car.Model?.FullName ?? "car"} at {car.SpeedKmh:F0} km/h.");
        });

        // Anything slow goes on a thread of your own, never in OnInit. Unloading says when the
        // mod is being reloaded.
        new Thread(() =>
        {
            nint gameBase = ModPlatform.WaitForGame();
            if (gameBase != 0 && !Unloading.IsCancellationRequested) Log($"the game is mapped at 0x{gameBase:X}");
        }) { IsBackground = true, Name = "HelloMod wait" }.Start();
    }
}
