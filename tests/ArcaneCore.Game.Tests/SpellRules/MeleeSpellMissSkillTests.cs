using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>
/// vmangos SpellCaster::RollMeleeOutcomeAgainst (SpellCaster.cpp:456-465) passes <c>fullSkillDiff = attackerWeaponSkill - victimDefenseSkill</c>
/// to GetMeleeMissChance for melee and ranged spells alike, so a victim whose defense is ahead raises the miss chance (0.2 per point
/// against a creature more than 10 points ahead, SpellCaster.cpp:366-373).
/// </summary>
public sealed class MeleeSpellMissSkillTests
{
    private const uint Strike = 989_330; // a melee-class weapon damage spell
    private const uint Shot = 989_331;   // a ranged-class weapon damage spell

    /// <summary>Every hand rolls with level x 5 (a ranged weapon without the items area), so the ranged case is not a 0 skill.</summary>
    private sealed class FlatSkillHooks : CombatHooks
    {
        public override int GetWeaponSkill(Unit unit, WeaponAttackType attackType, Unit? victim) => unit.Level * 5;
    }

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => value;
    }

    private static SpellInfo Weapon(uint id, SpellDamageClass damageClass) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.WeaponDamage, 5, SpellImplicitTarget.UnitEnemy))
        with { DamageClass = damageClass, RangeIndex = 2, Range = new SpellRange(0, 30) };

    [Theory]
    [InlineData(Strike)]
    [InlineData(Shot)]
    public void AVictimWhoseDefenseIsAhead_RaisesTheMissChance(uint spellId)
    {
        using var kit = new SpellTestKit(Weapon(Strike, SpellDamageClass.Melee), Weapon(Shot, SpellDamageClass.Ranged));
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60; // weapon skill 300 (no skills attached: level x 5)
        var mob = new CombatTestUnit(level: 63); // defense 315
        mob.Spawn(player.Map!, 2, 0, orientation: MathF.PI);
        kit.System.Random = new FixedRandom(500); // 5.00: inside 5 + 15 x 0.2 = 8% miss, outside a 3.5% one
        player.Map!.Combat.Hooks = new FlatSkillHooks();
        var rules = new VanillaSpellCombatRules();

        SpellMissInfo result = rules.RollHit(kit.System, player, mob, kit.Store.Get(spellId)!);

        Assert.Equal(SpellMissInfo.Miss, result);
    }

    [Theory]
    [InlineData(Strike)]
    [InlineData(Shot)]
    public void AnAttackerWhoseSkillIsAhead_LowersTheMissChance(uint spellId)
    {
        using var kit = new SpellTestKit(Weapon(Strike, SpellDamageClass.Melee), Weapon(Shot, SpellDamageClass.Ranged));
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;
        var mob = new CombatTestUnit(level: 57); // defense 285: miss 5 - 15 x 0.1 = 3.5%
        mob.Spawn(player.Map!, 2, 0); // faces away: no parry
        kit.System.Random = new FixedRandom(400);
        player.Map!.Combat.Hooks = new FlatSkillHooks();
        var rules = new VanillaSpellCombatRules();

        SpellMissInfo result = rules.RollHit(kit.System, player, mob, kit.Store.Get(spellId)!);

        Assert.NotEqual(SpellMissInfo.Miss, result);
    }
}
