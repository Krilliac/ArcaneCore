using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>
/// vmangos SpellCaster::RollMeleeOutcomeAgainst, used for melee spells through MeleeSpellHitResult (SpellCaster.cpp:732-740),
/// decides which active defenses may roll before the dodge and parry bands (SpellCaster.cpp:553-582): a player cannot dodge
/// from behind, nothing parries from behind, a creature with CREATURE_FLAG_EXTRA_NO_PARRY never parries, and a spell with
/// SPELL_ATTR_NO_ACTIVE_DEFENSE (Overpower, Ambush, Cheap Shot, Garrote) can be neither dodged nor parried.
/// </summary>
public sealed class MeleeSpellActiveDefenseTests
{
    private const uint Strike = 989_341;

    /// <summary>Every hand rolls with level x 5 (no skills attached).</summary>
    private sealed class FlatSkillHooks : CombatHooks
    {
        public override int GetWeaponSkill(Unit unit, WeaponAttackType attackType, Unit? victim) => unit.Level * 5;
    }

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => value;
    }

    private static SpellInfo StrikeSpell(SpellAttributesCombat attributes = SpellAttributesCombat.None) =>
        SpellTestKit.Spell(Strike, SpellTestKit.Effect(SpellEffectName.WeaponDamage, 5, SpellImplicitTarget.UnitEnemy))
        with { DamageClass = SpellDamageClass.Melee, RangeIndex = 2, Range = new SpellRange(0, 30), Attributes = (SpellAttributes)attributes };

    [Theory]
    [InlineData(1300)] // the 6.5% dodge band of a level-63 mob (800..1449)
    [InlineData(1800)] // the 14% parry band (1450..2849)
    public void ANoActiveDefenseSpell_IsNeitherDodgedNorParried(int roll)
    {
        using var kit = new SpellTestKit(StrikeSpell(SpellAttributesCombat.NoActiveDefense));
        (Player player, CombatTestUnit mob) = MobScene(kit, facing: MathF.PI);
        kit.System.Random = new FixedRandom(roll);

        Assert.Equal(SpellMissInfo.None, new VanillaSpellCombatRules().RollHit(kit.System, player, mob, kit.Store.Get(Strike)!));
    }

    [Fact]
    public void ANoActiveDefenseSpell_StillMisses()
    {
        using var kit = new SpellTestKit(StrikeSpell(SpellAttributesCombat.NoActiveDefense));
        (Player player, CombatTestUnit mob) = MobScene(kit, facing: MathF.PI);
        kit.System.Random = new FixedRandom(700); // inside the 8% miss band

        Assert.Equal(SpellMissInfo.Miss, new VanillaSpellCombatRules().RollHit(kit.System, player, mob, kit.Store.Get(Strike)!));
    }

    [Fact]
    public void ANoParryCreature_DoesNotParryAMeleeSpell()
    {
        using var kit = new SpellTestKit(StrikeSpell());
        (Player player, CombatTestUnit mob) = MobScene(kit, facing: MathF.PI);
        mob.CanParry = false;
        kit.System.Random = new FixedRandom(1800);
        MeleeRollInput white = player.Map!.Combat.BuildRollInput(player, mob, WeaponAttackType.BaseAttack);

        Assert.NotEqual(MeleeHitOutcome.Parry, MeleeHitTable.Roll(white, 1800).Outcome); // the white swing glances there instead
        Assert.Equal(SpellMissInfo.None, new VanillaSpellCombatRules().RollHit(kit.System, player, mob, kit.Store.Get(Strike)!));
    }

    [Theory]
    [InlineData(MathF.PI, SpellMissInfo.Dodge)] // facing the attacker: 5% miss (0..499), then 7% dodge (500..1199)
    [InlineData(0f, SpellMissInfo.None)] // facing away: a player cannot dodge from behind
    public void APlayerVictim_DodgesAMeleeSpellOnlyFromTheFront(float facing, SpellMissInfo expected)
    {
        using var kit = new SpellTestKit(StrikeSpell());
        (Player attacker, _) = kit.AddPlayer(1);
        attacker.Level = 60;
        (Player victim, _) = kit.AddPlayer(2, x: 2);
        victim.Level = 60;
        victim.Orientation = facing;
        victim.SetFloat(UpdateFields.PlayerDodgePercentage, 7f);
        attacker.Map!.Combat.Hooks = new FlatSkillHooks();
        kit.System.Random = new FixedRandom(800);

        Assert.Equal(expected, new VanillaSpellCombatRules().RollHit(kit.System, attacker, victim, kit.Store.Get(Strike)!));
    }

    private static (Player Player, CombatTestUnit Mob) MobScene(SpellTestKit kit, float facing)
    {
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60; // weapon skill 300
        var mob = new CombatTestUnit(level: 63); // defense 315, base dodge and parry 5%
        mob.Spawn(player.Map!, 2, 0, orientation: facing);
        player.Map!.Combat.Hooks = new FlatSkillHooks();
        return (player, mob);
    }
}
