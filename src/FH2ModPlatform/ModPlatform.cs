using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2ModPlatform;

/// <summary>
/// The platform itself: what's the same for every mod in the process. Valid
/// once the platform has called in (inside and after <see cref="Mod.OnInit"/>).
/// </summary>
public static unsafe class ModPlatform
{
    /// <summary>The platform version this SDK was built against (the platform submodule's), passed in <see cref="ModInfo.PlatformVersion"/> by default.</summary>
    public const string BuiltAgainstVersion = "2.8.0";

    /// <summary>The <c>FH2MOD_API_VERSION</c> this SDK was built against.</summary>
    public const ApiLevel BuiltAgainstApi = ApiLevel.TitleEntries;

    private static FH2ModApi* s_api;

    /// <summary>The raw function table, for anything the wrapper doesn't cover.</summary>
    public static FH2ModApi* Api => s_api is not null ? s_api : throw new InvalidOperationException("The FH2 Mod Platform hasn't called in yet.");

    /// <summary>True once the platform has passed in its API.</summary>
    public static bool IsAttached => s_api is not null;

    /// <summary>True when loaded through FH2DotNet.dll (a shared runtime), false when compiled to a NativeAOT DLL of its own.</summary>
    public static bool IsHosted { get; internal set; }

    private static FH2Mod* s_gameOwner;

    /// <summary>
    /// Whose name the FH2 classes' own hooks and log lines go under: the host (Platform.NET) for
    /// hosted mods, which share them, or the mod itself when it's a NativeAOT DLL.
    /// </summary>
    internal static FH2Mod* GameOwner
    {
        get => s_gameOwner;
        set { if (s_gameOwner is null) s_gameOwner = value; }
    }

    internal static void Attach(FH2ModApi* api)
    {
        if (api is null) throw new ArgumentNullException(nameof(api));
        s_api = api;
    }

    /// <summary>The platform's <c>api_version</c>.</summary>
    public static ApiLevel ApiVersion => (ApiLevel)Api->ApiVersion;

    /// <summary>True if the running platform offers <paramref name="level"/>'s functions.</summary>
    public static bool Supports(ApiLevel level) => Api->ApiVersion >= (uint)level;

    internal static void Require(ApiLevel level, string what)
    {
        if (!Supports(level))
            throw new NotSupportedException(
                $"{what} needs FH2 Mod Platform API {(uint)level}, but the installed platform ({PlatformVersion}) has API {Api->ApiVersion}.");
    }

    /// <summary>The running platform's version, like "2.8.0".</summary>
    public static string PlatformVersion => NativeString.FromUtf8(Api->PlatformVersion) ?? "";

    /// <summary>The folder with azimuth.exe, no trailing slash.</summary>
    public static string AzimuthDir => NativeString.FromUtf16(Api->AzimuthDir) ?? "";

    /// <summary>The fh2-mods folder.</summary>
    public static string ModsDir => Path.Combine(AzimuthDir, "fh2-mods");

    /// <summary>The game's Mount folder (game:\), or "" if not found.</summary>
    public static string GameDir => NativeString.FromUtf16(Api->GameDir) ?? "";

    /// <summary>Base address of the mapped game, or 0 if it isn't mapped yet.</summary>
    public static nint GameBase => (nint)Api->GameBase();

    /// <summary>Waits until the game is mapped (or the timeout passes) and returns its base, or 0. Don't call it from <see cref="Mod.OnInit"/>.</summary>
    public static nint WaitForGame(TimeSpan? timeout = null) => (nint)Api->WaitForGame(Ms(timeout));

    /// <summary>
    /// Waits until the game is mapped and Extra Options has finished patching it: then
    /// it's safe to hook game code. Returns the game's base, or 0 on timeout. Call it from your own thread.
    /// </summary>
    public static nint WaitForCode(TimeSpan? timeout = null)
    {
        Require(ApiLevel.Hooks, nameof(WaitForCode));
        return (nint)Api->WaitForCode(Ms(timeout));
    }

    /// <summary>Waits until every mod's fh2mod_init has returned. False on timeout, and straight away from inside OnInit.</summary>
    public static bool WaitForMods(TimeSpan? timeout = null)
    {
        Require(ApiLevel.Files, nameof(WaitForMods));
        return Api->WaitForMods(Ms(timeout)) != 0;
    }

    /// <summary>The interface another mod published as <paramref name="name"/>, if at least <paramref name="minVersion"/>; otherwise 0.</summary>
    public static nint GetInterface(string name, uint minVersion)
    {
        Require(ApiLevel.Files, nameof(GetInterface));
        using var u = new Utf8Scope();
        return (nint)Api->GetInterface(u.Str(name), minVersion);
    }

    /// <summary>The published interface as a struct of function pointers, or null.</summary>
    public static T* GetInterface<T>(string name, uint minVersion) where T : unmanaged => (T*)GetInterface(name, minVersion);

    /// <summary>The version of the registered mod called <paramref name="name"/>, or null if it isn't installed.</summary>
    public static string? ModVersion(string name)
    {
        Require(ApiLevel.Files, nameof(ModVersion));
        using var u = new Utf8Scope();
        return NativeString.FromUtf8(Api->ModVersion(u.Str(name)));
    }

    /// <summary>True when the game was started from the FH2MP launcher. False on platforms before 2.8.0.</summary>
    public static bool PlayingOnline => Supports(ApiLevel.TitleEntries) && Api->PlayingOnline() != 0;

    /// <summary>This run of the game's id, like "20261021-203224-1234".</summary>
    public static string SessionId
    {
        get
        {
            Require(ApiLevel.TitleEntries, nameof(SessionId));
            return NativeString.FromUtf8(Api->SessionId()) ?? "";
        }
    }

    internal static uint Ms(TimeSpan? t) =>
        t is not { } v || v == Timeout.InfiniteTimeSpan ? 0xFFFFFFFFu
        : (uint)Math.Clamp(v.TotalMilliseconds, 0, 0xFFFFFFFEu);
}
