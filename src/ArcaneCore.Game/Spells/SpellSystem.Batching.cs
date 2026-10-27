using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Spell batching (opt-in, <see cref="SpellBatchWindowMs"/>). Retail 1.12 did not resolve a spell the moment it went off: the server ran
/// "a single game loop that processed all messages ... every server tick", so actions that landed in one tick resolved together, and Blizzard
/// reproduced that on its WoW Classic servers with a 400 ms window (Kaivax, Blizzard, 2019, https://us.forums.blizzard.com/en/wow/t/137118/1:
/// casts moved "to a low-priority loop that will cause them to be processed at the frequency that best fits how the game actually played in
/// version 1.12"; 1.13.7 PTR notes, https://us.forums.blizzard.com/en/wow/t/838945: "a reduction from the 1.13.6 live window of 400 milliseconds").
/// <para>
/// A player's non-triggered cast that reaches its go (an instant from <see cref="Prepare"/>, a finished cast bar from <see cref="UpdateCast"/>)
/// is checked as <see cref="Cast"/> would check it, then held: it leaves the caster's cast slot, so an interrupt, a stun or a move arriving in the
/// same window can no longer stop it. At each window boundary <see cref="FlushSpellBatch"/> sends the held casts through <see cref="Cast"/> in
/// the order they arrived; their go re-checks everything but the caster's own state, range and line of sight (<c>batchedGo</c>), which were
/// judged when the window opened. So two players who kill, charge or interrupt each other in one window both get their action. A cast that
/// fails its check is not held: it fails at once, as without batching.
/// </para>
/// <para>
/// Not batched: triggered casts (they resolve inside the cast that triggers them), creature and game-object casts, channels (the channel
/// keeps its cast slot), next-swing spells (they go with the swing), auto-repeat shots and item-equip casts. Melee swings, periodic ticks and
/// aura expiry are not batched either. Thread affinity: the world thread, like the rest of <see cref="SpellSystem"/>.
/// </para>
/// </summary>
public sealed partial class SpellSystem
{
    private readonly List<SpellCast> _batchedGoes = [];
    private uint _batchElapsedMs;

    /// <summary>
    /// The spell batch window in ms (<c>SpellRules:SpellBatchWindowMs</c>); 0 (the default) resolves every cast the moment it goes off.
    /// Retail 1.12 and Blizzard's 1.13.6 Classic servers used 400.
    /// </summary>
    public uint SpellBatchWindowMs { get; set; }

    /// <summary>Casts waiting for the next batch boundary.</summary>
    internal int PendingBatchedGoes => _batchedGoes.Count;

    private bool IsBatchable(SpellCast cast)
        => SpellBatchWindowMs != 0 && !cast.IsTriggered && cast.Caster is Player && _objectCastDepth == 0 && !cast.Spell.IsChanneled
            && !cast.Spell.IsNextMeleeSwing && !cast.AutoRepeatShot && !cast.IsItemEquipCast && !cast.IsItemTriggeredCast;

    /// <summary>The go of <paramref name="cast"/>: at once, or held for the batch when <see cref="IsBatchable"/> and its go-time check passes.</summary>
    private SpellCastResult GoOrBatch(SpellCast cast)
    {
        if (!IsBatchable(cast) || PrecheckGo(cast) != SpellCastResult.CastOk)
        {
            return Cast(cast); // a failing go fails now, with its own replies
        }

        if (_states.TryGetValue(cast.Caster.Guid, out UnitSpellState? state) && ReferenceEquals(state.CurrentCast, cast))
        {
            state.CurrentCast = null; // committed: nothing in the window can interrupt it
        }

        cast.BatchedGo = true;
        _batchedGoes.Add(cast);
        return SpellCastResult.CastOk;
    }

