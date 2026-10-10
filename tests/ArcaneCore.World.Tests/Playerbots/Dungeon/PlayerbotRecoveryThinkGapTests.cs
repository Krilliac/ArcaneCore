using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Xunit;
using Step = ArcaneCore.World.Playerbots.PlayerbotRecoveryStep;
using Healer = ArcaneCore.World.Playerbots.PlayerbotSpiritHealerStep;

namespace ArcaneCore.World.Tests.Playerbots.Dungeon;

/// <summary>
/// The recovery's bounds under think starvation. Live 2026-10-10 (200+ bots, a boost storm with world updates of up to 8.7 s and the
/// 8 ms per-tick think cap): Graveweaver's ghost faulted with <see cref="PlayerbotRecovery.SpiritHealerFailed"/> three times within
/// 3.5 minutes, each time at its first think after a gap, and was disabled. The motion runs every tick (ManagedPlayerbotFeature pumps it
/// for every bot), so the ghost kept walking between thinks; the bounds (<see cref="PlayerbotRecovery.StuckMs"/>,
/// <see cref="PlayerbotRecovery.NoProgressMs"/>) were judged in world time alone, at the start of the think, from the closing the
/// previous think had measured. A gap longer than a bound was a stall the bot had no chance to avoid.
/// </summary>
public sealed class PlayerbotRecoveryThinkGapTests
{
    /// <summary>
    /// A ghost on its way to the spirit healer is not thought for longer than <see cref="PlayerbotRecovery.StuckMs"/> while its walk
    /// goes on: the next think must not fault, and the ghost is revived at the healer.
    /// </summary>
    [Fact]
    public async Task AGhostWalkingToTheSpiritHealer_NotThoughtForLongerThanTheStuckBound_DoesNotFault_AndIsRevived()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.TeleportAsync(0, DeadminesTestContent.Graveyard.X - 60f, DeadminesTestContent.Graveyard.Y, DeadminesTestContent.OutsideFloor);
        await host.KillAndReleaseAsync();
        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        recovery.TakeSpiritHealer();

        await ThinkAsync(host, recovery);
        Assert.Equal(Healer.WalkToHealer, recovery.LastSpiritHealerStep);

        // No think for longer than the stuck bound; the motion runs every tick meanwhile.
        await RunWithoutThinkingAsync(host, (uint)PlayerbotRecovery.StuckMs + 1_000);

        bool alive = false;
        for (int think = 0; think < 40 && !alive; think++)
        {
            alive = await ThinkAsync(host, recovery);
            if (!alive) await RunWithoutThinkingAsync(host, 500);
        }

        Assert.True(alive, $"still a ghost: {recovery.LastStep}/{recovery.LastSpiritHealerStep}");
    }

    /// <summary>
    /// A corpse run whose ghost is not thought for longer than <see cref="PlayerbotRecovery.StuckMs"/> while it walks on is not judged
    /// stuck: the ghost keeps its body and does not take the spirit healer.
    /// </summary>
    [Fact]
    public async Task ACorpseRun_NotThoughtForLongerThanTheStuckBound_KeepsItsBody()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.TeleportAsync(0, DeadminesTestContent.Graveyard.X - 300f, DeadminesTestContent.Graveyard.Y, DeadminesTestContent.OutsideFloor);
        await host.KillAndReleaseAsync();
        // The body lies 150 yards from the ghost, far beyond the reclaim radius and well within the ghost's walk.
        await host.OnWorldAsync(() =>
        {
            Corpse corpse = host.Player.Combat.Corpse!;
            corpse.SetPosition(host.Player.X + 150f, host.Player.Y, host.Player.Z, 0);
        });
        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });

        await ThinkAsync(host, recovery);
        Assert.Equal(Step.WalkToCorpse, recovery.LastStep);
        await RunWithoutThinkingAsync(host, (uint)PlayerbotRecovery.StuckMs + 1_000);
        await ThinkAsync(host, recovery);

        Assert.False(recovery.UsingSpiritHealer, $"the ghost gave up its body after a think gap ({recovery.LastStep})");
    }

    /// <summary>
    /// A dead bot that has not released its spirit yet and is not thought for longer than <see cref="PlayerbotRecovery.NoProgressMs"/>
    /// releases at its next think. Before, that think faulted with <see cref="PlayerbotRecovery.Stalled"/> without trying.
    /// </summary>
    [Fact]
    public async Task ADeadBody_NotThoughtForLongerThanTheNoProgressBound_IsReleased_NotFaulted()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.TeleportAsync(0, DeadminesTestContent.Graveyard.X - 60f, DeadminesTestContent.Graveyard.Y, DeadminesTestContent.OutsideFloor);
        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        await host.OnWorldAsync(() =>
        {
            host.Player.Health = 0;
            host.Player.Map!.Combat.KillPlayer(host.Player);
            // The first think finds no budget left (other bots used it): it starts the bound and does nothing.
            host.Session.ManagedBudget = new ManagedActionBudget(0);
            recovery.Update(host.Player, 500);
        });

        await RunWithoutThinkingAsync(host, (uint)PlayerbotRecovery.NoProgressMs + 1_000);
        await ThinkAsync(host, recovery);

        Assert.Equal(Step.Release, recovery.LastStep);
    }

    /// <summary>One think of the recovery with a fresh action budget (world thread); whether the bot is alive afterwards.</summary>
    private static Task<bool> ThinkAsync(DungeonBotHost host, PlayerbotRecovery recovery) => host.OnWorldAsync(() =>
    {
        PlayerbotMovementControl.Update(host.Session, host.Player);
        if (host.Player.IsAlive) return true;
        host.Session.ManagedBudget = new ManagedActionBudget(4);
        recovery.Update(host.Player, 500);
        return host.Player.IsAlive;
    });

    /// <summary>Let <paramref name="milliseconds"/> of world time pass in 50 ms ticks, the motion pumped every tick as the feature does.</summary>
    private static async Task RunWithoutThinkingAsync(DungeonBotHost host, uint milliseconds)
    {
        for (uint spent = 0; spent < milliseconds; spent += 50)
        {
            await host.AdvanceAsync(50);
            await host.OnWorldAsync(() =>
            {
                PlayerbotMovementControl.Update(host.Session, host.Player);
                PlayerbotMotion.Pump(host.Session, host.Player, host.Host.World.NowMs);
            });
        }
    }
}
