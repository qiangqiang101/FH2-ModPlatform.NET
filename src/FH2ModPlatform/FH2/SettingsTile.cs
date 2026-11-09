using System.Runtime.InteropServices;
using System.Text;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>
/// A tile of your mod's own on Pause &gt; Settings, next to Controls, opening a page with only your mod's rows
/// (<see cref="Mod.AddSetting"/>) instead of having them among every mod's on the MODS pages. One mod can have it.
/// </summary>
/// <remarks>
/// How: the Settings page's XAML is edited as the game reads it (Controls becomes a square tile, yours goes in the
/// other half of its place); your tile opens the platform's Mods page as the MODS tile does, and that page, read
/// right after your tile was pressed, keeps only your rows (read at any other time, it loses them). If the game's
/// pages aren't as expected, your rows stay on the MODS pages (platform.log says so).
/// </remarks>
public static unsafe class SettingsTile
{
    private const string ButtonName = "PART_Button_FH2ModTile";
    private static Mod? s_mod;
    private static Func<string> s_title = () => "MOD";
    private static string s_picture = "";
    private static string s_suffix = "";      // the end of our rows' descriptions on the Mods page
    private static nint s_base, s_imageEnd;
    private static FH2Mod* s_owner;
    private static void** s_getResource, s_wireTiles, s_hudScene;
    private static volatile bool s_tile, s_failed, s_refreshOk;
    private static long s_asked, s_opening;
    private static int s_builds;
    private static List<(string Key, string Property)> s_openProps = new();
    private static readonly List<(string Key, nint Descriptor)> s_descriptors = new();
    private static readonly List<nint> s_refresh = new();
    private static nint s_rowSetOriginal;
    private static nint s_noType;
    private static readonly object s_lock = new();

    /// <summary>The mod with the tile, and the tile's title now.</summary>
    internal static Mod? Owner => s_failed ? null : s_mod;
    internal static string CurrentTitle
    {
        get
        {
            try { return s_title(); } catch { return "MOD"; }
        }
    }

    /// <summary>True once the Settings page has been given the tile (it's read when it first shows).</summary>
    public static bool IsShown => s_tile;

    /// <summary>
    /// Gives <paramref name="mod"/> the tile (from its OnInit, after adding its rows). <paramref name="title"/> is the
    /// tile's and the page's title, asked each time they're read (so it can follow the game's language), and
    /// <paramref name="pictureXaml"/> what's drawn on the tile (XAML in a 1x1 tile's Grid, about 280 x 280, in the
    /// light grey of the game's icons: #FFDCDCDC).
    /// </summary>
    public static void Add(Mod mod, Func<string> title, string pictureXaml)
    {
        if (s_mod is not null) throw new InvalidOperationException("Another mod has the Settings tile.");
        s_mod = mod;
        s_title = title;
        s_picture = pictureXaml;
        s_suffix = " (" + Xml(mod.Info.Name) + ")\"";
        new Thread(Install) { IsBackground = true, Name = "FH2 settings tile" }.Start();
    }

    /// <summary>
    /// The open page shows the row's new value next time it's touched: for rows you changed yourself (a preset
    /// that sets other rows). The platform shows the row the player changed by itself.
    /// </summary>
    public static void RefreshRow(string key)
    {
        lock (s_lock)
            foreach (var (k, d) in s_descriptors)
                if (k == key) s_refresh.Add(d);
    }

    private static string Xml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // The title for the Settings page's XAML, which isn't read as UTF-8: what isn't ASCII as character references.
    private static string Title()
    {
        string t;
        try { t = s_title(); } catch { t = "MOD"; }
        var sb = new StringBuilder();
        foreach (var r in Xml(t).EnumerateRunes())
            sb.Append(r.Value < 0x80 ? ((char)r.Value).ToString() : $"&#x{r.Value:X};");
        return sb.ToString();
    }

    // ---- Installing -------------------------------------------------------------------------------

