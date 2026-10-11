using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The real terrain behind the live spirit-healer fault of 2026-10-11 (Grogeil, w23/w24): its body lay in the Barrens at
/// (1437.1, -2804.0, 219.8) on Kalimdor, and its ghost stood, each time the fallback faulted, 120 to 180 yards west at
/// (1255.6..1318.3, -2764), on the hills where the terrain's area cells hold no area (0xFFFF: zone 0, area 0), so no graveyard is
/// linked there (vmangos ObjectMgr::GetClosestGraveYard returns null for an area and zone with no link). See
/// PlayerbotUnsafeBodyRecoveryTests.AGhostWalkingToItsGraveyardThroughCellsWithoutAnArea_KeepsItsGoal_AndReachesTheHealer.
/// </summary>
public sealed class PlayerbotGhostAreaTerrainTests
{
    private const uint TheBarrens = 17;

    [RealAreaTerrainFact]
    public void RealTerrain_TheBodyIsInTheBarrens_AndTheGhostsSpotsHaveNoArea()
    {
        string dbc = Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR")!;
        string root = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!;
        var terrain = new TerrainManager(root)
        {
            Areas = new AreaTable(AreaTableDbcReader.Read(Path.Combine(dbc, "AreaTable.dbc")).Select(r => new AreaTemplate(
                r.Entry, r.MapId, r.ZoneId, r.ExploreFlag, r.Flags, r.AreaLevel, r.Name, r.Team, r.LiquidTypeId)), MapRegistry.Default),
        };
        TerrainInfo kalimdor = terrain.For(1);

        Assert.Equal((TheBarrens, TheBarrens), kalimdor.GetZoneAndAreaId(1437.1f, -2804.0f, 219.8f));
        foreach ((float x, float y, float z) in new[] { (1255.6f, -2764.4f, 206.6f), (1289.6f, -2764.8f, 211.1f), (1291.8f, -2764.4f, 211.2f), (1318.3f, -2762.9f, 211.3f) })
        {
            Assert.Equal(ushort.MaxValue, kalimdor.GetAreaFlag(x, y, z));
            Assert.Equal((0u, 0u), kalimdor.GetZoneAndAreaId(x, y, z));
        }
    }
}
