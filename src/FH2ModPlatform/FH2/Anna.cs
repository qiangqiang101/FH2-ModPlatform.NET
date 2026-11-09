using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FH2ModPlatform;
using FH2ModPlatform.Interop;

namespace FH2;

/// <summary>
/// ANNA, the in-car assistant from the FH2 ANNA mod (D-pad down opens her menu on the HUD). Add
/// entries to her menu from your mod. Needs FH2 ANNA installed (its interface, version 4 or newer);
/// without it, <see cref="IsAvailable"/> is false and additions do nothing.
/// </summary>
/// <example>
/// <code>
/// ANNA.Menu.Add("Say hi", () => Log("hi"));                       // her top list
/// ANNA.Car.Add("Horn", Honk);                                       // her Car list
/// ANNA.CarLights.Add("Full beam", () => Player.Car!.HeadlightsOn = true);
/// var tools = ANNA.Menu.AddList("My Tools");                        // a list of your own
/// tools.Add("Noon", () => World.TimeOfDay = TimeSpan.FromHours(12));
/// </code>
/// </example>
public static unsafe class ANNA
{
    /// <summary>ANNA's own lists, to put entries in.</summary>
    public static class Lists
    {
        public const string Car = "car";
        public const string CarLights = "car.lights";
        public const string CarDoors = "car.doors";
        public const string Route = "route";
    }

    // ---- FH2 ANNA's interface (FH2ANNA/include/fh2anna.h), as it lays it out ------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public uint Size;
        public byte* Key, Parent, Label;
        public delegate* unmanaged<void*, byte*, void> OnChoose;
        public delegate* unmanaged<void*, byte*, int> Shown;
        public delegate* unmanaged<void*, byte*, byte*> LabelNow;
        public void* User;
        public int StaysOpen;                                 // version 3
        public delegate* unmanaged<void*, byte*, int> Enabled; // version 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Api
    {
        public uint Size;
        public delegate* unmanaged<int> IsOpen;
        public delegate* unmanaged<uint, int> UsesKey;
        public delegate* unmanaged<Entry*, int> AddEntry;    // version 2
        public delegate* unmanaged<int, void> HoldInput;     // version 5
    }

    private const string InterfaceName = "FH2ANNA";
    private static Api* s_api;
    private static int s_state;   // 0 not looked up, 1 looking, 2 found, 3 not there
    private static readonly ConcurrentQueue<MenuItem> s_pending = new();
    private static readonly ConcurrentDictionary<string, MenuItem> s_items = new();

    /// <summary>True when FH2 ANNA is installed and has given its menu to other mods.</summary>
    public static bool IsAvailable
    {
        get
        {
            Resolve(wait: false);
            return s_api is not null;
        }
    }

    /// <summary>True while ANNA's menu is open on the HUD. Any thread.</summary>
    public static bool IsOpen => IsAvailable && s_api->IsOpen() != 0;

    /// <summary>ANNA's menu: the top list.</summary>
    public static readonly MenuList Menu = new(null);

    /// <summary>ANNA's Car list (Lights, Doors, Roof...).</summary>
    public static readonly MenuList Car = new(Lists.Car);

    /// <summary>ANNA's Car > Lights list.</summary>
    public static readonly MenuList CarLights = new(Lists.CarLights);

    /// <summary>ANNA's Car > Doors list.</summary>
    public static readonly MenuList CarDoors = new(Lists.CarDoors);

    /// <summary>ANNA's Set Route To Nearest list.</summary>
    public static readonly MenuList Route = new(Lists.Route);

    /// <summary>
    /// Any list in ANNA's menu by its key: another mod's ("carspawner.carspawner" is CarSpawner's
    /// list; a list made with <see cref="MenuList.AddList"/> is "&lt;its mod&gt;.&lt;its label&gt;", and
    /// <see cref="MenuList.Key"/> says), a native mod's (its author's docs say), or one of hers.
    /// Your entries show once that list exists; if its mod isn't installed, they never do.
    /// </summary>
    public static MenuList List(string key) => new(key);

    /// <summary>
    /// A list in ANNA's menu by what it's called: one of hers ("Car", "Car > Lights", "Car > Doors",
    /// "Set Route To Nearest"), or one a .NET mod added ("CarSpawner"); "A > B" for a list in a list.
    /// A key works too. The list doesn't have to be there yet: entries wait for it until every mod
    /// has started (the log says if no list ever had that name).
    /// </summary>
    public static MenuList Items(string name) => new(FindKey(name), name);

    /// <summary>A list in ANNA's menu: the top list, one of hers (<see cref="Lists"/>), or one of yours.</summary>
    public sealed class MenuList
    {
        internal MenuList(string? key, string? query = null) => (_key, Query) = (key, query);

        private string? _key;
        internal string? Query { get; }

        /// <summary>Its key (null for the top list, or for a list looked up by name that isn't there yet).</summary>
        public string? Key => _key ??= Query is null ? null : FindKey(Query);

