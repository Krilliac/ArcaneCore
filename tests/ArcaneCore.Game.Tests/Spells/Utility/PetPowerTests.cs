using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Utility.Targets;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Utility;

/// <summary>
/// Pet power mechanics of the warlock: SPELL_EFFECT_POWER_DRAIN (Dark Pact, vmangos SpellEffects.cpp:1696-1760), the per-second channel cost
/// (Health Funnel, SpellAuras.cpp:7296-7330) and the Life Tap script (spell_warlock.cpp:112-159). The numbers are quoted shapes of the test,
/// not read from any reference data.
/// </summary>
public sealed class PetPowerTests
{
    private const uint DarkPact = 964_001;
    private const uint DarkPactMultiplied = 964_002;
    private const uint SelfDrain = 964_003;
    private const uint HealthFunnel = 964_004;
    private const uint LifeTapRank1 = 1454; // the vmangos script id
    private const uint ImprovedLifeTap = 964_005;
    private const uint ShadowPower = 964_006;
    private const uint SlowFunnel = 964_007;
    private const uint RealFunnel = 964_008;

    private static SpellInfo Instant(uint id, params SpellEffectInfo[] effects)
        => Spell(id, effects) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static PetTestKit Kit() => new(
    [
        Instant(DarkPact, Effect(SpellEffectName.PowerDrain, 150, (SpellImplicitTarget)5, misc: (int)PowerType.Mana)) with { School = SpellSchool.Shadow },
        Instant(DarkPactMultiplied, Effect(SpellEffectName.PowerDrain, 150, (SpellImplicitTarget)5, misc: (int)PowerType.Mana) with { MultipleValue = 1.5f }),
        Instant(SelfDrain, Effect(SpellEffectName.PowerDrain, 100, misc: (int)PowerType.Mana)),
        // Health Funnel shape: pays 11 health at the start and 5 health per second, heals the pet 12 per second for 10 seconds.
        Instant(HealthFunnel, Effect(SpellEffectName.ApplyAura, 12, (SpellImplicitTarget)5, AuraType.PeriodicHeal, amplitude: 1000)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(10_000, 0, 10_000),
            PowerType = SpellMath.PowerHealth,
            ManaCost = 11,
            ManaPerSecond = 5,
            SpellVisual = 1,
            School = SpellSchool.Shadow,
        },
        // The same shape with the "no target per second costs" attribute: it only pays when the caster's target is its caster.
        Instant(SlowFunnel, Effect(SpellEffectName.ApplyAura, 12, (SpellImplicitTarget)5, AuraType.PeriodicHeal, amplitude: 1000)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            AttributesEx2 = (SpellAttributesEx2)0x800,
            Duration = new SpellDuration(10_000, 0, 10_000),
            PowerType = SpellMath.PowerHealth,
            ManaPerSecond = 5,
            SpellVisual = 1,
        },
        // The real classic-db z2815 shape of Health Funnel 755: AttributesEx2 2056 (0x808), a periodic heal on the pet (target 5) and aura 88
        // (MOD_HEALTH_REGEN_PERCENT -101) on the caster (target 1), 5 health per second.
        Instant(RealFunnel,
            Effect(SpellEffectName.ApplyAura, 12, (SpellImplicitTarget)5, AuraType.PeriodicHeal, amplitude: 1000),
            Effect(SpellEffectName.ApplyAura, -101, SpellImplicitTarget.UnitCaster, AuraType.ModHealthRegenPercent)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            AttributesEx2 = (SpellAttributesEx2)0x808,
            Duration = new SpellDuration(10_000, 0, 10_000),
            PowerType = SpellMath.PowerHealth,
            ManaCost = 11,
            ManaPerSecond = 5,
            SpellVisual = 1,
            School = SpellSchool.Shadow,
        },
        Instant(LifeTapRank1, Effect(SpellEffectName.Dummy, 30)) with { School = SpellSchool.Shadow },
        Instant(ImprovedLifeTap, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.Dummy)) with
        {
            SpellFamilyName = 5,
            SpellIconId = 208,
            Duration = new SpellDuration(60_000, 0, 60_000),
            SpellVisual = 1,
        },
        Instant(ShadowPower, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModDamageDone, misc: 0x20)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            SpellVisual = 1,
        },
    ]);

    private static PetTestKit Prepared()
    {
        PetTestKit kit = Kit();
        PetTargets.Install(kit.Spells.System);
        SpellScriptDispatcher.Install(kit.Spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    private static void SetMana(Unit unit, uint current, uint max)
    {
        unit.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        unit.SetUInt32(UpdateFields.UnitFieldMaxpower1, max);
        unit.SetUInt32(UpdateFields.UnitFieldPower1, current);
    }

    private static uint Mana(Unit unit) => SpellSystem.GetPower(unit, PowerType.Mana);

    private static (Player Warlock, Creature Pet, FakeSession Session) WarlockWithPet(PetTestKit kit)
    {
        (Player warlock, FakeSession session) = kit.AddPlayer(1);
        kit.Cast(warlock, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures, c => c.IsPet);
        SetMana(warlock, 0, 1000);
        SetMana(pet, 300, 500);
        session.Clear();
        return (warlock, pet, session);
    }

    private static SpellCastResult Cast(PetTestKit kit, Unit caster, uint spell)
        => kit.Spells.System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: true);

    [Fact]
    public void PowerDrain_MovesTheMasterManaToTheCaster()
    {
        using PetTestKit kit = Prepared();
        (Player warlock, Creature pet, _) = WarlockWithPet(kit);

        Cast(kit, warlock, DarkPact);

        Assert.Equal(150u, Mana(pet));
        Assert.Equal(150u, Mana(warlock));
    }

    [Fact]
    public void PowerDrain_IsCappedAtTheCurrentPowerOfTheTarget()
    {
        using PetTestKit kit = Prepared();
        (Player warlock, Creature pet, _) = WarlockWithPet(kit);
        SetMana(pet, 100, 500);

        Cast(kit, warlock, DarkPact);

        Assert.Equal(0u, Mana(pet));
        Assert.Equal(100u, Mana(warlock));
    }

    [Fact]
    public void PowerDrain_OfAnotherPowerType_DoesNothing()
    {
        using PetTestKit kit = Prepared();
        (Player warlock, Creature pet, _) = WarlockWithPet(kit);
        pet.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Rage); // the pet uses rage, the spell drains mana

        Cast(kit, warlock, DarkPact);

        Assert.Equal(300u, Mana(pet));
        Assert.Equal(0u, Mana(warlock));
    }

    [Fact]
    public void PowerDrain_UsesTheManaMultiplier()
    {
        using PetTestKit kit = Prepared();
        (Player warlock, Creature pet, _) = WarlockWithPet(kit);

        Cast(kit, warlock, DarkPactMultiplied);

        Assert.Equal(150u, Mana(pet));
        Assert.Equal(225u, Mana(warlock)); // 150 * 1.5
    }

    [Fact]
    public void PowerDrain_OfOneself_GivesNothingBack()
    {
        using PetTestKit kit = Prepared();
        (Player warlock, _, _) = WarlockWithPet(kit);
        SetMana(warlock, 400, 1000);

        Cast(kit, warlock, SelfDrain);

        Assert.Equal(300u, Mana(warlock));
    }

    [Fact]
    public void PowerDrain_GetsTheSpellPowerOfTheCaster()
    {
        using PetTestKit kit = Prepared();
        CasterSpellModules.Register(kit.Spells.System, null);
        (Player warlock, Creature pet, _) = WarlockWithPet(kit);
        Cast(kit, warlock, ShadowPower);

        Cast(kit, warlock, DarkPact);

        // A direct damage bonus applies to the drained amount (vmangos EffectPowerDrain): the pet loses more than the 150 of the spell data.
        Assert.True(Mana(pet) < 150u, $"pet mana {Mana(pet)}");
    }

    [Fact]
    public void ChannelPerSecondCost_IsPaidByTheCasterEverySecond_WhileThePetIsHealed()
    {
        using PetTestKit kit = Prepared();
        (Player warlock, Creature pet, _) = WarlockWithPet(kit);
        warlock.Health = 100;
        pet.Health = 10;
        kit.Spells.Spellbook.Teach(warlock, HealthFunnel);

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.HandleCastRequest(warlock, HealthFunnel, SpellCastTargets.ForSelf()));
        Assert.Equal(89u, warlock.Health); // the 11 health of the cast
        kit.Spells.Advance(3000);

        Assert.Equal(74u, warlock.Health); // 89 - 3 * 5
        Assert.Equal(46u, pet.Health); // 10 + 3 * 12
    }

    [Fact]
    public void ChannelPerSecondCost_FizzlesTheChannelWhenTheCasterCannotPay()
    {
        using PetTestKit kit = Prepared();
        (Player warlock, Creature pet, FakeSession session) = WarlockWithPet(kit);
        warlock.Health = 30;
        pet.Health = 10;
        kit.Spells.Spellbook.Teach(warlock, HealthFunnel);
        kit.Spells.System.HandleCastRequest(warlock, HealthFunnel, SpellCastTargets.ForSelf());
        session.Clear();

        kit.Spells.Advance(5000);

        // 30 - 11 = 19; seconds 1-3 pay (14, 9, 4); at second 4 the health 4 is not above 5: the channel ends with FIZZLE.
        Assert.Equal(4u, warlock.Health);
        Assert.False(kit.Spells.System.HasAura(pet, HealthFunnel));
        Assert.Null(kit.Spells.System.GetState(warlock.Guid)?.CurrentCast);
        Assert.Equal(46u, pet.Health); // three heals landed before it stopped
        Assert.Contains(SpellTestKit.Packets(session, WorldOpcode.SmsgCastResult),
            p => BitConverter.ToUInt32(p, 0) == HealthFunnel && p[4] == 2 && p[5] == (byte)SpellCastResult.Fizzle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChannelPerSecondCost_RealHealthFunnelShape_IsPaidExactlyOncePerSecond_WhateverTheWarlockHasSelected(bool petSelected)
    {
        using PetTestKit kit = Prepared();
        (Player warlock, Creature pet, _) = WarlockWithPet(kit);
        warlock.Health = 100;
        pet.Health = 10;
        warlock.Target = petSelected ? pet.Guid : default;
        kit.Spells.Spellbook.Teach(warlock, RealFunnel);

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.HandleCastRequest(warlock, RealFunnel, SpellCastTargets.ForSelf()));
        Assert.Equal(89u, warlock.Health);
        kit.Spells.Advance(3000);

        // vmangos pays once for the two holders (its comment "avoid double cost for health funnel"); retail charges the caster 5 per second.
        Assert.Equal(74u, warlock.Health);
        Assert.Equal(46u, pet.Health);
    }

    [Fact]
    public void ChannelPerSecondCost_SkipsASpellWithTheNoTargetCostAttribute_WhenTheCasterDoesNotTargetItself()
    {
        using PetTestKit kit = Prepared();
        (Player warlock, Creature pet, _) = WarlockWithPet(kit);
        warlock.Health = 100;
        warlock.Target = pet.Guid; // the caster's selection is not the caster
        Cast(kit, warlock, SlowFunnel);

        kit.Spells.Advance(3000);

        Assert.Equal(100u, warlock.Health);
    }

    [Fact]
    public void LifeTap_TradesHealthForManaOneForOne_AndLogsTheEnergize()
    {
        using PetTestKit kit = Prepared();
        (Player warlock, _, FakeSession session) = WarlockWithPet(kit);
        warlock.Health = 1000;
        kit.Spells.Spellbook.Teach(warlock, LifeTapRank1);

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.HandleCastRequest(warlock, LifeTapRank1, SpellCastTargets.ForSelf()));

        Assert.Equal(970u, warlock.Health);
        Assert.Equal(30u, Mana(warlock));
        Assert.Empty(SpellTestKit.Packets(session, WorldOpcode.SmsgSpellnonmeleedamagelog)); // shouldn't appear in the combat log
        byte[] log = Assert.Single(SpellTestKit.Packets(session, WorldOpcode.SmsgSpellenergizelog));
        Assert.True(log.AsSpan().IndexOf(BitConverter.GetBytes(LifeTapScriptEnergizeSpell)) >= 0, "the energize log names spell 31818");
    }

    private const uint LifeTapScriptEnergizeSpell = 31818;

    [Fact]
    public void LifeTap_FizzlesAtTheCastCheck_WhenHealthIsNotAboveTheBasePoints()
    {
        using PetTestKit kit = Prepared();
        (Player warlock, _, _) = WarlockWithPet(kit);
        warlock.Health = 29; // the check reads the base points (29), not the rolled value (30)
        kit.Spells.Spellbook.Teach(warlock, LifeTapRank1);

        Assert.Equal(SpellCastResult.Fizzle, kit.Spells.System.HandleCastRequest(warlock, LifeTapRank1, SpellCastTargets.ForSelf()));

        Assert.Equal(29u, warlock.Health);
        Assert.Equal(0u, Mana(warlock));
    }

    [Fact]
    public void LifeTap_FizzlesInTheEffect_WhenHealthEqualsTheRolledValue()
    {
        using PetTestKit kit = Prepared();
        (Player warlock, _, FakeSession session) = WarlockWithPet(kit);
        warlock.Health = 30; // passes the check (30 > 29) but the effect needs more than 30
        kit.Spells.Spellbook.Teach(warlock, LifeTapRank1);

        kit.Spells.System.HandleCastRequest(warlock, LifeTapRank1, SpellCastTargets.ForSelf());

        Assert.Equal(30u, warlock.Health);
        Assert.Equal(0u, Mana(warlock));
        Assert.Contains(SpellTestKit.Packets(session, WorldOpcode.SmsgCastResult), p => p[4] == 2 && p[5] == (byte)SpellCastResult.Fizzle);
    }

    [Fact]
    public void ImprovedLifeTap_ScalesTheMana_NotTheHealthCost()
    {
        using PetTestKit kit = Prepared();
        (Player warlock, _, _) = WarlockWithPet(kit);
        warlock.Health = 1000;
        Cast(kit, warlock, ImprovedLifeTap);
        kit.Spells.Spellbook.Teach(warlock, LifeTapRank1);

        kit.Spells.System.HandleCastRequest(warlock, LifeTapRank1, SpellCastTargets.ForSelf());

        Assert.Equal(970u, warlock.Health);
        Assert.Equal(33u, Mana(warlock)); // (10 + 100) * 30 / 100
    }

    [Fact]
    public void LifeTap_UsesSpellPower_AndTheManaEqualsTheHealthLost()
    {
        using PetTestKit kit = Prepared();
        CasterSpellModules.Register(kit.Spells.System, null);
        (Player warlock, _, _) = WarlockWithPet(kit);
        warlock.Health = 1000;
        Cast(kit, warlock, ShadowPower);
        kit.Spells.Spellbook.Teach(warlock, LifeTapRank1);

        kit.Spells.System.HandleCastRequest(warlock, LifeTapRank1, SpellCastTargets.ForSelf());

        uint lost = 1000 - warlock.Health;
        Assert.InRange(lost, 72u, 73u); // 30 + 100 * 1.5/3.5 = 72.86, dithered
        Assert.Equal(lost, Mana(warlock));
    }
}
