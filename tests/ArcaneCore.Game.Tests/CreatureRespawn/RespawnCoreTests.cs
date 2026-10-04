using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureRespawn;

/// <summary>
/// Respawn delay and corpse decay as vmangos does them (Objects/Creature.cpp:1325-1341 corpse delay by rank, :1963 respawn delay
/// drawn once per object, :2242-2262 SetDeathState, :3355-3401 AllLootRemovedFromCorpse). All on the map clock; no wall time.
/// </summary>
public sealed class RespawnCoreTests
{
    private static (WorldRuntime World, CreatureMapSystem System, Creature Wolf) Start(
        CreatureSpawn spawn, CreatureTemplate? template = null, CreatureOptions? options = null)
    {
        CreatureContent content = Content([template ?? Template()], [spawn]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, options: options);
        AddPlayer(world, 1, 0, 0);
        return (world, system, Assert.Single(system.Creatures));
    }

    private static CreatureSpawn RareSpawn()
    {
        // The rares of classic-db carry a range (e.g. 2 h to 3 h): 757 of its 765 rank-4 spawns do.
        CreatureSpawn s = Spawn(1, WolfEntry, 10, 0);
        return s with { SpawnTimeMinSeconds = 7200, SpawnTimeMaxSeconds = 10800 };
    }

    /// <summary>Kill the creature, return the respawn delay (ms on the map clock) it got, and bring it back.</summary>
    private static long DieAndComeBack(CreatureMapSystem system, Creature wolf)
    {
        system.KillCreature(wolf);
        Assert.Equal(CreatureDeathState.Corpse, wolf.DeathState);
        long delay = wolf.RespawnAtMs - system.ClockMs;
        system.ForceRespawn(wolf);
        Assert.Equal(CreatureDeathState.Alive, wolf.DeathState);
        return delay;
    }

