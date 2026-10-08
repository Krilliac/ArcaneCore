using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Hunter;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// Counterattack (vmangos scripts/spells/spell_hunter.cpp:121-136 with the parry reaction of Unit::ProcSkillsAndReactives). Build 5875 shape of 19306
/// (classic-db z2815): a melee-class SCHOOL_DAMAGE of 40 at an enemy that needs the caster aura state HUNTER_PARRY (7); its root effect is left out. The
/// reactive services are wired as the world wires them (aura states, combo points, reactive windows).
/// </summary>
public sealed class CounterattackScriptTests : IDisposable
{
    private const uint Counterattack = 19306;

    private readonly SpellTestKit _kit;
    private readonly Player _hunter;
    private readonly Player _attacker;
    private readonly Player _other;
    private readonly ReactiveService _reactives;

    public CounterattackScriptTests()
    {
        _kit = new SpellTestKit(Spell(Counterattack, Effect(SpellEffectName.SchoolDamage, 40, SpellImplicitTarget.UnitEnemy)) with
        {
            CasterAuraState = AuraState.HunterParry,
            DamageClass = SpellDamageClass.Melee,
            SpellFamilyName = 9,
            RangeIndex = 2,
            Range = new SpellRange(0, 5),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });
        (_hunter, _) = _kit.AddPlayer(1);
        _hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        (_attacker, _) = _kit.AddPlayer(2, 2, 0);
        (_other, _) = _kit.AddPlayer(3, 0, 2);
        _kit.System.Relations = new FakeRelations { Hostile = { _attacker.Guid, _other.Guid } };
        var combos = new ComboPointService(_kit.System, (_, guid) => _kit.World.FindOnlinePlayer(guid));
        combos.Install();
        var states = new AuraStateService(_kit.System, _ => []);
        AuraStateCastChecks.Install(_kit.System, states);
        _reactives = new ReactiveService(states, () => combos);
        _kit.System.CombatRules = new NoCritNoResistRules();
        SpellScriptDispatcher.Install(_kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
    }

    public void Dispose() => _kit.Dispose();

    private SpellCastResult Cast(Unit target) => _kit.System.CastSpell(_hunter, Counterattack, SpellCastTargets.ForUnit(target.Guid), triggered: false);

    [Fact]
    public void WithoutAParry_CounterattackIsRefused()
    {
        Assert.NotEqual(SpellCastResult.CastOk, Cast(_attacker));
        Assert.NotEqual(SpellCastResult.CastOk, Cast(_other));
    }

    [Fact]
    public void AfterAParry_CounterattackHitsTheParriedAttacker_AndNobodyElse()
    {
        _reactives.OnAttackAvoided(_attacker, _hunter, AttackAvoidance.Parry);

        Assert.Equal(SpellCastResult.BadTargets, Cast(_other));  // GetReactiveTarget(REACTIVE_HUNTER_PARRY) is the attacker
        uint before = _attacker.Health;
        Assert.Equal(SpellCastResult.CastOk, Cast(_attacker));
        Assert.Equal(before - 40, _attacker.Health);
    }

    [Fact]
    public void ALaterParry_MovesTheTarget()
    {
        _reactives.OnAttackAvoided(_attacker, _hunter, AttackAvoidance.Parry);
        _reactives.OnAttackAvoided(_other, _hunter, AttackAvoidance.Parry);

        Assert.Equal(SpellCastResult.BadTargets, Cast(_attacker));
        Assert.Equal(SpellCastResult.CastOk, Cast(_other));
    }

    [Fact]
    public void WhenTheWindowCloses_CounterattackIsRefusedAgain()
    {
        _reactives.OnAttackAvoided(_attacker, _hunter, AttackAvoidance.Parry);
        _reactives.Update(ReactiveService.ReactiveTimerStartMs);

        Assert.Equal(SpellCastResult.CasterAurastate, Cast(_attacker));
    }
}
