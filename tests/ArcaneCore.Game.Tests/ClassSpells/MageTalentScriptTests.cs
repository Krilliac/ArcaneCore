using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mage;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Procs;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// Ignite and Combustion (vmangos scripts/spells/spell_mage.cpp:50-205). The spells keep their build 5875 rows (classic-db z2815 spell_template):
/// Ignite rank 5 12848 and rank 1 11119 (passive DUMMY aura, procFlags DEAL_HARMFUL_SPELL; spell_proc_event: school mask 4, procEx CRITICAL_HIT), the
/// Ignite damage over time 12654 (PERIODIC_DAMAGE every 2000 ms, 4 s, stack 5), Combustion 11129 (DUMMY proc aura, 3 charges, plus TRIGGER_SPELL
/// 28682; spell_proc_event school mask 4) and its buff 28682 (ADD_FLAT_MODIFIER crit chance 10, stack 10). The fire crits are offered to the proc engine
/// as the spell hit path offers them (a harmful fire spell, CRITICAL_HIT, the crit's original amount).
/// </summary>
public sealed class MageTalentScriptTests : IDisposable
{
    private const uint IgniteRank1 = 11119;
    private const uint IgniteRank5 = 12848;
    private const uint Fireball = 133;
    private const uint Frostbolt = 116;

    private readonly SpellTestKit _kit;
    private readonly Player _mage;
    private readonly Player _enemy;

