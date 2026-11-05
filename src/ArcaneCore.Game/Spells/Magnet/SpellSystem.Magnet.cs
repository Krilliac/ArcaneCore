using ArcaneCore.Game.Combat;
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
    /// suppress-target-procs attributes bypass it. With <see cref="MagnetRequiresAttackable"/> (default off,
    /// <c>SpellRules:MagnetRequiresAttackable</c>) one addition over vmangos applies, taken from
    /// mangos-classic Unit::SelectMagnetTarget (Unit.cpp:6924, <c>magnet->IsAlive() &amp;&amp; CanAttack(magnet)</c>):
    /// a magnet the caster may not attack is skipped. vmangos has no such test (the default; mangoszero refuses only unattackable-flagged targets); mangos-classic applies it to every
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
                || (MagnetRequiresAttackable && updateExplicitTarget && (!cast.IsTriggered || cast.Targets.FromClient)
                    && CheckExplicitHostileTargetRules(cast.Caster, spell, magnet) != SpellCastResult.CastOk
                    && !IsFreeForAllOpponentOwned(cast.Caster, magnet)))
            {
                // The last test applies only with MagnetRequiresAttackable (default off, as vmangos). It is the mangos-classic CanAttack(magnet) rule
                // (Unit.cpp:6924), expressed through the explicit-hostile-target gate. The landing CheckCast ran against the client's named unit (the victim), not the magnet, so a legitimately hostile
                // victim that holds a magnet the caster may not attack (a friendly or unflagged owner's totem) is skipped here instead of
                // being redirected onto it. A magnet that a free-for-all opponent of the caster controls is attackable (see
                // IsFreeForAllOpponentOwned) and is not skipped. This is a gameplay eligibility rule, not an anti-crafting protection: a client
                // cannot choose the magnet. Scope: non-triggered casts and client-named blocks, like CheckCast; a server-chosen triggered cast
                // is left alone. Like CheckCast it reads TargetA only: a spell with its enemy selector only in TargetB is not gated.
                continue;
            }

            cast.MagnetTarget = magnet;
            if (updateExplicitTarget)
            {
                // The redirect keeps FromClient, so a later reader of the block (SpellCastTargets.ForUnit(guid, parentCast), which re-aims a
                // child at the unit) sees a client-named target and gates it again. With MagnetRequiresAttackable on, the magnet itself was gated in the loop above.
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

    /// <summary>
    /// A magnet (a totem, a pet) whose controlling player and the caster's controlling player are two free-for-all flagged players that the
    /// relation treats as hostile (different players, not grouped) is attackable. mangos-classic GetReactionTo returns hostile for a pair of
    /// free-for-all controlling players (Relations.cpp, IsPvPFreeForAll on the controlling players) and vmangos GetReactionTo has the same
    /// clause on the affecting players (Object.cpp); a template-only reaction (hooks that read a player-controlled unit by its faction template alone) has no
    /// such clause and reads the totem as a friend of its own team. The production hooks and the base <c>CombatHooks</c> carry the clause, so with them this
    /// exemption is reached only for a magnet without the player-controlled flag or on a group-test mismatch. The player pair itself is judged by the same relation, so a
    /// group mate or the caster's own totem is not exempted. A magnet that is itself a player is judged by that relation already. The magnet's own state must still allow an attack (<see cref="CombatHooks.IsAttackableState"/>).
    /// </summary>
    private bool IsFreeForAllOpponentOwned(Unit caster, Unit magnet)
        => DuelRules.ControllingPlayer(caster) is { } casterPlayer
            && DuelRules.ControllingPlayer(magnet) is { } magnetPlayer
            && !ReferenceEquals(casterPlayer, magnetPlayer)
            && (casterPlayer.Flags & PlayerFlags.FfaPvp) != 0
            && (magnetPlayer.Flags & PlayerFlags.FfaPvp) != 0
            && Relations.IsHostile(casterPlayer, magnetPlayer)
            && CombatHooks.IsAttackableState(magnet); // the owners only decide the relation: a magnet that is itself not attackable (non-attackable flag, taxi, evade, GM) stays refused, as mangos-classic Unit::CanAttack does (Relations.cpp:351)

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