        /// <summary>
        /// Adds an entry that runs <paramref name="onChoose"/> (on the game's thread) when it's chosen.
        /// With <paramref name="staysOpen"/>, ANNA stays open after it, for things to flip back and forth.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public MenuItem Add(string label, Action onChoose, bool staysOpen = false, string? parent = null, string? key = null) =>
            ANNA.Add(ModPlatform.IsHosted ? Assembly.GetCallingAssembly() : AotMod(), label, onChoose, staysOpen, parent ?? Key, key, parent is null && Key is null ? Query : null);

        /// <summary>Adds a list of your own under this one; it shows once something is in it. Its key is "&lt;your mod&gt;.&lt;label&gt;".</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public MenuList AddList(string label, string? key = null)
        {
            var item = ANNA.Add(ModPlatform.IsHosted ? Assembly.GetCallingAssembly() : AotMod(), label, null, false, Key, key, Key is null ? Query : null);
            return new MenuList(item.Key);
        }
    }

    /// <summary>One entry in ANNA's menu. Change its label, or when it shows, any time.</summary>
    public sealed class MenuItem
    {
        internal MenuItem(string key, string label, string? parent) => (Key, Label, Parent) = (key, label, parent);

        public string Key { get; }
        /// <summary>The key of the list it's in (null: the top list, or one looked up by name and not there yet).</summary>
        public string? Parent { get; internal set; }
        internal string? ParentQuery;   // the list's name, until it's found
        internal bool IsList;
        /// <summary>What it says. Changes show the next time its list opens.</summary>
        public string Label { get; set; }
        /// <summary>Asked each time its list opens: false hides the entry.</summary>
        public Func<bool>? Visible { get; set; }
        /// <summary>Asked each time its list opens: false greys it out.</summary>
        public Func<bool>? Enabled { get; set; }
        internal volatile Action? OnChoose;
        internal bool StaysOpen;
        internal Assembly? Owner;
        internal nint LabelBuffer;
    }

    private static MenuItem Add(Assembly owner, string label, Action? onChoose, bool staysOpen, string? parent, string? key, string? parentQuery = null)
    {
        key ??= MakeKey(owner, parent ?? (parentQuery is null ? null : Clean(parentQuery)), label);
        // A reloaded mod adds its entries again: ANNA keeps the first, so the old one takes the new code.
        if (s_items.TryGetValue(key, out var existing))
        {
            existing.Label = label;
            existing.OnChoose = onChoose;
            existing.StaysOpen = staysOpen;
            existing.Owner = owner;
            existing.Visible = null;
            existing.Enabled = null;
            return existing;
        }
        var item = new MenuItem(key, label, parent)
        {
            OnChoose = onChoose, StaysOpen = staysOpen, Owner = owner, IsList = onChoose is null, ParentQuery = parentQuery,
        };
        s_items[key] = item;
        s_pending.Enqueue(item);
        Resolve(wait: true);
        return item;
    }

    // "scriptdemo.say_hi": letters, digits, '.', '_' (what ANNA allows), under the mod's name.
    // In a NativeAOT DLL the runtime has one mod (and can't ask for the calling assembly); hosted
    // mods share this runtime, so Add and AddList ask for theirs.
    private static Assembly AotMod() => ModLoader.Mods.FirstOrDefault()?.GetType().Assembly ?? typeof(ANNA).Assembly;

