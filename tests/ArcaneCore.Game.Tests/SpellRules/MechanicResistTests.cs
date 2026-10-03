using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Application;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>Per-effect mechanic resistance (vmangos Unit::IsEffectResist, Unit.cpp:2461-2470).</summary>
public sealed class MechanicResistTests
{
    private const uint RootAndDamage = 931_001;
    private const uint SameMechanic = 931_002;
    private const uint ResistRoot = 931_010;
    private const uint ResistFull = 931_011;

    private static SpellTestKit Kit()
    {
        SpellEffectInfo damage = SpellTestKit.Effect(SpellEffectName.SchoolDamage, 6, SpellImplicitTarget.UnitEnemy);
        SpellEffectInfo root = SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModRoot) with { Mechanic = (uint)SpellMechanic.Root };
        SpellInfo common = SpellTestKit.Spell(1) with
        {
            Duration = new SpellDuration(5000, 0, 5000),
            SpellVisual = 1,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        return new SpellTestKit(
            common with { Id = RootAndDamage, Effects = [damage, root] },
            common with { Id = SameMechanic, Mechanic = (uint)SpellMechanic.Root, Effects = [damage, root] },
            RuleTestSupport.Grant(ResistRoot, AuraType.ModMechanicResistance, 25, (int)SpellMechanic.Root),
            RuleTestSupport.Grant(ResistFull, AuraType.ModMechanicResistance, 100, (int)SpellMechanic.Root));
    }

    private static (Player Caster, Player Victim) Setup(SpellTestKit kit)
    {
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.System.ApplicationRules.Add(new MechanicResistRule());
        return (caster, victim);
    }

    private static void Hit(SpellTestKit kit, Unit caster, Unit victim, uint spell) =>
        kit.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

    [Fact]
    public void ResistingTheRootEffect_KeepsTheDamage()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Setup(kit);
        RuleTestSupport.Apply(kit, victim, ResistFull);
        uint before = victim.Health;

        Hit(kit, caster, victim, RootAndDamage);

        Assert.Equal(before - 6, victim.Health);
        Assert.DoesNotContain(kit.System.GetAuras(victim), h => h.Spell.Id == RootAndDamage);
        Assert.False(victim.IsRooted);
    }

    [Fact]
    public void WithoutResistance_TheRootLands()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Setup(kit);

        Hit(kit, caster, victim, RootAndDamage);

        Assert.Contains(kit.System.GetAuras(victim), h => h.Spell.Id == RootAndDamage);
        Assert.True(victim.IsRooted);
    }

    [Fact]
    public void AnEffectWithTheSpellsOwnMechanic_IsNotRolled_ItIsResistedInTheHitRoll()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Setup(kit);
        RuleTestSupport.Apply(kit, victim, ResistFull);

        Hit(kit, caster, victim, SameMechanic);

        Assert.True(victim.IsRooted);
    }

    [Theory]
    [InlineData(24, true)]
    [InlineData(25, false)]
    [InlineData(0, true)]
    public void TheRollIsZeroToNinetyNineBelowTheResistance(int roll, bool resisted)
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Setup(kit);
        RuleTestSupport.Apply(kit, victim, ResistRoot);
        kit.System.Random = new ScriptedRandom(roll);

        Hit(kit, caster, victim, RootAndDamage);

        Assert.Equal(resisted, !victim.IsRooted);
    }

    [Fact]
    public void ResistedAboutAQuarterOfTheTime_At25Percent()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Setup(kit);
        RuleTestSupport.Apply(kit, victim, ResistRoot);
        int resisted = 0;

        for (int i = 0; i < 2000; i++)
        {
            kit.System.RemoveAuras(victim, RootAndDamage);
            Hit(kit, caster, victim, RootAndDamage);
            victim.Health = victim.MaxHealth;
            if (!kit.System.GetAuras(victim).Any(h => h.Spell.Id == RootAndDamage))
            {
                resisted++;
            }
        }

        Assert.InRange(resisted, 400, 600);
    }
}
