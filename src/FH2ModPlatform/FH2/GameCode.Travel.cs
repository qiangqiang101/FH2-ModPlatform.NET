// The sat-nav's waypoint and the game's own car placement (fast travel), for World.Waypoint and Player.TeleportTo.
// Found by static analysis (the map's set-route handler 0xD6A1F0, fast_travel.xml's behaviours); to be confirmed in game.
namespace FH2;

internal static partial class Addresses
{
    // ---- The waypoint ---------------------------------------------------------------------------------
    // HUDSatNavComponent (vtable 0x2CDCFF8): its per-frame update is vtable slot 3. In it: +0x1E0 the sat-nav's type
    // (0 = the player's own), +0x2C0 the map pin's position (x, y, z; zero when there's none), and through its module
    // (+0x220, a weak_ptr whose control block is at +0x228) the SatNav system (+0x150), an array of shared_ptr<SatNav>
    // by type (16 bytes each). A SatNav's route (+0x118) is valid when its +8 byte is set; its nodes are a vector of
    // pointers at +0x40/+0x48, the last the destination, its position (road-snapped, with a real height) at +0x14.
    public const int SatNavUpdateSlot = 0x2CDD010;
    public static readonly Code SatNavUpdate = new("HUDSatNavComponent update", 0x783A40, "48 8B C4 57 48 81 EC D0 00 00 00 48 C7 44 24 40");
    public const int SatNavType = 0x1E0, SatNavPin = 0x2C0, SatNavModule = 0x220, SatNavModuleCtrl = 0x228, SatNavSystem = 0x150;
    public const int SatNavRoute = 0x118, SatNavRouteValid = 0x8, SatNavNodes = 0x40, SatNavNodePosition = 0x14;

    // ---- Placing the car (fast travel) ------------------------------------------------------------------
    // shared_ptr<FastTravelModule> GetFastTravel(eventBus, shared_ptr* out), the event bus at [game+0x34C0F40].
    public const int EventBusGlobal = 0x34C0F40;
    public static readonly Code GetFastTravelModule = new("get FastTravelModule", 0x8CA820, "40 53 48 83 EC 30 48 8B 49 08 48 8B DA C7 44 24");
    // void FastTravelModule::DebugFastTravel(module, const __m128* position): the nearest road node to the position,
    // the car placed there along the road (ForzaCar::PlaceAt: ground snap, physics reset); the node at module+0x110.
    public static readonly Code DebugFastTravel = new("FastTravelModule::DebugFastTravel", 0x54EE40, "40 57 48 83 EC 30 48 C7 44 24 20 FE FF FF FF 48");
    public const int FastTravelNode = 0x110;
    // Player* LocalPlayer(playerManager, 0), and void SetStreamingFocus(player, const __m128* position).
    public static readonly Code LocalPlayer = new("local player", 0x21E0C0, "48 8B 49 48 48 85 C9 0F 85 53 34 0C 00 33 C0 C3");
    public static readonly Code StreamingFocus = new("player streaming focus", 0xF92360, "40 53 48 83 EC 20 C5 F8 28 02 48 8B D9 C5 F8 29");
    public const int DynamicsAngularVelocity = 0x60;

    public static readonly Code[] Waypoint = [SatNavUpdate];
    public static readonly Code[] Teleport = [GetFastTravelModule, DebugFastTravel, LocalPlayer, StreamingFocus];
}
