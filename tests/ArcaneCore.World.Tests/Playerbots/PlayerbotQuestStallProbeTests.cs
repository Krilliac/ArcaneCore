using System.Numerics;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed partial class PlayerbotRealTerrainNavigationTests
{
    // ClassicDB creature_spawn enders: 6929, 12736, 3453, 6577 respectively. These points came from the
    // Bot-QA stalls and the predecessor's route probe. A planned final position proves a path hint, not a live turn-in.
    public static TheoryData<string, uint, float, float, float, float, float, float, float> QuestStallPoints => new()
    {
        { "#228 Ride to Orgrimmar", 1, 537.3f, 208.5f, 92.9f, 1634f, -4439f, 16f, QuestNpcServices.InteractionDistance },
        // Je'neu's model 12953 has 0.306 yard radius in ClassicDB; the player has 0.389 yard radius.
        { "#218 Trouble in the Deeps", 1, 620.3f, 441.3f, 90.1f, 3355.07f, 1033.14f, 6.03f,
            QuestNpcServices.InteractionDistance + 0.306f + 0.389f },
        { "#186 Wharfmaster Dizzywig", 1, -5433.3f, -3837.9f, -48.7f, -986f, -3797f, 5f,
            QuestNpcServices.InteractionDistance },
        { "#222 Find Bingles", 0, -8800.5f, -494.9f, 101.6f, -5369f, -3747f, 305f,
            QuestNpcServices.InteractionDistance },
    };

    [RealTerrainBotTheory]
    [MemberData(nameof(QuestStallPoints))]
    public async Task QuestStallPath_ReachesAnEnderInteractionPoint(string name, uint map, float x, float y, float z,
        float ex, float ey, float ez, float reach)
    {
        Vector3 ender = new(ex, ey, ez);
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(new Vector3(x, y, z), map);
        await RegionGraphAsync(terrain, map);
        var options = new PlayerbotOptions();
        Vector3 at = new(x, y, z);
        string reason = "leg limit";
        TileWait[] wait = [new TileWait()];
        for (int leg = 0; leg < 40; leg++)
        {
            PlayerbotRoute? route = null;
            Vector3 position = default;
            PlanOutcome outcome = PlanOutcome.Pending;
            while (outcome == PlanOutcome.Pending)
            {
                (outcome, route, position) = await terrain.Host.OnWorldAsync(() =>
                {
                    Vector3 current = new(terrain.Player.X, terrain.Player.Y, terrain.Player.Z);
                    PlanOutcome res = PlayerbotNavigation.PlanToward(terrain.Player, ender, options, ref wait[0], terrain.Host.World.NowMs, out PlayerbotRoute? next);
                    return (res, next, current);
                });
                if (outcome != PlanOutcome.Pending) break;
                await Task.Delay(20);
                await terrain.Host.World.AdvanceClockAsync(100);
            }

            at = position;
            if (Vector3.Distance(at, ender) < reach) break;
            if (route is null) { reason = $"no route at leg {leg}"; break; }
            Vector3 nextPoint = route.Points[^1];
            if (Vector3.Distance(at, nextPoint) < 1f) { reason = $"no progress at leg {leg}"; break; }
            await terrain.PlaceAsync(nextPoint with { Z = nextPoint.Z + 0.5f }, map);
            at = nextPoint with { Z = nextPoint.Z + 0.5f };
        }
        Assert.True(Vector3.Distance(at, ender) < reach,
            $"{name}: {reason}; {Vector3.Distance(at, ender):F2} yards from ender at {at}");
    }
}
