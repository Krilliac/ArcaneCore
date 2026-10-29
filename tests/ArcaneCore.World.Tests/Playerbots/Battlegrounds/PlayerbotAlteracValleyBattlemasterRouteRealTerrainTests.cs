using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// The Horde Alterac Valley battlemasters on the real navigation meshes, walked as the battleground AI walks to one (route legs from
/// <c>PlayerbotNavigation.PlanToward</c>, no lift): from the spots where live bots gave up the walk ("bot-bg/objective-unreachable", could
/// not walk / timed out walking to the Alterac Valley battlemaster) the static mesh joins the battlemaster, in Orgrimmar (Kartra
/// Bloodsnarl, 14942) and in the Undercity (Grizzle Halfmane, 347, on the lowest level). So those live failures are not a mesh gap, an
/// off-mesh spawn or a missing lift on these ways; this guards that finding. Spawn positions: the world database's creature spawns of
/// entries 14942 and 347 (classic-db).
/// </summary>
public sealed class PlayerbotAlteracValleyBattlemasterRouteRealTerrainTests : RealTerrainNavigationTestBase
{
    public PlayerbotAlteracValleyBattlemasterRouteRealTerrainTests(ITestOutputHelper output) : base(output) { }

    private static readonly Vector3 Kartra = new(1980.9f, -4787.78f, 55.8796f);
    private static readonly Vector3 Grizzle = new(1331.94f, 334.713f, -63.6249f);

    [RealTerrainBotFact]
    public Task Orgrimmar_FromTheValleyOfStrength_TheAlteracValleyBattlemasterIsWalked() => WalksAsync(new Vector3(1925.6f, -4369.0f, 22.5f), Kartra, 1);

    [RealTerrainBotFact]
    public Task Orgrimmar_FromOutsideTheGate_TheAlteracValleyBattlemasterIsWalked() => WalksAsync(new Vector3(1601.0f, -4437.2f, 6.6f), Kartra, 1);

    [RealTerrainBotFact]
    public Task Undercity_FromTheUpperRing_TheAlteracValleyBattlemasterIsWalked() => WalksAsync(new Vector3(1665.1f, 532.3f, -11.3f), Grizzle, 0);
}
