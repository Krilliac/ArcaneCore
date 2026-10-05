using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private readonly List<DeathAuraRemoval> _deathAuraRemovals = [];

    private sealed record DeathAuraRemoval(SpellAuraHolder Holder, WeakReference<Unit> Caster);

    private void RemoveOwnedTrackingAurasOnDeath(Unit caster)
    {
        if (caster.IsAlive || !caster.IsInWorld || caster.Map is null
            || !ReferenceEquals(Units.Find(caster, caster.Guid), caster))
        {
            return;
        }

        // Upstream's registry is gated by the spell_template Custom single-target flag.
        // That content producer is not present here. Restrict this carve-out to the known
        // Hunter's Mark family/flag (SpellClassMask.h:251), not all MOD_STALKED spells.
        foreach (UnitSpellState state in _states.Values.ToArray())
        {
            if (!state.Unit.IsInWorld || !ReferenceEquals(state.Unit.Map, caster.Map)
                || !ReferenceEquals(Units.Find(caster, state.Unit.Guid), state.Unit))
            {
                continue;
            }

            foreach (SpellAuraHolder holder in state.Auras.Where(h => !h.IsRemoved
                && h.Spell.IsFitToFamily(9, 10) && h.Spell.HasAura(AuraType.ModStalked)
                && ReferenceEquals(h.CasterOwner.Caster, caster)).ToArray())
            {
                if (IsQuestSettlementPending(caster) || IsQuestSettlementPending(holder.Target))
                {
                    if (!_deathAuraRemovals.Any(p => ReferenceEquals(p.Holder, holder)))
                    {
                        _deathAuraRemovals.Add(new DeathAuraRemoval(holder, new WeakReference<Unit>(caster)));
                    }
                }
                else
                {
                    RemoveHolder(state, holder);
                }
            }
        }
    }

    /// <summary>Called before the regular spell update so a held mark's death cleanup is completed exactly once.</summary>
    private void ProcessDeathAuraRemovals()
    {
        foreach (DeathAuraRemoval pending in _deathAuraRemovals.ToArray())
        {
            SpellAuraHolder holder = pending.Holder;
            if (holder.IsRemoved || GetState(holder.Target.Guid) is not { } state
                || !ReferenceEquals(state.Unit, holder.Target) || !state.Auras.Contains(holder))
            {
                _deathAuraRemovals.Remove(pending);
                continue;
            }

            pending.Caster.TryGetTarget(out Unit? caster);
            if (IsQuestSettlementPending(holder.Target) || IsQuestSettlementPending(caster)
                || (!holder.Target.IsInWorld && IsInTransit(holder.Target)))
            {
                continue;
            }

            _deathAuraRemovals.Remove(pending);
            if (holder.Target.IsInWorld)
            {
                RemoveHolder(state, holder);
            }
        }
    }

    /// <summary>
    /// Unapply a dead creature's holders before the existing corpse-disposal spell-state reset.
    /// The same object may respawn, so handler contributions must be undone before ownership is forgotten.
    /// </summary>
    public void OnCreatureCorpseRemoving(Creature creature) => ClearCreatureLifeAuras(creature);

    /// <summary>
    /// Clear every old-life holder before respawn rebuilds fields and invokes the AI's fresh passive casts
    /// (vmangos Creature.cpp:827). Also catches a script's aura applied during the invisible DEAD interval.
    /// </summary>
    public void OnCreatureRespawning(Creature creature) => ClearCreatureLifeAuras(creature);

    private void ClearCreatureLifeAuras(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.IsAlive)
        {
            return;
        }

        RevokeAuraCaster(creature);
        if (GetState(creature.Guid) is not { } state || !ReferenceEquals(state.Unit, creature))
        {
            return;
        }

        foreach (SpellAuraHolder holder in state.Auras.ToArray())
        {
            RemoveHolder(state, holder);
        }
    }
}
