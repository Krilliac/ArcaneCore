using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>
/// vmangos SpellCaster::RollMeleeOutcomeAgainst applies the weapon-skill difference to dodge and parry
/// (SpellCaster.cpp:458-461, 585-615) for a melee spell as well as a white swing. A raw 5% leaves a
/// level-63 mob dodging at 5% instead of 6.5% and parrying at 5% instead of 14%.
/// </summary>
public sealed class MeleeSpellDefenseSkillTests
{
    private const uint Strike = 989_332;

    /// <summary>Every hand rolls with level x 5 (no skills attached).</summary>
    private sealed class FlatSkillHooks : CombatHooks
    {
        public override int GetWeaponSkill(Unit unit, WeaponAttackType attackType, Unit? victim) => unit.Level * 5;
    }

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => value;
    }

    private static SpellInfo StrikeSpell() =>
        SpellTestKit.Spell(Strike, SpellTestKit.Effect(SpellEffectName.WeaponDamage, 5, SpellImplicitTarget.UnitEnemy))
        with { DamageClass = SpellDamageClass.Melee, RangeIndex = 2, Range = new SpellRange(0, 30) };

    [Fact]
    public void ALevel63Mob_DodgesAMeleeSpellAtTheWhiteSwingRate()
    {
        // Facing away: no parry. Miss is 8% (800). Raw dodge is 5% (800..1299); the skill term makes it 6.5% (800..1449).
        using var kit = new SpellTestKit(StrikeSpell());
        (Player player, CombatTestUnit mob) = Scene(kit, facing: 0f);
        kit.System.Random = new FixedRandom(1300);
        MeleeRollInput white = player.Map!.Combat.BuildRollInput(player, mob, WeaponAttackType.BaseAttack);

        Assert.Equal(MeleeHitOutcome.Dodge, MeleeHitTable.Roll(white, 1300).Outcome);
        Assert.Equal(SpellMissInfo.Dodge, new VanillaSpellCombatRules().RollHit(kit.System, player, mob, kit.Store.Get(Strike)!));
    }

    [Fact]
    public void ALevel63Mob_ParriesAMeleeSpellAtTheWhiteSwingRate()
    {
        // Facing the attacker. After the 8% miss and the 6.5% dodge, parry runs 1450..2849 (14%), not 1300..1799 (5%).
        using var kit = new SpellTestKit(StrikeSpell());
        (Player player, CombatTestUnit mob) = Scene(kit, facing: MathF.PI);
        kit.System.Random = new FixedRandom(1800);
        MeleeRollInput white = player.Map!.Combat.BuildRollInput(player, mob, WeaponAttackType.BaseAttack);

        Assert.Equal(MeleeHitOutcome.Parry, MeleeHitTable.Roll(white, 1800).Outcome);
        Assert.Equal(SpellMissInfo.Parry, new VanillaSpellCombatRules().RollHit(kit.System, player, mob, kit.Store.Get(Strike)!));
    }

    private static (Player Player, CombatTestUnit Mob) Scene(SpellTestKit kit, float facing)
    {
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60; // weapon skill 300
        var mob = new CombatTestUnit(level: 63); // defense 315, base dodge and parry 5%
        mob.Spawn(player.Map!, 2, 0, orientation: facing);
        player.Map!.Combat.Hooks = new FlatSkillHooks();
        return (player, mob);
    }
}