    private static void Install()
    {
        var owner = ModPlatform.GameOwner;
        var b = ModPlatform.WaitForCode();
        if (b == 0) return;
        s_base = b;
        s_owner = owner;
        s_imageEnd = b + *(int*)(b + *(int*)(b + 0x3C) + 0x50);   // SizeOfImage
        Menus.Base = Menus.Base == 0 ? b : Menus.Base;
        s_refreshOk = Game.Mismatch(b, [Addresses.NotifyProperty, Addresses.MakeAvuiString, Addresses.FindProperty]) is null;
        if (Game.Mismatch(b, [Addresses.WireButton, Addresses.HudTile, Addresses.GameAllocate, Addresses.GameDelete, Addresses.ObjectInit,
                              Addresses.ObjectCleanup, Addresses.PauseWireTiles, Addresses.HudSceneRecord, Addresses.GetResource]) is { } bad)
        {
            Game.Log(owner, $"SettingsTile off: the Settings page's code isn't as expected at {bad}; the rows stay on the MODS pages");
            s_failed = true;
            return;
        }
        s_noType = (nint)NativeMemory.AllocZeroed(0x100);   // no properties of its own and no base: only the platform's are found
        s_getResource = Slot(); s_wireTiles = Slot(); s_hudScene = Slot();
        using var u = new Utf8Scope();
        var api = ModPlatform.Api;
        var wire = api->HookFunction(owner, u.Str("FH2 settings tile: wire the tiles"), (void*)(b + Addresses.PauseWireTiles.Offset),
            (delegate* unmanaged<nint, void>)&OnWireTiles, s_wireTiles);
        var scene = api->HookFunction(owner, u.Str("FH2 settings tile: HUD page scene record"), (void*)(b + Addresses.HudSceneRecord.Offset),
            (delegate* unmanaged<nint, nint>)&OnHudScene, s_hudScene);
        var xaml = api->HookFunction(owner, u.Str("FH2 settings tile: page XAML"), (void*)(b + Addresses.GetResource.Offset),
            (delegate* unmanaged<nint, nint*, nint*>)&OnGetResource, s_getResource);
        if (wire is null || scene is null || xaml is null)
        {
            if (wire is not null) api->DisableHooks(owner, wire);
            if (scene is not null) api->DisableHooks(owner, scene);
            Game.Log(owner, "SettingsTile off: couldn't hook the Settings page; the rows stay on the MODS pages");
            s_failed = true;
            return;
        }
        api->EnableHooks(owner, wire);
        api->EnableHooks(owner, scene);
        api->EnableHooks(owner, xaml);   // last: the rest is ready for it
    }

