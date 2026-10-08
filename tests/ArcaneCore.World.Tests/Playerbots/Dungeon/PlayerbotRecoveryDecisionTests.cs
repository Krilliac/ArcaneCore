using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Xunit;
using Step = ArcaneCore.World.Playerbots.PlayerbotRecoveryStep;
using Place = ArcaneCore.World.Playerbots.PlayerbotCorpsePlace;
using Healer = ArcaneCore.World.Playerbots.PlayerbotSpiritHealerStep;

namespace ArcaneCore.World.Tests.Playerbots.Dungeon;

/// <summary>
/// The recovery decision tree (<see cref="PlayerbotRecovery.Decide"/>, <see cref="PlayerbotRecovery.DecideSpiritHealer"/>) and its
/// world behaviour: a body on this map is walked to; a body in a dungeon is reached through the entrance trigger; a hostile near
/// the body means waiting; a stalled run takes the spirit healer and nothing throws until that fails as well.
/// </summary>
public sealed class PlayerbotRecoveryDecisionTests
{
    public static TheoryData<bool, string, bool, bool, long, bool, bool, bool, string> Tree => new()
    {
        // ghost, corpse, entrance known, at corpse, reclaim wait, hostile, stalled, fallback taken -> step
        { false, "ThisMap", false, false, 0, false, false, false, "Release" },
        { false, "ThisMap", false, false, 0, false, true, false, "Fault" },
        { true, "ThisMap", false, false, 0, false, false, false, "WalkToCorpse" },
        { true, "ThisMap", false, true, 30, false, false, false, "WaitForReclaimDelay" },
        { true, "ThisMap", false, true, 30, true, false, false, "WaitForReclaimDelay" },
        { true, "ThisMap", false, true, 0, true, false, false, "WaitForHostiles" },
        { true, "ThisMap", false, true, 0, false, false, false, "Reclaim" },
        { true, "OtherMap", true, false, 0, false, false, false, "WalkToEntrance" },
        { true, "OtherMap", false, false, 0, false, false, false, "SpiritHealer" },
        { true, "None", false, false, 0, false, false, false, "SpiritHealer" },
        { true, "ThisMap", false, false, 0, false, true, false, "SpiritHealer" },
        { true, "ThisMap", false, true, 0, true, true, false, "SpiritHealer" },
        { true, "OtherMap", true, false, 0, false, true, false, "SpiritHealer" },
        { true, "ThisMap", false, true, 0, false, false, true, "SpiritHealer" },
    };

    [Theory]
    [MemberData(nameof(Tree))]
    public void Decide(bool ghost, string corpse, bool entrance, bool atCorpse, long wait, bool hostile, bool stalled, bool fallback, string expected)
        => Assert.Equal(Enum.Parse<Step>(expected),
            PlayerbotRecovery.Decide(ghost, Enum.Parse<Place>(corpse), entrance, atCorpse, wait, hostile, stalled, fallback));

    [Theory]
    [InlineData(true, true, false, false, "Activate")]
    [InlineData(true, false, false, false, "WalkToHealer")]
    [InlineData(true, false, true, false, "WalkToHealer")]
    [InlineData(false, false, true, false, "WalkToGraveyard")]
    [InlineData(false, false, false, false, "Fault")]
    [InlineData(true, false, true, true, "Fault")]
    public void DecideSpiritHealer(bool healer, bool inReach, bool graveyard, bool stalled, string expected)
        => Assert.Equal(Enum.Parse<Healer>(expected), PlayerbotRecovery.DecideSpiritHealer(healer, inReach, graveyard, stalled));

