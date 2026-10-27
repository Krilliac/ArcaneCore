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
        using SemaphoreSlim flipped = new(0); // declared before the world so it outlives any advance still running when an assert throws
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
        int toggles = 0, built = 0, lastSeen = -1, restarts = 0;
        Func<bool> flip = () =>
        {
            Player victim = world.Player(tank.Id);
            foreach (Player attacker in new[] { world.Player(healer.Id), world.Player(mage.Id) })
            {
                if (attacking) victim.Map!.Combat.AttackStop(attacker);
                else victim.Map!.Combat.Attack(attacker, victim);
            }

            attacking = !attacking;
            Volatile.Write(ref toggles, toggles + 1);
            flipped.Release(); // lockstep: the test thread requests its next trace only after a flip
            return Volatile.Read(ref built) >= Target;
        };
        Task<bool> running = world.RunUntilAsync(AdvanceMs, flip);
        // Stuck means no trace for stallLimit, not "300 traces took over a minute": how long 300 lockstep round trips take is machine speed,
        // and a loaded run can spend a fixed total budget while every trace still comes through. The watchdog restarts at every trace.
        TimeSpan stallLimit = TimeSpan.FromSeconds(60);
        Stopwatch sinceProgress = Stopwatch.StartNew();
        while (Volatile.Read(ref built) < Target && sinceProgress.Elapsed < stallLimit)
        {
            if (running.IsCompleted)
            {
                await running; // surfaces a fault
                restarts++;
                running = world.RunUntilAsync(AdvanceMs, flip);
            }

            // Lockstep: a trace is requested only once a flip has happened after the previous trace ran. lastSeen is the toggle count read on
            // the world thread inside that trace's command, and the flip runs after the tick's commands, so the next trace runs in a later tick.
            if (Volatile.Read(ref toggles) == lastSeen)
            {
                await flipped.WaitAsync(TimeSpan.FromMilliseconds(250)); // bounded; the loop re-checks the advance and the real-time cap
                continue;
            }

            (string trace, int seen) = await world.OnWorldAsync(() => (world.Trace(), toggles));
            Assert.False(string.IsNullOrEmpty(trace));
            Assert.True(seen > lastSeen, $"trace {built} was built in the same tick as the previous one (world-side flips {seen}, previous {lastSeen}, {restarts} advance restarts)");
            lastSeen = seen;
            Interlocked.Increment(ref built);
            sinceProgress.Restart();
        }

        // Fails only if no trace came for stallLimit, i.e. the world is stuck.
        Assert.Equal(Target, built);
        await running;
    }
}
