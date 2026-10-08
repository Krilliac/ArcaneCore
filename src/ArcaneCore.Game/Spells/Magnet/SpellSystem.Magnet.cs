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
    /// SpellCaster::SelectMagnetTarget (SpellCaster.cpp:31-68), re-implemented: only enemy
    /// explicit selectors consult SPELL_MAGNET; abilities, poisons and the no-redirection /
    /// suppress-target-procs attributes bypass it. Charges are spent at selection, before hit
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
                || !IsMagnetTargetEligible(spell, magnet, casterSelector: !updateExplicitTarget))
            {
                continue;
            }

            cast.MagnetTarget = magnet;
            if (updateExplicitTarget)
            {
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

        // CheckTargetCreatureType (Spell.cpp:7579-7583): Grounding's 8179 holder
        // explicitly bypasses creature type restrictions. UNIT_CASTER also skips them.
        if (casterSelector || spell.TargetCreatureType == 0 || HasAura(magnet, 8179))
        {
            return true;
        }

        uint creatureType = magnet is Creature creature ? creature.Template.CreatureType : 7; // humanoid player
        return creatureType == 0 || (creatureType <= 32 && (spell.TargetCreatureType & (1u << ((int)creatureType - 1))) != 0);
    }
}
