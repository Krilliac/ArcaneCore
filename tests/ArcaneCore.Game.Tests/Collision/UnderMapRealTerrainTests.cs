using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The under-map detectors on real extracted data (<c>ARCANECORE_TEST_TERRAIN_DIR</c>, see <see cref="RealTerrainDataTests"/>): a creature
/// or a player standing in a cave or a city is below the terrain surface on purpose and must not be reported. The same points were reported
/// before the detectors consulted the vmap models (the terrain-only height chain: <c>MapCreatureHeightProvider</c> -> <c>Map.GetHeight</c> ->
/// <c>TerrainInfo.GetHeight</c>). Expected positions are world-database positions.
/// </summary>
public sealed class UnderMapRealTerrainTests(ITestOutputHelper output)
{
    private static string Root => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    /// <summary>Places that lie under the terrain surface and are not under the map: map, x, y, z.</summary>
    public static TheoryData<string, uint, float, float, float> NotUnderTheMap => new()
    {
        { "Fargodeep Mine", 0u, -9861.6f, 156.0f, 8.5f },
        { "Ironforge", 0u, -5010.6f, -1060.9f, 502.0f },
        { "Undercity", 0u, 1475.6f, 376.3f, -59.4f },
        { "Teldrassil barrow", 1u, 10095.2f, 1088.3f, 1327.7f },
    };

    private WorldRuntime RealWorld(uint mapId, float x, float y)
    {
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = Root;
        WorldCollision.Of(world).Install(lineOfSight: vmaps);
        (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
        Assert.True(vmaps.LoadTile(mapId, tx, ty), "the vmap tile of the point");
        return world;
    }

    [RealTerrainTheory]
    [MemberData(nameof(NotUnderTheMap))]
    public void ACreatureInACaveOrACity_IsNotReportedUnderTheMap(string name, uint mapId, float x, float y, float z)
    {
        const uint entry = 949101;
        using WorldRuntime world = RealWorld(mapId, x, y);
        Map map = world.GetMap(mapId);
        output.WriteLine($"{name}: z {z}, terrain {map.GetHeight(x, y, z)}");
        Assert.True(map.GetHeight(x, y, z) - z > CreatureAnomalies.UnderMapMarginYards, "the terrain surface is above the creature by more than the margin");

        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject.StartsWith($"creature entry {entry} ", StringComparison.Ordinal));
        CreatureContent content = Content([Template(entry)], [Spawn(1, entry, x, y, z, mapId)]);
        var system = new CreatureMapSystem(map, content, random: new Random(1));
        map.AddUpdater(system);
        var session = new FakeSession(1);
        world.AddPlayer(TestWorld.CreatePlayer(1, x, y, session, mapId));
        world.RunTick(0);
        Run(world, 3_000);

        Assert.Single(system.Creatures);
        Assert.DoesNotContain(capture.Notices, n => n.Key == "creature/under-map");
    }

    [RealTerrainTheory]
    [MemberData(nameof(NotUnderTheMap))]
    public void APlayerInACaveOrACity_IsNotReportedUnderTheMap(string name, uint mapId, float x, float y, float z)
    {
        using WorldRuntime world = RealWorld(mapId, x, y);
        Map map = world.GetMap(mapId);
        float terrain = map.GetHeight(x, y, z);
        output.WriteLine($"{name}: z {z}, terrain {terrain}, depth {terrain - z}");

        var session = new FakeSession(77);
        Player player = TestWorld.CreatePlayer(77, x, y, session, mapId);
        world.AddPlayer(player);
        world.RunTick(0);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "move/under-map" && n.Subject.Contains("(guid 77)", StringComparison.Ordinal));

        MovementInfo previous = player.Movement;
        var incoming = new MovementInfo { Flags = MovementFlags.None, Time = 1, X = x, Y = y, Z = z };
        var context = new MovementObserverContext(player, world, WorldOpcode.MsgMoveHeartbeat);
        MovementObservers.Before(context, in previous, ref incoming);
        player.ApplyClientMovement(incoming, 1);
        MovementObservers.After(context, in previous);

        Assert.Empty(capture.Notices);
    }
}
