using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>A <see cref="Random"/> that replays scripted values (rolls the tests need to pin) and then falls back to a seeded stream.</summary>
internal sealed class ScriptedRandom(params int[] values) : Random(7)
{
    private readonly Queue<int> _values = new(values);

    public override int Next(int minValue, int maxValue) => _values.Count > 0 ? _values.Dequeue() : base.Next(minValue, maxValue);

    public override double NextDouble() => _values.Count > 0 ? _values.Dequeue() / 10_000.0 : base.NextDouble();
}

/// <summary>Helpers shared by the spell-rule tests: synthetic spells and aura grants.</summary>
internal static class RuleTestSupport
{
    /// <summary>A hostile magic spell of <paramref name="school"/> with one damage effect.</summary>
    public static SpellInfo Magic(uint id, SpellSchool school = SpellSchool.Frost) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy))
        with { School = school, DamageClass = SpellDamageClass.Magic };

    /// <summary>A spell that applies one permanent self aura of <paramref name="type"/> (the way tests give a unit a modifier).</summary>
    public static SpellInfo Grant(uint id, AuraType type, int amount, int misc = 0) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.ApplyAura, amount, aura: type, misc: misc))
        with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    /// <summary>Cast the self-aura spell <paramref name="id"/> on <paramref name="unit"/>.</summary>
    public static void Apply(SpellTestKit kit, Unit unit, uint id) => kit.System.CastSpell(unit, id, SpellCastTargets.ForSelf(), triggered: true);
}