    /// <summary>
    /// Whether <paramref name="caster"/> has a cast waiting in the batch whose go will start a cooldown that <paramref name="spell"/> would
    /// have to wait for: the same spell, or a spell of the same cooldown <paramref name="category"/> (0: none). The cooldown starts at the go
    /// (<see cref="PayCastCosts"/>), so without this an off-GCD cooldown spell sent twice in one window would go off twice.
    /// </summary>
    private bool HasBatchedGoOnCooldown(Unit caster, SpellInfo spell, uint category)
    {
        foreach (SpellCast held in _batchedGoes)
        {
            if (!ReferenceEquals(held.Caster, caster) || held.State == SpellCastState.Finished)
            {
                continue;
            }

            ItemSpellCooldown cooldown = ItemSpellCooldowns.Pick(held.Spell, held.CastItem);
            if (held.Spell.Id == spell.Id && (cooldown.RecoveryTime != 0 || cooldown.CategoryRecoveryTime != 0 || held.ItemCooldownMs > 0))
            {
                return true;
            }

            uint heldCategory = held.ItemCategory ?? cooldown.Category;
            if (category != 0 && heldCategory == category && (cooldown.CategoryRecoveryTime != 0 || held.ItemCategoryCooldownMs > 0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The checks <see cref="Cast"/> runs before it pays, without their replies: the cast item is still owned and usable, and CheckCast passes.</summary>
    private SpellCastResult PrecheckGo(SpellCast cast)
    {
        Unit caster = cast.Caster;
        if (cast.CastItem is { } castItem && (caster is not Player owner || !owner.Inventory.OwnsItemAt(castItem, cast.ItemBag, cast.ItemSlot)))
        {
            return SpellCastResult.ItemNotReady;
        }

        if (cast.CastItem is { } liveItem && caster is Player liveOwner
            && !CanStartItemUse(liveOwner, liveItem, cast.ItemSpellIndex, out _, requireCharges: true))
        {
            return SpellCastResult.ItemNotReady;
        }

        UnitSpellState state = GetOrCreateState(caster);
        return CheckCast(state, cast.Spell, cast.Targets, ResolveUnitTarget(caster, cast.Targets, cast.Spell), cast.IsTriggered, strict: false,
            skipCooldown: true, castItem: cast.CastItem, triggeringSpell: cast.TriggeringSpell, itemCategory: cast.ItemCategory);
    }

    /// <summary>Counts the window; at its boundary (or at once when batching was switched off with casts held) the held casts go off.</summary>
    private void UpdateSpellBatch(uint diffMs)
    {
        uint window = SpellBatchWindowMs;
        if (_batchedGoes.Count == 0)
        {
            _batchElapsedMs = window == 0 ? 0 : (_batchElapsedMs + diffMs) % window;
            return;
        }

        if (window != 0)
        {
            _batchElapsedMs += diffMs;
            if (_batchElapsedMs < window)
            {
                return;
            }

            _batchElapsedMs %= window;
        }
        else
        {
            _batchElapsedMs = 0;
        }

        FlushSpellBatch();
    }

    /// <summary>
    /// The held casts go off in arrival order. One whose caster left the world (logout, a far teleport) ends without effect; a dead or stunned
    /// caster's still goes off (it was committed before the window closed). A fault in one cast does not stop the others.
    /// </summary>
    private void FlushSpellBatch()
    {
        SpellCast[] batch = [.. _batchedGoes];
        _batchedGoes.Clear();
        foreach (SpellCast cast in batch)
        {
            if (cast.State == SpellCastState.Finished)
            {
                continue;
            }

            if (!cast.Caster.IsInWorld)
            {
                Finish(cast);
                continue;
            }

            try
            {
                Cast(cast);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Cast already recorded the fault ("spell.script") and finished the cast; the rest of the batch still goes off.
                FaultLedger.Shared.Record("spell.batch", ex);
                _logger.LogFault("spell-batch", ex, "spell {Spell} of {Caster} failed in the spell batch", cast.Spell.Id, cast.Caster.Guid);
            }
        }
    }
}
