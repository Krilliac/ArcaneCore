using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>
/// Holder lifecycle against vmangos: in-place refresh (SpellAuraHolder::Refresh/CanBeRefreshedBy, SpellAuras.cpp:311-394,
/// Unit.cpp:3138-3154), stack changes through the handler (SetStackAmount, SpellAuras.cpp:6965-6992) and the removal mode
/// (Unit::RemoveSpellAuraHolder, Unit.cpp:3817-3873). Every test runs on the virtual clock.
/// </summary>
public sealed class AuraLifecycleTests
{
    private const uint Dot = 941001;
    private const uint Stacking = 941002;
    private const uint ChargeDot = 941003;
    private const uint Buff = 941004;
    private const uint DispelOne = 941005;
    private const uint FriendBuff = 941006;
    private const AuraType Probe = SpellHandlerModuleTests.FixtureAura;

    private static SpellInfo DotSpell(uint id, uint charges = 0, uint stack = 0) => Spell(
        id, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
    {
        Duration = new SpellDuration(15000, 0, 15000),
        Attributes = SpellAttributes.AuraIsDebuff,
        SpellVisual = 1,
        ProcCharges = charges,
        StackAmount = stack,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit NewKit() => new(
        DotSpell(Dot),
        DotSpell(ChargeDot, charges: 3),
        Spell(Stacking, Effect(SpellEffectName.ApplyAura, -35, SpellImplicitTarget.UnitCaster, Probe)) with
        {
            StackAmount = 5,
            Dispel = 1,
            Duration = new SpellDuration(30000, 0, 30000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(Buff, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitCaster, AuraType.ModStat)) with
        {
            Duration = new SpellDuration(30000, 0, 30000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(FriendBuff, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitFriend, AuraType.ModStat)) with
        {
            Duration = new SpellDuration(30000, 0, 30000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(DispelOne, Effect(SpellEffectName.Dispel, 1, SpellImplicitTarget.Unit, misc: 1)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    private static void Cast(SpellTestKit kit, Unit caster, uint spell, Unit target)
        => kit.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

    [Fact]
    public void RecastBySameCaster_RefreshesTheSameHolderInPlace()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        int added = 0;
        int removed = 0;
        kit.System.HolderAdded += _ => added++;
        kit.System.HolderRemoved += _ => removed++;

        Cast(kit, caster, Dot, target);
        SpellAuraHolder first = kit.System.GetAuras(target).Single();
        byte slot = first.Slot;
        kit.Advance(7000); // two ticks done, 8 s left, timer partway through a period
        Assert.Equal(2, first.Auras[0]!.TickCount);
        Assert.True(first.Duration < first.MaxDuration);

        Cast(kit, caster, Dot, target);

        SpellAuraHolder second = kit.System.GetAuras(target).Single();
        Assert.Same(first, second);
        Assert.Equal(slot, second.Slot);
        Assert.Equal(15000, second.Duration);
        Assert.Equal(15000, second.MaxDuration);
        Assert.Equal(3000, second.Auras[0]!.PeriodicTimer);
        Assert.Equal(0, second.Auras[0]!.TickCount);
        Assert.Equal((1, 0), (added, removed)); // no remove/add: diminishing returns never see the aura end and restart
        Assert.False(second.IsRemoved);
    }

    [Fact]
    public void RefreshOfAChargeAura_GoesThroughTheReplacePath_NotTheInPlaceRefresh()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        Cast(kit, caster, ChargeDot, target);
        SpellAuraHolder first = kit.System.GetAuras(target).Single();

        Cast(kit, caster, ChargeDot, target);

        SpellAuraHolder second = kit.System.GetAuras(target).Single();
        Assert.NotSame(first, second);
        Assert.True(first.IsRemoved);
        Assert.Equal(AuraRemoveMode.Stack, first.RemoveMode);
    }

    [Fact]
    public void DispellingOneOfThreeStacks_UnappliesAndReappliesTheAmountThroughTheHandler()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player owner, _) = kit.AddPlayer(2, 2);
        var relations = new FakeRelations();
        relations.Hostile.Add(owner.Guid);
        kit.System.Relations = relations;
        kit.Spellbook.Teach(caster, DispelOne);
        var calls = new List<(bool Apply, int Amount)>();
        kit.System.RegisterAura(Probe, new AuraHandler((_, _, aura, apply) => calls.Add((apply, aura.Amount)), null));
        for (int i = 0; i < 3; i++)
        {
            Cast(kit, owner, Stacking, owner);
        }

        calls.Clear();
        kit.System.HandleCastRequest(caster, DispelOne, SpellCastTargets.ForUnit(owner.Guid));

        SpellAuraHolder holder = kit.System.GetAuras(owner).Single();
        Assert.Equal(2, holder.StackAmount);
        Assert.Equal(-70, holder.Auras[0]!.Amount);
        // HEAD rewrote the amount without calling the handler, leaving the old -105 contribution on the unit.
        Assert.Equal([(false, -105), (true, -70)], calls);
    }

    [Fact]
    public void RemovalMode_TellsExpiryFromCancelFromReplacementFromDeath()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        var modes = new List<(uint Spell, AuraRemoveMode Mode)>();
        kit.System.HolderRemoved += h => modes.Add((h.Spell.Id, h.RemoveMode));

        // expiry
        Cast(kit, caster, Dot, target);
        kit.Advance(16000);

        // cancel (positive self aura, CMSG_CANCEL_AURA)
        Cast(kit, target, Buff, target);
        kit.System.CancelAura(target, Buff);

        // death
        Cast(kit, target, Buff, target);
        target.Health = 0;
        kit.System.OnUnitDied(target);

        Assert.Equal(
            [(Dot, AuraRemoveMode.Expire), (Buff, AuraRemoveMode.Cancel), (Buff, AuraRemoveMode.Death)],
            modes);
    }

    [Fact]
    public void AnotherCastersPositiveAura_ReplacesTheOld_WithTheStackMode()
    {
        using SpellTestKit kit = NewKit();
        (Player a, _) = kit.AddPlayer(1);
        (Player b, _) = kit.AddPlayer(2, 2);
        (Player target, _) = kit.AddPlayer(3, 3);
        Cast(kit, a, FriendBuff, target);
        SpellAuraHolder first = kit.System.GetAuras(target).Single();

        Cast(kit, b, FriendBuff, target);

        Assert.True(first.IsRemoved);
        Assert.Equal(AuraRemoveMode.Stack, first.RemoveMode);
        Assert.Equal(b.Guid, kit.System.GetAuras(target).Single().CasterGuid);
    }
}
