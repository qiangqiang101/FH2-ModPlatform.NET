// A C# script: no project, no usings (System, System.Linq, FH2, FH2ModPlatform... come with it).
public class HelloScript : Mod
{
    public override ModInfo Info => new("HelloScript", "1.0.0") { Description = "A C# script" };

    protected override void OnInit()
    {
        Log($"hello from a C# script, {CarCatalog.All.Count} cars, {Enumerable.Range(1, 3).Sum()}");
        AddCommand("ping", "replies pong", _ => "pong from a script");
    }
}
