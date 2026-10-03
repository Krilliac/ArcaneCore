using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Application;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>Unit immunities derived from live auras and creature masks (vmangos Unit.cpp:5404-5658, SpellAuras.cpp:4042-4180, Creature.cpp:2438-2480).</summary>
public sealed class ImmunityTests
{
    private const uint FireBolt = 950_001;
    private const uint Heal = 950_002;
    private const uint FrostDot = 950_003;
    private const uint StunSpell = 950_004;
    private const uint DamageAndStun = 950_005;
    private const uint MagicDebuff = 950_006;
    private const uint NoImmunitiesBolt = 950_007;
    private const uint NoSchoolImmunitiesBolt = 950_008;
    private const uint FrostBuff = 950_009;
    private const uint IceBlock = 950_010;
    private const uint DivineShield = 950_011;
    private const uint HarmfulImmunity = 950_012;
    private const uint StunImmunity = 950_013;
    private const uint MaskImmunity = 950_014;
    private const uint DispelImmunity = 950_015;
    private const uint EffectImmunity = 950_016;
    private const uint StateImmunity = 950_017;
    private const uint DamageImmunity = 950_018;
    private const uint FrostBolt = 950_019;

    private const uint PurgesEffect = 0x8000;
    private const uint HostileAndFriendly = 0x10000;

    private static SpellInfo Immunity(uint id, AuraType type, int misc, bool purges = false, bool both = false, bool negative = false)
    {
        SpellInfo spell = RuleTestSupport.Grant(id, type, 0, misc);
        uint ex = (purges ? PurgesEffect : 0) | (both ? HostileAndFriendly : 0);
        return spell with
        {
            AttributesEx = (SpellAttributesEx)ex,
            Attributes = negative ? SpellAttributes.AuraIsDebuff : SpellAttributes.None,
            Duration = new SpellDuration(30_000, 0, 30_000),
        };
    }

    private static SpellInfo Hostile(uint id, SpellSchool school, params SpellEffectInfo[] effects) =>
        SpellTestKit.Spell(id, effects) with
        {
            School = school,
            DamageClass = SpellDamageClass.Magic,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            Duration = new SpellDuration(30_000, 0, 30_000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellEffectInfo Damage(int value = 6) => SpellTestKit.Effect(SpellEffectName.SchoolDamage, value, SpellImplicitTarget.UnitEnemy);

    private static SpellEffectInfo StunAura() => SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModStun);

    private static SpellTestKit Kit() => new(
        Hostile(FireBolt, SpellSchool.Fire, Damage()),
        Hostile(FrostBolt, SpellSchool.Frost, Damage()),
        SpellTestKit.Spell(Heal, SpellTestKit.Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend)) with { School = SpellSchool.Holy, DamageClass = SpellDamageClass.Magic },
        Hostile(FrostDot, SpellSchool.Frost, SpellTestKit.Effect(SpellEffectName.ApplyAura, 3, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000))
            with { Attributes = SpellAttributes.AuraIsDebuff },
        Hostile(StunSpell, SpellSchool.Nature, StunAura()) with { Mechanic = (uint)SpellMechanic.Stun },
        Hostile(DamageAndStun, SpellSchool.Nature, Damage(), StunAura() with { Mechanic = (uint)SpellMechanic.Stun }),
        Hostile(MagicDebuff, SpellSchool.Arcane, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with { Dispel = (uint)DispelType.Magic },
        Hostile(NoImmunitiesBolt, SpellSchool.Fire, Damage()) with { Attributes = (SpellAttributes)SpellRuleFlags.NoImmunities },
        Hostile(NoSchoolImmunitiesBolt, SpellSchool.Fire, Damage()) with { AttributesEx2 = (SpellAttributesEx2)SpellRuleFlags.Ex2NoSchoolImmunities },
        Immunity(FrostBuff, AuraType.Dummy, 0),
        Immunity(IceBlock, AuraType.SchoolImmunity, (int)SpellSchoolMasks.All, purges: true),
        Immunity(DivineShield, AuraType.SchoolImmunity, (int)SpellSchoolMasks.All, purges: true, both: true),
        Immunity(HarmfulImmunity, AuraType.SchoolImmunity, (int)SpellSchoolMasks.All, purges: true, negative: true),
        Immunity(StunImmunity, AuraType.MechanicImmunity, (int)SpellMechanic.Stun),
        Immunity(MaskImmunity, AuraType.MechanicImmunityMask, (int)(SpellMechanics.Mask(SpellMechanic.Stun) | SpellMechanics.Mask(SpellMechanic.Fear))),
        Immunity(DispelImmunity, AuraType.DispelImmunity, (int)DispelType.Magic),
        Immunity(EffectImmunity, AuraType.EffectImmunity, (int)SpellEffectName.SchoolDamage),
        Immunity(StateImmunity, AuraType.StateImmunity, (int)AuraType.ModStun),
        Immunity(DamageImmunity, AuraType.DamageImmunity, (int)SpellSchoolMasks.All));

    private static (Player Caster, Player Victim) Pair(SpellTestKit kit)
    {
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };
        return (caster, victim);
    }

