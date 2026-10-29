using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Dungeon;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The kill observer's progress-hit count (<see cref="PlayerbotKillObserver.ProgressHitsBy"/>): a hit counts only when it takes a
/// victim's health to a new low for that victim.
/// </summary>
public sealed class PlayerbotKillObserverProgressHitsTests
{
    private const uint Entry = 1000;

    private static ObjectGuid Victim(uint counter) => ObjectGuid.WithEntry(HighGuid.Unit, Entry, counter);

    [Fact]
    public void Note_NewVictim_Counts()
    {
        var hits = new PlayerbotKillObserver.HitProgress();
        hits.Note(Victim(1), 500);
        Assert.Equal(1UL, hits.Count);
        hits.Note(Victim(2), 500); // a second victim at the same health is also new
        Assert.Equal(2UL, hits.Count);
    }

    [Fact]
    public void Note_HitThatDoesNotGoBelowTheRecordedLow_DoesNotCount()
    {
        var hits = new PlayerbotKillObserver.HitProgress();
        hits.Note(Victim(1), 500);
        hits.Note(Victim(1), 500); // equal: pinned at a threshold
        hits.Note(Victim(1), 800); // healed back up
        Assert.Equal(1UL, hits.Count);
    }

    [Fact]
    public void Note_LowerHealth_CountsAndBecomesTheNewLow()
    {
        var hits = new PlayerbotKillObserver.HitProgress();
        hits.Note(Victim(1), 500);
        hits.Note(Victim(1), 499);
        Assert.Equal(2UL, hits.Count);
        hits.Note(Victim(1), 499); // the lower value was recorded, so repeating it is not a new low
        hits.Note(Victim(1), 500);
        Assert.Equal(2UL, hits.Count);
    }

    [Fact]
    public void Note_LethalHitRecordsZeroAndKeepsTheEntry_SoASurvivedVictimIsNotCountedAgain()
    {
        var hits = new PlayerbotKillObserver.HitProgress();
        hits.Note(Victim(1), 0);
        hits.Note(Victim(1), 0);
        Assert.Equal(1UL, hits.Count);
    }

    [Fact]
    public void Forget_MakesTheNextHitOnThatVictimCountAgain()
    {
        var hits = new PlayerbotKillObserver.HitProgress();
        hits.Note(Victim(1), 100);
        hits.Note(Victim(2), 100);
        hits.Forget(Victim(1));
        hits.Note(Victim(1), 100); // forgotten: new again
        hits.Note(Victim(2), 100); // untouched: still not a new low
        hits.Forget(Victim(3)); // unknown victim: no effect
        Assert.Equal(3UL, hits.Count);
    }

    [Fact]
    public void Note_MoreThanMaxVictims_ClearsMemoryWhenTheNextNewVictimArrives()
    {
        var hits = new PlayerbotKillObserver.HitProgress();
        for (uint i = 1; i <= PlayerbotKillObserver.HitProgress.MaxVictims; i++) hits.Note(Victim(i), 100);
        Assert.Equal((ulong)PlayerbotKillObserver.HitProgress.MaxVictims, hits.Count);

        // Exactly MaxVictims remembered: a repeat on a remembered victim does not clear anything and does not count.
        hits.Note(Victim(1), 100);
        Assert.Equal((ulong)PlayerbotKillObserver.HitProgress.MaxVictims, hits.Count);

        // One more distinct victim: it counts, and the memory is cleared before it is recorded (the known, documented limit).
        uint extra = PlayerbotKillObserver.HitProgress.MaxVictims + 1;
        hits.Note(Victim(extra), 100);
        Assert.Equal((ulong)PlayerbotKillObserver.HitProgress.MaxVictims + 1, hits.Count);

        // The newest victim survived the clear: not counted again.
        hits.Note(Victim(extra), 100);
        Assert.Equal((ulong)PlayerbotKillObserver.HitProgress.MaxVictims + 1, hits.Count);

        // An older victim was forgotten: the same hit counts again.
        hits.Note(Victim(1), 100);
        Assert.Equal((ulong)PlayerbotKillObserver.HitProgress.MaxVictims + 2, hits.Count);
    }

    /// <summary>
    /// Through the real path: a creature held at an invincibility threshold (a scripted surrender) takes hits that go nowhere.
    /// Health 1000, threshold 900. MapCombat.DealDamage lowers health by min(health - threshold, damage) while health is above the
    /// threshold: 50 -> 950 (new low, counts), 50 -> 900 (new low, counts), 50 -> 900 (pinned, after == recorded low, no count),
    /// then 200 twice -> 900 (pinned). So exactly two of the five hits count.
    /// </summary>
    [Fact]
    public async Task ProgressHitsBy_VictimHeldAtInvincibilityThreshold_CountsOnlyTheHitsThatLowerHealth()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, StallSeconds = 120 });
        try
        {
            // The first think registers the bot with the observer (UpdateCore -> WatchKills).
            await host.World.AdvanceClockAsync(500);
            Creature target = await host.OnWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                Player player = session.Player!;
                Creature creature = DungeonBotHost.AddCreature(player.Map!, 995_400, player.X + 3f, player.Y, player.Z, 0, host.World.NowMs, 35);
                creature.MaxHealth = 1000;
                creature.Health = 1000;
                creature.InvincibilityHpThreshold = 900;
                return creature;
            });

            (ulong before, ulong after, uint health) = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                MapCombat combat = player.Map!.Combat;
                PlayerbotKillObserver observer = PlayerbotKillObserver.For(combat);
                ulong start = observer.ProgressHitsBy(player);
                foreach (uint damage in new uint[] { 50, 50, 50, 200, 200 }) combat.DealDamage(player, target, damage, startsCombat: false);
                return (start, observer.ProgressHitsBy(player), target.Health);
            });

            Assert.Equal(900u, health);
            Assert.Equal(before + 2, after);
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
