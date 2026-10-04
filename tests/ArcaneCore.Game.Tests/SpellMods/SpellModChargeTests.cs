using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.SpellMods.ModTestSupport;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>
/// Charged modifiers (Clearcasting, Nature's Grace, Nature's Swiftness, Shadow Trance shapes) after vmangos
/// Player::ApplySpellMod / DropModCharge / RestoreSpellMods / RemoveSpellMods (Player.cpp:17617-17783, 22444-22453) and
/// Spell::prepare / cast / cancel / finish (Spell.cpp:3395, 3436, 3534, 3646-3658, 3834, 4364, 4399): a charge is spent by the
/// cast that uses it, a mod with no charge left stays pinned to that cast and its aura goes when the cast succeeds, a cancelled or
/// failed cast gives the charge back. Synthetic spells; the casts are stepped by the test clock.
/// </summary>
public sealed class SpellModChargeTests
{
    private const uint Bolt = 945001;          // 2 s cast, 100 rage, family mask 1
    private const uint Instant = 945002;       // instant, 40 rage, family mask 1
    private const uint OtherBolt = 945003;     // 2 s cast, 100 rage, family mask 2 (not covered)
    private const uint FreeCast = 945010;      // Clearcasting shape: pct cost -100, 1 charge
    private const uint TwoCharges = 945011;    // pct cost -50, 2 charges
    private const uint GraceShape = 945012;    // flat cast time -500, 1 charge
    private const uint SwiftShape = 945013;    // pct cast time -100, 1 charge
    private const uint Stacky = 945014;        // flat damage +1, 3 charges but StackAmount 3 -> never consumed
    private const uint Channel = 945020;       // channelled, 100 rage, family mask 1
    private const uint ShadowTrance = 17941;   // custom 1 charge
    private const uint CritShape = 945030;     // flat crit +100, 1 charge
    private const uint CritBolt = 945031;      // instant fire bolt, family mask 1
    private const uint ProcAura = 945032;      // a timed (saveable) buff granting a pct cost -100 mod with 2 charges

    private static SpellTestKit Kit() => new(
        InFamily(Spell(Bolt, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            CastTime = new SpellCastTime(2000, 0, 0),
            PowerType = (int)PowerType.Rage,
            ManaCost = 50,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }),
        InFamily(Spell(Instant, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            PowerType = (int)PowerType.Rage,
            ManaCost = 20,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }),
        InFamily(Spell(OtherBolt, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            CastTime = new SpellCastTime(2000, 0, 0),
            PowerType = (int)PowerType.Rage,
            ManaCost = 50,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }, flags: 2),
        ModPassive(FreeCast, AuraType.AddPctModifier, SpellModOp.Cost, -100, procCharges: 1),
        ModPassive(TwoCharges, AuraType.AddPctModifier, SpellModOp.Cost, -50, procCharges: 2),
        ModPassive(GraceShape, AuraType.AddFlatModifier, SpellModOp.CastingTime, -500, procCharges: 1),
        ModPassive(SwiftShape, AuraType.AddPctModifier, SpellModOp.CastingTime, -100, procCharges: 1),
        ModPassive(Stacky, AuraType.AddFlatModifier, SpellModOp.Damage, 1, procCharges: 3) with { StackAmount = 3 },
        InFamily(Spell(Channel, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(5000, 0, 5000),
            SpellVisual = 1,
            PowerType = (int)PowerType.Rage,
            ManaCost = 50,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }),
        ModPassive(ShadowTrance, AuraType.AddPctModifier, SpellModOp.CastingTime, -100),
        ModPassive(ProcAura, AuraType.AddPctModifier, SpellModOp.Cost, -100, procCharges: 2) with
        {
            Attributes = 0,
            Duration = new SpellDuration(15000, 0, 15000),
            SpellVisual = 1,
        },
        ModPassive(CritShape, AuraType.AddFlatModifier, SpellModOp.CriticalChance, 100, procCharges: 1),
        InFamily(Spell(CritBolt, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            School = SpellSchool.Fire,
            DamageClass = SpellDamageClass.Magic,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }));

    private static (SpellTestKit Kit, Player Caster, Player Target) Setup(params uint[] learn)
    {
        SpellTestKit kit = Kit();
        (Player caster, _) = CasterWithRage(kit, 1, 100);
        (Player target, _) = kit.AddPlayer(2, 3, 0);
        kit.Spellbook.Teach(caster, Bolt, Instant, OtherBolt, Channel);
        foreach (uint spell in learn)
        {
            kit.System.LearnSpell(caster, spell);
        }

        return (kit, caster, target);
    }

    private static SpellCastResult Cast(SpellTestKit kit, Player caster, Player target, uint spell)
        => kit.System.HandleCastRequest(caster, spell, SpellCastTargets.ForUnit(target.Guid));

    private static uint Rage(Player player) => SpellSystem.GetPower(player, PowerType.Rage);

