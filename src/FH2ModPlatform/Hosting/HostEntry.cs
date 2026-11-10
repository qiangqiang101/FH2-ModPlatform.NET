using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2ModPlatform.Hosting;

/// <summary>
/// Where FH2DotNet.dll comes in, once it has the runtime going. Not used by NativeAOT mods.
/// </summary>
/// <remarks>
/// Hosted mods live in <c>fh2-mods-dotnet\&lt;Name&gt;\&lt;Name&gt;.dll</c> (next to fh2-mods) and start in
/// the folders' alphabetical order, each in an AssemblyLoadContext of its own, so
/// their dependencies can't clash. They all share this assembly (the SDK) from the
/// default context. A folder whose name ends in <c>.disabled</c> is skipped.
///
/// Scripts, <c>fh2-mods-dotnet\scripts\&lt;Name&gt;.cs</c> or <c>.vb</c>, are compiled when the
/// game starts (ScriptHost) and then start like a folder's mod, after the folders.
///
/// Mods are read into memory rather than opened in place, so a build can replace
/// them while the game runs, and <c>reload</c> then starts the new build: a new
/// load context, a new copy of the Mod class, and its OnInit again. The old copy
/// is retired (OnUnload, its hooks off) but stays in memory until the game closes,
/// since a game thread may still be inside one of its detours.
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only runs on the JIT runtime FH2DotNet.dll hosts.")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only runs on the JIT runtime FH2DotNet.dll hosts.")]
[UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Only runs on the JIT runtime FH2DotNet.dll hosts.")]
[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Only runs on the JIT runtime FH2DotNet.dll hosts.")]
public static unsafe partial class HostEntry
{
    private sealed class Entry(string name, string? script)
    {
        public readonly string Name = name;
        /// <summary>The script file, for a script; null for a folder's DLL.</summary>
        public string? Script = script;
        public Mod? Mod;
        public string State = "not loaded";
        public int Loads;

        /// <summary>Where it is, as the player sees it.</summary>
        public string Where => Script is null ? $"fh2-mods-dotnet\\{Name}" : $"fh2-mods-dotnet\\scripts\\{Path.GetFileName(Script)}";
    }

    private const string ScriptsFolder = "scripts";

    private static FH2ModApi* s_api;
    private static FH2Mod* s_host;
    private static string s_modsDir = "";
    private static readonly object s_lock = new();
    private static readonly SortedDictionary<string, Entry> s_mods = new(StringComparer.OrdinalIgnoreCase);
    private static FileSystemWatcher? s_watcher;
    private static readonly Dictionary<string, Timer> s_pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called by FH2DotNet.dll from its fh2mod_init. Returns how many mods failed to start.</summary>
    [UnmanagedCallersOnly]
    public static int Start(FH2ModApi* api, FH2Mod* host, char* modsDir)
    {
        try
        {
            s_api = api;
            s_host = host;
            ModPlatform.Attach(api);
            ModPlatform.IsHosted = true;
            ModPlatform.GameOwner = host;
            // Before the game builds its HUD: notifications need to see its notification panel being made.
            FH2.UI.Prepare();
            // Every hosted mod shares this runtime, so their Console output is the host's to log.
            LogWriter.Redirect(Log);
            s_modsDir = new string(modsDir);
            var failed = LoadAll();
            AddModsPage();
            return failed;
        }
        catch (Exception e)
        {
            Log("couldn't start .NET mods: " + e);
            return 1;
        }
    }

    /// <summary>FH2DotNet.dll's commands ("reload [name]", "mods"): writes the reply, UTF-8.</summary>
    [UnmanagedCallersOnly]
    public static void Command(byte* command, byte* args, byte* reply, nuint replySize)
    {
        string answer;
        try
        {
            var a = (NativeString.FromUtf8(args) ?? "").Trim();
            answer = (NativeString.FromUtf8(command) ?? "") switch
            {
                "reload" => Reload(a.Length == 0 ? null : a),
                "mods" => ListMods(),
                var c => $"error: no command {c}",
            };
        }
        catch (Exception e)
        {
            answer = "error: " + e.Message;
        }
        NativeString.WriteUtf8(answer, reply, replySize);
    }

    /// <summary>Reload a mod whenever its folder's DLLs change (a build copied in), or stop.</summary>
    [UnmanagedCallersOnly]
    public static void SetAutoReload(int on)
    {
        try
        {
            lock (s_lock)
            {
                if (on != 0 && s_watcher is null && Directory.Exists(s_modsDir))
                {
                    s_watcher = new FileSystemWatcher(s_modsDir)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    };
                    s_watcher.Filters.Add("*.dll");
                    s_watcher.Filters.Add("*.cs");
                    s_watcher.Filters.Add("*.vb");
                    s_watcher.Changed += (_, e) => Changed(e.FullPath);
                    s_watcher.Created += (_, e) => Changed(e.FullPath);
                    s_watcher.Renamed += (_, e) => Changed(e.FullPath);
                    s_watcher.EnableRaisingEvents = true;
                    Log("reloading hosted mods when their DLLs or scripts change");
                }
                else if (on == 0 && s_watcher is not null)
                {
                    s_watcher.Dispose();
                    s_watcher = null;
                    Log("no longer reloading hosted mods when their DLLs change");
                }
            }
        }
        catch (Exception e)
        {
            Log("couldn't watch for rebuilt mods: " + e.Message);
        }
    }

    // ---- Loading ----------------------------------------------------------

    private static int LoadAll()
    {
        Log($".NET {Environment.Version}, FH2ModPlatform {typeof(Mod).Assembly.GetName().Version}");
        if (!Directory.Exists(s_modsDir))
        {
            Log($"no .NET mods ({s_modsDir} doesn't exist)");
            return 0;
        }
        int failed = 0, started = 0;
        lock (s_lock)
        {
            foreach (var (name, script) in Available(quiet: false))
            {
                var e = new Entry(name, script);
                s_mods[name] = e;
                switch (Load(e, quiet: false))
                {
                    case true: started++; break;
                    case false: failed++; break;
                }
            }
        }
        Log($"{started} .NET mod(s) started" + (failed > 0 ? $", {failed} failed" : ""));
        return failed;
    }

    /// <summary>
    /// Every mod to start, by name: the folders' (script null), sorted, then the scripts' (their
    /// file), sorted. A script with a folder's name is left out (logged unless quiet).
    /// </summary>
    private static List<(string Name, string? Script)> Available(bool quiet)
    {
        var all = Folders(quiet).Select(n => (n, (string?)null)).ToList();
        var scripts = Path.Combine(s_modsDir, ScriptsFolder);
        if (!Directory.Exists(scripts)) return all;
        var files = Directory.GetFiles(scripts).Where(ScriptHost.IsScript).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (all.Any(m => string.Equals(m.Item1, name, StringComparison.OrdinalIgnoreCase)))
            {
                if (!quiet) Log($"scripts\\{Path.GetFileName(file)}: a mod called {name} is already there; skipped");
                continue;
            }
            all.Add((name, file));
        }
        return all;
    }

    /// <summary>The mod folders to start, sorted, without disabled ones (logged unless quiet).</summary>
    private static List<string> Folders(bool quiet = false)
    {
        var names = new List<string>();
        foreach (var folder in Directory.GetDirectories(s_modsDir))
        {
            var name = Path.GetFileName(folder);
            if (name.Equals(ScriptsFolder, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                if (!quiet) Log($"{name} is disabled; not loaded");
                continue;
            }
            if (!File.Exists(Path.Combine(folder, name + ".dll")))
            {
                if (!quiet) Log($"{name}: no {name}.dll in its folder; skipped");
                continue;
            }
            names.Add(name);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>Loads and starts (or restarts) one mod. true started, false failed, null held back.</summary>
    private static bool? Load(Entry e, bool quiet, bool retrying = false)
    {
        var dll = Path.Combine(s_modsDir, e.Name, e.Name + ".dll");
        Mod mod;
        try
        {
            e.Loads++;
            var alcName = e.Loads == 1 ? e.Name : $"{e.Name}#{e.Loads}";
            Assembly asm;
            if (e.Script is { } script)
            {
                var compiled = ScriptHost.Compile(script, e.Name, out var messages);
                foreach (var m in messages.Take(30)) Log($"{e.Name}: {m}");
                if (compiled is null)
                {
                    e.State = "failed: it doesn't compile";
                    Log($"{e.Name}: {Path.GetFileName(script)} doesn't compile (the errors are above); not started");
                    return false;
                }
                asm = new ModLoadContext(alcName, Path.GetDirectoryName(script)!, null).LoadBytes(compiled.Value.Dll, compiled.Value.Pdb);
            }
            else
            {
                asm = new ModLoadContext(alcName, Path.GetDirectoryName(dll)!, dll).LoadUnlocked(dll);
            }
            var type = asm.GetCustomAttribute<ModEntryAttribute>()?.ModType ?? (e.Script is null ? null : ScriptModType(asm));
            if (type is null)
            {
                e.State = e.Script is null ? "failed: no [assembly: ModEntry]" : "failed: no Mod class";
                Log(e.Script is null
                    ? $"{e.Name}: {e.Name}.dll has no [assembly: ModEntry] (set <FH2ModType> in its project); skipped"
                    : $"{e.Name}: {Path.GetFileName(e.Script)} has no public class that inherits Mod; skipped");
                return false;
            }
            mod = (Mod)Activator.CreateInstance(type)!;
            mod.ModFolder = Path.GetDirectoryName(e.Script ?? dll)!;
        }
        // A build still copying files: let ReloadOne try again.
        catch (Exception ex) when (!(retrying && ex is IOException or BadImageFormatException))
        {
            e.State = "failed to load";
            Log($"{e.Name}: FAILED to load: {Unwrap(ex)}");
            return false;
        }

        var old = e.Mod;
        Mod.StartResult result;
        if (old is null)
        {
            result = ModLoader.Start(mod);
        }
        else
        {
            old.Retire();
            result = mod.StartReplacing(old.Slot);
        }
        e.Mod = mod;
        switch (result)
        {
            case Mod.StartResult.Started:
                e.State = e.Loads == 1 ? "running" : $"running (reload {e.Loads - 1})";
                return true;
            case Mod.StartResult.HeldOffline:
                e.State = "offline only; not started";
                if (!quiet) Log($"{e.Name} doesn't run in online play (its author says so), so it isn't started");
                return null;
            case Mod.StartResult.Refused:
                e.State = "refused by the platform";
                Log($"{e.Name}: the platform refused to register it");
                return false;
            default:
                e.State = "failed in OnInit";
                Log($"{e.Name}: failed while starting (its lines above say why)");
                return false;
        }
    }

    /// <summary>A script's Mod class: the first public, non-abstract class that inherits Mod.</summary>
    private static Type? ScriptModType(Assembly asm) =>
        asm.GetExportedTypes().FirstOrDefault(t => t.IsClass && !t.IsAbstract && typeof(Mod).IsAssignableFrom(t) &&
                                                  t.GetConstructor(Type.EmptyTypes) is not null);

    // ---- Reloading --------------------------------------------------------

    /// <summary>Reloads <paramref name="name"/>, or every hosted mod (and starts new ones) if null.</summary>
    private static string Reload(string? name)
    {
        lock (s_lock)
        {
            var folders = Available(quiet: true);
            var reply = new StringBuilder();
            if (name is null)
            {
                foreach (var f in folders) reply.AppendLine(ReloadOne(f.Name, folders));
                foreach (var gone in s_mods.Keys.Where(k => !folders.Any(f => string.Equals(f.Name, k, StringComparison.OrdinalIgnoreCase))).ToList())
                    reply.AppendLine(ReloadOne(gone, folders));
                if (reply.Length == 0) reply.Append("no hosted mods");
            }
            else
            {
                reply.Append(ReloadOne(name, folders));
            }
            RefreshRows();
            return reply.ToString().TrimEnd();
        }
    }

    private static string ReloadOne(string name, List<(string Name, string? Script)> available)
    {
        var found = available.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        var present = found.Name;
        s_mods.TryGetValue(name, out var e);
        if (present is null)
        {
            if (e?.Mod is null) return $"error: no hosted mod {name} (fh2-mods-dotnet\\{name}\\{name}.dll, or fh2-mods-dotnet\\scripts\\{name}.cs or .vb)";
            // Its folder went away (or was disabled): retire it, nothing replaces it.
            e.Mod.Retire();
            e.Mod.Slot.Silence();
            e.Mod = null;
            e.State = "removed";
            Log($"{e.Name} unloaded (its folder or script is gone or disabled); what it added stays until the game restarts, and does nothing");
            return $"{e.Name}: unloaded";
        }
        if (e is null) s_mods[present] = e = new Entry(present, found.Script);
        // A mod that can't be swapped while the game runs keeps running as it is.
        if (e.Mod is { Reloadable: false })
        {
            Log($"{e.Name} can't be reloaded while the game runs: it keeps running, and a new build starts with the next launch");
            return $"{e.Name}: can't be reloaded while the game runs (restart the game for a new build)";
        }
        e.Script = found.Script;

        var isNew = e.Mod is null && e.Loads == 0;
        bool? ok = null;
        // A build may still be copying the files: try a few times.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                ok = Load(e, quiet: true, retrying: attempt < 3);
                break;
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException) { Thread.Sleep(300); }
        }
        var what = isNew ? "started (new: its settings, pages and hotkeys show from the next launch)" : "reloaded";
        Log($"{e.Name} {(ok == true ? what : e.State)}");
        return ok == true ? $"{e.Name}: {what}" : $"error: {e.Name}: {e.State}";
    }

    private static void Changed(string path)
    {
        // fh2-mods-dotnet\<Name>\...: reload <Name> once the files settle.
        var rel = Path.GetRelativePath(s_modsDir, path);
        var sep = rel.IndexOfAny(['\\', '/']);
        if (sep <= 0) return;
        var name = rel[..sep];
        if (name.Equals(ScriptsFolder, StringComparison.OrdinalIgnoreCase))
        {
            if (!ScriptHost.IsScript(path) || rel.IndexOfAny(['\\', '/'], sep + 1) >= 0) return;
            name = Path.GetFileNameWithoutExtension(path);
        }
        lock (s_pending)
        {
            if (s_pending.TryGetValue(name, out var t))
            {
                t.Change(750, Timeout.Infinite);
                return;
            }
            s_pending[name] = new Timer(_ =>
            {
                lock (s_pending)
                {
                    s_pending[name].Dispose();
                    s_pending.Remove(name);
                }
                try { Reload(name); }
                catch (Exception ex) { Log($"auto-reload of {name} failed: {ex.Message}"); }
            }, null, 750, Timeout.Infinite);
        }
    }

    private static string ListMods()
    {
        lock (s_lock)
        {
            if (s_mods.Count == 0) return "no hosted mods";
            var sb = new StringBuilder();
            foreach (var e in s_mods.Values)
            {
                var info = e.Mod?.Info;
                sb.AppendLine($"{e.Name,-24} {(info is null ? "" : info.Version),-10} {(e.Script is null ? "" : "script, "),-8}{e.State}");
            }
            sb.Append(s_watcher is null ? "auto-reload is off" : "auto-reload is on");
            return sb.ToString();
        }
    }

    // ---- Helpers ----------------------------------------------------------

    private static string Unwrap(Exception e) =>
        e is TargetInvocationException { InnerException: { } inner } ? inner.ToString() : e.ToString();

    private static void Log(string line)
    {
        if (s_api is null || s_host is null) return;
        using var u = new Utf8Scope();
        foreach (var l in line.Split('\n'))
            s_api->Log(s_host, u.Str(l.TrimEnd('\r')));
    }

    /// <summary>One mod's context: its own dependencies from its folder, the SDK shared from the default context.</summary>
    private sealed class ModLoadContext(string name, string dir, string? mainAssembly) : AssemblyLoadContext("FH2Mod:" + name)
    {
        private static readonly string s_sdkName = typeof(Mod).Assembly.GetName().Name!;
        private readonly string _dir = dir;
        // Reads the mod's deps.json: NuGet packages' runtimes\<rid>\... files. A script has none.
        private readonly AssemblyDependencyResolver? _resolver = mainAssembly is null ? null : new(mainAssembly);

        /// <summary>A compiled script, straight from memory.</summary>
        public Assembly LoadBytes(byte[] dll, byte[]? pdb)
        {
            using var d = new MemoryStream(dll);
            if (pdb is null) return LoadFromStream(d);
            using var p = new MemoryStream(pdb);
            return LoadFromStream(d, p);
        }

        /// <summary>Loads from memory, with its pdb for line numbers and debugging, so the file stays free to rebuild.</summary>
        public Assembly LoadUnlocked(string path)
        {
            var pdb = Path.ChangeExtension(path, ".pdb");
            using var dll = new MemoryStream(File.ReadAllBytes(path));
            if (!File.Exists(pdb)) return LoadFromStream(dll);
            using var symbols = new MemoryStream(File.ReadAllBytes(pdb));
            return LoadFromStream(dll, symbols);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, s_sdkName, StringComparison.OrdinalIgnoreCase))
                return null; // the one the host loaded, so Mod is the same type for everyone
            // The mod's own dependencies, from its folder, read into memory like the mod.
            var path = _resolver?.ResolveAssemblyToPath(assemblyName);
            if (path is null)
            {
                var local = Path.Combine(_dir, assemblyName.Name + ".dll");
                if (File.Exists(local)) path = local;
            }
            return path is null ? null : LoadUnlocked(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            // Native DLLs can't be loaded from memory: these stay locked until the game closes.
            var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
