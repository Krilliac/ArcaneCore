using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Dungeon;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotStonardHealerTerrainTests : RealTerrainNavigationTestBase
{
    public PlayerbotStonardHealerTerrainTests(ITestOutputHelper output) : base(output) { }

    // The Stonard spirit healer (creature 6491, spawn 40523) and the live ghost of Deeleibreind under it (w25, 2026-10-11 13:37-13:40).
    private static readonly Vector3 StonardHealer = new(-10575.0f, -3377.0f, 22.344f);
    private static readonly Vector3 UnderTheStonardBluff = new(-10576.8f, -3370.7f, 4.5f);

    // Deeleibreind's body, in the lake south-west of Stonard (Blasted Lands by the terrain's area cells).
    private static readonly Vector3 DeeleibreindBody = new(-10726.8f, -3375.4f, -11.0f);

    /// <summary>
    /// Live w25 (2026-10-11 13:36-13:40, Deeleibreind, level-40 undead): its body lay in the lake below Stonard among creatures too
    /// strong for it; it took the spirit healer, and its ghost stood in the basin under the Stonard bluff, 18 yards below the healer
    /// that stands on top (6.6 yards apart on the map). The healer's server range is 5 yards in 3D (vmangos GetNPCIfCanInteractWith,
    /// INTERACTION_DISTANCE), so it could not be activated from there. The navigation mesh joins the basin to the bluff only by a
    /// 1,010-yard road round it; the near search (vmangos' 2048 nodes) answered a partial route ending right under the healer, the
    /// walk stopped closing, and the fallback faulted ("its walk stopped closing for 11 s; goal healer 6491, 19.0 yd away, route
    /// none") three times until the bot was disabled. Now the stalled walk plans the long way once, with the largest search budget,
    /// and the ghost walks it, the healer out of its sight for most of the way, until the healer is in its reach.
    /// </summary>
    [RealTerrainBotFact]
    public async Task AGhostUnderTheStonardBluff_TakesTheLongWayUp_ToTheSpiritHealer()
    {
        var clock = new RevivalClock(1_000);
        await using Terrain terrain = await Terrain.StartAsync(withFactions: true);
        await terrain.Host.OnWorldAsync(() => DeathHooks.Register(terrain.Host.World, new DeathHooks(new DeathOptions(), clock)));
        await terrain.PlaceAsync(DeeleibreindBody);
        await terrain.KillAndReleaseAsync();
        await terrain.PlaceAsync(UnderTheStonardBluff);
        Creature healer = await terrain.Host.OnWorldAsync(() => DungeonBotHost.AddCreature(terrain.Player.Map!, 994700u,
            StonardHealer.X, StonardHealer.Y, StonardHealer.Z, (uint)NpcFlags.SpiritHealer, terrain.Host.World.NowMs, faction: 1));
        await terrain.WaitAsync(() => terrain.Player.VisibleObjects.Contains(healer.Guid), "the ghost sees the healer");
        clock.Seconds += 125;

        var recovery = new PlayerbotRecovery(terrain.Session, new PlayerbotOptions { Enabled = true });
        await terrain.Host.OnWorldAsync(recovery.TakeSpiritHealer); // the live bot's body was judged unsafe: the healer is taken
        string? fault = null;
        bool detoured = false, inReach = false;
        var trail = new List<string>();
        for (int think = 0; think < 600 && fault is null && !inReach; think++)
        {
            (fault, inReach) = await terrain.Host.OnWorldAsync<(string?, bool)>(() =>
            {
                PlayerbotMovementControl.Update(terrain.Session, terrain.Player);
                terrain.Session.ManagedBudget = new ManagedActionBudget(4);
                try { recovery.Update(terrain.Player, 500); }
                catch (InvalidOperationException e) { return (e.Message, false); }
                detoured |= recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.DetourToHealer;
                string step = $"{recovery.LastStep}.{recovery.LastSpiritHealerStep}";
                if (trail.Count == 0 || !trail[^1].StartsWith(step + " ", StringComparison.Ordinal))
                    trail.Add($"{step} ({terrain.Player.X:F0},{terrain.Player.Y:F0},{terrain.Player.Z:F0})");
                return (null, recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.Activate);
            });
            for (int tick = 0; tick < 10 && fault is null && !inReach; tick++)
            {
                await terrain.Host.World.AdvanceClockAsync(50);
                await terrain.Host.OnWorldAsync(() => PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs));
            }
        }

        string path = string.Join(" -> ", trail);
        Assert.True(fault is null, $"the recovery faulted ({fault}): {path}");
        Assert.True(detoured, "the long way round was never planned: " + path);
        Assert.True(inReach, "the ghost never reached the healer: " + path);
        await terrain.Host.OnWorldAsync(() => Assert.True(
            Vector3.Distance(new(terrain.Player.X, terrain.Player.Y, terrain.Player.Z), StonardHealer) <= PlayerbotRecovery.ServerReachProbeYards,
            $"activating {Vector3.Distance(new(terrain.Player.X, terrain.Player.Y, terrain.Player.Z), StonardHealer):F1} yards from the healer: {path}"));
    }
}
