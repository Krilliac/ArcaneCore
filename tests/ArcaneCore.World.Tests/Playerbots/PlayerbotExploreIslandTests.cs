using System.Numerics;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed partial class PlayerbotRealTerrainNavigationTests
{
    [RealTerrainBotFact]
    public async Task LowerStormwindLedge_IsAnIsolatedMeshPatch()
    {
        // The exploring bot descended here from the #207 ridge. All twelve paths toward
        // 60-, 120- and 200-yard probes were incomplete, ending at most 50 yards away.
        Vector3 at = new(-8497.907f, 510.1643f, 116.99477f);
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(at, 0);
        Assert.True(await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.IsOnMeshIsland(terrain.Player, new PlayerbotOptions())));
    }

    [RealTerrainBotTheory]
    [InlineData(-10038.9f, 649.6f, 38.2f)] // #221, Elwynn
    [InlineData(-10233.5f, 1362.6f, 40.3f)] // #180, outside the Molsen wall
    [InlineData(-10628.1f, 417.2f, 34.9f)] // #130, Duskwood
    [InlineData(-10606.8f, 294.0f, 31.8f)] // #131, Duskwood
    public async Task ConnectedExploreGround_DoesNotTriggerAnIslandTeleport(float x, float y, float z)
    {
        var start = new Vector3(x, y, z);
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(start, 0);
        var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true });
        await terrain.Host.OnWorldAsync(() =>
        {
            terrain.Player.Home = new HomeBind(0, 40, -6240.3f, 331f, 382.8f);
            Assert.False(PlayerbotNavigation.IsOnMeshIsland(terrain.Player, new PlayerbotOptions()));
        });

        for (int tick = 0; tick < 300; tick++)
        {
            await terrain.Host.OnWorldAsync(() =>
            {
                terrain.Session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(100);
                PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs);
            });
            await terrain.Host.World.AdvanceClockAsync(100);
        }

        Vector3 after = await terrain.Host.OnWorldAsync(() => new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
        Assert.True(Vector2.Distance(new(after.X, after.Y), new(start.X, start.Y)) < 300f,
            $"connected ground at {start} unexpectedly teleported the bot to {after}");
        brain.Stop();
    }

    [RealTerrainBotTheory]
    [InlineData(-10038.9f, 649.6f, 38.2f)] // #221, Elwynn
    [InlineData(-10233.5f, 1362.6f, 40.3f)] // #180, outside the Molsen wall
    public async Task ConnectedGround_DwellOver15Seconds_RunsABoundedCountedIslandCheck_AndDoesNotTeleport(float x, float y, float z)
    {
        var start = new Vector3(x, y, z);
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(start, 0);
        var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true });
        await terrain.Host.OnWorldAsync(() => terrain.Player.Home = new HomeBind(0, 40, -6240.3f, 331f, 382.8f));

        // The bot stays put (an empty action budget starts no movement) for 20 seconds, so the 15-second confinement watch fires.
        var probe = new PlayerbotThinkProbe();
        int maxQueries = 0;
        var perTick = new List<int>();
        for (int tick = 0; tick < 200; tick++)
        {
            await terrain.Host.OnWorldAsync(() =>
            {
                terrain.Session.ManagedBudget = new ManagedActionBudget(0);
                probe.Begin();
                brain.Update(100);
                probe.End();
                maxQueries = Math.Max(maxQueries, probe.Queries);
                perTick.Add(probe.Queries);
            });
            await terrain.Host.World.AdvanceClockAsync(100);
        }

        Vector3 after = await terrain.Host.OnWorldAsync(() => new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
        Assert.True(Vector2.Distance(new(after.X, after.Y), new(start.X, start.Y)) < PlayerbotNavigation.IslandYards,
            $"the dwelling bot moved to {after}: it must stay inside the confinement radius for the check to fire");
        // A think plans one route query; the think that runs the island check (connected ground: its first probe proves it) plans none
        // of its own, so no think counts more than one. Were the check additive to the plan, that think would count two.
        Assert.True(maxQueries == 1, $"the busiest think made {maxQueries} counted queries: {string.Join(',', perTick)}");
        int checkQueries = await terrain.Host.OnWorldAsync(() =>
        {
            probe.Begin();
            Assert.False(PlayerbotNavigation.IsOnMeshIsland(terrain.Player, new PlayerbotOptions()));
            probe.End();
            return probe.Queries;
        });
        Assert.True(checkQueries is >= 1 and <= PlayerbotNavigation.IslandDirections, $"the island check counted {checkQueries} queries");
        brain.Stop();
    }

    // Bot QA #207's upper Stormwind ridge is a small mesh island. The prior probe measured
    // routes ending within ten yards and watched the bot remain there until the 120-second stall bound.
    [RealTerrainBotTheory]
    [InlineData(-8489.9f, 488.5f, 150f)] // upper ridge from the original probe
    [InlineData(-8485.9f, 509.8f, 121.4f)] // w27 bind escape log
    public async Task StormwindRidge_ExploringBotEscapesTheIslandBeforeTheStallBound(float x, float y, float z)
    {
        var ridge = new Vector3(x, y, z);
        var bind = new Vector3(-10628.3f, 1036.7f, 34.1f);
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(ridge, 0);
        var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true });
        await terrain.Host.OnWorldAsync(() =>
        {
            terrain.Player.Home = new HomeBind(0, 40, bind.X, bind.Y, bind.Z);
            Assert.True(PlayerbotNavigation.IsOnMeshIsland(terrain.Player, new PlayerbotOptions()));
        });

        for (int tick = 0; tick < 300; tick++)
        {
            await terrain.Host.OnWorldAsync(() =>
            {
                terrain.Session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(100);
                PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs);
            });
            await terrain.Host.World.AdvanceClockAsync(100);
        }

        Vector3 after = await terrain.Host.OnWorldAsync(() => new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
        bool stillIsland = await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.IsOnMeshIsland(terrain.Player, new PlayerbotOptions()));
        // It may already have resumed exploring from the bind by the last tick. A bot cannot
        // walk the more than two thousand yards between these places in thirty seconds.
        Assert.True(Vector2.Distance(new(after.X, after.Y), new(ridge.X, ridge.Y)) > 1_000f,
            $"still on the Stormwind ridge at {after} after 30 seconds: goal {brain.Goal}, island {stillIsland}");
        brain.Stop();
    }
}
