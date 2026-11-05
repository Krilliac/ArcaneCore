using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// <see cref="SpellSystem.InterruptCurrentSpell"/>, the system behind the DB script command INTERRUPT_SPELL (mangos-classic Unit::InterruptSpell,
/// Unit.cpp:4302-4329; ScriptMgr.cpp:2983-2991): slot 1 (CURRENT_GENERIC_SPELL) is the cast being prepared, slot 3 (CURRENT_CHANNELED_SPELL) the
/// channel, and neither touches the other. Written without a build: unproven until the gate.
/// </summary>
public sealed class InterruptCurrentSpellTests
{
    private const uint PlainChannel = 900951;

    private static SpellTestKit NewKit() => new(
        Spell(PlainChannel, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(5000, 0, 5000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
        });

    private static bool Is(SpellTestKit kit, Unit caster, SpellCastState state)
        => kit.System.GetState(caster.Guid)?.CurrentCast is { } cast && cast.State == state;

    private static Player StartPreparedCast(SpellTestKit kit)
    {
        (Player caster, _) = kit.AddPlayer(1);
        FlagForPvp(caster);
        (Player target, _) = kit.AddHostilePlayer(2, 10, 0);
        kit.World.RunTick(0);
        kit.Spellbook.Teach(caster, CastBolt);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
        Assert.True(Is(kit, caster, SpellCastState.Preparing));
        return caster;
    }

    private static Player StartChannel(SpellTestKit kit)
    {
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, PlainChannel, SpellCastTargets.ForSelf(), triggered: false));
        Assert.True(Is(kit, caster, SpellCastState.Casting));
        return caster;
    }

    [Fact]
    public void Slot3_CancelsAChannel_ButNotAPreparedCast()
    {
        using (SpellTestKit kit = NewKit())
        {
            Player caster = StartPreparedCast(kit);
            Assert.False(kit.System.InterruptCurrentSpell(caster, 3));
            Assert.True(Is(kit, caster, SpellCastState.Preparing));
        }

        using (SpellTestKit kit = NewKit())
        {
            Player caster = StartChannel(kit);
            Assert.True(kit.System.InterruptCurrentSpell(caster, 3));
            Assert.False(Is(kit, caster, SpellCastState.Casting));
        }
    }

    [Fact]
    public void Slot1_CancelsAPreparedCast_ButNotAChannel()
    {
        using (SpellTestKit kit = NewKit())
        {
            Player caster = StartChannel(kit);
            Assert.False(kit.System.InterruptCurrentSpell(caster, 1));
            Assert.True(Is(kit, caster, SpellCastState.Casting));
        }

        using (SpellTestKit kit = NewKit())
        {
            Player caster = StartPreparedCast(kit);
            Assert.True(kit.System.InterruptCurrentSpell(caster, 1));
            Assert.False(Is(kit, caster, SpellCastState.Preparing));
        }
    }

    [Fact]
    public void ASlotAboveThree_DoesNothing()
    {
        using SpellTestKit kit = NewKit();
        Player caster = StartChannel(kit);
        Assert.False(kit.System.InterruptCurrentSpell(caster, 4));
        Assert.True(Is(kit, caster, SpellCastState.Casting));
    }
}