    [Fact]
    public void TheRespawnDelayIsDrawnOncePerObject_NotAtEveryDeath()
    {
        (WorldRuntime w, CreatureMapSystem system, Creature wolf) = Start(RareSpawn());
        using WorldRuntime world = w;

        long first = DieAndComeBack(system, wolf);
        long second = DieAndComeBack(system, wolf);
        long third = DieAndComeBack(system, wolf);

        Assert.InRange(first, 7_200_000, 10_800_000);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void WithDrawDelayAtLoadOff_EveryDeathDrawsAgain()
    {
        var options = new CreatureOptions();
        options.Respawn.DrawDelayAtLoad = false;
        (WorldRuntime w, CreatureMapSystem system, Creature wolf) = Start(RareSpawn(), options: options);
        using WorldRuntime world = w;

        var delays = new HashSet<long>();
        for (int i = 0; i < 5; i++)
        {
            delays.Add(DieAndComeBack(system, wolf));
        }

        Assert.True(delays.Count > 1, "five fresh draws from a 3601 s range all landing on one value is not plausible");
    }

    [Fact]
    public void ADelayWithoutARange_IsExactlySpawntimesecs()
    {
        (WorldRuntime w, CreatureMapSystem system, Creature wolf) = Start(Spawn(1, WolfEntry, 10, 0, respawnSeconds: 120));
        using WorldRuntime world = w;

        Assert.Equal(120_000, DieAndComeBack(system, wolf));
    }

    [Fact]
    public void CorpseDecay_IsByRankOnly_UnlessTheTemplateOverrideIsSwitchedOn()
    {
        CreatureTemplate withOverride = Template() with { CorpseDecaySeconds = 42 }; // cmangos-only column; vmangos ignores it

        (WorldRuntime w1, CreatureMapSystem retail, Creature wolf1) = Start(Spawn(1, WolfEntry, 10, 0), withOverride);
        using WorldRuntime world1 = w1;
        retail.KillCreature(wolf1);
        Assert.Equal(300_000u, wolf1.CorpseDecayMs); // rank normal: Corpse.Decay.NORMAL = 300 s

        var options = new CreatureOptions();
        options.Respawn.HonorTemplateCorpseDecay = true;
        (WorldRuntime w2, CreatureMapSystem cmangos, Creature wolf2) = Start(Spawn(1, WolfEntry, 10, 0), withOverride, options);
        using WorldRuntime world2 = w2;
        cmangos.KillCreature(wolf2);
        Assert.Equal(42_000u, wolf2.CorpseDecayMs);
    }

    private static Creature LootedOutCorpse(uint respawnSeconds, out CreatureMapSystem system, out WorldRuntime world)
    {
        (world, system, Creature wolf) = Start(Spawn(1, WolfEntry, 10, 0, respawnSeconds: respawnSeconds));
        system.KillCreature(wolf);
        return wolf;
    }

    [Fact]
    public void AllLootRemoved_WithTheDefaultRateZero_DecaysAfterAThirdOfTheRespawnDelay()
    {
        // spawntimesecs 180 s <= the 300 s corpse delay: min(300 s, 180/3 = 60 s) (Creature.cpp:3369-3385).
        Creature wolf = LootedOutCorpse(180, out CreatureMapSystem system, out WorldRuntime world);
        using (world)
        {
            wolf.OnAllLootRemoved(lootedDecayRate: 0f);
            Assert.Equal(60_000u, wolf.CorpseDecayMs);
        }
    }

    [Fact]
    public void AllLootRemoved_ALongRespawnDelay_AlwaysUsesTheLootedDelay_EvenWhenLonger()
    {
        // spawntimesecs 3600 s > the 300 s corpse delay: the looted delay (3600/3 = 1200 s) replaces the timer (Creature.cpp:3378-3380).
        Creature wolf = LootedOutCorpse(3600, out _, out WorldRuntime world);
        using (world)
        {
            wolf.OnAllLootRemoved(lootedDecayRate: 0f);
            Assert.Equal(1_200_000u, wolf.CorpseDecayMs);
        }
    }

    [Fact]
    public void AllLootRemoved_WithAPositiveRate_UsesThatShareOfTheCorpseDelay()
    {
        Creature wolf = LootedOutCorpse(180, out _, out WorldRuntime world);
        using (world)
        {
            wolf.OnAllLootRemoved(lootedDecayRate: 0.25f); // 300 s * 0.25 = 75 s, kept by the min(..) with the 300 s timer
            Assert.Equal(75_000u, wolf.CorpseDecayMs);
        }
    }

    [Fact]
    public void AllLootRemoved_ASkinnedCorpseGoesAtOnce_AndSoDoesOneWhoseRespawnIsAlreadyDue()
    {
        Creature skinned = LootedOutCorpse(180, out _, out WorldRuntime world1);
        using (world1)
        {
            skinned.LootedForSkin = true;
            skinned.OnAllLootRemoved(lootedDecayRate: 0f);
            Assert.Equal(0u, skinned.CorpseDecayMs);
        }

        Creature due = LootedOutCorpse(180, out CreatureMapSystem system, out WorldRuntime world2);
        using (world2)
        {
            due.RespawnAtMs = system.ClockMs - 1; // the respawn time has already passed: it will respawn next tick (Creature.cpp:3388-3399)
            due.OnAllLootRemoved(lootedDecayRate: 0f);
            Assert.Equal(0u, due.CorpseDecayMs);
        }
    }

    private static Creature SpawnlessCorpse(out CreatureMapSystem system, out WorldRuntime world)
    {
        (world, _, system) = CreateAiSystem(Content([Template()], []));
        AddPlayer(world, 1, 0, 0);
        Creature summoned = system.SpawnTemporary(Template(), 10, 0, 83.5f, 0);
        system.KillCreature(summoned);
        Assert.Equal(CreatureDeathState.Corpse, summoned.DeathState);
        return summoned;
    }

    [Fact]
    public void AllLootRemoved_ASpawnlessCorpse_IsNotTreatedAsPastItsRespawnTime()
    {
        // GM-added and summoned creatures have no spawn row: their respawn delay is 0, so the "respawn time already passed" branch would drop the corpse at once.
        Creature summoned = SpawnlessCorpse(out CreatureMapSystem system, out WorldRuntime world);
        using (world)
        {
            world.RunTick(50);
            Assert.True(summoned.RespawnAtMs < system.ClockMs);
            uint before = summoned.CorpseDecayMs;

            summoned.OnAllLootRemoved(lootedDecayRate: 0f);
            Assert.Equal(before, summoned.CorpseDecayMs);
            Assert.NotEqual(0u, summoned.CorpseDecayMs);

            summoned.OnAllLootRemoved(lootedDecayRate: 0.25f); // a configured rate still shortens it: 300 s * 0.25
            Assert.Equal(75_000u, summoned.CorpseDecayMs);
        }
    }

    [Fact]
    public void AllLootRemoved_ASkinnedSpawnlessCorpse_StillGoesAtOnce()
    {
        Creature summoned = SpawnlessCorpse(out _, out WorldRuntime world);
        using (world)
        {
            summoned.LootedForSkin = true;
            summoned.OnAllLootRemoved(lootedDecayRate: 0f);
            Assert.Equal(0u, summoned.CorpseDecayMs);
        }
    }

    [Fact]
    public void TheRespawnOptions_DefaultToRetail()
    {
        var options = new CreatureOptions();
        Assert.True(options.Respawn.DrawDelayAtLoad);
        Assert.False(options.Respawn.HonorTemplateCorpseDecay);
    }
}
