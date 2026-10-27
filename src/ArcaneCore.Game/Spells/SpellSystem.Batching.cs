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
/// is checked as <see cref="Cast"/> would check it, then held: an interrupt, a stun, a cancel or a move arriving in the same window can no
/// longer stop it (a finished cast bar stays in the cast slot until its go, so the unit still reads as casting; an instant leaves it). At each window boundary <see cref="FlushSpellBatch"/> sends the held casts through <see cref="Cast"/> in
/// the order they arrived; their go re-checks everything but the caster's own state, range and line of sight (<c>batchedGo</c>), which were
/// judged when the window opened. So two players who kill, charge or interrupt each other in one window both get their action. A cast that
/// fails its check is not held: it fails at once, as without batching.
/// </para>
/// <para>
/// Creature casts are batched too (<see cref="SpellBatchCreatures"/>), and white swings close with the same windows (<see cref="SpellBatchMelee"/>,
/// <see cref="Combat.MapCombat"/>). Not batched: triggered casts (they resolve inside the cast that triggers them), game-object casts, channels
/// (the channel keeps its cast slot), next-swing spells (they go with the swing), auto-repeat shots and item-equip casts. Thread affinity: the
/// world thread, like the rest of <see cref="SpellSystem"/>.
/// </para>
/// </summary>
public sealed partial class SpellSystem
{
    private readonly List<SpellCast> _batchedGoes = [];
    private uint _batchIndex; // BatchIndex at which the held casts were held

    /// <summary>
    /// The batch a moment falls in: <c>now / window</c>. Every system that batches (the casts here, the white swings in
    /// <see cref="Combat.MapCombat"/>, aura expiry) closes its window at the same instants, so one batch is one batch everywhere.
    /// </summary>
    internal static uint BatchIndex(uint nowMs, uint windowMs) => nowMs / windowMs;

    /// <summary>Whether creature (and pet and totem) casts are batched too when <see cref="SpellBatchWindowMs"/> is set (<c>SpellRules:SpellBatchCreatures</c>).</summary>
    public bool SpellBatchCreatures { get; set; } = true;

    /// <summary>Whether white melee swings are batched when <see cref="SpellBatchWindowMs"/> is set (<c>SpellRules:SpellBatchMelee</c>, read by <see cref="Combat.MapCombat"/>).</summary>
    public bool SpellBatchMelee { get; set; } = true;

    /// <summary>
    /// The spell batch window in ms (<c>SpellRules:SpellBatchWindowMs</c>); 0 (the default) resolves every cast the moment it goes off.
    /// Retail 1.12 and Blizzard's 1.13.6 Classic servers used 400.
    /// </summary>
    public uint SpellBatchWindowMs { get; set; } = DefaultSpellBatchWindowMs;

    /// <summary>
    /// The window a new <see cref="SpellSystem"/> starts with, and the one <c>SpellRules:SpellBatchWindowMs = 0</c> falls back to: 0, always,
    /// in a server. Only a test assembly sets it (once, in its module initializer, from <c>ARCANECORE_TEST_SPELL_BATCH_MS</c>) to run a whole
    /// suite with batching on (docs/areas/spell-rules.md).
    /// </summary>
    internal static uint DefaultSpellBatchWindowMs { get; set; }

    /// <summary>Casts waiting for the next batch boundary.</summary>
    internal int PendingBatchedGoes => _batchedGoes.Count;

    /// <summary>
    /// Whether <paramref name="unit"/> has a cast waiting in the spell batch for its go: a held cast bar (also still in its cast slot) or an
    /// instant (which already left it). A caller that acts on a cast's effect (a bot lighting a campfire, then cooking at it) waits while this
    /// is true. Always false with batching off. No allocation.
    /// </summary>
    public bool HasPendingGo(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        foreach (SpellCast held in _batchedGoes)
        {
            if (ReferenceEquals(held.Caster, unit) && held.State != SpellCastState.Finished)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsBatchable(SpellCast cast)
        => SpellBatchWindowMs != 0 && !cast.IsTriggered && (cast.Caster is Player || (SpellBatchCreatures && cast.Caster is Creatures.Creature))
            && _objectCastDepth == 0 && !cast.Spell.IsChanneled
            && !cast.Spell.IsNextMeleeSwing && !cast.AutoRepeatShot && !cast.IsItemEquipCast && !cast.IsItemTriggeredCast;

    /// <summary>The go of <paramref name="cast"/>: at once, or held for the batch when <see cref="IsBatchable"/> and its go-time check passes.</summary>
    private SpellCastResult GoOrBatch(SpellCast cast)
    {
        if (!IsBatchable(cast) || PrecheckGo(cast) != SpellCastResult.CastOk)
        {
            return Cast(cast); // a failing go fails now, with its own replies
        }

        // An instant leaves the cast slot (a second off-GCD instant in the same window still goes through, as on retail); a finished
        // cast bar keeps it, so everything that asks whether the unit is casting (bots, creature AI, melee pauses) still sees it until
        // its go. Either way it is committed: Cancel, UpdateCast and the interrupt effect leave a held cast alone.
        if (cast.CastTime == 0 && _states.TryGetValue(cast.Caster.Guid, out UnitSpellState? state) && ReferenceEquals(state.CurrentCast, cast))
        {
            state.CurrentCast = null;
        }

        if (_batchedGoes.Count == 0)
        {
            _batchIndex = BatchIndex(NowMs, SpellBatchWindowMs);
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

    /// <summary>At the first update in a later batch (or at once when batching was switched off with casts held) the held casts go off.</summary>
    private void UpdateSpellBatch()
    {
        if (_batchedGoes.Count == 0)
        {
            return;
        }

        uint window = SpellBatchWindowMs;
        if (window != 0 && BatchIndex(NowMs, window) == _batchIndex)
        {
            return;
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
