using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    // vmangos SpellDefines.h at 0e3ff01e76d4758e8a7c3108b2717cc785ed56fa.
    private const uint MagnetNoRedirection = 0x00000008;
    private const uint MagnetOnlyOnPlayer = 0x00000100;
    private const uint MagnetSuppressTargetProcs = 0x00020000;
    private const uint MagnetPoisonDispel = 4;

    /// <summary>
    /// SpellCaster::SelectMagnetTarget (vmangos SpellCaster.cpp:31-68), re-implemented: only enemy
    /// explicit selectors consult SPELL_MAGNET; abilities, poisons and the no-redirection /
    /// suppress-target-procs attributes bypass it. One deliberate addition over vmangos, taken from
    /// mangos-classic Unit::SelectMagnetTarget (Unit.cpp:6924, <c>magnet->IsAlive() &amp;&amp; CanAttack(magnet)</c>):
    /// a magnet the caster may not attack is skipped. vmangos has no such test; mangos-classic applies it to every
    /// cast, here it is limited to the scope of the explicit-hostile gate in CheckCast (non-triggered casts and
    /// client-named blocks). See the Discrepancies table in docs/areas/spells.md. Charges are spent at selection, before hit
    /// resolution, including misses. The caster ownership token prevents GUID reuse from
    /// transferring protection to a new unit. No extra range or LOS check is applied to the
    /// magnet: the source checks those against the original victim before selecting targets.
    /// </summary>
    private Unit SelectMagnetTarget(SpellCast cast, Unit victim, bool updateExplicitTarget = true)
    {
        if (cast.MagnetTarget is { } selected)
        {
            return selected;
        }

        SpellInfo spell = cast.Spell;
        if ((((uint)spell.AttributesEx) & MagnetNoRedirection) != 0
            || (spell.AttributesEx3 & MagnetSuppressTargetProcs) != 0
            || (spell.DamageClass != SpellDamageClass.Magic && spell.SpellVisual != 7250)
            || spell.Dispel == MagnetPoisonDispel || spell.HasAttribute(SpellAttributes.IsAbility)
            || !Relations.IsHostile(cast.Caster, victim))
        {
            return victim;
        }

        foreach (SpellAuraHolder holder in GetAuras(victim).ToArray())
        {
            if (holder.IsRemoved || !holder.HasAura(AuraType.SpellMagnet)
                || ResolveAuraCaster(holder) is not { IsAlive: true } magnet
                || !ReferenceEquals(magnet.Map, cast.Caster.Map) || IsQuestSettlementPending(magnet)
                || !IsMagnetTargetEligible(spell, magnet, casterSelector: !updateExplicitTarget)
                || (updateExplicitTarget && (!cast.IsTriggered || cast.Targets.FromClient)
                    && CheckExplicitHostileTargetRules(cast.Caster, spell, magnet) != SpellCastResult.CastOk))
            {
                // The last test is the mangos-classic CanAttack(magnet) rule (Unit.cpp:6924), expressed through the explicit-hostile-target
                // gate. The landing CheckCast ran against the client's named unit (the victim), not the magnet, so a legitimately hostile
                // victim that holds a magnet the caster may not attack (a friendly or unflagged owner's totem) is skipped here instead of
                // being redirected onto it. This is a gameplay eligibility rule, not an anti-crafting protection: a client cannot choose the
                // magnet. Scope: non-triggered casts and client-named blocks, like CheckCast; a server-chosen triggered cast is left alone.
                continue;
            }

            cast.MagnetTarget = magnet;
            if (updateExplicitTarget)
            {
                // The redirect keeps FromClient, so a later reader of the block (SpellCastTargets.ForUnit(guid, parentCast), which re-aims a
                // child at the unit) sees a client-named target and gates it again. The magnet itself was gated in the loop above.
                cast.Targets.Unit = magnet.Guid;
            }
            if (holder.Charges > 0)
            {
                holder.Charges--; // rewrites the client's charge count (SpellAuraHolder.UpdateAuraApplication)
                if (holder.Charges == 0)
                {
                    // Removing the caster's source also removes its area children. A missed or
                    // non-damaging spell consumes protection without inventing a totem death.
                    RemoveAuras(magnet, holder.Spell.Id);
                    if (!holder.IsRemoved && GetState(victim.Guid) is { } state)
                    {
                        RemoveHolder(state, holder);
                    }
                }
            }

            return magnet;
        }

        return victim;
    }

    private bool IsMagnetTargetEligible(SpellInfo spell, Unit magnet, bool casterSelector)
    {
        if ((magnet.UnitFlags & (UnitFlags.Spawning | UnitFlags.NotSelectable)) != 0
            || (magnet is Player { IsGameMaster: true } && !spell.IsPositive)
            || (!casterSelector && (spell.AttributesEx3 & MagnetOnlyOnPlayer) != 0 && magnet is not Player))
        {
            return false;
        }

        // CheckTargetCreatureType (Spell.cpp:7577-7605): Grounding's 8179 holder bypasses creature type restrictions.
        // UNIT_CASTER selection also skips them.
        return casterSelector || CheckTargetCreatureType(spell, magnet);
    }
}