    private static SpellMissInfo Roll(SpellTestKit kit, Unit caster, Unit target, uint spell) =>
        new VanillaSpellCombatRules().RollHit(kit.System, caster, target, kit.Store.Get(spell)!);

    [Fact]
    public void IceBlock_MakesHostileSpellsImmune_AndLetsFriendlyOnesThrough()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, IceBlock);

        Assert.Equal(SpellMissInfo.Immune, Roll(kit, caster, victim, FireBolt));
        Assert.Equal(SpellMissInfo.Immune, Roll(kit, caster, victim, FrostBolt));
        Assert.Equal(SpellMissInfo.None, Roll(kit, caster, victim, Heal)); // same polarity as the immunity spell
    }

    [Fact]
    public void ADivineShieldShapedImmunity_ThatAlsoBlocksFriendlyEffects_BlocksHeals()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, DivineShield);

        Assert.Equal(SpellMissInfo.Immune, Roll(kit, caster, victim, Heal));
        Assert.Equal(SpellMissInfo.Immune, Roll(kit, caster, victim, FireBolt));
    }

    [Fact]
    public void NoImmunities_AndNoSchoolImmunities_BypassTheSchoolImmunity()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, IceBlock);

        Assert.Equal(SpellMissInfo.None, Roll(kit, caster, victim, NoImmunitiesBolt));
        Assert.Equal(SpellMissInfo.None, Roll(kit, caster, victim, NoSchoolImmunitiesBolt));
    }

    [Fact]
    public void AnImmunityToOneSchool_DoesNotBlockOtherSchools()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        kit.System.Random = new ScriptedRandom(9999, 9999);
        RuleTestSupport.Apply(kit, victim, DamageImmunity);

        Assert.Equal(SpellMissInfo.Immune, Roll(kit, caster, victim, FireBolt));
        Assert.Equal(SpellMissInfo.None, Roll(kit, caster, victim, Heal));
    }

    [Fact]
    public void ApplyingAPositiveImmunity_PurgesNegativeAurasOfThoseSchoolsOnly_AndFlagsTheUnit()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        kit.System.CastSpell(caster, FrostDot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        RuleTestSupport.Apply(kit, victim, FrostBuff);
        Assert.True(kit.System.HasAura(victim, FrostDot));

        RuleTestSupport.Apply(kit, victim, IceBlock);

        Assert.False(kit.System.HasAura(victim, FrostDot));
        Assert.True(kit.System.HasAura(victim, FrostBuff));
        Assert.True(kit.System.HasAura(victim, IceBlock));
        Assert.True((victim.UnitFlags & UnitFlags.Immune) != 0);

        kit.System.RemoveAuras(victim, IceBlock);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Immune);
    }

    [Fact]
    public void ANegativeImmunitySpell_DoesNotPurge()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        kit.System.CastSpell(caster, FrostDot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        RuleTestSupport.Apply(kit, victim, HarmfulImmunity);

        Assert.True(kit.System.HasAura(victim, FrostDot));
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Immune);
    }

    [Fact]
    public void MechanicImmunity_BlocksTheSpellsMechanic_AndStripsOnlyTheMatchingEffect()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        kit.System.ApplicationRules.Add(new ImmunityApplicationRule());
        RuleTestSupport.Apply(kit, victim, StunImmunity);
        uint before = victim.Health;

        Assert.Equal(SpellMissInfo.Immune, Roll(kit, caster, victim, StunSpell));
        kit.System.CastSpell(caster, DamageAndStun, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        Assert.Equal(before - 6, victim.Health);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Stunned);
        Assert.Equal(SpellMissInfo.None, Roll(kit, caster, victim, DamageAndStun)); // the spell itself has no mechanic: only its stun effect does
    }

    [Fact]
    public void MechanicImmunityMask_BlocksEveryMechanicOfItsMask()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, MaskImmunity);
        SpellInfo stun = kit.Store.Get(StunSpell)!;

        Assert.Equal(SpellMissInfo.Immune, new VanillaSpellCombatRules().RollHit(kit.System, caster, victim, stun));
        Assert.Equal(SpellMissInfo.Immune, new VanillaSpellCombatRules().RollHit(kit.System, caster, victim, stun with { Mechanic = (uint)SpellMechanic.Fear }));
        Assert.Equal(SpellMissInfo.None, new VanillaSpellCombatRules().RollHit(kit.System, caster, victim, stun with { Mechanic = (uint)SpellMechanic.Root }));
    }

    [Fact]
    public void DispelImmunity_BlocksSpellsOfThatDispelType()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, DispelImmunity);

        Assert.Equal(SpellMissInfo.Immune, Roll(kit, caster, victim, MagicDebuff));
        Assert.Equal(SpellMissInfo.None, Roll(kit, caster, victim, StunSpell));
    }

    [Fact]
    public void EffectImmunity_AndStateImmunity_RemoveJustTheirEffects()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        kit.System.ApplicationRules.Add(new ImmunityApplicationRule());
        RuleTestSupport.Apply(kit, victim, EffectImmunity);
        uint before = victim.Health;

        kit.System.CastSpell(caster, DamageAndStun, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.Equal(before, victim.Health); // the damage effect is immune
        Assert.True((victim.UnitFlags & UnitFlags.Stunned) != 0);

        kit.System.RemoveAuras(victim, DamageAndStun);
        kit.System.RemoveAuras(victim, EffectImmunity);
        RuleTestSupport.Apply(kit, victim, StateImmunity);
        kit.System.CastSpell(caster, DamageAndStun, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.Equal(before - 6, victim.Health);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Stunned); // the stun aura is immune
    }

    [Fact]
    public void AStateImmunityWithPurge_RemovesTheAurasItBlocks()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        kit.System.CastSpell(caster, StunSpell, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.True((victim.UnitFlags & UnitFlags.Stunned) != 0);

        RuleTestSupport.Apply(kit, victim, StateImmunity);
        Assert.True((victim.UnitFlags & UnitFlags.Stunned) != 0); // no purge attribute: the stun stays

        using SpellTestKit purging = new(Hostile(StunSpell, SpellSchool.Nature, StunAura()), Immunity(StateImmunity, AuraType.StateImmunity, (int)AuraType.ModStun, purges: true));
        (Player c2, Player v2) = Pair(purging);
        purging.System.CastSpell(c2, StunSpell, SpellCastTargets.ForUnit(v2.Guid), triggered: true);
        RuleTestSupport.Apply(purging, v2, StateImmunity);
        Assert.Equal(UnitFlags.None, v2.UnitFlags & UnitFlags.Stunned);
    }

    [Fact]
    public void ImmunityEnforcementOff_DisablesEveryCheck()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, IceBlock);
        kit.System.ImmunityEnforcement = false;
        kit.System.Random = new ScriptedRandom(9999);

        Assert.Equal(SpellMissInfo.None, Roll(kit, caster, victim, FireBolt));
    }

    [Fact]
    public void ADotTickOnAnImmuneTarget_DealsNothing_AndTellsTheClient()
    {
        using SpellTestKit kit = Kit();
        (Player caster, FakeSession casterSession) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };
        kit.System.CastSpell(caster, FrostDot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        RuleTestSupport.Apply(kit, victim, DamageImmunity); // no purge: the DoT stays on
        uint before = victim.Health;
        casterSession.Clear();

        kit.Advance(3100);

        Assert.Equal(before, victim.Health);
        Assert.Contains(casterSession.Sent, p => p.Opcode == WorldOpcode.SmsgSpellordamageImmune);
        Assert.DoesNotContain(casterSession.Sent, p => p.Opcode == WorldOpcode.SmsgPeriodicauralog);
    }

    // --- creature static immunities ---------------------------------------------------------------

    private sealed class FixedProvider(uint mechanicMask, uint schoolMask) : ICreatureImmunityProvider
    {
        public uint MechanicImmuneMask(Unit unit) => mechanicMask;

        public uint SchoolImmuneMask(Unit unit) => schoolMask;
    }

    [Fact]
    public void CreatureMechanicMask_BlocksMechanicSpellsFromOthers_NotSelfCasts()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        Creature boss = FoundationTests.MakeCreature(3, 63);
        kit.System.CreatureImmunities = new FixedProvider(SpellMechanics.Mask(SpellMechanic.Stun) | SpellMechanics.Mask(SpellMechanic.Fear), 0);
        SpellInfo stun = kit.Store.Get(StunSpell)!;

        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, stun, castOnSelf: false));
        Assert.False(ImmunityRules.IsImmuneToSpell(kit.System, boss, stun, castOnSelf: true));
        Assert.False(ImmunityRules.IsImmuneToSpell(kit.System, boss, stun with { Mechanic = (uint)SpellMechanic.Root }, castOnSelf: false));
        Assert.True(ImmunityRules.IsImmuneToSpellEffect(kit.System, boss, kit.Store.Get(DamageAndStun)!, 1, castOnSelf: false));
        Assert.False(ImmunityRules.IsImmuneToSpellEffect(kit.System, boss, kit.Store.Get(DamageAndStun)!, 0, castOnSelf: false));
        _ = caster;
    }

    [Fact]
    public void CreatureSchoolMask_BlocksHostileDamageOfThatSchool_ButNotPositiveSpells()
    {
        using SpellTestKit kit = Kit();
        Creature golem = FoundationTests.MakeCreature(1, 40);
        kit.System.CreatureImmunities = new FixedProvider(0, SpellSchoolMasks.Of(SpellSchool.Frost));

        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, golem, kit.Store.Get(FrostBolt)!, castOnSelf: false));
        Assert.True(ImmunityRules.IsImmuneToDamage(kit.System, golem, SpellSchoolMasks.Of(SpellSchool.Frost), kit.Store.Get(FrostBolt)));
        Assert.False(ImmunityRules.IsImmuneToSpell(kit.System, golem, kit.Store.Get(FireBolt)!, castOnSelf: false));
        Assert.False(ImmunityRules.IsImmuneToSpell(kit.System, golem, kit.Store.Get(FrostBuff)! with { School = SpellSchool.Frost }, castOnSelf: false));
        Assert.False(ImmunityRules.IsImmuneToDamage(kit.System, golem, SpellSchoolMasks.Of(SpellSchool.Frost), kit.Store.Get(NoImmunitiesBolt)));
    }

    [Fact]
    public void ABossShapedCreature_IsImmuneToAStunCast_ThroughTheHitRoll()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        Creature boss = FoundationTests.MakeCreature(3, 63);
        kit.System.CreatureImmunities = new FixedProvider(SpellMechanics.Mask(SpellMechanic.Stun), 0);

        Assert.Equal(SpellMissInfo.Immune, Roll(kit, caster, boss, StunSpell));
    }
}
