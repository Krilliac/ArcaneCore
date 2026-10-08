using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Paladin;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// The paladin class scripts (vmangos UnitAuraProcHandler.cpp:979-1053 and :1428-1466, SpellEffects.cpp:4502-4529, scripts/spells/spell_paladin.cpp,
/// SpellAuras.cpp:6815-6893, Unit.cpp:3355-3560, Spell.cpp:5634-5636). The spells carry their build 5875 ids, families, flags and the effects the
/// scripts read; amounts are chosen to keep the arithmetic visible.
/// </summary>
public sealed class PaladinScriptTests : IDisposable
{
    private const uint SealOfRighteousness = 21084;      // rank 1 (dummy aura; effect 2 names JoR 20187)
    private const uint SorDamage = 25742;
    private const uint JudgementOfRighteousness = 20187;
    private const uint SealOfLight = 20165;               // effect 2 names JoL 20185
    private const uint SealOfLightHeal = 20167;
    private const uint JudgementOfLight = 20185;
    private const uint JudgementOfLightHeal = 20267;
    private const uint BlessingOfMight1 = 19740;
    private const uint BlessingOfMight2 = 19834;
    private const uint BlessingOfWisdom = 19742;
    private const uint DivineShield = 642;
    private const uint HolyShock = 20473;
    private const uint HolyShockHurt = 25912;
    private const uint HolyShockHeal = 25914;
    private const uint JocDummy = 20425;
    private const uint JocDamage = 20467;
    private const uint Stun = 990_601;
    private const uint HammerOfWrath = 24239;

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo Seal(uint id, SpellEffectInfo first, uint judgement) => Instant(Spell(id,
        first,
        new SpellEffectInfo(),
        Effect(SpellEffectName.ApplyAura, (int)judgement, aura: AuraType.Dummy)) with
    {
        Name = $"Seal {id}",
        SpellFamilyName = PaladinSpells.Family,
        SpellFamilyFlags = 1UL << 27,
        ProcFlags = ProcFlags.DealMeleeSwing,
        ProcChance = 100,
        Duration = new SpellDuration(30_000, 0, 30_000),
    });

    private static SpellInfo Blessing(uint id, string name, AuraType aura, int amount) => Instant(Spell(id,
        Effect(SpellEffectName.ApplyAura, amount, SpellImplicitTarget.UnitFriend, aura)) with
    {
        Name = name,
        SpellFamilyName = PaladinSpells.Family,
        SpellFamilyFlags = 0x10000000,
        Duration = new SpellDuration(300_000, 0, 300_000),
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
    });

    private static SpellInfo AtEnemy(SpellInfo spell) => Instant(spell with { RangeIndex = 4, Range = new SpellRange(0, 30) });

    private readonly SpellTestKit _kit;
    private readonly Player _paladin;
    private readonly Player _enemy;
    private readonly Player _friend;

