using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The places where the live stress test's bots stalled (2026-10-08, 200 managed bots in every starting zone; 74 stalled,
/// stress-bot-failures-20261008.txt), on the real terrain: a bot is put where a stalled one stood and
/// follows <see cref="PlayerbotNavigation.PlanToward"/> routes, as its quest travel does, to the creature its goal named
/// (positions from the stall reports and the world database's spawns). Not every pairing is a recorded stall: the Quest 3087 camp
/// spot to Jen'shan pairing comes from the old probe cases, and the Quest 2161 Grosk and Ukor targets were chosen by the author
/// (the recorded 2161 stalls were Explore goals, which name no creature). Each walk waits for the region graph first, so the
/// planning path does not depend on the timing of its background build. The on-demand navigation tile mechanism is guarded by
/// <c>NavMeshOnDemandTileTests</c> (LoadTiles), not here. The walks are spread over several classes (the walks themselves are in
/// <see cref="RealTerrainNavigationTestBase.WalksAsync"/>): xUnit runs the tests of one class one after another and the classes in
/// parallel, and these walks are minutes each.
/// </summary>
public sealed class PlayerbotStallPatternWalksPart1Tests : RealTerrainNavigationTestBase
{
    public PlayerbotStallPatternWalksPart1Tests(ITestOutputHelper output) : base(output) { }

    /// <summary>
    /// Quest 1656 (A Task Unfinished) to Innkeeper Kauth in Bloodhoof Village, from the edge of the Red Cloud Mesa: 102 stall reports.
    /// The mesa's way down runs south; the search ended on the cliff edge (the point nearest the innkeeper on the loaded tiles),
    /// and the straight-line steps ended there too.
    /// </summary>
    [RealTerrainBotFact]
    public Task Quest1656_FromTheMesaEdge_TheBotWalksDownToInnkeeperKauth()
        => WalksAsync(new(-2764.0f, -312.0f, 54.5f), InnkeeperKauth);

    /// <summary>Quest 3087 from a stall spot to Galgar, 23 yards away.</summary>
    [RealTerrainBotFact]
    public Task Quest3087_FromTheCampSpot_TheBotWalksToGalgar()
        => WalksAsync(new(-585.1f, -4223.8f, 38.5f), new(-561.6f, -4221.8f, 41.7f));

    /// <summary>Quest 3087 from the same spot to Jenshan, 50 yards the other way (a pairing from the old probe, not a recorded stall).</summary>
    [RealTerrainBotFact]
    public Task Quest3087_FromTheCampSpot_TheBotWalksToJenshan()
        => WalksAsync(new(-585.1f, -4223.8f, 38.5f), new(-635.5f, -4227.5f, 38.4f));

    /// <summary>Quest 3361 from a stall spot to Felix, 39 yards away, on the eastern kingdoms map.</summary>
    [RealTerrainBotFact]
    public Task Quest3361_FromTheHeights_TheBotWalksToFelix()
        => WalksAsync(new(-6135.3f, 386.1f, 395.6f), new(-6098.1f, 396.1f, 395.5f), map: 0);
}

public sealed class PlayerbotStallPatternWalksPart2Tests : RealTerrainNavigationTestBase
{
    public PlayerbotStallPatternWalksPart2Tests(ITestOutputHelper output) : base(output) { }

    /// <summary>
    /// Quest 7 (Kobold Camp Cleanup) back to Marshal McBride in Northshire Abbey, from Stormwind: the way is 1,268 yards for 630
    /// straight, and needs more than 8,192 search nodes.
    /// </summary>
    [RealTerrainBotFact]
    public Task Quest7_FromStormwind_TheBotWalksBackToMarshalMcBride()
        => WalksAsync(new(-8842.1f, 464.0f, 109.8f), new(-8902.6f, -162.6f, 82.0f), map: 0);

    /// <summary>
    /// Quest 2159 from a spot in Shadowglen whose nearest navigation polygon is an eleven-yard fragment the mesh does not join to the
    /// ground beside it: every route from it ended on the fragment.
    /// </summary>
    [RealTerrainBotFact]
    public Task Quest2159_FromAFragmentOfMeshInShadowglen_TheBotStepsOffItAndWalksToDolanaar()
        => WalksAsync(new(10429.6f, 729.9f, 1318.4f), InnkeeperKeldamyr);

    /// <summary>Quest 4495 (A Good Friend) to Iverron, from a fragment of mesh in Shadowglen (25 stall reports for the quest).</summary>
    [RealTerrainBotFact]
    public Task Quest4495_FromAFragmentOfMeshInShadowglen_TheBotWalksToIverron()
        => WalksAsync(new(10486.4f, 744.3f, 1313.9f), new(10711.6f, 1034.9f, 1347.3f));
}

public sealed class PlayerbotStallPatternWalksPart3Tests : RealTerrainNavigationTestBase
{
    public PlayerbotStallPatternWalksPart3Tests(ITestOutputHelper output) : base(output) { }

    /// <summary>Quest 2159 from a second place its bots stalled (positions of main's former probe cases).</summary>
    [RealTerrainBotFact]
    public Task Quest2159_FromTheHillNorthEastOfDolanaar_TheBotWalksToInnkeeperKeldamyr()
        => WalksAsync(new(10199.2f, 991.2f, 1376.4f), InnkeeperKeldamyr);

