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
    private const int Iterations = 3000;

    [Fact]
    public async Task TraceBuiltWhileTheWorldRuns_NeverThrows_OverManyIterations()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var tank = await world.AddBotAsync("Racetank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Raceheal", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        var mage = await world.AddBotAsync("Racemage", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(180_000, () => world.Player(tank.Id).Combat.Victim is Creature { Entry: OgreEntry }), world.Trace());

        // The fight goes on (the coordinator notes events) and, on every tick, the tank's attacker set changes through the real combat API,
        // while the test thread builds traces.
        bool attacking = false;
        Task<bool> running = world.RunUntilAsync(240_000, () =>
        {
            Player victim = world.Player(tank.Id);
            foreach (Player attacker in new[] { world.Player(healer.Id), world.Player(mage.Id) })
            {
                if (attacking) victim.Map!.Combat.AttackStop(attacker);
                else victim.Map!.Combat.Attack(attacker, victim);
            }

            attacking = !attacking;
            return false;
        });
        int built = 0;
        while (built < Iterations && !running.IsCompleted)
        {
            string trace = world.Trace();
            Assert.False(string.IsNullOrEmpty(trace));
            built++;
        }

        Assert.True(built >= 100, $"only {built} traces were built before the advance ended");
    }
}