    private static void** Slot() => (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));

    // ---- The tile: wired as the platform wires MODS, opening the HUD page pointed at the Mods page ----------

    [UnmanagedCallersOnly]
    private static void OnWireTiles(nint page)
    {
        Volatile.Write(ref s_builds, 0);
        ((delegate* unmanaged<nint, void>)*s_wireTiles)(page);
        if (!s_tile) return;
        if (s_pmf is null)
        {
            // Kept for good, as the game may keep what it's wired with: a member function pointer {function, adjust}.
            s_pmf = (nint*)NativeMemory.AllocZeroed(2 * (nuint)sizeof(nint));
            s_pmf[0] = (nint)(delegate* unmanaged<nint, void>)&OnTilePressed;
            s_buttonName = (byte*)Marshal.StringToHGlobalAnsi(ButtonName);
        }
        ((delegate* unmanaged<nint, byte*, nint*, void>)(s_base + Addresses.WireButton.Offset))(page, s_buttonName, s_pmf);
    }

    private static nint* s_pmf;
    private static byte* s_buttonName;

    [UnmanagedCallersOnly]
    private static void OnTilePressed(nint page)
    {
        Volatile.Write(ref s_builds, 2);
        Interlocked.Exchange(ref s_asked, Environment.TickCount64);
        ((delegate* unmanaged<nint, void>)(s_base + Addresses.HudTile.Offset))(page);
    }

    [UnmanagedCallersOnly]
    private static nint OnHudScene(nint output)
    {
        var r = ((delegate* unmanaged<nint, nint>)*s_hudScene)(output);
        if (Volatile.Read(ref s_builds) > 0 && Environment.TickCount64 - Interlocked.Read(ref s_asked) < 1500)
        {
            Interlocked.Decrement(ref s_builds);
            if (PointAtModsPage(r)) Interlocked.Exchange(ref s_opening, Environment.TickCount64);
            else Game.Log(s_owner, "SettingsTile: the HUD page's scene record isn't as expected; the tile opened the HUD page");
        }
        return r;
    }

    // L"Anthem\A_HudOptions" -> L"Anthem\A_ModOptions", in place (as the platform's MODS tile does).
    private static bool PointAtModsPage(nint rec)
    {
        if (rec == 0 || !Readable(rec, 0x48)) return false;
        ulong size = *(ulong*)(rec + 0x38), cap = *(ulong*)(rec + 0x40);
        var p = cap > 7 ? *(char**)(rec + 0x28) : (char*)(rec + 0x28);
        if (size != 19 || !Readable((nint)p, 40) || new string(p, 0, 19) != "Anthem\\A_HudOptions") return false;
        "A_ModOptions".AsSpan().CopyTo(new Span<char>(p + 7, 12));
        return true;
    }

    // ---- The pages' XAML, as the game reads it -------------------------------------------------------

    [UnmanagedCallersOnly]
    private static nint* OnGetResource(nint reader, nint* output)
    {
        var r = ((delegate* unmanaged<nint, nint*, nint*>)*s_getResource)(reader, output);
        try
        {
            // The two pages are told by their content (their x:Class near the top), not by who reads them: other mods
            // (FH2 ANNA, Extra Options) hook this reading too, and come between.
            if (r == output && output != null && *output != 0) EditPage(output);
        }
        catch (Exception e)
        {
            Game.Log(s_owner, "SettingsTile: " + e.Message);
        }
        return r;
    }

    // The reader's resource: an AVUI object with the text at +0x28 and its length at +0x30.
    private static bool ResourceText(nint obj, out string text)
    {
        text = "";
        if (!Readable(obj, 0x38)) return false;
        var bytes = *(nint*)(obj + 0x28);
        int length = *(int*)(obj + 0x30);
        if (bytes == 0 || length <= 0 || length > (8 << 20) || !Readable(bytes, length)) return false;
        // Only the two pages: their class is near the top.
        var head = Encoding.UTF8.GetString((byte*)bytes, Math.Min(length, 2048));
        if (!head.Contains("x:Class=\"ForzaUI.A_PauseOptions\"") && !head.Contains("x:Class =\"ForzaUI.A_HudOptions\"")) return false;
        text = Encoding.UTF8.GetString((byte*)bytes, length);
        return true;
    }

    private static void EditPage(nint* output)
    {
        if (!ResourceText(*output, out var text)) return;
        if (text.Contains("x:Class=\"ForzaUI.A_PauseOptions\""))
        {
            if (!AddTile(ref text) || !Replace(output, text))
            {
                if (!s_failed) Game.Log(s_owner, "SettingsTile: the Settings page isn't as expected; the rows stay on the MODS pages");
                s_failed = true;
                return;
            }
            if (!s_tile) Game.Log(s_owner, "SettingsTile: Pause > Settings has the tile");
            s_tile = true;
            return;
        }
        // The rows leave the MODS pages only once the tile is there: if it isn't, they stay where they were.
        if (s_failed || !s_tile) return;
        // The game reads the page more than once as the tile opens it (7 ms apart): every read in the moment after is ours.
        bool ours = Environment.TickCount64 - Interlocked.Read(ref s_opening) < 2000;
        if (!ours && FilterInstalled(ref text))
        {
            if (Replace(output, text)) LogOnce(ref s_loggedInstalled, "SettingsTile: MODS > Installed mods without the tile's rows");
        }
        else if (FilterModsPage(ref text, ours))
        {
            if (Replace(output, text))
            {
                if (ours)
                {
                    Game.Log(s_owner, "SettingsTile: the tile's page opened with its rows");
                    FindDescriptors();
                }
                else LogOnce(ref s_loggedMods, "SettingsTile: MODS > All settings without the tile's rows");
            }
        }
        else if (ours) Game.Log(s_owner, "SettingsTile: the Mods page isn't as expected; it shows every mod's rows");
    }

    private static bool s_loggedInstalled, s_loggedMods;

    private static void LogOnce(ref bool done, string line)
    {
        if (done) return;
        done = true;
        Game.Log(s_owner, line);
    }

    private static string TileResources() => $$$"""

            <local:TileMenuDataItem x:Key="FH2ModTile_MenuDataItem" Heading="{{{Title()}}}" Icon="" />
            <ControlTemplate x:Key="FH2ModTile_Template" TargetType="{x:Type Button}">
                <Grid Style="{StaticResource A_Style_FrameworkElement_DefaultTransition}" local:UIPageTransitionManager.TransitionIndex="{Binding (local:UIPageTransitionManager.TransitionIndex), RelativeSource={RelativeSource TemplatedParent}}">
                    <Grid Style="{StaticResource A_Style_Grid_HiddenWhenLocked}">
                        <local:FZMaterial Style="{StaticResource A_Style_FZMaterial_TileShadow}" />
                    </Grid>
                    <Grid Style="{StaticResource A_Style_Grid_MotionBlur}">
                        <local:FZMaterial Style="{StaticResource A_Style_FZMaterial_TileBackground}" />
                        <Grid ClipToBounds="True">
                            {{{s_picture}}}
                        </Grid>
                        <Grid Margin="{StaticResource A_Thickness_TilePadding}">
                            <local:FZTextBlock Text="{Binding DataContext.Heading, RelativeSource={RelativeSource TemplatedParent}}" Style="{StaticResource A_Style_FZTextBlock_TileTitle}" HorizontalAlignment="Left" TextAlignment="Left" MaxWidth="{Binding Width, RelativeSource={RelativeSource TemplatedParent}}" />
                        </Grid>
                        <local:FZMaterial Style="{StaticResource A_Style_FZMaterial_TileSelectionBar}" />
                    </Grid>
                </Grid>
            </ControlTemplate>
            <ControlTemplate x:Key="FH2ModTile_Template_Controls" TargetType="{x:Type Button}">
                <Grid Style="{StaticResource A_Style_FrameworkElement_DefaultTransition}" local:UIPageTransitionManager.TransitionIndex="{Binding (local:UIPageTransitionManager.TransitionIndex), RelativeSource={RelativeSource TemplatedParent}}">
                    <Grid Style="{StaticResource A_Style_Grid_HiddenWhenLocked}">
                        <local:FZMaterial Style="{StaticResource A_Style_FZMaterial_TileShadow}" />
                    </Grid>
                    <Grid Style="{StaticResource A_Style_Grid_MotionBlur}">
                        <local:FZMaterial Style="{StaticResource A_Style_FZMaterial_TileBackground}" />
                        <Grid ClipToBounds="True">
                            <local:FZMaterial Style="{StaticResource A_Style_FZMaterial_LockableImage}" ImageSource="{Binding DataContext.Icon, RelativeSource={RelativeSource TemplatedParent}}" Width="558" Height="276" HorizontalAlignment="Right" VerticalAlignment="Center" StretchDirection="Both" />
                        </Grid>
                        <Grid Margin="{StaticResource A_Thickness_TilePadding}">
                            <local:FZTextBlock Text="{Binding DataContext.Heading, RelativeSource={RelativeSource TemplatedParent}}" Style="{StaticResource A_Style_FZTextBlock_TileTitle}" HorizontalAlignment="Left" TextAlignment="Left" MaxWidth="{Binding Width, RelativeSource={RelativeSource TemplatedParent}}" />
                            <local:FZMaterial Style="{StaticResource A_Style_Tile_LockIcon}" />
                        </Grid>
                        <local:FZMaterial Style="{StaticResource A_Style_FZMaterial_TileSelectionBar}" />
                    </Grid>
                </Grid>
            </ControlTemplate>

""";

    private const string TileButton = """

                                <Button x:Name="PART_Button_FH2ModTile"
                                        local:TileMenuNavigationManager.TileState="Enabled"
                                        DataContext="{StaticResource FH2ModTile_MenuDataItem}"
                                        local:TileMenuNavigationManager.GridX="2"
                                        local:TileMenuNavigationManager.GridY="0"
                                        Style="{DynamicResource A_Style_Button_Tile_1x1}"
                                        Canvas.Left="{StaticResource A_Double_TileCoord_X_2}"
                                        Canvas.Top="{StaticResource A_Double_TileCoord_Y_0}"
                                        Template="{StaticResource FH2ModTile_Template}"
                                        local:UIPageTransitionManager.TransitionIndex="1"
                                        local:BindingProxy.C="DisableMotionBlur"/>
""";

    private static bool ReplaceOnce(ref string s, string from, string to)
    {
        int at = s.IndexOf(from, StringComparison.Ordinal);
        if (at < 0) return false;
        s = s[..at] + to + s[(at + from.Length)..];
        return true;
    }

    // A_PauseOptions.xaml: Controls in a square, ours beside it.
    private static bool AddTile(ref string page)
    {
        int named = page.IndexOf("x:Name=\"PART_Button_Controls\"", StringComparison.Ordinal);
        int item = page.IndexOf("x:Key=\"A_MenuDataItem_TeamCredits\"", StringComparison.Ordinal);
        if (named < 0 || item < 0 || page.Contains(ButtonName) || page.Contains("TileMenuNavigationManager.GridX=\"2\"")) return false;
        int a = page.LastIndexOf("<Button", named, StringComparison.Ordinal), b = page.IndexOf("/>", named, StringComparison.Ordinal);
        int itemEnd = page.IndexOf('\n', item);
        if (a < 0 || b < 0 || itemEnd < 0 || itemEnd > a) return false;
        var el = page.Substring(a, b + 2 - a);
        if (!ReplaceOnce(ref el, "A_Style_Button_Tile_2x1", "A_Style_Button_Tile_1x1") ||
            !ReplaceOnce(ref el, "A_ControlTemplate_HorizontalImageTile", "FH2ModTile_Template_Controls"))
            return false;
        page = page[..a] + el + TileButton + page[(b + 2)..];
        page = page[..(itemEnd + 1)] + TileResources() + page[(itemEnd + 1)..];
        return true;
    }

    // The Mods page ("ALL SETTINGS"): only our rows (ours), or all but them.
    private static bool FilterModsPage(ref string page, bool ours)
    {
        const string listAt = "<local:CommonListBox x:Name=\"OptionsList\"", listEnd = "</local:CommonListBox>";
        const string item = "<local:TileMenuDataItem x:Key=\"FH2Mod_Item_", tag = "Tag=\"{StaticResource FH2Mod_Item_", first = " x:Name=\"FirstItem\"";
        if (!page.Contains("Content=\"ALL SETTINGS\"")) return false;
        var mine = new List<string>();
        var props = new List<(string, string)>();
        var labels = s_mod?.SettingLabels ?? [];
        for (int p = page.IndexOf(item, StringComparison.Ordinal); p >= 0; p = page.IndexOf(item, p + 1, StringComparison.Ordinal))
        {
            int n = p + item.Length, q = page.IndexOf('"', n), e = page.IndexOf("/>", n, StringComparison.Ordinal);
            if (q < 0 || e < 0) return false;
            var el = page[n..e];
            if (!el.Contains(s_suffix)) continue;
            mine.Add(page[n..q]);
            int h = el.IndexOf("Heading=\"", StringComparison.Ordinal), he = h < 0 ? -1 : el.IndexOf('"', h + 9);
            if (he < 0) continue;
            var heading = el[(h + 9)..he];
            foreach (var (key, label) in labels)
                if (Xml(label) == heading) props.Add((key, "FH2Mod_" + mine[^1]));
        }
        if (mine.Count == 0) return false;
        int l = page.IndexOf(listAt, StringComparison.Ordinal);
        l = l < 0 ? -1 : page.IndexOf('>', l);
        int le = l < 0 ? -1 : page.IndexOf(listEnd, l, StringComparison.Ordinal);
        if (le < 0) return false;
        var kept = new StringBuilder();
        int rows = 0;
        bool anyOther = false;
        for (int p = l + 1; p < le;)
        {
            int e = page.IndexOf('\n', p);
            e = e < 0 || e > le ? le : e + 1;
            var line = page[p..e];
            p = e;
            int t = line.IndexOf(tag, StringComparison.Ordinal);
            if (t >= 0)
            {
                int n = t + tag.Length, q = line.IndexOf('}', n);
                if (q < 0) return false;
                bool isMine = mine.Contains(line[n..q]);
                anyOther |= !isMine;
                if (isMine != ours) continue;
                while (ReplaceOnce(ref line, first, "")) { }
                if (rows++ == 0)   // the page's first row is named FirstItem
                {
                    int close = line.LastIndexOf("/>", StringComparison.Ordinal);
                    if (close < 0) return false;
                    line = line[..close] + first[1..] + " " + line[close..];
                }
            }
            kept.Append(line);
        }
        if (rows == 0 || (!ours && !anyOther)) return false;
        page = page[..(l + 1)] + kept + page[le..];
        if (ours)
        {
            lock (s_lock) s_openProps = props;
            ReplaceOnce(ref page, "Content=\"ALL SETTINGS\"", "Content=\"" + Title() + "\"");
            while (ReplaceOnce(ref page, s_suffix, "\"")) { }   // the descriptions needn't say whose they are
        }
        return true;
    }

    // MODS > Installed mods: our rows go from our mod's view, as they have a page of their own. The platform finds
    // what A does on a row by its place in the list, so only from the end of a list (ours are last in our mod's).
    private static bool FilterInstalled(ref string page)
    {
        const string item = "<local:TileMenuDataItem x:Key=\"", tag = "Tag=\"{StaticResource ";
        const string listAt = "<local:CommonListBox", listEnd = "</local:CommonListBox>";
        if (!page.Contains("Content=\"INSTALLED MODS\"")) return false;
        var mine = new List<string>();
        for (int p = page.IndexOf(item, StringComparison.Ordinal); p >= 0; p = page.IndexOf(item, p + 1, StringComparison.Ordinal))
        {
            int n = p + item.Length, q = page.IndexOf('"', n), e = page.IndexOf("/>", n, StringComparison.Ordinal);
            if (q < 0 || e < 0) return false;
            if (page[n..e].Contains(s_suffix)) mine.Add(page[n..q]);
        }
        if (mine.Count == 0) return false;
        bool changed = false;
        for (int l = page.IndexOf(listAt, StringComparison.Ordinal); l >= 0; l = page.IndexOf(listAt, l + 1, StringComparison.Ordinal))
        {
            int open = page.IndexOf('>', l), le = open < 0 ? -1 : page.IndexOf(listEnd, open, StringComparison.Ordinal);
            if (le < 0) return false;
            int cut = le, p = le;
            while (p > open + 1)
            {
                int start = page.LastIndexOf('\n', p - 2) + 1;
                var line = page[start..p];
                int t = line.IndexOf(tag, StringComparison.Ordinal);
                if (t >= 0)
                {
                    int n = t + tag.Length, q = line.IndexOf('}', n);
                    if (q < 0 || !mine.Contains(line[n..q]) || line.Contains("x:Name=")) break;
                    cut = start;
                }
                else if (line.Trim().Length > 0) break;   // something else (the list's own tag)
                p = start;
            }
            if (cut == le) continue;
            page = page[..cut] + "                                " + page[le..];
            changed = true;
        }
        return changed;
    }

    // ---- Our resource in place of the reader's ----------------------------------------------------------

    private static readonly List<(nint From, nint Copy)> s_vtables = new();

    // The original's vtable, with a destructor that frees what we allocated.
    private static nint OurVtable(nint from)
    {
        lock (s_vtables)
        {
            foreach (var (f, c) in s_vtables)
                if (f == from) return c;
            var slots = (nint*)NativeMemory.Alloc(7 * (nuint)sizeof(nint));
            Buffer.MemoryCopy((void*)(from - 8), slots, 7 * sizeof(nint), 7 * sizeof(nint));   // [-1] is the RTTI locator
            slots[1] = (nint)(delegate* unmanaged<nint, uint, nint>)&DeleteResource;
            s_vtables.Add((from, (nint)(slots + 1)));
            return (nint)(slots + 1);
        }
    }

    [UnmanagedCallersOnly]
    private static nint DeleteResource(nint r, uint flags)
    {
        ((delegate* unmanaged<nint, void>)(s_base + Addresses.ObjectCleanup.Offset))(r);
        if ((flags & 1) != 0) ((delegate* unmanaged<nint, void>)(s_base + Addresses.GameDelete.Offset))(r);
        return r;
    }

    private static bool Replace(nint* output, string text)
    {
        var original = *output;
        var bytes = Encoding.UTF8.GetBytes(text);
        int capacity = bytes.Length + 1;
        var r = ((delegate* unmanaged<nuint, nint>)(s_base + Addresses.GameAllocate.Offset))((nuint)(0x38 + capacity));
        if (r == 0) return false;
        var data = r + 0x38;
        Marshal.Copy(bytes, 0, data, bytes.Length);
        *(byte*)(data + bytes.Length) = 0;
        ((delegate* unmanaged<nint, void>)(s_base + Addresses.ObjectInit.Offset))(r);
        *(nint*)r = OurVtable(*(nint*)original);
        *(nint*)(r + 0x28) = data;
        *(int*)(r + 0x30) = bytes.Length;
        *(int*)(r + 0x34) = capacity;
        *output = r;
        UiElement.Release(original);
        return true;
    }

    // ---- Rows that change with others (a preset): their descriptors' setter becomes ours -------------------

    private static void FindDescriptors()
    {
        if (!s_refreshOk) return;
        List<(string Key, string Property)> props;
        lock (s_lock) props = s_openProps;
        foreach (var (key, prop) in props)
        {
            lock (s_lock)
                if (s_descriptors.Exists(d => d.Key == key)) continue;
            var d = ((delegate* unmanaged<nint, nint, nint>)(s_base + Addresses.FindProperty.Offset))(s_noType, ButtonIcons.AvuiString(prop));
            if (d == 0 || !WrapSetter(d)) continue;
            lock (s_lock) s_descriptors.Add((key, d));
        }
    }

    private static bool WrapSetter(nint d)
    {
        var setter = (nint*)(d + Addresses.DescriptorSetter);
        var ours = (nint)(delegate* unmanaged<nint, nint, nint, void>)&RowSet;
        if (*setter == ours) return true;
        if (*setter == 0 || (s_rowSetOriginal != 0 && *setter != s_rowSetOriginal)) return false;
        s_rowSetOriginal = *setter;
        *setter = ours;
        return true;
    }

    // After the platform's setter (and so the mod's Changed): the rows asked for are read again by the page.
    [UnmanagedCallersOnly]
    private static void RowSet(nint desc, nint obj, nint value)
    {
        ((delegate* unmanaged<nint, nint, nint, void>)s_rowSetOriginal)(desc, obj, value);
        nint[] now;
        lock (s_lock)
        {
            now = s_refresh.ToArray();
            s_refresh.Clear();
        }
        if (obj != 0)
            foreach (var d in now) ((delegate* unmanaged<nint, nint, void>)(s_base + Addresses.NotifyProperty.Offset))(obj, d);
    }

    // ---- Memory checks ---------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    private static bool Readable(nint p, nint n)
    {
        MemoryInfo m;
        if (p == 0 || VirtualQuery(p, &m, (nuint)sizeof(MemoryInfo)) == 0 || m.State != 0x1000 /* MEM_COMMIT */) return false;
        if ((m.Protect & (0x01 /* NOACCESS */ | 0x100 /* GUARD */)) != 0) return false;
        return p + n <= m.BaseAddress + (nint)m.RegionSize;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, MemoryInfo* info, nuint length);
}
