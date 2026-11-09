using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using FH2ModPlatform.Native;

namespace FH2ModPlatform;

/// <summary>
/// Names a mod assembly's <see cref="Mod"/> class. The build adds it for you
/// from <c>&lt;FH2ModType&gt;</c>; FH2DotNet.dll uses it to find the class.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ModEntryAttribute(
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type modType) : Attribute
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type ModType { get; } = modType;
}

/// <summary>Starts mods. The generated <c>fh2mod_init</c> export calls <c>InitNative</c>.</summary>
public static unsafe class ModLoader
{
    private static readonly List<Mod> s_mods = new();

    /// <summary>All mods started in this runtime (one for NativeAOT, every hosted one for FH2DotNet.dll).</summary>
    public static IReadOnlyList<Mod> Mods
    {
        get { lock (s_mods) return s_mods.ToArray(); }
    }

    /// <summary>The body of a NativeAOT mod's <c>fh2mod_init</c>.</summary>
    public static int InitNative<T>(FH2ModApi* api) where T : Mod, new()
    {
        try
        {
            return InitNative(api, new T());
        }
        catch
        {
            // Nothing registered to log to; the platform logs the non-zero return.
            return 1;
        }
    }

    internal static int InitNative(FH2ModApi* api, Mod mod)
    {
        ModPlatform.Attach(api);
        // A NativeAOT mod has the runtime to itself, so its Console output can be its own log lines.
        bool redirected = false;
        mod.Registered += () =>
        {
            if (redirected) return;
            redirected = true;
            Interop.LogWriter.Redirect(mod.Log);
        };
        return Start(mod) is Mod.StartResult.Started or Mod.StartResult.HeldOffline ? 0 : 1;
    }

    internal static Mod.StartResult Start(Mod mod)
    {
        lock (s_mods) s_mods.Add(mod);
        return mod.Start();
    }
}

/// <summary>
/// fh2mod_init for NativeAOT mods written in languages that can't declare it
/// themselves (VB.NET has no [UnmanagedCallersOnly]). The build compiles it in only
/// for those (UnmanagedEntryPointsAssembly), with the mod's assembly name baked in
/// as FH2ModPlatform.ModAssembly; the class comes from its [assembly: ModEntry].
/// </summary>
internal static unsafe class NativeExport
{
    [UnmanagedCallersOnly(EntryPoint = "fh2mod_init")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The mod's assembly is compiled into the image; ModEntry keeps its constructor.")]
    private static int Init(FH2ModApi* api)
    {
        try
        {
            var name = AppContext.GetData("FH2ModPlatform.ModAssembly") as string;
            if (string.IsNullOrEmpty(name)) return Fail(api, "FH2ModPlatform.ModAssembly isn't set (built without the FH2ModPlatform targets?)");
            var entry = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<ModEntryAttribute>(System.Reflection.Assembly.Load(name));
            if (entry is null) return Fail(api, $"{name} has no [assembly: ModEntry] (set <FH2ModType> in its project)");
            return ModLoader.InitNative(api, (Mod)Activator.CreateInstance(entry.ModType)!);
        }
        catch (Exception e)
        {
            return Fail(api, e.ToString());
        }
    }

    // Registers under the assembly's name just to say why, so the problem is in platform.log.
    private static int Fail(FH2ModApi* api, string why)
    {
        try
        {
            ModPlatform.Attach(api);
            using var u = new Interop.Utf8Scope();
            var name = AppContext.GetData("FH2ModPlatform.ModAssembly") as string ?? "a .NET mod";
            var info = new FH2ModInfo { Size = (uint)sizeof(FH2ModInfo), Name = u.Str(name), Version = u.Str("") };
            var mod = api->RegisterMod(&info);
            if (mod is not null)
                foreach (var line in ("FAILED to start: " + why).Split('\n'))
                    api->Log(mod, u.Str(line.TrimEnd('\r')));
        }
        catch { }
        return 1;
    }
}