    // A key part: letters and digits, '_' for the rest ("Car Lights (x)" -> "car_lights_x").
    private static string Clean(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : sb.Length > 0 && sb[^1] == '_' ? "" : "_");
        return sb.ToString().Trim('_');
    }

    // Always under the mod's name, so two mods' entries can't clash: "scriptdemo.scriptdemo" (a
    // list), "scriptdemo.scriptdemo.noon" (in it), "scriptdemo.car.lights.headlights" (in one of ANNA's).
    private static string MakeKey(Assembly owner, string? parent, string label)
    {
        var mod = Clean(owner.GetName().Name ?? "mod");
        var under = parent is null ? mod : parent.StartsWith(mod + ".", StringComparison.Ordinal) ? parent : mod + "." + parent;
        return under + "." + Clean(label);
    }

    // ANNA's own lists: (the list they're in, what they're called, their key).
    private static readonly (string? Parent, string Label, string Key)[] s_hers =
    [
        (null, "Car", Lists.Car), (null, "Set Route To Nearest", Lists.Route), (null, "Route", Lists.Route),
        (Lists.Car, "Lights", Lists.CarLights), (Lists.Car, "Doors", Lists.CarDoors),
    ];

    /// <summary>The key of the list called <paramref name="name"/> ("Car > Lights", "CarSpawner", or a key), or null if none (yet).</summary>
    private static string? FindKey(string name)
    {
        var n = name.Trim();
        if (s_hers.Any(h => h.Key == n) || s_items.TryGetValue(n, out var byKey) && byKey.IsList) return n;
        string? at = null;
        foreach (var part in n.Split('>', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var found = s_hers.FirstOrDefault(h => h.Parent == at && h.Label.Equals(part, StringComparison.OrdinalIgnoreCase)).Key
                     ?? s_items.Values.FirstOrDefault(i => i.IsList && i.Parent == at && i.Label.Equals(part, StringComparison.OrdinalIgnoreCase))?.Key;
            if (found is null) return null;
            at = found;
        }
        return at;
    }

    /// <summary>A hosted mod is being replaced: its entries do nothing until it adds them again.</summary>
    internal static void Forget(Assembly assembly)
    {
        foreach (var item in s_items.Values)
            if (item.Owner == assembly)
            {
                item.OnChoose = null;
                item.Visible = () => false;
            }
    }

    // ---- Talking to FH2 ANNA ----------------------------------------------------------------

    // FH2 ANNA publishes its interface from its own start; mods start in name order, so look it
    // up once every mod has started (straight away when it's already there).
    private static void Resolve(bool wait)
    {
        if (!ModPlatform.IsAttached) return;
        if (s_api is null && Interlocked.CompareExchange(ref s_state, 1, 0) == 0)
        {
            s_api = (Api*)ModPlatform.GetInterface(InterfaceName, 4);
            if (s_api is not null) s_state = 2;
            else
                new Thread(() =>
                {
                    ModPlatform.WaitForMods(TimeSpan.FromSeconds(60));
                    s_api = (Api*)ModPlatform.GetInterface(InterfaceName, 4);
                    s_state = s_api is not null ? 2 : 3;
                    Flush();
                }) { IsBackground = true, Name = "FH2.ANNA lookup" }.Start();
        }
        if (s_api is not null) Flush();
    }

    private static int s_lateFlush;

    private static void Flush(bool final = false)
    {
        if (s_api is null) return;
        var waiting = new List<MenuItem>();
        while (s_pending.TryDequeue(out var item))
        {
            if (item.ParentQuery is { } query)
            {
                var parent = FindKey(query);
                if (parent is null && !final)
                {
                    waiting.Add(item);   // its list may come from a mod that hasn't started yet
                    continue;
                }
                if (parent is null)
                {
                    LogError($"ANNA entry {item.Label}", new InvalidOperationException($"no list in ANNA's menu is called '{query}'"));
                    continue;
                }
                item.Parent = parent;
                item.ParentQuery = null;
            }
            using var u = new Utf8Scope();
            var e = new Entry
            {
                Size = (uint)sizeof(Entry),
                Key = u.Str(item.Key),
                Parent = u.Str(item.Parent),
                Label = u.Str(item.Label),
                OnChoose = item.OnChoose is null ? null : &OnChoose,
                Shown = &OnShown,
                LabelNow = &OnLabel,
                User = (void*)GCHandle.ToIntPtr(GCHandle.Alloc(item)),
                StaysOpen = item.StaysOpen ? 1 : 0,
                Enabled = &OnEnabled,
            };
            s_api->AddEntry(&e);
        }
        if (waiting.Count == 0) return;
        foreach (var w in waiting) s_pending.Enqueue(w);
        if (Interlocked.Exchange(ref s_lateFlush, 1) == 0)
            new Thread(() =>
            {
                ModPlatform.WaitForMods(TimeSpan.FromSeconds(60));
                Flush(final: true);
            }) { IsBackground = true, Name = "FH2.ANNA late entries" }.Start();
    }

    private static MenuItem Item(void* user) => (MenuItem)GCHandle.FromIntPtr((nint)user).Target!;

    [UnmanagedCallersOnly]
    private static void OnChoose(void* user, byte* key)
    {
        var item = Item(user);
        try { item.OnChoose?.Invoke(); }
        catch (Exception e) { LogError($"ANNA entry {item.Key}", e); }
    }

    [UnmanagedCallersOnly]
    private static int OnShown(void* user, byte* key)
    {
        var item = Item(user);
        try { return item.Visible?.Invoke() ?? true ? 1 : 0; }
        catch (Exception e) { LogError($"ANNA entry {item.Key} (Visible)", e); return 1; }
    }

    [UnmanagedCallersOnly]
    private static int OnEnabled(void* user, byte* key)
    {
        var item = Item(user);
        try { return item.Enabled?.Invoke() ?? true ? 1 : 0; }
        catch (Exception e) { LogError($"ANNA entry {item.Key} (Enabled)", e); return 1; }
    }

    // ANNA copies the text straight away; the buffer stays until the next ask.
    [UnmanagedCallersOnly]
    private static byte* OnLabel(void* user, byte* key)
    {
        var item = Item(user);
        var text = item.Label ?? "";
        int n = Encoding.UTF8.GetByteCount(text);
        var buf = (byte*)NativeMemory.Alloc((nuint)n + 1);
        Encoding.UTF8.GetBytes(text, new Span<byte>(buf, n));
        buf[n] = 0;
        var old = Interlocked.Exchange(ref item.LabelBuffer, (nint)buf);
        if (old != 0) NativeMemory.Free((void*)old);
        return buf;
    }

    private static void LogError(string what, Exception e)
    {
        var owner = ModPlatform.GameOwner;
        if (owner is null) return;
        using var u = new Utf8Scope();
        foreach (var l in $"{what} failed: {e}".Split('\n'))
            ModPlatform.Api->Log(owner, u.Str(l.TrimEnd('\r')));
    }
}
