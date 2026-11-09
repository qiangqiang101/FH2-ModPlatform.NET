public class Broken : Mod
{
    public override ModInfo Info => new("Broken", "1.0.0");
    protected override void OnInit() { Log(ThisDoesNotExist); }
}
