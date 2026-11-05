using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Passive and hidden auras in vmangos Unit::RemoveNoStackAurasDueToAuraHolder (Unit.cpp:3433-3453): a holder already on the target with
/// SPELL_ATTR_PASSIVE or SPELL_ATTR_DO_NOT_DISPLAY only meets a new holder of the same caster (or when either is a party area aura), and then
/// only a rank of its own chain, which is refused when it is the weaker. A hidden aura from another caster is never replaced.
/// </summary>
public sealed class PassiveAuraStackingTests : IDisposable
{
    private const uint Toughness1 = 12299;
    private const uint Toughness2 = 12761;
    private const uint PolymorphSheep = 118;
    private const uint PolymorphPig = 28272;
    private const uint FamilyMage = 3;
    private const uint FamilyWarrior = 4;

    private static SpellInfo Passive(uint id, int amount) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.Dummy)) with
        {
            Name = "Toughness",
            SpellFamilyName = FamilyWarrior,
            SpellVisual = 1,
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
        };

    private static SpellInfo Polymorph(uint id, string name, SpellAttributes extra) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitEnemy, AuraType.ModConfuse)) with
        {
            Name = name,
            SpellFamilyName = FamilyMage,
            PreventionType = 1,
            SpellVisual = 1,
            Attributes = SpellAttributes.AuraIsDebuff | extra,
            Duration = new SpellDuration(20_000, 0, 20_000),
        };

    private readonly SpellTestKit _kit;
    private readonly Player _caster;
    private readonly Player _otherCaster;
    private readonly Player _target;

    public PassiveAuraStackingTests()
    {
        _kit = new SpellTestKit(
            Passive(Toughness1, 10),
            Passive(Toughness2, 20),
            Polymorph(PolymorphSheep, "Polymorph", SpellAttributes.DoNotDisplay),
            Polymorph(PolymorphPig, "Polymorph: Pig", SpellAttributes.None));
        (_caster, _) = _kit.AddPlayer(1, 0, 0);
        (_target, _) = _kit.AddPlayer(2, 3, 0);
        (_otherCaster, _) = _kit.AddPlayer(3, -2, 0);
    }

    public void Dispose() => _kit.Dispose();

    [Fact]
    public void ALowerRankOverTheSameCastersPassive_IsRefused()
    {
        Assert.True(_kit.System.AddAura(_target, Toughness2, permanent: true));

        Assert.False(_kit.System.AddAura(_target, Toughness1, permanent: true));

        Assert.True(_kit.System.HasAura(_target, Toughness2));
        Assert.False(_kit.System.HasAura(_target, Toughness1));
    }

    [Fact]
    public void ALowerRankOverAnotherCastersPassive_Stacks()
    {
        Assert.True(_kit.System.AddAura(_target, Toughness2, permanent: true, caster: _otherCaster));

        Assert.True(_kit.System.AddAura(_target, Toughness1, permanent: true, caster: _caster));

        Assert.True(_kit.System.HasAura(_target, Toughness2));
        Assert.True(_kit.System.HasAura(_target, Toughness1));
    }

    [Fact]
    public void AHiddenPolymorphFromAnotherCaster_IsNotReplaced()
    {
        Assert.True(_kit.System.AddAura(_target, PolymorphSheep, caster: _otherCaster));

        Assert.True(_kit.System.AddAura(_target, PolymorphPig, caster: _caster));

        Assert.True(_kit.System.HasAura(_target, PolymorphSheep));
        Assert.True(_kit.System.HasAura(_target, PolymorphPig));
    }
}