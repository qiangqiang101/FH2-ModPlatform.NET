// Uses most of the SDK, for tests/run_host_test.sh and fake_platform.exe.
using FH2ModPlatform;

namespace TestMod;

public sealed class TestMod : Mod
{
    // Each reload is a new assembly (statics start over), in a load context named TestMod#2, #3...
    private readonly string _copy = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(TestMod).Assembly)?.Name ?? "?";

    public override ModInfo Info => new("TestMod.NET", "0.1.0") { Description = "SDK test mod" };

    protected override void OnUnload() => Log($"OnUnload (copy {_copy})");

    protected override void OnInit()
    {
        Log($"OnInit (copy {_copy}, reload: {IsReload})");
        Console.WriteLine($"Console.WriteLine from copy {_copy}");
        System.Diagnostics.Trace.WriteLine("Trace.WriteLine works too");
        Log($"load context: {System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(GetType().Assembly)?.Name}");
        Log(TestModHelper.Greeter.Greet("the platform"));
        Log($"session {ModPlatform.SessionId}, online {ModPlatform.PlayingOnline}, mods dir {ModPlatform.ModsDir}");

        ReplaceString("Main.str", "Ünïcødé", "テキスト ✓");
        AddSql("UPDATE Data_Car SET Year = Year WHERE 0;");
        SetFile("UI/test.xml", "<x/>"u8);

        AddSetting(new SliderSetting("Volume", "Volume", 0.25f, 0, 1)
        {
            Decimals = 2,
            Changed = v => Log($"slider -> {v} (copy {_copy})"),
        });
        AddSetting(new ChoiceSetting("Button", "Button", ["LB", "RB", "A"], 0)
        {
            Changed = i => Log($"choice -> {i}"),
        });
        AddSetting(new ToggleSetting("Boom", "Throws", true)
        {
            Changed = _ => throw new InvalidOperationException("thrown from a setting callback"),
        });
        AddSetting(new LinkSetting("Docs", "Docs", "https://example.com"));

        AddPage(new PageOptions("Stats", "Stats") { Opened = () => Log("page opened") });
        SetPageRows("Stats",
        [
            PageRow.Heading("Heading"),
            new PageRow("Distance", "12 km"),
            new PageRow("Locked") { Style = RowStyle.Dim },
        ]);

        AddTitleEntry(new TitleEntryOptions("Go", "Start somewhere") { Pressed = () => TitleResult.Start });
        AddHotkey(new HotkeyOptions("Mark", "Mark spot", new(0x78, KeyModifiers.None, PadChord.LB | PadChord.RB | PadChord.Y))
        {
            Pressed = () => Log($"hotkey pressed (copy {_copy})"),
        });

        // ANNA lists by name: hers, another mod's (ScriptDemo starts before TestMod), and none.
        FH2.ANNA.Items("Car > Lights").Add("Test light", () => Log("test light"));
        FH2.ANNA.Items("ScriptDemo").Add("From TestMod", () => Log("from TestMod"));
        FH2.ANNA.Items("No Such List").Add("Lost", () => { });

        AddCommand("echo", "echoes", args => $"echo {args} " + new string('x', 100));
        AddCommand("fail", "throws", _ => throw new Exception("command blew up"));
    }
}
