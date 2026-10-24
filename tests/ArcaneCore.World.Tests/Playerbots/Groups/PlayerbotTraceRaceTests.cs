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
        using SemaphoreSlim flipped = new(0);
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
        Stopwatch wall = Stopwatch.StartNew();
        while (Volatile.Read(ref built) < Target && wall.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (running.IsCompleted)
            {
                await running; // surfaces a fault
                restarts++;
                running = world.RunUntilAsync(AdvanceMs, flip);
            }

            // Lockstep: a trace is requested only once the world has flipped the attacker set since the previous request, so every trace
            // is built in a different tick from the last, with a flip between them, whatever the relative speed of the two threads.
            int before = Volatile.Read(ref toggles);
            if (before == lastSeen)
            {
                await flipped.WaitAsync(TimeSpan.FromMilliseconds(250)); // bounded; the loop re-checks the advance and the real-time cap
                continue;
            }

            string trace = await world.TraceAsync();
            Assert.False(string.IsNullOrEmpty(trace));
            interleaved++;
            lastSeen = before;
            Interlocked.Increment(ref built);
        }

        // Fails only if the real-time cap was hit, i.e. the world is stuck.
        Assert.Equal(Target, built);
        Assert.True(interleaved == built, $"only {interleaved} of {built} traces saw the attacker set change ({toggles} flips, {restarts} advance restarts)");
        await running;
    }
}