    /// <summary>
    /// Quest 2159 (Dolanaar Delivery) to Innkeeper Keldamyr, from the hillside west of Shadowglen the straight-line steps led bots up
    /// (35 stall reports for the quest).
    /// </summary>
    [RealTerrainBotFact]
    public Task Quest2159_FromTheHillWestOfShadowglen_TheBotWalksToInnkeeperKeldamyr()
        => WalksAsync(new(10176.0f, 842.4f, 1395.4f), InnkeeperKeldamyr);

    /// <summary>
    /// Quest 2161 (A Peon's Burden) north of Razor Hill: the bot stood on a fragment of mesh with an exploration goal, every way out
    /// refused (18 stall reports). The target, Innkeeper Grosk, is the author's choice: the recorded stalls were Explore goals.
    /// </summary>
    [RealTerrainBotFact]
    public Task Quest2161_FromAFragmentOfMeshNorthOfRazorHill_TheBotWalksToInnkeeperGrosk()
        => WalksAsync(new(207.5f, -4458.7f, 27.7f), new(340.4f, -4686.3f, 16.5f));
}

public sealed class PlayerbotStallPatternWalksPart4Tests : RealTerrainNavigationTestBase
{
    public PlayerbotStallPatternWalksPart4Tests(ITestOutputHelper output) : base(output) { }

    /// <summary>The same quest from the hill north of Camp Narache where the first stalls began.</summary>
    [RealTerrainBotFact]
    public Task Quest1656_FromTheHillNorthOfCampNarache_TheBotWalksToInnkeeperKauth()
        => WalksAsync(new(-2937.9f, -67.2f, 111.2f), InnkeeperKauth);

    /// <summary>Quest 3087 from a far stall spot (about 540 yards out) to Jenshan.</summary>
    [RealTerrainBotFact]
    public Task Quest3087_FromFarOut_TheBotWalksToJenshan()
        => WalksAsync(new(-212.3f, -4559.7f, 72.0f), new(-635.5f, -4227.5f, 38.4f));
}

public sealed class PlayerbotStallPatternWalksPart5Tests : RealTerrainNavigationTestBase
{
    public PlayerbotStallPatternWalksPart5Tests(ITestOutputHelper output) : base(output) { }

    /// <summary>Quest 459 (The Woodland Protector) to Tarindrella, from a fragment of mesh at Dolanaar (16 stall reports for the quest).</summary>
    [RealTerrainBotFact]
    public Task Quest459_FromAFragmentOfMeshAtDolanaar_TheBotWalksToTarindrella()
        => WalksAsync(new(9740.8f, 835.7f, 1299.2f), new(10296.9f, 870.2f, 1335.9f));

    /// <summary>Quest 459 (The Woodland Protector) from a second stall spot near Dolanaar, on its way to Tarindrella.</summary>
    [RealTerrainBotFact]
    public Task Quest459_FromTheSecondStallSpotNearDolanaar_TheBotWalksToTarindrella()
        => WalksAsync(new(10037.1f, 958.1f, 1337.4f), new(10296.9f, 870.2f, 1335.9f));

    /// <summary>Quest 4495 (A Good Friend) from a second stall spot in Shadowglen, on its way to Iverron.</summary>
    [RealTerrainBotFact]
    public Task Quest4495_FromTheSecondStallSpotInShadowglen_TheBotWalksToIverron()
        => WalksAsync(new(10577.5f, 894.5f, 1312.8f), new(10711.6f, 1034.9f, 1347.3f));

    /// <summary>
    /// Quest 752 (A Humble Task) to Greatmother Hawkwind, from Chief Hawkwind's platform in Camp Narache: 22 stall reports, every one
    /// at the same spot. The platform is ringed by steep polygons, which every bot route excludes: no route anywhere.
    /// </summary>
    [RealTerrainBotFact]
    public Task Quest752_FromChiefHawkwindsPlatform_TheBotWalksToGreatmotherHawkwind()
        => WalksAsync(new(-2881.9f, -229.1f, 55.2f), new(-3052.5f, -522.5f, 27.0f));
}

public sealed class PlayerbotStallPatternWalksPart6Tests : RealTerrainNavigationTestBase
{
    public PlayerbotStallPatternWalksPart6Tests(ITestOutputHelper output) : base(output) { }

    /// <summary>Quest 2161 from the mesh fragment north of Razor Hill to Ukor, 880 yards west (the target is the author's choice).</summary>
    [RealTerrainBotFact]
    public Task Quest2161_FromAFragmentOfMeshNorthOfRazorHill_TheBotWalksToUkor()
        => WalksAsync(new(207.5f, -4458.7f, 27.7f), new(-599.4f, -4715.3f, 35.2f));

    /// <summary>Quest 2161 to Ukor from the neighbouring start the old probe used, a few yards from the fragment above.</summary>
    [RealTerrainBotFact]
    public Task Quest2161_FromTheProbesStartNorthOfRazorHill_TheBotWalksToUkor()
        => WalksAsync(new(200.53f, -4458.667f, 30.16f), new(-599.4f, -4715.3f, 35.2f));
}