    private static SpellMod? ModOf(SpellTestKit kit, Player player, uint spell, SpellModOp op)
        => kit.System.Mods.ModsOf(player, op).SingleOrDefault(m => m.SpellId == spell);

    [Fact]
    public void AChargedMod_IsRegistered_WithItsCharges()
    {
        (SpellTestKit kit, Player caster, _) = Setup(FreeCast, TwoCharges);
        using (kit)
        {
            Assert.Equal(1, ModOf(kit, caster, FreeCast, SpellModOp.Cost)!.Charges);
            Assert.Equal(2, ModOf(kit, caster, TwoCharges, SpellModOp.Cost)!.Charges);
        }
    }

    [Fact]
    public void ClearcastingShape_MakesTheNextCastFree_ThenItsAuraIsGone()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(FreeCast);
        using (kit)
        {
            Assert.Equal(SpellCastResult.CastOk, Cast(kit, caster, target, Instant));

            Assert.Equal(100u, Rage(caster));                         // free
            Assert.False(kit.System.HasAura(caster, FreeCast));       // the pinned mod's aura went with the successful cast
            Assert.Null(ModOf(kit, caster, FreeCast, SpellModOp.Cost));

            Assert.Equal(SpellCastResult.CastOk, Cast(kit, caster, target, Instant));
            Assert.Equal(80u, Rage(caster));                          // the next one pays
        }
    }

    [Fact]
    public void ACastNotCoveredByTheMask_DoesNotSpendTheCharge()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(FreeCast);
        using (kit)
        {
            Cast(kit, caster, target, OtherBolt);
            kit.Advance(2100);

            Assert.Equal(50u, Rage(caster));                          // paid in full
            Assert.Equal(1, ModOf(kit, caster, FreeCast, SpellModOp.Cost)!.Charges);
        }
    }

    [Fact]
    public void TwoCharges_SurviveTheFirstCast_WithOneLeft()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(TwoCharges);
        using (kit)
        {
            Cast(kit, caster, target, Instant);

            Assert.Equal(100u - 10u, Rage(caster));
            Assert.Equal(1, ModOf(kit, caster, TwoCharges, SpellModOp.Cost)!.Charges);
            Assert.True(kit.System.HasAura(caster, TwoCharges));
        }
    }

    [Fact]
    public void ACostModOnACastTimeSpell_IsSpentWhenTheBarEnds_NotWhenItStarts()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(FreeCast);
        using (kit)
        {
            Assert.Equal(SpellCastResult.CastOk, Cast(kit, caster, target, Bolt));

            // vmangos prepare computes the cost without dropping a charge (Spell.cpp:3395); the bar end does (:3646).
            Assert.Equal(1, ModOf(kit, caster, FreeCast, SpellModOp.Cost)!.Charges);
            kit.Advance(2100);

            Assert.Equal(100u, Rage(caster));
            Assert.False(kit.System.HasAura(caster, FreeCast));
        }
    }

    [Fact]
    public void CancellingWhilePreparing_GivesTheCastTimeChargeBack()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(GraceShape);
        using (kit)
        {
            Assert.Equal(SpellCastResult.CastOk, Cast(kit, caster, target, Bolt));
            SpellMod mod = ModOf(kit, caster, GraceShape, SpellModOp.CastingTime)!;
            Assert.Equal(-1, mod.Charges);                            // consumed by the cast time, pinned to this cast
            Assert.Equal(1500, kit.System.GetState(caster.Guid)!.CurrentCast!.CastTime);

            kit.System.Interrupt(kit.System.GetState(caster.Guid)!.CurrentCast!);

            Assert.Equal(1, mod.Charges);                             // vmangos RestoreSpellMods: -1 becomes 1
            Assert.True(kit.System.HasAura(caster, GraceShape));
        }
    }

    [Fact]
    public void ANatureSwiftnessShape_MakesTheBoltInstant_AndIsRemovedAfterwards()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(SwiftShape);
        using (kit)
        {
            Cast(kit, caster, target, Bolt);

            Assert.True(target.Health < 60u);                         // already landed: the bolt was instant
            Assert.False(kit.System.HasAura(caster, SwiftShape));
        }
    }

    [Fact]
    public void AFlatCastTimeMod_IsNotSpent_WhenAnInstantPctModCoversTheSpell_NaturesGraceAfterSwiftness()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(GraceShape, SwiftShape);
        using (kit)
        {
            Cast(kit, caster, target, Bolt);

            // Patch 1.11: Nature's Grace is not consumed by a spell Nature's Swiftness made instant (Player.cpp:22444-22453).
            Assert.Equal(1, ModOf(kit, caster, GraceShape, SpellModOp.CastingTime)!.Charges);
            Assert.False(kit.System.HasAura(caster, SwiftShape));
            Assert.True(kit.System.HasAura(caster, GraceShape));
        }
    }

    [Fact]
    public void TheNaturesGraceRule_CanBeSwitchedOff()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(GraceShape, SwiftShape);
        using (kit)
        {
            kit.System.Mods.Options.InstantCastKeepsFlatCastTimeCharge = false;

            Cast(kit, caster, target, Bolt);

            Assert.False(kit.System.HasAura(caster, GraceShape));
        }
    }

    [Fact]
    public void AnInstantSpell_SpendsNoCastTimeCharge()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(GraceShape);
        using (kit)
        {
            Cast(kit, caster, target, Instant);

            Assert.Equal(1, ModOf(kit, caster, GraceShape, SpellModOp.CastingTime)!.Charges);   // cast time 0: never asked (SpellEntry.cpp:488-494)
        }
    }

    [Fact]
    public void APctModOnAZeroValue_SpendsNothing()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(SwiftShape);
        using (kit)
        {
            Cast(kit, caster, target, Instant);   // an instant spell: cast time 0, the pct mod is skipped

            Assert.Equal(1, ModOf(kit, caster, SwiftShape, SpellModOp.CastingTime)!.Charges);
        }
    }

    [Fact]
    public void AFailedCheckCast_SpendsNoCharge()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(SwiftShape);
        using (kit)
        {
            SpellSystem.SetPower(caster, PowerType.Rage, 10);   // not enough for Bolt

            Assert.Equal(SpellCastResult.NoPower, Cast(kit, caster, target, Bolt));

            Assert.Equal(1, ModOf(kit, caster, SwiftShape, SpellModOp.CastingTime)!.Charges);
        }
    }

    [Fact]
    public void ACastThatFailsAtTheBarEnd_RestoresTheCharge()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(TwoCharges);
        using (kit)
        {
            Cast(kit, caster, target, Bolt);
            SpellSystem.SetPower(caster, PowerType.Rage, 10);   // the rage went away while casting
            kit.Advance(2100);

            Assert.Equal(2, ModOf(kit, caster, TwoCharges, SpellModOp.Cost)!.Charges);
        }
    }

    [Fact]
    public void AChannel_RemovesAPinnedModWhenTheChannelStarts()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(FreeCast);
        using (kit)
        {
            Assert.Equal(SpellCastResult.CastOk, Cast(kit, caster, target, Channel));

            Assert.Equal(100u, Rage(caster));
            Assert.False(kit.System.HasAura(caster, FreeCast));   // gone at channel start, not at its end (Spell.cpp:3834)
            Assert.NotNull(kit.System.GetState(caster.Guid)!.CurrentCast);
        }
    }

    [Fact]
    public void ASavedChargedMod_ComesBackWithItsHolderCharges_OnRelog()
    {
        (SpellTestKit kit, Player caster, _) = Setup();
        using (kit)
        {
            kit.System.CastSpell(caster, ProcAura, SpellCastTargets.ForSelf(), triggered: true);
            Assert.Equal(2, ModOf(kit, caster, ProcAura, SpellModOp.Cost)!.Charges);
            SpellStateSnapshot saved = kit.System.CaptureState(caster, 1_800_000_000_000);
            kit.System.RemoveAuras(caster, ProcAura);
            Assert.Null(ModOf(kit, caster, ProcAura, SpellModOp.Cost));

            kit.System.RestoreAuras(caster, saved.Auras, 1_800_000_001_000);

            // The holder charges are set before the holder is added, so the handler sees them (SpellSystem.Persistence.cs).
            Assert.Equal(2, ModOf(kit, caster, ProcAura, SpellModOp.Cost)!.Charges);
        }
    }

    [Fact]
    public void AStackableMod_NeverCarriesCharges()
    {
        (SpellTestKit kit, Player caster, _) = Setup(Stacky);
        using (kit)
        {
            Assert.Equal(0, ModOf(kit, caster, Stacky, SpellModOp.Damage)!.Charges);
        }
    }

    [Fact]
    public void ShadowTrance_HasItsCustomSingleCharge_AndIsSpentByTheBolt()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(ShadowTrance);
        using (kit)
        {
            Assert.Equal(1, ModOf(kit, caster, ShadowTrance, SpellModOp.CastingTime)!.Charges);

            Cast(kit, caster, target, Bolt);

            Assert.False(kit.System.HasAura(caster, ShadowTrance));
        }
    }

    [Fact]
    public void ACritChanceMod_IsSpentWhenTheDamageLands()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(CritShape);
        using (kit)
        {
            kit.System.CombatRules = new VanillaSpellCombatRules();
            kit.System.Relations = new FakeRelations { Hostile = { target.Guid } };
            kit.Spellbook.Teach(caster, CritBolt);
            Cast(kit, caster, target, CritBolt);

            Assert.False(kit.System.HasAura(caster, CritShape));   // the crit roll of the landing hit used it
        }
    }
}