    private static SpellInfo Passive(uint id, SpellEffectInfo effect) => Spell(id, effect) with
    {
        Attributes = SpellAttributes.Passive,
        Duration = new SpellDuration(-1, 0, -1),
        SpellFamilyName = 3,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo IgniteTalent(uint id) => Passive(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        ProcFlags = ProcFlags.DealHarmfulSpell,
        ProcChance = 100,
    };

    private static SpellInfo Bolt(uint id, SpellSchool school) => Spell(id, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with
    {
        School = school, SpellFamilyName = 3, RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    public MageTalentScriptTests()
    {
        _kit = new SpellTestKit(
            IgniteTalent(IgniteRank1),
            IgniteTalent(IgniteRank5),
            Spell(IgniteScript.IgniteDot, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 2000)) with
            {
                School = SpellSchool.Fire,
                SpellFamilyName = 3,
                SpellFamilyFlags = 0x8000000,
                StackAmount = IgniteScript.MaxStacks,
                Duration = new SpellDuration(4000, 0, 4000),
                RangeIndex = 13,
                Range = new SpellRange(0, 50_000),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Passive(CombustionScript.ProcAura, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            {
                Attributes = 0,
                School = SpellSchool.Fire,
                ProcFlags = ProcFlags.DealHarmfulSpell,
                ProcChance = 100,
                ProcCharges = 3,
                Effects =
                [
                    Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy),
                    Effect(SpellEffectName.TriggerSpell, 0, trigger: CombustionScript.CritBuff),
                ],
            },
            Passive(CombustionScript.CritBuff, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.AddFlatModifier, misc: 7)) with
            {
                Attributes = 0,
                School = SpellSchool.Fire,
                StackAmount = 10,
            },
            Bolt(Fireball, SpellSchool.Fire),
            Bolt(Frostbolt, SpellSchool.Frost));
        (_mage, _) = _kit.AddPlayer(1);
        (_enemy, _) = _kit.AddPlayer(2, 5, 0);
        _kit.System.Relations = new FakeRelations { Hostile = { _enemy.Guid } };
        _kit.System.CombatRules = new NoCritNoResistRules();
        _kit.System.ProcEvents = new ProcCatalog(
            new SpellProcEventRecord(IgniteRank1, 4, 0, 0, 0, 0, 0, (uint)ProcFlagsEx.CriticalHit, 0, 0, 0),
            new SpellProcEventRecord(IgniteRank5, 4, 0, 0, 0, 0, 0, (uint)ProcFlagsEx.CriticalHit, 0, 0, 0),
            new SpellProcEventRecord(CombustionScript.ProcAura, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        _enemy.MaxHealth = 100_000;
        _enemy.Health = 100_000;
    }

    public void Dispose() => _kit.Dispose();

    /// <summary>The proc event of a spell of <paramref name="spellId"/> that hit the enemy for <paramref name="amount"/> (a crit or not).</summary>
    private void FireHit(uint spellId, uint amount, bool crit)
        => _kit.System.ProcDamageAndSpell(_mage, new ProcEvent
        {
            Victim = _enemy,
            AttackerFlags = ProcFlags.DealHarmfulSpell,
            VictimFlags = ProcFlags.TakeHarmfulSpell,
            Extra = crit ? ProcFlagsEx.CriticalHit : ProcFlagsEx.NormalHit,
            Amount = amount,
            OriginalAmount = amount,
            ProcSpell = _kit.Store.Get(spellId),
        });

    private void Learn(uint passive)
    {
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_mage, passive, SpellCastTargets.ForSelf(), triggered: true));
        _kit.Advance(100);
    }

    private SpellAuraHolder? Holder(Unit unit, uint spell) => _kit.System.GetAuras(unit).FirstOrDefault(h => !h.IsRemoved && h.Spell.Id == spell);

    /// <summary>The Ignite damage over time on the enemy (the test fails when there is none).</summary>
    private SpellAuraHolder Ignite() => Assert.IsType<SpellAuraHolder>(Holder(_enemy, IgniteScript.IgniteDot));

    // --- Ignite -------------------------------------------------------------------------------------------------

    [Fact]
    public void Ignite_ACritPutsTwentyPercentPerTickOnTheVictim_AndTheTicksDealIt()
    {
        Learn(IgniteRank5);

        FireHit(Fireball, 1000, crit: true);

        SpellAuraHolder ignite = Ignite();
        Assert.Equal(_mage.Guid, ignite.CasterGuid);
        Assert.Equal(200, ignite.Auras[0]!.Amount);          // int32(0.20f * 1000) per tick
        Assert.Equal(1, ignite.StackAmount);
        _kit.Advance(4000);
        Assert.Equal(100_000u - 400, _enemy.Health);          // two ticks of 200: 40% of the crit over 4 seconds
        Assert.Null(Holder(_enemy, IgniteScript.IgniteDot));
    }

    [Fact]
    public void Ignite_TheRankSetsTheShare()
    {
        Learn(IgniteRank1);

        FireHit(Fireball, 1000, crit: true);

        Assert.Equal(40, Ignite().Auras[0]!.Amount); // 4%
    }

    [Fact]
    public void Ignite_NeedsAFireCrit()
    {
        Learn(IgniteRank5);

        FireHit(Fireball, 1000, crit: false);
        FireHit(Frostbolt, 1000, crit: true);

        Assert.Null(Holder(_enemy, IgniteScript.IgniteDot));
    }

    [Fact]
    public void Ignite_ASecondCritAddsItsShare_AStack_AndRollsTheTickDamageIntoAFreshFourSeconds()
    {
        Learn(IgniteRank5);
        FireHit(Fireball, 1000, crit: true);
        _kit.Advance(2000);                                   // one tick of 200 dealt, one left
        SpellAuraHolder ignite = Ignite();
        Assert.Equal(1, ignite.Auras[0]!.TickCount);
        Assert.Equal(100_000u - 200, _enemy.Health);

        FireHit(Fireball, 500, crit: true);

        Assert.Same(ignite, Ignite());
        Assert.Equal(300, ignite.Auras[0]!.Amount);          // 200 + int32(0.20f * 500)
        Assert.Equal(2, ignite.StackAmount);
        Assert.Equal(0, ignite.Auras[0]!.TickCount);          // the ticks start over
        Assert.Equal(4000, ignite.Duration);                  // a fresh 4 seconds
        _kit.Advance(4000);
        Assert.Equal(100_000u - 200 - 300 - 300, _enemy.Health);
        Assert.Null(Holder(_enemy, IgniteScript.IgniteDot));
    }

    [Fact]
    public void Ignite_AtFiveStacks_ACritOnlyRefreshes()
    {
        Learn(IgniteRank5);
        for (int i = 0; i < 5; i++)
        {
            FireHit(Fireball, 1000, crit: true);
        }

        SpellAuraHolder ignite = Ignite();
        Assert.Equal((5, 1000), (ignite.StackAmount, ignite.Auras[0]!.Amount));
        _kit.Advance(1000);

        FireHit(Fireball, 1000, crit: true);

        Assert.Equal((5, 1000), (ignite.StackAmount, ignite.Auras[0]!.Amount));
        Assert.Equal(4000, ignite.Duration);
    }

    // --- Combustion ---------------------------------------------------------------------------------------------

    [Fact]
    public void Combustion_EveryFireHitAddsAStack_OnlyCritsSpendCharges_AndTheThirdCritEndsIt()
    {
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_mage, CombustionScript.ProcAura, SpellCastTargets.ForSelf(), triggered: true));
        _kit.Advance(100);
        SpellAuraHolder proc = Assert.IsType<SpellAuraHolder>(Holder(_mage, CombustionScript.ProcAura));
        SpellAuraHolder buff = Assert.IsType<SpellAuraHolder>(Holder(_mage, CombustionScript.CritBuff));
        Assert.Equal((3, 1), (proc.Charges, (int)buff.StackAmount)); // the cast's TRIGGER_SPELL gives the first stack

        FireHit(Fireball, 100, crit: false);
        FireHit(Fireball, 100, crit: false);
        Assert.Equal((3, 3), (proc.Charges, (int)buff.StackAmount)); // a hit adds a stack, no charge
        Assert.Equal(30, buff.Auras[0]!.Amount);                     // 10% per stack

        FireHit(Frostbolt, 100, crit: true);
        Assert.Equal((3, 3), (proc.Charges, (int)buff.StackAmount)); // not fire

        FireHit(Fireball, 100, crit: true);
        Assert.Equal((2, 4), (proc.Charges, (int)buff.StackAmount));
        FireHit(Fireball, 100, crit: true);
        Assert.Equal((1, 5), (proc.Charges, (int)buff.StackAmount));

        FireHit(Fireball, 100, crit: true);                         // the last charge and a crit: the buff ends, the charge goes
        Assert.Null(Holder(_mage, CombustionScript.CritBuff));
        Assert.Null(Holder(_mage, CombustionScript.ProcAura));
    }

