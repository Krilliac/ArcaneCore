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

    // True while the held swings resolve (UpdateHeldSwings): a held swing of a player who stopped attacking within the window still lands
    // (it was committed), but it does not start the player's auto-attack again (DealDamage's re-attack of a player without a victim).
    private bool _resolvingHeldSwings;

    /// <summary>Swings waiting for the next batch boundary on this map.</summary>
    internal int PendingSwings => _heldSwings.Count;

    /// <summary>The melee batch window: the spell system's window when it batches swings, else 0 (no batching).</summary>
    private uint SwingBatchWindowMs => SpellMitigation is { SpellBatchMelee: true } spells ? spells.SpellBatchWindowMs : 0;

    /// <summary>A ready swing: resolved now, or held for the batch.</summary>
    private void Swing(Unit attacker, Unit victim, WeaponAttackType hand)
    {
        uint window = SwingBatchWindowMs;
        if (window == 0)
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
        if (_heldSwings.Count == 0)
        {
            return;
        }

        uint window = SwingBatchWindowMs;
        if (window != 0 && SpellSystem.BatchIndex(_world.NowMs, window) == _heldSwingIndex)
        {
            return; // still the batch the swings were held in
        }

        (Unit Attacker, Unit Victim, WeaponAttackType Hand)[] swings = [.. _heldSwings];
        _heldSwings.Clear();
        _resolvingHeldSwings = true;
        try
        {
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
        finally
        {
            _resolvingHeldSwings = false;
        }
    }
}