    public PaladinScriptTests()
    {
        _kit = new SpellTestKit(
            Seal(SealOfRighteousness, Effect(SpellEffectName.ApplyAura, 1000, aura: AuraType.Dummy), JudgementOfRighteousness),
            AtEnemy(Spell(SorDamage, Effect(SpellEffectName.SchoolDamage, 0, SpellImplicitTarget.UnitEnemy)) with { School = SpellSchool.Holy }),
            AtEnemy(Spell(JudgementOfRighteousness, Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy)) with
            {
                School = SpellSchool.Holy, SpellFamilyName = PaladinSpells.Family, SpellFamilyFlags = 0x400, BaseLevel = 1,
            }),
            Seal(SealOfLight, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ProcTriggerSpell, trigger: SealOfLightHeal), JudgementOfLight) with
            {
                ProcFlags = ProcFlags.None, // keep the seal's own proc out of the way
            },
            Instant(Spell(SealOfLightHeal, Effect(SpellEffectName.Heal, 10))),
            AtEnemy(Spell(JudgementOfLight, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ProcTriggerSpell, trigger: 5373)) with
            {
                Name = "Judgement of Light",
                SpellFamilyName = PaladinSpells.Family,
                SpellFamilyFlags = 0x80000,
                BaseLevel = 1,
                ProcFlags = ProcFlags.TakeMeleeSwing,
                ProcChance = 100,
                Duration = new SpellDuration(10_000, 0, 10_000),
                Attributes = SpellAttributes.AuraIsDebuff,
            }),
            Instant(Spell(JudgementOfLightHeal, Effect(SpellEffectName.Heal, 25))),
            AtEnemy(Spell(PaladinSpells.Judgement, Effect(SpellEffectName.ScriptEffect, 0, SpellImplicitTarget.UnitEnemy)) with
            {
                SpellFamilyName = PaladinSpells.Family,
                SpellFamilyFlags = 1UL << 23,
                CasterAuraState = AuraState.Judgement,
            }),
            Blessing(BlessingOfMight1, "Blessing of Might", AuraType.ModAttackPower, 20),
            Blessing(BlessingOfMight2, "Blessing of Might", AuraType.ModAttackPower, 36),
            Blessing(BlessingOfWisdom, "Blessing of Wisdom", AuraType.PeriodicEnergize, 10) with { SpellFamilyFlags = 0x10010000 },
            Instant(Spell(DivineShield, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.SchoolImmunity, misc: 1)) with
            {
                Mechanic = (uint)SpellMechanic.Invulnerability,
                Duration = new SpellDuration(10_000, 0, 10_000),
            }),
            Instant(Spell(ForbearanceObserver.Forbearance, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.MechanicImmunity,
                misc: (int)SpellMechanic.Invulnerability)) with
            {
                Mechanic = (uint)SpellMechanic.Invulnerability,
                School = SpellSchool.Holy,
                SpellFamilyName = PaladinSpells.Family,
                Attributes = (SpellAttributes)0x24000000, // AURA_IS_DEBUFF | NO_IMMUNITIES (build 5875 row)
                Duration = new SpellDuration(60_000, 0, 60_000),
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
            }),
            AtEnemy(Spell(HolyShock, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.Unit)) with { SpellFamilyName = PaladinSpells.Family }),
            AtEnemy(Spell(HolyShockHurt, Effect(SpellEffectName.SchoolDamage, 30, SpellImplicitTarget.UnitEnemy)) with { School = SpellSchool.Holy }),
            AtEnemy(Spell(HolyShockHeal, Effect(SpellEffectName.Heal, 40, SpellImplicitTarget.UnitFriend))),
            AtEnemy(Spell(JocDummy, Effect(SpellEffectName.Dummy, (int)JocDamage, SpellImplicitTarget.UnitEnemy))),
            AtEnemy(Spell(JocDamage, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with
            {
                School = SpellSchool.Holy, DamageClass = SpellDamageClass.Melee,
            }),
            AtEnemy(Spell(Stun, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModStun)) with
            {
                Duration = new SpellDuration(5_000, 0, 5_000),
            }),
            AtEnemy(Spell(HammerOfWrath, Effect(SpellEffectName.SchoolDamage, 500, SpellImplicitTarget.UnitEnemy)) with
            {
                School = SpellSchool.Holy, SpellFamilyName = PaladinSpells.Family, DamageClass = SpellDamageClass.Ranged,
            }));
        (_paladin, _) = _kit.AddPlayer(1, 0, 0);
        (_enemy, _) = _kit.AddPlayer(2, 3, 0);
        (_friend, _) = _kit.AddPlayer(3, -3, 0);
        _kit.System.Relations = new FakeRelations { Hostile = { _enemy.Guid } };
        SpellScriptDispatcher.Install(_kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        AuraStateCastChecks.Install(_kit.System, new AuraStateService(_kit.System, _ => []));
        foreach (Player player in new[] { _paladin, _enemy, _friend })
        {
            player.MaxHealth = 10_000;
            player.Health = 5_000;
        }
    }

    public void Dispose() => _kit.Dispose();

    private SpellAuraHolder? Holder(Unit unit, uint spell) => _kit.System.GetAuras(unit).FirstOrDefault(h => !h.IsRemoved && h.Spell.Id == spell);

    private SpellCastResult Cast(Unit caster, uint spell, Unit? target = null)
        => _kit.System.CastSpell(caster, spell, target is null ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(target.Guid), triggered: true);

    /// <summary>A seal put on a millisecond before the event under test (an aura applied at the event's own time does not proc from it).</summary>
    private void PutOn(uint seal)
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(_paladin, seal));
        _kit.Now++;
    }

    private static MeleeDamageInfo Swing(Unit attacker, Unit victim) => new()
    {
        Attacker = attacker,
        Target = victim,
        AttackType = WeaponAttackType.BaseAttack,
        Outcome = MeleeHitOutcome.Normal,
        HitInfo = HitInfo.AffectsVictim,
        TargetState = VictimState.Normal,
        TotalDamage = 10,
    };

    private bool HasJudgementState(Unit unit) => (unit.GetUInt32(UpdateFields.UnitFieldAurastate) & PaladinAuraRules.JudgementStateBit) != 0;

    [Fact]
    public void ASeal_SetsTheJudgementAuraState_AndTheLastSealClearsIt()
    {
        PutOn(SealOfRighteousness);
        Assert.True(HasJudgementState(_paladin));

        _kit.System.RemoveAuras(_paladin, SealOfRighteousness);

        Assert.False(HasJudgementState(_paladin));
    }

    [Fact]
    public void ASecondSeal_ReplacesTheFirst()
    {
        PutOn(SealOfRighteousness);
        PutOn(SealOfLight);

        Assert.Null(Holder(_paladin, SealOfRighteousness));
        Assert.NotNull(Holder(_paladin, SealOfLight));
        Assert.True(HasJudgementState(_paladin));
    }

    [Fact]
    public void Blessings_OnePerCaster_AndOneRankOfAChainPerTarget()
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(_paladin, BlessingOfMight2, _friend));
        Assert.Equal(SpellCastResult.CastOk, Cast(_paladin, BlessingOfWisdom, _friend));
        Assert.Null(Holder(_friend, BlessingOfMight2));            // the same caster's other blessing goes
        Assert.NotNull(Holder(_friend, BlessingOfWisdom));

        (Player ally, _) = _kit.AddPlayer(4, -2, 0);
        Cast(ally, BlessingOfMight2, _friend);                     // another caster's blessing stays beside it
        Assert.NotNull(Holder(_friend, BlessingOfWisdom));
        Assert.NotNull(Holder(_friend, BlessingOfMight2));

        Cast(_paladin, BlessingOfMight1, _friend);                 // a weaker rank of the chain never replaces a stronger one
        Assert.Null(Holder(_friend, BlessingOfMight1));
        Assert.NotNull(Holder(_friend, BlessingOfMight2));
        Assert.NotNull(Holder(_friend, BlessingOfWisdom));
    }

    [Fact]
    public void SealOfRighteousness_AWhiteSwing_CastsTheRanksDamageSpell_ScaledByWeaponSpeed()
    {
        PutOn(SealOfRighteousness);
        uint before = _enemy.Health;

        _kit.System.OnMeleeSwingResolved(Swing(_paladin, _enemy));

        // no weapon: 2.0 s; min 1000/87, max 1000/25: (40 - 11.494) * 0.2 + 11.494 = 17.195, dithered
        Assert.InRange(before - _enemy.Health, 17u, 18u);
        Assert.Equal(17.1954f, SealOfRighteousnessProc.BaseDamage(1000, 2.0f), 3);
        Assert.Equal(40f, SealOfRighteousnessProc.BaseDamage(1000, 4.0f), 3);
        Assert.Equal(1000f / 87, SealOfRighteousnessProc.BaseDamage(1000, 1.5f), 3);
    }

    [Fact]
    public void Judgement_CancelsTheSeal_AndCastsTheJudgementItNames()
    {
        PutOn(SealOfRighteousness);
        uint before = _enemy.Health;

        Assert.Equal(SpellCastResult.CastOk, Cast(_paladin, PaladinSpells.Judgement, _enemy));

        Assert.Equal(before - 15u, _enemy.Health);                   // Judgement of Righteousness
        Assert.Null(Holder(_paladin, SealOfRighteousness));
        Assert.False(HasJudgementState(_paladin));
    }

    [Fact]
    public void Judgement_WithoutASeal_FailsOnTheCasterAuraState()
    {
        Assert.Equal(SpellCastResult.CasterAurastate, Cast(_paladin, PaladinSpells.Judgement, _enemy));
    }

    [Fact]
    public void JudgementOfLight_HealsTheUnitThatStrikesTheJudgedTarget()
    {
        PutOn(SealOfLight);
        Assert.Equal(SpellCastResult.CastOk, Cast(_paladin, PaladinSpells.Judgement, _enemy));
        Assert.NotNull(Holder(_enemy, JudgementOfLight));
        _kit.Now++;
        uint before = _friend.Health;

        _kit.System.OnMeleeSwingResolved(Swing(_friend, _enemy));

        Assert.Equal(before + 25u, _friend.Health);
    }

    [Fact]
    public void ABubble_PutsForbearanceOnItsTarget_AndForbearanceRefusesTheNextBubble()
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(_paladin, DivineShield));
        Assert.NotNull(Holder(_paladin, ForbearanceObserver.Forbearance));

        _kit.System.RemoveAuras(_paladin, DivineShield);
        _kit.System.ClearCooldown(_paladin, DivineShield);

        Assert.Equal(SpellCastResult.TargetAurastate, Cast(_paladin, DivineShield));
        Assert.Null(Holder(_paladin, DivineShield));
    }

    [Fact]
    public void HolyShock_HealsAFriend_HurtsAnEnemyInFront_AndNeedsTheEnemyInFront()
    {
        uint friendBefore = _friend.Health;
        Assert.Equal(SpellCastResult.CastOk, Cast(_paladin, HolyShock, _friend));
        Assert.Equal(friendBefore + 40u, _friend.Health);

        _paladin.Orientation = MathF.PI; // the enemy at +x is behind
        Assert.Equal(SpellCastResult.UnitNotInfront, Cast(_paladin, HolyShock, _enemy));

        _paladin.Orientation = 0;
        uint enemyBefore = _enemy.Health;
        Assert.Equal(SpellCastResult.CastOk, Cast(_paladin, HolyShock, _enemy));
        Assert.Equal(enemyBefore - 30u, _enemy.Health);
    }

    [Fact]
    public void JudgementOfCommand_CastsTheNamedDamage_HalvedUnlessTheTargetIsStunned()
    {
        uint before = _enemy.Health;
        Cast(_paladin, JocDummy, _enemy);
        Assert.Equal(before - 50u, _enemy.Health);

        Cast(_paladin, Stun, _enemy);
        _enemy.UnitFlags |= UnitFlags.Stunned;
        before = _enemy.Health;
        Cast(_paladin, JocDummy, _enemy);
        Assert.Equal(before - 100u, _enemy.Health);
    }

    /// <summary>Adds 100 to a direct magic-class damage amount only: tells the spell formulas from the weapon ones.</summary>
    private sealed class MagicOnlyBonus : ISpellAmountModifier
    {
        public float Modify(SpellAmountStage stage, Unit caster, Unit target, SpellInfo spell, int effectIndex, float amount, uint stack)
            => stage == SpellAmountStage.DirectDamage && spell.DamageClass == SpellDamageClass.Magic ? amount + 100 : amount;
    }

    [Fact]
    public void HammerOfWrath_TakesTheSpellDamageBonuses_ThoughItIsARangedSpell()
    {
        _kit.System.AmountModifier = new MagicOnlyBonus();
        uint before = _enemy.Health;

        Assert.Equal(SpellCastResult.CastOk, Cast(_paladin, HammerOfWrath, _enemy));

        Assert.Equal(before - 600u, _enemy.Health);
    }
}
