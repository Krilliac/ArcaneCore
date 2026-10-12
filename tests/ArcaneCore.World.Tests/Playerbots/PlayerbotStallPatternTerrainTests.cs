using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The places where the live stress test's bots stalled (2026-10-08, 200 managed bots in every starting zone; 74 stalled,
/// D:/ArcaneCore-lanes/_plan/stress-bot-failures-20261008.txt), on the real terrain: a bot is put where a stalled one stood and
/// follows <see cref="PlayerbotNavigation.PlanToward"/> routes, as its quest travel does, to the creature its goal named
/// (positions from the stall reports and the world database's spawns).
/// </summary>
public sealed partial class PlayerbotRealTerrainNavigationTests
{
    private static readonly PlayerbotOptions LiveOptions = new() { MaxPathPoints = 128, MaxRouteYards = 2000 };

    /// <summary>
    /// Quest 1656 (A Task Unfinished) to Innkeeper Kauth in Bloodhoof Village, from the edge of the Red Cloud Mesa: 99 stall reports.
    /// The mesa's way down runs south through a navigation tile no grid held; the search ended on the cliff edge (the point nearest
    /// the innkeeper on the loaded tiles), and the straight-line steps ended there too.
    /// </summary>
    [RealTerrainBotFact]
    public Task Quest1656_FromTheMesaEdge_TheBotWalksDownToInnkeeperKauth()
        => WalksAsync(new(-2764.0f, -312.0f, 54.5f), InnkeeperKauth);

    /// <summary>The same quest from the hill north of Camp Narache where the first stalls began.</summary>
    [RealTerrainBotFact]
    public Task Quest1656_FromTheHillNorthOfCampNarache_TheBotWalksToInnkeeperKauth()
        => WalksAsync(new(-2937.9f, -67.2f, 111.2f), InnkeeperKauth);

    /// <summary>
    /// Quest 752 (A Humble Task) to Greatmother Hawkwind, from Chief Hawkwind's platform in Camp Narache: 22 stall reports, every one
    /// at the same spot. The platform is ringed by steep polygons, which every bot route excludes: no route anywhere.
    /// </summary>
    [RealTerrainBotFact]
    public Task Quest752_FromChiefHawkwindsPlatform_TheBotWalksToGreatmotherHawkwind()
        => WalksAsync(new(-2881.9f, -229.1f, 55.2f), new(-3052.5f, -522.5f, 27.0f));

    /// <summary>
    /// Quest 2159 (Dolanaar Delivery) to Innkeeper Keldamyr, from the hillside west of Shadowglen the straight-line steps led bots up
    /// (30 stall reports for the quest).
    /// </summary>
    [RealTerrainBotFact]
    public Task Quest2159_FromTheHillWestOfShadowglen_TheBotWalksToInnkeeperKeldamyr()
        => WalksAsync(new(10176.0f, 842.4f, 1395.4f), InnkeeperKeldamyr);

    /// <summary>
    /// Quest 2159 from a spot in Shadowglen whose nearest navigation polygon is an eleven-yard fragment the mesh does not join to the
    /// ground beside it: every route from it ended on the fragment.
    /// </summary>
    [RealTerrainBotFact]
    public Task Quest2159_FromAFragmentOfMeshInShadowglen_TheBotStepsOffItAndWalksToDolanaar()
        => WalksAsync(new(10429.6f, 729.9f, 1318.4f), InnkeeperKeldamyr);

    /// <summary>Quest 459 (The Woodland Protector) to Tarindrella, from a fragment of mesh at Dolanaar (15 stall reports for the quest).</summary>
    [RealTerrainBotFact]
    public Task Quest459_FromAFragmentOfMeshAtDolanaar_TheBotWalksToTarindrella()
        => WalksAsync(new(9740.8f, 835.7f, 1299.2f), new(10296.9f, 870.2f, 1335.9f));

    /// <summary>Quest 4495 (A Good Friend) to Iverron, from a fragment of mesh in Shadowglen (24 stall reports for the quest).</summary>
    [RealTerrainBotFact]
    public Task Quest4495_FromAFragmentOfMeshInShadowglen_TheBotWalksToIverron()
        => WalksAsync(new(10486.4f, 744.3f, 1313.9f), new(10711.6f, 1034.9f, 1347.3f));

    /// <summary>
    /// Quest 2161 (A Peon's Burden) north of Razor Hill: the bot stood on a fragment of mesh with an exploration goal, every way out
    /// refused (10 stall reports). It walks to Innkeeper Grosk.
    /// </summary>
    [RealTerrainBotFact]
    public Task Quest2161_FromAFragmentOfMeshNorthOfRazorHill_TheBotWalksToInnkeeperGrosk()
        => WalksAsync(new(207.5f, -4458.7f, 27.7f), new(340.4f, -4686.3f, 16.5f));

    /// <summary>
    /// Quest 7 (Kobold Camp Cleanup) back to Marshal McBride in Northshire Abbey, from Stormwind: the way is 1,268 yards for 630
    /// straight, and needs more than 8,192 search nodes.
    /// </summary>
    [RealTerrainBotFact]
    public Task Quest7_FromStormwind_TheBotWalksBackToMarshalMcBride()
        => WalksAsync(new(-8842.1f, 464.0f, 109.8f), new(-8902.6f, -162.6f, 82.0f), map: 0);

    // Innkeeper Kauth (creature 6747), Bloodhoof Village; Innkeeper Keldamyr (6736), Dolanaar.
    private static readonly Vector3 InnkeeperKauth = new(-2365.4f, -347.3f, -8.9f);
    private static readonly Vector3 InnkeeperKeldamyr = new(9802.2f, 982.6f, 1314.0f);

    private async Task WalksAsync(Vector3 from, Vector3 to, uint map = 1)
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(from, map);
        float left = await terrain.WalkTowardAsync(to, 5f, 300_000, output, LiveOptions); // a refused route fails inside
        Assert.True(left <= 5f, $"stopped {left:F1} yards from the destination");
    }
}
