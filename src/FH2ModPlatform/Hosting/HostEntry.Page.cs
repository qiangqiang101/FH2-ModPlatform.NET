using System.Runtime.InteropServices;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2ModPlatform.Hosting;

// The "Hosted .NET mods" page under MODS > Installed mods > Platform.NET. The MODS tab lists one
// row per DLL in fh2-mods, and every hosted mod is loaded by FH2DotNet.dll, so this page is
// where they're listed one by one: name, version, what it's doing, and A to reload it.
public static unsafe partial class HostEntry
{
    private const string PageKey = "Hosted";

    private sealed record ModRow(string Folder, string Label, string Value, string Description, string Footer, bool Alert, bool Dim);

    // Rows as of the last load or reload. The page reads this on the game's UI thread, so it
    // never waits on s_lock (held through a mod's OnInit while it reloads).
    private static volatile ModRow[] s_rows = [];

    private static void AddModsPage()
    {
        if (!ModPlatform.Supports(ApiLevel.Pages)) return;
        RefreshRows();
        using var u = new Utf8Scope();
        var page = new FH2Page
        {
            Size = (uint)sizeof(FH2Page),
            Key = u.Str(PageKey),
            Title = u.Str(".NET mods"),
            Label = u.Str("Hosted .NET mods"),
            Description = u.Str("The mods FH2DotNet runs from fh2-mods-dotnet, each with its version and what it's doing."),
            OnOpen = &PageOpened,
            OnSelect = &PageSelected,
        };
        if (s_api->AddPage(s_host, &page) != 0) SetRows();
    }

    /// <summary>Takes a new snapshot of every hosted mod (called under s_lock, or before anything else runs).</summary>
    private static void RefreshRows()
    {
        var rows = new List<ModRow>();
        foreach (var e in s_mods.Values)
        {
            var info = e.Mod?.Info;
            var running = e.State.StartsWith("running", StringComparison.Ordinal);
            var failed = e.State.StartsWith("failed", StringComparison.Ordinal) || e.State.StartsWith("refused", StringComparison.Ordinal);
            var about = info?.Description?.Trim() is { Length: > 0 } d ? d + (".!?".Contains(d[^1]) ? " " : ". ") : "";
            // The platform shows a mod's settings under Installed mods only for the DLL's own mod (Platform.NET's):
            // say where a hosted mod's are.
            if (running && e.Mod is { SettingRows: > 0 } m)
                about += FH2.SettingsTile.Owner == m
                    ? $"Its settings are in Pause > Settings > {FH2.SettingsTile.CurrentTitle}. "
                    : $"Its settings are in Pause > Settings > MODS and MODS > All settings ({m.SettingRows} row{(m.SettingRows == 1 ? "" : "s")}). ";
            rows.Add(new ModRow(
                e.Name,
                info?.Name ?? e.Name,
                failed ? "Error" : running ? info?.Version ?? "" : "Off",
                about + (failed
                    ? $"It isn't working ({e.State}); fh2-mods\\platform.log says why. Press A to try again."
                    : running ? "Press A to reload it (after you've rebuilt it)." : $"It isn't running: {e.State}."),
                e.Where,
                failed,
                !running && !failed));
        }
        try
        {
            foreach (var dir in Directory.GetDirectories(s_modsDir, "*.disabled"))
            {
                var folder = Path.GetFileName(dir);
                rows.Add(new ModRow(folder, folder[..^".disabled".Length], "Off",
                    "Turned off: its folder's name ends in .disabled. Rename it without that and restart the game to turn it on.",
                    $"fh2-mods-dotnet\\{folder}", false, true));
            }
        }
        catch (IOException) { }
        if (rows.Count == 0)
            rows.Add(new ModRow("", "No hosted .NET mods", "", "Hosted mods go in fh2-mods-dotnet\\<Name>\\<Name>.dll.", "", false, true));
        s_rows = rows.ToArray();
    }

    private static void SetRows()
    {
        var rows = s_rows;
        using var u = new Utf8Scope();
        var native = (FH2PageRow*)u.Alloc((nuint)(sizeof(FH2PageRow) * rows.Length));
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            native[i] = new FH2PageRow
            {
                Size = (uint)sizeof(FH2PageRow),
                // Only mods that are loaded (or failed) can be reloaded from here.
                Key = r.Dim || r.Folder.Length == 0 ? null : u.Str(r.Folder),
                Label = u.Str(r.Label),
                Value = u.Str(r.Value),
                Description = u.Str(r.Description),
                Footer = u.Str(r.Footer),
                Style = r.Dim ? (int)RowStyle.Dim : (int)RowStyle.Normal,
                ValueAlert = r.Alert ? 1 : 0,
            };
        }
        s_api->SetPageRows(s_host, u.Str(PageKey), native, (uint)rows.Length);
    }

    [UnmanagedCallersOnly]
    private static void PageOpened(void* user, byte* pageKey)
    {
        try { SetRows(); }
        catch (Exception e) { Log("the Hosted .NET mods page failed: " + e.Message); }
    }

    [UnmanagedCallersOnly]
    private static void PageSelected(void* user, byte* pageKey, byte* rowKey)
    {
        var folder = NativeString.FromUtf8(rowKey);
        if (string.IsNullOrEmpty(folder)) return;
        // Not on the UI thread: a reload runs the mod's OnInit, which can take a while.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Reload(folder); }
            catch (Exception e) { Log($"reloading {folder} failed: {e.Message}"); }
        });
    }
}
