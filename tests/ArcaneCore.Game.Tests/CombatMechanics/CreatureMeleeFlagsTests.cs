using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// The creature template flags the melee table and the attack start read: CREATURE_FLAG_EXTRA_NO_PARRY / NO_BLOCK and
/// CREATURE_STATIC_FLAG_2_NO_CRUSHING_BLOWS (vmangos SpellCaster::RollMeleeOutcomeAgainst, SpellCaster.cpp:550-571), the cmangos
/// NO_PARRY_HASTEN extra flag (cmangos-classic Unit::DealMeleeDamage) and the no-melee flags (vmangos CREATURE_STATIC_FLAG_NO_MELEE,
/// CreatureAI.cpp:40; cmangos CREATURE_EXTRA_FLAG_NO_MELEE).
/// </summary>
public sealed class CreatureMeleeFlagsTests
{
    private static uint s_entry = 989_320;

    private sealed class PlainAI(Creature creature) : CreatureAI(creature);

    private static (WorldRuntime World, Map Map, ScriptedRandom Random, Player Player) Scene()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, TestCombatHooks _) = CombatTestKit.CreateWorld();
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetFloat(UpdateFields.UnitFieldMindamage, 10);
        player.SetFloat(UpdateFields.UnitFieldMaxdamage, 10);
        return (world, map, random, player);
    }

    private static Creature Spawn(Map map, Func<CreatureTemplate, CreatureTemplate> configure)
    {
        uint entry = Interlocked.Increment(ref s_entry);
        CreatureTemplate template = configure(new CreatureTemplate { Entry = entry, Name = "Flagged", MinLevel = 60, MaxLevel = 60,
            MinLevelHealth = 10_000, MaxLevelHealth = 10_000, DisplayIds = [1], Faction = 35 });
        var creature = new Creature(entry, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        creature.Relocate(2, 0, 83.5f, MathF.PI, 0); // faces the player at the origin
        creature.MapId = 0;
        map.AddObject(creature);
        map.Combat.Track(creature);
        return creature;
    }

    [Theory]
    [InlineData(CreatureExtraFlagsDialect.VMangos)]
    [InlineData(CreatureExtraFlagsDialect.CMangos)]
    public void NoParryAndNoBlock_TakeTheCreatureOutOfThoseRolls(CreatureExtraFlagsDialect dialect)
    {
        (WorldRuntime world, Map map, ScriptedRandom _, Player player) = Scene();
        using WorldRuntime _ = world;
        Creature noParry = Spawn(map, t => t with { ExtraFlags = 0x04, ExtraFlagsDialect = dialect });
        Creature noBlock = Spawn(map, t => t with { ExtraFlags = 0x10, ExtraFlagsDialect = dialect });
        Creature plain = Spawn(map, t => t);

        MeleeRollInput parryInput = map.Combat.BuildRollInput(player, noParry, WeaponAttackType.BaseAttack);
        MeleeRollInput blockInput = map.Combat.BuildRollInput(player, noBlock, WeaponAttackType.BaseAttack);
        MeleeRollInput plainInput = map.Combat.BuildRollInput(player, plain, WeaponAttackType.BaseAttack);

        Assert.Equal((false, true), (parryInput.VictimCreatureCanParry, parryInput.VictimCreatureCanBlock));
        Assert.Equal((true, false), (blockInput.VictimCreatureCanParry, blockInput.VictimCreatureCanBlock));
        Assert.Equal((true, true), (plainInput.VictimCreatureCanParry, plainInput.VictimCreatureCanBlock));
    }

    [Fact]
    public void ANoParryCreature_NeverParries_TheRollFallsThroughToTheNextRange()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, Player player) = Scene();
        using WorldRuntime _ = world;
        Creature noParry = Spawn(map, t => t with { ExtraFlags = 0x04, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos });
        random.Ints.Enqueue(1200); // miss 0-499, dodge 500-999, parry 1000-1499 for a creature that can parry

        MeleeDamageInfo hit = map.Combat.CalculateMeleeDamage(player, noParry, WeaponAttackType.BaseAttack);

        Assert.NotEqual(MeleeHitOutcome.Parry, hit.Outcome);
    }

    [Fact]
    public void NoCrushingBlowsStaticFlag_StopsCrushingBlows()
    {
        (WorldRuntime world, Map map, ScriptedRandom _, Player player) = Scene();
        using WorldRuntime _ = world;
        Creature noCrush = Spawn(map, t => t with { StaticFlags2 = 0x10 });
        Creature plain = Spawn(map, t => t);

        Assert.False(map.Combat.BuildRollInput(noCrush, player, WeaponAttackType.BaseAttack).AttackerCanCrush);
        Assert.True(map.Combat.BuildRollInput(plain, player, WeaponAttackType.BaseAttack).AttackerCanCrush);
    }

    [Theory]
    [InlineData(0x08u, 1500u)]  // cmangos NO_PARRY_HASTEN: the parry leaves the creature's swing timer alone
    [InlineData(0x00u, 700u)]   // otherwise above 60% of a 2000 ms swing the timer is pulled back by 40% (1500 - 800)
    public void NoParryHasten_KeepsTheCreaturesSwingTimerOnAParry(uint extraFlags, uint expectedTimer)
    {
        (WorldRuntime world, Map map, ScriptedRandom random, Player player) = Scene();
        using WorldRuntime _ = world;
        Creature creature = Spawn(map, t => t with { ExtraFlags = extraFlags, ExtraFlagsDialect = CreatureExtraFlagsDialect.CMangos });
        creature.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2000);
        creature.Combat.SetAttackTimer(WeaponAttackType.BaseAttack, 1500);
        random.Ints.Enqueue(1200); // parry

        MeleeDamageInfo? hit = map.Combat.AttackerStateUpdate(player, creature, WeaponAttackType.BaseAttack);

        Assert.Equal(MeleeHitOutcome.Parry, hit!.Outcome);
        Assert.Equal(expectedTimer, creature.Combat.GetAttackTimer(WeaponAttackType.BaseAttack));
    }

    [Theory]
    [InlineData(0x00020000u, 0u, CreatureExtraFlagsDialect.CMangos)]  // cmangos CREATURE_EXTRA_FLAG_NO_MELEE
    [InlineData(0u, 0x00100000u, CreatureExtraFlagsDialect.VMangos)]  // vmangos CREATURE_STATIC_FLAG_NO_MELEE
    public void ANoMeleeCreature_StartsItsAttackWithoutSwinging(uint extraFlags, uint staticFlags1, CreatureExtraFlagsDialect dialect)
    {
        (WorldRuntime world, Map map, ScriptedRandom _, Player player) = Scene();
        using WorldRuntime _ = world;
        Creature noMelee = Spawn(map, t => t with { ExtraFlags = extraFlags, StaticFlags1 = staticFlags1, ExtraFlagsDialect = dialect });

        noMelee.OnAttackedBy(player); // no AI: the creature turns on its attacker itself

        Assert.Same(player, noMelee.Combat.Victim);
        Assert.False(noMelee.Combat.IsMeleeAttacking);
        Assert.False(new PlainAI(noMelee).MeleeEnabled);
        Assert.True(new PlainAI(Spawn(map, t => t)).MeleeEnabled);
    }
}
