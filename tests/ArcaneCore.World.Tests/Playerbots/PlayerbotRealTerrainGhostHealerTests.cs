using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Dungeon;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Ghosts that took the spirit healer and were released at its graveyard, a few yards from it, on the real terrain and navigation
/// meshes. Live 2026-10-16 (world.debug.log 19:35-19:46): Bruma at Sentinel Hill (graveyard 4, the healer 15.5 yards away) and
/// Zoglathnae at Ratchet (graveyard 249, the healer 8.4 yards away) "take the spirit healer instead of its body", stand at the
/// graveyard, and ten seconds later "go back to its body ... the spirit healer cannot be reached": the navigation mesh has no path
/// onto the healer's spawn (bot-nav/no-path-connected, 5 searches), so the walk never closes, and the ghost runs back to the body it
/// had just given up (a death loop at Bruma's) instead of being resurrected beside it.
/// </summary>
public sealed partial class PlayerbotRealTerrainNavigationTests
{
    // Westfall, Sentinel Hill: world_safe_locs 4 and its Spirit Healer (creature 6491, spawn 40524); Bruma's body (live log 19:45:55).
    private static readonly Vector3 SentinelHillGraveyard = new(-10546.9f, 1197.24f, 31.7263f);
    private static readonly Vector3 SentinelHillHealer = new(-10559.0f, 1206.9f, 31.5f);
    private static readonly Vector3 BrumaBody = new(-10112.3f, 1164.3f, 36.3f);

    // The Barrens, Ratchet: world_safe_locs 249 and its Spirit Healer (spawn 40569); Zoglathnae's body (live log 19:35:19).
    private static readonly Vector3 RatchetGraveyard = new(-1081.4f, -3478.68f, 63.6066f);
    private static readonly Vector3 RatchetHealer = new(-1073.0f, -3479.0f, 63.0f);
    private static readonly Vector3 ZoglathnaeBody = new(-1142.5f, -3383.2f, 91.5f);

    [RealTerrainBotFact]
    public Task AtSentinelHill_AGhostReleasedBesideTheSpiritHealer_IsResurrectedThere_NotSentBackToItsBody()
        => GhostAtGraveyardTakesTheHealer(map: 0, BrumaBody, SentinelHillGraveyard, 4, SentinelHillHealer);

    [RealTerrainBotFact]
    public Task AtRatchet_AGhostReleasedBesideTheSpiritHealer_IsResurrectedThere_NotSentBackToItsBody()
        => GhostAtGraveyardTakesTheHealer(map: 1, ZoglathnaeBody, RatchetGraveyard, 249, RatchetHealer);

    private async Task GhostAtGraveyardTakesTheHealer(uint map, Vector3 bodyAt, Vector3 graveyardAt, uint graveyardId, Vector3 healerAt)
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(bodyAt, map);
        Vector3 body = await terrain.KillAndReleaseAsync();
        await terrain.PlaceAsync(graveyardAt, map);
        Creature healer = await terrain.Host.OnWorldAsync(() =>
            DungeonBotHost.AddCreature(terrain.Player.Map!, 996491u, healerAt.X, healerAt.Y, healerAt.Z, (uint)NpcFlags.SpiritHealer,
                terrain.Host.World.NowMs));
        await terrain.WaitAsync(() => terrain.Player.VisibleObjects.Contains(healer.Guid), "the ghost sees the spirit healer");
        // As live: the bot was revived at this healer before and stalled there twice as a living bot ("teleports home ... stalled on
        // ground no route leaves" from the healer's spot), so the healer's 10-yard cell is a dead-end pin for it, and every route of
        // this bot that ends there is refused (PlayerbotNavigation.IsDeadEndPin).
        await terrain.Host.OnWorldAsync(() =>
        {
            var watch = new PlayerbotStallWatch { World = terrain.Host.World };
            watch.RecordPinStall(map, healerAt.X, healerAt.Y, terrain.Host.World.NowMs);
            watch.RecordPinStall(map, healerAt.X, healerAt.Y, terrain.Host.World.NowMs);
            Assert.True(watch.IsDeadEndPin(map, healerAt.X, healerAt.Y, terrain.Host.World.NowMs));
            PlayerbotNavigation.SetStallWatch(terrain.Player, watch);
            return true;
        });

        var graveyard = new WorldSafeLoc(graveyardId, map, graveyardAt.X, graveyardAt.Y, graveyardAt.Z, 0f, "graveyard " + graveyardId);
        var recovery = new PlayerbotRecovery(terrain.Session, new PlayerbotOptions { Enabled = true, AllowedMaps = [0, 1] })
        {
            GraveyardLookup = (_, _) => graveyard,
        };
        recovery.TakeSpiritHealer();

        bool resumedCorpseRun = false, activated = false, alive = false;
        float nearestHealer = float.PositiveInfinity, nearestBody = float.PositiveInfinity;
        for (int think = 0; think < 240 && !alive && !resumedCorpseRun; think++)
        {
            alive = await terrain.Host.OnWorldAsync(() =>
            {
                PlayerbotMovementControl.Update(terrain.Session, terrain.Player);
                if (terrain.Player.IsAlive) return true;
                terrain.Session.ManagedBudget = new ManagedActionBudget(4);
                recovery.Update(terrain.Player, 500);
                resumedCorpseRun |= recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.ResumeCorpseRun;
                activated |= recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.Activate;
                var at = new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z);
                nearestHealer = MathF.Min(nearestHealer, Vector3.Distance(at, healerAt));
                nearestBody = MathF.Min(nearestBody, Vector3.Distance(at, body));
                return terrain.Player.IsAlive;
            });
            for (int tick = 0; tick < 10 && !alive; tick++)
            {
                await terrain.Host.World.AdvanceClockAsync(50);
                await terrain.Host.OnWorldAsync(() => PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs));
            }
        }

        string? refusal = await terrain.Host.OnWorldAsync(() => terrain.Player.IsAlive ? null
            : terrain.Host.WorldServices.GetService<QuestNpcFeature>()?.Services.SpiritHealerRefusal(terrain.Player, healer.Guid));
        output.WriteLine($"activation refusal now: {refusal ?? "none"}");
        output.WriteLine($"resumed corpse run {resumedCorpseRun}, activated {activated}, alive {alive}, nearest to healer {nearestHealer:F1} yd, "
            + $"healer relocated {recovery.HealerRelocated}, last step {recovery.LastStep}/{recovery.LastSpiritHealerStep}");
        Assert.False(resumedCorpseRun, $"the ghost went back to its body {Vector3.Distance(graveyardAt, body):F0} yd away instead of the spirit healer "
            + $"{Vector3.Distance(graveyardAt, healerAt):F1} yd away (nearest {nearestHealer:F1} yd)");
        Assert.True(alive, $"the ghost is still dead (activation refusal: {refusal ?? "none"}; last step {recovery.LastSpiritHealerStep})");
        Assert.True(activated, $"the ghost never activated the spirit healer (nearest {nearestHealer:F1} yd; last step {recovery.LastSpiritHealerStep})");
        Assert.True(nearestHealer <= PlayerbotRecovery.SpiritHealerReachYards + 0.5f, $"the ghost came no nearer than {nearestHealer:F1} yd to the healer");
    }
}
