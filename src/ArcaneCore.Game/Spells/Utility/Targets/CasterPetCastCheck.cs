using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Utility.Targets;

/// <summary>
/// vmangos Spell::CheckCast "check pet presents" (Spell.cpp:5545-5568): a spell with an effect aimed at TARGET_UNIT_CASTER_PET needs a pet. No
/// pet is NO_PET, a dead one TARGETS_DEAD; a server-triggered cast answers DONT_REPORT instead (vmangos does so only for a cast triggered by an
/// aura, <c>m_triggeredByAuraSpell</c>; <see cref="SpellCastCheckContext"/> does not tell the two triggered kinds apart, so every triggered
/// cast is silent: documented simplification). The line-of-sight half of vmangos (:5567) is not checked: the pet follows its owner.
/// Runs in <see cref="SpellCheckPhase.Items"/> before the equipment check, because a spell whose only target is the pet has no explicit unit
/// target and so never reaches <see cref="SpellCheckPhase.Target"/>.
/// </summary>
internal sealed class CasterPetCastCheck : ISpellCastCheck
{
    public SpellCheckPhase Phase => SpellCheckPhase.Items;

    public int Order => SpellCastCheckOrder.Equipment - 50;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        if (!context.Spell.Effects.Any(e => !e.IsEmpty && e.TargetA == SpellImplicitTarget.UnitCasterPet))
        {
            return SpellCastResult.CastOk;
        }

        Unit? pet = PetTargets.PetOf(context.Caster);
        if (pet is null)
        {
            return context.Triggered ? SpellCastResult.DontReport : SpellCastResult.NoPet;
        }

        return pet.IsAlive ? SpellCastResult.CastOk : context.Triggered ? SpellCastResult.DontReport : SpellCastResult.TargetsDead;
    }
}