    /// <summary>A ghost whose body lies on its own map walks to it (the ordinary corpse run).</summary>
    [Fact]
    public async Task ABodyOnThisMap_IsWalkedTo()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.TeleportAsync(1, 100f, 100f, DeadminesTestContent.OutsideFloor);
        await host.KillAndReleaseAsync();
        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        await host.OnWorldAsync(() =>
        {
            Corpse corpse = host.Player.Combat.Corpse!;
            corpse.SetPosition(host.Player.X + 60, host.Player.Y, host.Player.Z, 0);
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(recovery.Update(host.Player, 500));
            Assert.Equal(Step.WalkToCorpse, recovery.LastStep);
            Assert.True(host.Player.Movement.HasFlag(MovementFlags.Forward));
        });
    }

    /// <summary>
    /// A bot killed in The Deadmines is released at the graveyard outside; its recovery walks to entrance trigger 78 (the trigger
    /// whose teleport leads to the body's map), walks in as a ghost and the server revives it at the entrance inside. Before, a body
    /// on another map made the recovery wait without doing anything until it threw "playerbot-recovery-stalled".
    /// </summary>
    [Fact]
    public async Task ABodyInADungeon_IsReachedThroughItsEntrance_AndTheGhostIsRevivedInside()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.OnWorldAsync(() => host.Player.Level = 10);
        await host.TeleportAsync(DungeonEntryScenario.Deadmines, -16.4f, -383.07f, 61.78f, 1.9f);
        await host.KillAndReleaseAsync(p => p.MapId == 0);
        AreaTriggerTemplate entrance = await host.OnWorldAsync(() => WorldMaps.Of(host.Host.World).FindAreaTrigger(DungeonEntryScenario.EntranceTrigger)!);
        await host.OnWorldAsync(() =>
        {
            Assert.Equal(DeadminesTestContent.Graveyard.X, host.Player.X, 1);
            Assert.Equal(DungeonEntryScenario.Deadmines, host.Player.Combat.Corpse!.MapId);
        });

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        float first = await host.OnWorldAsync(() =>
        {
            Assert.Same(entrance, recovery.FindEntrance(host.Player, DungeonEntryScenario.Deadmines));
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(recovery.Update(host.Player, 500));
            Assert.Equal(Step.WalkToEntrance, recovery.LastStep);
            Assert.Equal(new Vector3(entrance.X, entrance.Y, entrance.Z).X, recovery.RouteEnd!.Value.X, 1);
            return Vector2.Distance(new(host.Player.X, host.Player.Y), new(entrance.X, entrance.Y));
        });

        bool revived = false;
        for (int think = 0; think < 200 && !revived; think++)
        {
            await host.AdvanceAsync(500);
            revived = await host.OnWorldAsync(() =>
            {
                if (PlayerbotMovementControl.Update(host.Session, host.Player)) return false;
                if (host.Player.IsAlive) return true;
                PlayerbotMotion.Pump(host.Session, host.Player, host.Host.World.NowMs);
                host.Session.ManagedBudget = new ManagedActionBudget(4);
                recovery.Update(host.Player, 500);
                Assert.False(recovery.UsingSpiritHealer, "the ghost gave up on its body");
                return false;
            });
        }

        Assert.True(revived, "the ghost was not revived");
        await host.AcknowledgeUntilAsync(p => p.MapId == DungeonEntryScenario.Deadmines, "the ghost lands inside");
        await host.OnWorldAsync(() =>
        {
            Assert.True(host.Player.IsAlive);
            Assert.Null(host.Player.Combat.Corpse);
            Assert.True(Vector2.Distance(new(host.Player.X, host.Player.Y), new(-16.4f, -383.07f)) < 1f);
        });
        Assert.True(first > 20f, $"started {first} yards from the entrance");
    }

    /// <summary>
    /// At its body, after the reclaim delay, a ghost does not reclaim while a hostile creature stands near the body; once it has left,
    /// the ghost reclaims (mangoszero ReviveFromCorpseAction).
    /// </summary>
    [Fact]
    public async Task AHostileNearTheBody_MeansWaiting_ThenTheGhostReclaimsWhenItLeaves()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.OnWorldAsync(() => DeathHooks.Register(host.Host.World, new DeathHooks(new DeathOptions(), clock)));
        await host.KillAndReleaseAsync();
        Creature wolf = await host.OnWorldAsync(() => DungeonBotHost.AddCreature(host.Player.Map!, 994301,
            host.Player.X + 10, host.Player.Y, host.Player.Z, 0, host.Host.World.NowMs, faction: 14)); // a monster
        await host.AcknowledgeUntilAsync(p => p.VisibleObjects.Contains(wolf.Guid), "the ghost sees the creature");
        clock.Seconds += 31;

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        await host.OnWorldAsync(() =>
        {
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.False(recovery.Update(host.Player, 500));
            Assert.Equal(Step.WaitForHostiles, recovery.LastStep);
            Assert.Equal(4, host.Session.ManagedBudget.Remaining);
            Assert.False(host.Player.IsAlive);

            wolf.Relocate(host.Player.X + 40, host.Player.Y, host.Player.Z, 0, host.Host.World.NowMs);
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(recovery.Update(host.Player, 500));
            Assert.Equal(Step.Reclaim, recovery.LastStep);
            Assert.True(host.Player.IsAlive);
        });
    }

    /// <summary>
    /// The hostile check is made where the ghost stands, because CMSG_RECLAIM_CORPSE revives the player there (vmangos
    /// MiscHandler.cpp:599, no relocation), and the walk stops as soon as the body is in reclaim range (about 39 yards). With the body
    /// 30 yards away: a creature 15 yards from the ghost (45 from the body) means waiting; one beside the body (35 yards from the
    /// ghost) does not. Before, both were measured from the body, the other way round.
    /// </summary>
    [Fact]
    public async Task TheHostileCheck_IsMadeAroundTheGhost_WhereTheReclaimRevivesIt()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.OnWorldAsync(() => DeathHooks.Register(host.Host.World, new DeathHooks(new DeathOptions(), clock)));
        await host.KillAndReleaseAsync();
        Vector3 ghost = await host.OnWorldAsync(() =>
        {
            Corpse corpse = host.Player.Combat.Corpse!;
            corpse.SetPosition(host.Player.X + 30, host.Player.Y, host.Player.Z, 0);
            return new Vector3(host.Player.X, host.Player.Y, host.Player.Z);
        });
        Creature wolf = await host.OnWorldAsync(() => DungeonBotHost.AddCreature(host.Player.Map!, 994302,
            ghost.X - 15, ghost.Y, ghost.Z, 0, host.Host.World.NowMs, faction: 14)); // a monster behind the ghost
        await host.AcknowledgeUntilAsync(p => p.VisibleObjects.Contains(wolf.Guid), "the ghost sees the creature");
        clock.Seconds += 31;

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        await host.OnWorldAsync(() =>
        {
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.False(recovery.Update(host.Player, 500));
            Assert.Equal(Step.WaitForHostiles, recovery.LastStep);
            Assert.False(host.Player.IsAlive);

            wolf.Relocate(ghost.X + 35, ghost.Y, ghost.Z, 0, host.Host.World.NowMs); // beside the body
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(recovery.Update(host.Player, 500));
            Assert.Equal(Step.Reclaim, recovery.LastStep);
            Assert.True(host.Player.IsAlive);
            Assert.True(Vector2.Distance(new(host.Player.X, host.Player.Y), new(ghost.X, ghost.Y)) < 1f, "revived away from where the ghost stood");
        });
    }

    /// <summary>
    /// A body that was never released (no ghost) and whose recovery counts as stalled faults with <see cref="PlayerbotRecovery.Stalled"/>
    /// (<see cref="Step.Fault"/>); the spirit-healer fallback is for ghosts only. Here the bot walked as a ghost and lost the ghost
    /// flag, so the stuck bound of the walk (<see cref="PlayerbotRecovery.StuckMs"/>) runs out well before the release bound
    /// (<see cref="PlayerbotRecovery.NoProgressMs"/>). Before, the Fault step fell through to the spirit-healer fallback.
    /// </summary>
    [Fact]
    public async Task AStalledBodyThatIsNoGhost_FaultsStalled_AndNeverTakesTheSpiritHealer()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.TeleportAsync(1, 100f, 100f, DeadminesTestContent.OutsideFloor);
        await host.KillAndReleaseAsync();
        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        await host.OnWorldAsync(() =>
        {
            Corpse corpse = host.Player.Combat.Corpse!;
            corpse.SetPosition(host.Player.X + 60, host.Player.Y, host.Player.Z, 0);
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(recovery.Update(host.Player, 500));
            Assert.Equal(Step.WalkToCorpse, recovery.LastStep);
            PlayerbotMovementControl.Stop(host.Session, host.Player);
        });
        await host.AdvanceAsync((uint)PlayerbotRecovery.StuckMs + 1_000);

        InvalidOperationException fault = await host.OnWorldAsync(() =>
        {
            host.Player.Flags &= ~PlayerFlags.Ghost;
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            return Assert.Throws<InvalidOperationException>(() => recovery.Update(host.Player, 500));
        });

        Assert.Equal(PlayerbotRecovery.Stalled, fault.Message);
        Assert.Equal(Step.Fault, recovery.LastStep);
        Assert.False(recovery.UsingSpiritHealer);
    }

    /// <summary>
    /// A corpse run that cannot get anywhere (the body lies far below the ground) is given up after <see cref="PlayerbotRecovery.StuckMs"/>
    /// for the spirit healer in sight: the ghost walks to it and activates it through the ordinary handler. Nothing throws. Before,
    /// the stuck walk threw "playerbot-recovery-stuck" and the fault quarantined the bot.
    /// </summary>
    [Fact]
    public async Task AStalledCorpseRun_TakesTheSpiritHealer_AndNothingThrows()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.TeleportAsync(0, DeadminesTestContent.Graveyard.X - 25f, DeadminesTestContent.Graveyard.Y, DeadminesTestContent.OutsideFloor);
        await host.KillAndReleaseAsync();
        ObjectGuid healer = await host.OnWorldAsync(() =>
        {
            Corpse corpse = host.Player.Combat.Corpse!;
            corpse.SetPosition(corpse.X, corpse.Y, corpse.Z - 500f, 0);
            return host.Player.VisibleObjects.Select(g => host.Player.Map!.FindObject(g)).OfType<Creature>()
                .Single(c => (c.NpcFlags & (uint)NpcFlags.SpiritHealer) != 0).Guid;
        });

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        bool alive = false;
        long stuckAt = -1;
        var trail = new List<string>(); // the steps taken, for a failure message
        for (int think = 0; think < 200 && !alive; think++)
        {
            alive = await host.OnWorldAsync(() =>
            {
                PlayerbotMovementControl.Update(host.Session, host.Player);
                if (host.Player.IsAlive) return true;
                PlayerbotMotion.Pump(host.Session, host.Player, host.Host.World.NowMs);
                host.Session.ManagedBudget = new ManagedActionBudget(4);
                try { recovery.Update(host.Player, 500); }
                catch (InvalidOperationException error) { trail.Add(error.Message); throw new InvalidOperationException(string.Join(" -> ", trail), error); }
                string step = $"{recovery.LastStep}/{recovery.LastSpiritHealerStep} at ({host.Player.X:F1}, {host.Player.Y:F1}, {host.Player.Z:F1})";
                if (trail.Count == 0 || !trail[^1].StartsWith($"{recovery.LastStep}/{recovery.LastSpiritHealerStep} ", StringComparison.Ordinal))
                    trail.Add(step);
                if (stuckAt < 0 && recovery.UsingSpiritHealer) stuckAt = (long)host.Host.World.Uptime.TotalMilliseconds;
                return host.Player.IsAlive;
            });
            if (!alive) await host.AdvanceAsync(500);
        }

        Assert.True(alive, "still a ghost: " + string.Join(" -> ", trail));
        Assert.True(stuckAt > 0, "the spirit healer was never chosen");
        await host.OnWorldAsync(() =>
        {
            Creature spirit = (Creature)host.Player.Map!.FindObject(healer)!;
            Assert.True(Vector3.Distance(new(spirit.X, spirit.Y, spirit.Z), new(host.Player.X, host.Player.Y, host.Player.Z)) <= 5f);
            Assert.Null(host.Player.Combat.Corpse);
        });
    }

    /// <summary>
    /// A ghost with no way back and no spirit healer or graveyard anywhere faults — but only once the fallback has failed too, with
    /// its own code; before the stuck bound it keeps trying.
    /// </summary>
    [Fact]
    public async Task WithoutAnySpiritHealer_TheRecoveryFaultsOnlyWhenTheFallbackFails()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        // Kalimdor: no graveyard, no healer, no trigger.
        await host.TeleportAsync(1, 100f, 100f, DeadminesTestContent.OutsideFloor);
        await host.KillAndReleaseAsync();
        await host.OnWorldAsync(() =>
        {
            Corpse corpse = host.Player.Combat.Corpse!;
            corpse.SetPosition(corpse.X, corpse.Y, corpse.Z - 500f, 0);
        });

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        InvalidOperationException? fault = null;
        long faultAt = 0, start = (long)host.Host.World.Uptime.TotalMilliseconds;
        for (int think = 0; think < 60 && fault is null; think++)
        {
            fault = await host.OnWorldAsync(() =>
            {
                host.Session.ManagedBudget = new ManagedActionBudget(4);
                try { recovery.Update(host.Player, 500); return null; }
                catch (InvalidOperationException error) { return error; }
            });
            faultAt = (long)host.Host.World.Uptime.TotalMilliseconds;
            if (fault is null) await host.AdvanceAsync(500);
        }

        Assert.NotNull(fault);
        Assert.Equal(PlayerbotRecovery.SpiritHealerFailed, fault!.Message);
        Assert.True(faultAt - start > PlayerbotRecovery.StuckMs, $"faulted after {faultAt - start} ms");
    }

    private sealed class TestDeathClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }
}
