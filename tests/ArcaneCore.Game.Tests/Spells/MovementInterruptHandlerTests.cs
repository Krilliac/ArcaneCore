using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The cast and channel half of vmangos Unit::HandleInterruptsOnMovement (Unit.cpp:10131-10140), run by RemoveAurasOnMovement for every
/// position-changing movement of any unit (a creature included): spells with SPELL_INTERRUPT_FLAG_MOVEMENT, then a channel with
/// MOVING_CANCELS in its channel interrupt flags (SpellCaster.cpp:2051-2066). A turn alone interrupts neither.
/// </summary>
public sealed class MovementInterruptHandlerTests
{
    private const uint MovingChannel = 900951;
    private const uint MovementFlagCast = 900952;
    private const uint PlainChannel = 900953;

    private static SpellTestKit NewKit() => new(
        Spell(MovingChannel, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            ChannelInterruptFlags = SpellAuraInterruptFlags.Moving,
            Duration = new SpellDuration(5000, 0, 5000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
        },
        Spell(MovementFlagCast, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            CastTime = new SpellCastTime(2000, 0, 0),
            InterruptFlags = SpellInterruptFlags.Movement,
            StartRecoveryCategory = 0,
        },
        Spell(PlainChannel, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(5000, 0, 5000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
        });

    private static bool Casting(SpellTestKit kit, Unit caster, uint spellId)
        => kit.System.GetState(caster.Guid)?.CurrentCast is { } cast && cast.Spell.Id == spellId;

    [Fact]
    public void Movement_InterruptsAChannelWithTheMovingChannelFlag_OnceEvenWhenTheUpdateLoopAlsoSeesTheMove()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, MovingChannel, SpellCastTargets.ForSelf(), triggered: false));
        session.Clear();

        // The caster stepped 3 yd: the handler half cancels it now, the 0.5 yd check of the update loop finds the cast gone.
        caster.ApplyMovement(new MovementInfo { Flags = MovementFlags.Forward, X = caster.X + 3, Y = caster.Y, Z = caster.Z, Orientation = caster.Orientation }, kit.Now);
        kit.System.RemoveAurasOnMovement(caster, positionChangedOrMoving: true);
        Assert.False(Casting(kit, caster, MovingChannel));
        kit.Advance(100);

        Assert.Single(Packets(session, WorldOpcode.SmsgSpellFailedOther));
    }

    [Fact]
    public void Movement_InterruptsAPreparingCastWithTheMovementInterruptFlag()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, MovementFlagCast, SpellCastTargets.ForSelf(), triggered: false));
        Assert.True(Casting(kit, caster, MovementFlagCast));

        kit.System.RemoveAurasOnMovement(caster, positionChangedOrMoving: true);

        Assert.False(Casting(kit, caster, MovementFlagCast));
    }

    [Fact]
    public void TurningAlone_InterruptsNeitherTheCastNorTheChannel()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, MovingChannel, SpellCastTargets.ForSelf(), triggered: false));

        kit.System.RemoveAurasOnMovement(caster, positionChangedOrMoving: false);

        Assert.True(Casting(kit, caster, MovingChannel));
    }

    [Fact]
    public void Movement_LeavesAChannelWithoutTheMovingFlagRunning()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, PlainChannel, SpellCastTargets.ForSelf(), triggered: false));

        kit.System.RemoveAurasOnMovement(caster, positionChangedOrMoving: true);

        Assert.True(Casting(kit, caster, PlainChannel));
    }
}
