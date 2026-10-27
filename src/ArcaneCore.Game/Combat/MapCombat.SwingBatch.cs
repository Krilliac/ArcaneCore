using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// White swings in the spell batch (SpellSystem.Batching.cs, <c>SpellRules:SpellBatchWindowMs</c> with <c>SpellRules:SpellBatchMelee</c>). A swing
/// whose timer is ready is held (its timers reset as before) and resolves through <see cref="AttackerStateUpdate"/> at the first update of this
/// map in a later batch, in the order the swings were held, so two units that kill each other in one window both land their swing (retail 1.12
/// processed every message of a server tick together: Kaivax, Blizzard, 2019, https://us.forums.blizzard.com/en/wow/t/137118/1). Batching off:
/// the swing resolves at once, as before. Thread affinity: this map's update.
/// </summary>
public sealed partial class MapCombat
{
    private readonly List<(Unit Attacker, Unit Victim, WeaponAttackType Hand)> _heldSwings = [];
    private uint _heldSwingIndex;

    /// <summary>Swings waiting for the next batch boundary on this map.</summary>
    internal int PendingSwings => _heldSwings.Count;

    /// <summary>A ready swing: resolved now, or held for the batch.</summary>
    private void Swing(Unit attacker, Unit victim, WeaponAttackType hand)
    {
        if (SpellMitigation is not { SpellBatchWindowMs: not 0 and var window, SpellBatchMelee: true })
        {
            AttackerStateUpdate(attacker, victim, hand);
            return;
        }

        if (_heldSwings.Count == 0)
        {
            _heldSwingIndex = SpellSystem.BatchIndex(_world.NowMs, window);
        }

        _heldSwings.Add((attacker, victim, hand));
    }

    /// <summary>At the first update in a later batch (or at once when melee batching was switched off) the held swings resolve.</summary>
    private void UpdateHeldSwings()
    {
        if (_heldSwings.Count == 0
            || (SpellMitigation is { SpellBatchWindowMs: not 0 and var window, SpellBatchMelee: true }
                && SpellSystem.BatchIndex(_world.NowMs, window) == _heldSwingIndex))
        {
            return;
        }

        (Unit Attacker, Unit Victim, WeaponAttackType Hand)[] swings = [.. _heldSwings];
        _heldSwings.Clear();
        foreach ((Unit attacker, Unit victim, WeaponAttackType hand) in swings)
        {
            // A unit that left this map (logout, a far teleport) loses its swing; a dead or stunned attacker still lands it (it was committed).
            if (!attacker.IsInWorld || !victim.IsInWorld || !ReferenceEquals(attacker.Map, _map) || !ReferenceEquals(victim.Map, _map))
            {
                continue;
            }

            try
            {
                AttackerStateUpdate(attacker, victim, hand);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("map.combat", ex); // as the per-unit update loop records it; the rest of the batch still resolves
            }
        }
    }
}
