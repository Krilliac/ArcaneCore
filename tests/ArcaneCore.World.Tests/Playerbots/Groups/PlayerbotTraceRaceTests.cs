using System.Diagnostics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// <see cref="GroupTestWorld"/> failure traces read world state (a player's attacker set, the coordinator's event list) that the world
/// thread keeps changing; building one on the test thread throws "Collection was modified" and hides the real assertion.
/// </summary>
public sealed class PlayerbotTraceRaceTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    [Fact]
    public async Task TraceBuiltWhileTheWorldRuns_NeverThrows_OverManyIterations()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var tank = await world.AddBotAsync("Racetank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Raceheal", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        var mage = await world.AddBotAsync("Racemage", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(180_000, () => world.Player(tank.Id).Combat.Victim is Creature { Entry: OgreEntry }), await world.TraceAsync());

        // The fight goes on (the coordinator notes events) and, on every tick, the tank's attacker set changes through the real combat API,
        // while the test thread builds traces. The trace count, not the game-time budget, ends the loop: a fixed budget runs as fast as the
        // CPU allows, so how many round trips fit in it depends on machine speed. Whenever an advance ends, a fresh one is started.
        const int Target = 300;
        const int AdvanceMs = 240_000;
        bool attacking = false;
        int toggles = 0, built = 0, interleaved = 0, lastSeen = -1, restarts = 0;
        Func<bool> flip = () =>
        {
            Player victim = world.Player(tank.Id);
            foreach (Player attacker in new[] { world.Player(healer.Id), world.Player(mage.Id) })
            {
                if (attacking) victim.Map!.Combat.AttackStop(attacker);
                else victim.Map!.Combat.Attack(attacker, victim);
            }

            attacking = !attacking;
            toggles++;
            return Volatile.Read(ref built) >= Target;
        };
        Task<bool> running = world.RunUntilAsync(AdvanceMs, flip);
        Stopwatch wall = Stopwatch.StartNew();
        while (Volatile.Read(ref built) < Target && wall.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (running.IsCompleted)
            {
                await running; // surfaces a fault
                restarts++;
                running = world.RunUntilAsync(AdvanceMs, flip);
            }

            var (trace, seen) = await world.OnWorldAsync(() => (world.Trace(), toggles));
            Assert.False(string.IsNullOrEmpty(trace));
            if (seen != lastSeen) interleaved++;
            lastSeen = seen;
            Interlocked.Increment(ref built);
        }

        // Fails only if the real-time cap was hit, i.e. the world is stuck.
        Assert.Equal(Target, built);
        // A tick drains the whole command queue, so a trace posted while the previous tick is still draining can share its tick; the
        // property that matters is that traces were mostly built in different ticks, between flips of the attacker set. That holds on
        // any machine speed (the old count only stood in for it through wall-clock speed); with no advance running it would be 0.
        Assert.True(interleaved >= Target / 4, $"only {interleaved} of {built} traces saw the attacker set change ({toggles} flips, {restarts} advance restarts)");
        await running;
    }
}
