using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// vmangos Unit::GetUnitDodgeChance / GetUnitParryChance / GetUnitBlockChance (Unit.cpp:2474-2550): a victim that is
/// casting a non-melee spell (IsNonMeleeSpellCasted(false)) or stunned has no dodge, parry or block, and a totem
/// (Creature::IsTotem, creature type TOTEM for parry) has none either.
/// </summary>
public sealed class MeleeVictimDefenseTests
{
    private sealed class CastingHooks(Unit caster) : IMeleeSpellHooks
    {
        public bool IsNonMeleeSpellCasted(Unit unit) => ReferenceEquals(unit, caster);

        public bool TryCastQueuedSwingSpell(Unit attacker, Unit victim) => false;

        public void OnMeleeAttackStopped(Unit attacker)
        {
        }
    }

    private static (WorldRuntime World, Map Map, Player Attacker) Scene()
    {
        (WorldRuntime world, Map map, ScriptedRandom _, TestCombatHooks _) = CombatTestKit.CreateWorld();
        Player attacker = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        return (world, map, attacker);
    }

    [Fact]
    public void ACastingCreatureVictim_CannotDodgeParryOrBlock()
    {
        (WorldRuntime world, Map map, Player attacker) = Scene();
        using WorldRuntime _ = world;
        var victim = new CombatTestUnit();
        victim.Spawn(map, 2, 0, orientation: MathF.PI); // faces the attacker
        CombatEnvironment.Register(world, new CombatEnvironment(new CombatOptions(), null, new CastingHooks(victim)));

        MeleeRollInput input = map.Combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack);

        Assert.Equal((0f, 0f, 0f), (input.DodgeChance, input.ParryChance, input.BlockChance));
    }

    [Fact]
    public void ACastingPlayerVictim_CannotDodgeParryOrBlock()
    {
        (WorldRuntime world, Map map, Player attacker) = Scene();
        using WorldRuntime _ = world;
        Player victim = CombatTestKit.AddPlayer(world, 2, 2, 0, new FakeSession(2), Race.Orc);
        victim.SetFloat(UpdateFields.PlayerDodgePercentage, 7f);
        CombatEnvironment.Register(world, new CombatEnvironment(new CombatOptions(), null, new CastingHooks(victim)));

        MeleeRollInput input = map.Combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack);

        Assert.Equal(0f, input.DodgeChance);
    }

    [Fact]
    public void ANonCastingCreatureVictim_KeepsTheFivePercentDefaults()
    {
        (WorldRuntime world, Map map, Player attacker) = Scene();
        using WorldRuntime _ = world;
        var victim = new CombatTestUnit();
        victim.Spawn(map, 2, 0, orientation: MathF.PI);
        CombatEnvironment.Register(world, new CombatEnvironment(new CombatOptions(), null, new CastingHooks(attacker)));

        MeleeRollInput input = map.Combat.BuildRollInput(attacker, victim, WeaponAttackType.BaseAttack);

        Assert.Equal((5f, 5f, 5f), (input.DodgeChance, input.ParryChance, input.BlockChance));
    }

    [Fact]
    public void ATotemVictim_CannotDodgeParryOrBlock()
    {
        (WorldRuntime world, Map map, Player attacker) = Scene();
        using WorldRuntime _ = world;
        var template = new CreatureTemplate { Entry = 989_310, Name = "Test Totem", MinLevel = 60, MaxLevel = 60,
            MinLevelHealth = 100, MaxLevelHealth = 100, DisplayIds = [1], Faction = 35, CreatureType = 11 };
        var totem = new Creature(989_310, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        totem.Relocate(2, 0, 83.5f, MathF.PI, 0);
        totem.MapId = 0;
        map.AddObject(totem);
        map.Combat.Track(totem);

        MeleeRollInput input = map.Combat.BuildRollInput(attacker, totem, WeaponAttackType.BaseAttack);

        Assert.Equal((0f, 0f, 0f), (input.DodgeChance, input.ParryChance, input.BlockChance));
    }
}