    [Fact]
    public void Combustion_CancellingTheBuff_RemovesTheProcAura()
    {
        _kit.System.CastSpell(_mage, CombustionScript.ProcAura, SpellCastTargets.ForSelf(), triggered: true);

        _kit.System.RemoveAuras(_mage, CombustionScript.CritBuff, AuraRemoveMode.Cancel);

        Assert.Null(Holder(_mage, CombustionScript.ProcAura));
    }

    [Fact]
    public void Combustion_AProcWithoutTheBuff_EndsTheProcAura()
    {
        _kit.System.CastSpell(_mage, CombustionScript.ProcAura, SpellCastTargets.ForSelf(), triggered: true);
        _kit.Advance(100);
        _kit.System.RemoveAuras(_mage, CombustionScript.CritBuff, AuraRemoveMode.Dispel); // dispelled: not a cancel
        Assert.NotNull(Holder(_mage, CombustionScript.ProcAura));

        FireHit(Fireball, 100, crit: false);

        Assert.Null(Holder(_mage, CombustionScript.ProcAura));
        Assert.Null(Holder(_mage, CombustionScript.CritBuff));
    }
}

/// <summary>Every spell lands, nothing crits and nothing is resisted, so the amounts are exact.</summary>
internal sealed class NoCritNoResistRules : VanillaSpellCombatRules
{
    public override SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => SpellMissInfo.None;

    public override bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => false;

    public override uint RollPartialResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage) => 0;

    public override int RollResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage, bool periodic) => 0;
}

/// <summary>A spell_proc_event table of explicit rows.</summary>
internal sealed class ProcCatalog(params SpellProcEventRecord[] rows) : ISpellProcEventCatalog
{
    public SpellProcEventRecord? Find(uint spellId) => rows.FirstOrDefault(r => r.Entry == spellId);
}
