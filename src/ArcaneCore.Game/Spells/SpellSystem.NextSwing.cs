using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// A non-triggered next-swing spell (Attributes 0x4 or 0x400: Heroic Strike, Cleave, Maul...) waits in the
    /// unit's melee slot instead of casting (vmangos Spell::GetCurrentContainer → CURRENT_MELEE_SPELL,
    /// Spell.cpp:7607-7611). The slot is independent of the generic/channel slot (SpellCaster::SetCurrentCastedSpell,
    /// SpellCaster.cpp:1917-1992): a new next-swing spell interrupts the queued one, a generic cast touches neither.
    /// Like any prepared spell it sends SMSG_SPELL_START and starts the global cooldown; power is taken, the
    /// result sent and the effects applied only when <see cref="CastQueuedMeleeSpell"/> casts it.
    /// </summary>
    private SpellCastResult QueueNextSwing(UnitSpellState state, Unit caster, SpellInfo spell, SpellCastTargets targets, Unit? unitTarget)
    {
        SpellCastResult result = CheckCast(state, spell, targets, unitTarget, triggered: false, strict: true);
        if (result != SpellCastResult.CastOk)
        {
            SendCastResult(caster, spell, result, triggered: false);
            return result;
        }

        var cast = new SpellCast(spell, caster, targets, triggered: false, castTime: 0, PowerCostFor(caster, spell), DurationFor(caster, spell));
        if (state.MeleeCast is { } queued)
        {
            Cancel(queued);   // vmangos InterruptSpell(CURRENT_MELEE_SPELL): the previous swing spell is interrupted
        }

        state.MeleeCast = cast;
        SendToSet(caster, WorldOpcode.SmsgSpellStart, SpellPackets.BuildSpellStart(
            caster.Guid, caster.Guid, spell.Id, SpellCastFlags.Unknown2, 0, targets), includeSelf: true);
        AddGlobalCooldown(state, spell);
        NotifyPrepared(cast);
        return SpellCastResult.CastOk;
    }

    /// <summary>
    /// The melee swing fires the queued next-swing spell at <paramref name="victim"/> (vmangos
    /// Unit::AttackerStateUpdate, Unit.cpp:2250-2254: the target is set to the victim, then Spell::cast).
    /// The cast re-checks the spell, takes the power and applies the effects; the slot is freed either way.
    /// <see cref="SpellCastResult.NotFound"/> when nothing is queued. The melee loop calls this; whether the
    /// swing also lands is its concern.
    /// </summary>
    public SpellCastResult CastQueuedMeleeSpell(Unit caster, Unit victim)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(victim);
        if (GetState(caster.Guid)?.MeleeCast is not { } cast)
        {
            return SpellCastResult.NotFound;
        }

        cast.Targets.Mask = SpellCastTargetFlags.Unit;
        cast.Targets.Unit = victim.Guid;
        return Cast(cast);
    }

    /// <summary>
    /// Interrupt the queued next-swing spell, if any (vmangos InterruptSpell(CURRENT_MELEE_SPELL): called by
    /// CMSG_CANCEL_CAST and when the unit stops attacking, Unit.cpp:4604). Returns whether one was cancelled.
    /// </summary>
    public bool CancelQueuedMeleeSpell(Unit caster)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (GetState(caster.Guid)?.MeleeCast is not { } cast)
        {
            return false;
        }

        Cancel(cast);
        return true;
    }
}
