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
/// <see cref="Combat.MapCombat"/>). Not batched: triggered casts (they resolve inside the cast that triggers them; the one exception is an item's later use spells, which wait
/// behind its held first spell, <see cref="FollowsHeldItemUse"/>), game-object casts, channels
/// (the channel keeps its cast slot), next-swing spells (they go with the swing), auto-repeat shots and item-equip casts. Thread affinity: the
/// world thread, like the rest of <see cref="SpellSystem"/>.
/// </para>
/// </summary>
public sealed partial class SpellSystem
{
    private readonly List<SpellCast> _batchedGoes = [];
    private uint _batchIndex; // BatchIndex at which the held casts were held
    private SpellCast? _resolvingBatchedGo; // the held cast FlushSpellBatch is sending through Cast, while it does

    /// <summary>
    /// The batch a moment falls in: <c>now / window</c>. Every system that batches (the casts here, the white swings in
    /// <see cref="Combat.MapCombat"/>, aura expiry) closes its window at the same instants, so one batch is one batch everywhere.
    /// </summary>
    internal static uint BatchIndex(uint nowMs, uint windowMs) => nowMs / windowMs;

    /// <summary>Whether creature (and pet and totem) casts are batched too when <see cref="SpellBatchWindowMs"/> is set (<c>SpellRules:SpellBatchCreatures</c>).</summary>
    public bool SpellBatchCreatures { get; set; } = true;

    /// <summary>Whether a timed aura that runs out stays until the batch boundary (<c>SpellRules:SpellBatchAuraExpiry</c>), with <see cref="SpellBatchWindowMs"/> set.</summary>
    public bool SpellBatchAuraExpiry { get; set; } = true;

    /// <summary>
    /// Whether the expiry of <paramref name="holder"/>, which has run out, waits for the batch boundary: with aura expiry batching on, a holder
    /// that is not a channel's stays until the first update in a later batch than the one it ran out in, so an aura ends together with the
    /// other events of its batch (a stun that ends mid-batch lets its target act only from the next one). False removes it now.
    /// </summary>
    private bool HoldAuraExpiry(SpellAuraHolder holder)
    {
        uint window = SpellBatchWindowMs;
        if (window == 0 || !SpellBatchAuraExpiry || holder.Spell.IsChanneled)
        {
            return false;
        }

        uint index = BatchIndex(NowMs, window);
        holder.ExpiryBatchIndex ??= index;
        return holder.ExpiryBatchIndex == index;
    }

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
        => SpellBatchWindowMs != 0 && (!cast.IsTriggered || FollowsHeldItemUse(cast))
            && (cast.Caster is Player || (SpellBatchCreatures && cast.Caster is Creatures.Creature))
            && _objectCastDepth == 0 && !cast.Spell.IsChanneled
            && !cast.Spell.IsNextMeleeSwing && !cast.AutoRepeatShot && !cast.IsItemEquipCast && !cast.IsItemTriggeredCast;

    /// <summary>
    /// Whether <paramref name="cast"/> is a later ON_USE spell of an item (cast triggered, ItemUseService.CastItemUseSpell) whose first spell is
    /// waiting in the batch. It waits behind it, so the item's spells still go off together and in their template order: without this it
    /// would resolve at once, before the held first spell.
    /// </summary>
    private bool FollowsHeldItemUse(SpellCast cast)
    {
        if (cast.CastItem is not { } item || _batchedGoes.Count == 0)
        {
            return false;
        }

        foreach (SpellCast held in _batchedGoes)
        {
            if (!held.IsTriggered && ReferenceEquals(held.CastItem, item) && ReferenceEquals(held.Caster, cast.Caster)
                && held.State != SpellCastState.Finished)
            {
                return true;
            }
        }

        return false;
    }

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

    /// <summary>
    /// The checks <see cref="Cast"/> runs before it pays, without their replies: the cast item is still owned and usable, CheckCast passes,
    /// and the reagents can be taken (<see cref="StageCastReagents"/>, a detached dry run: an equipped reagent the caster may not unequip now
    /// fails here, so the request fails at once instead of answering CastOk and failing at the boundary).
    /// </summary>
    private SpellCastResult PrecheckGo(SpellCast cast)
    {
        Unit caster = cast.Caster;
        if (cast.CastItem is { } castItem && (caster is not Player owner || !owner.Inventory.OwnsItemAt(castItem, cast.ItemBag, cast.ItemSlot)))
        {
            return SpellCastResult.ItemNotReady;
        }

        if (cast.CastItem is { } liveItem && caster is Player liveOwner
            && !CanStartItemUse(liveOwner, liveItem, cast.ItemSpellIndex, out _, requireCharges: !cast.IsTriggered))
        {
            return SpellCastResult.ItemNotReady;
        }

        UnitSpellState state = GetOrCreateState(caster);
        SpellCastResult result = CheckCast(state, cast.Spell, cast.Targets, ResolveUnitTarget(caster, cast.Targets, cast.Spell), cast.IsTriggered,
            strict: false, skipCooldown: true, castItem: cast.CastItem, triggeringSpell: cast.TriggeringSpell, itemCategory: cast.ItemCategory);
        return result == SpellCastResult.CastOk ? StageCastReagents(cast, out _) : result;
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
    /// caster's still goes off (it was committed before the window closed). A fault in one cast does not stop the others. An item use's
    /// first spell and the later use spells held behind it (<see cref="FollowsHeldItemUse"/>, always next to it: one item use prepares them
    /// in one call) go off as one use: from <see cref="HandleItemUse"/>, the item is consumed once, after the last of them, as without batching.
    /// </summary>
    private void FlushSpellBatch()
    {
        SpellCast[] batch = [.. _batchedGoes];
        _batchedGoes.Clear();
        for (int i = 0; i < batch.Length; i++)
        {
            SpellCast cast = batch[i];
            int last = i;
            while (last + 1 < batch.Length && IsHeldFollowUpOf(batch[last + 1], cast))
            {
                last++;
            }

            if (last == i || !cast.InItemUseDispatch || cast.Caster is not Player player || cast.CastItem is not { } item)
            {
                for (int j = i; j <= last; j++)
                {
                    GoHeld(batch[j]);
                }
            }
            else
            {
                var dispatch = new ItemUseDispatch(item);
                _itemUseDispatches.Push(dispatch);
                try
                {
                    for (int j = i; j <= last; j++)
                    {
                        GoHeld(batch[j]);
                    }
                }
                finally
                {
                    _itemUseDispatches.Pop();
                    SettleItemUseDispatch(player, dispatch);
                }
            }

            i = last;
        }
    }

    private static bool IsHeldFollowUpOf(SpellCast cast, SpellCast first)
        => cast.IsTriggered && !first.IsTriggered && first.CastItem is { } item && ReferenceEquals(cast.CastItem, item)
            && ReferenceEquals(cast.Caster, first.Caster);

    /// <summary>One held cast's go, at the batch boundary.</summary>
    private void GoHeld(SpellCast cast)
    {
        if (cast.State == SpellCastState.Finished)
        {
            return;
        }

        if (!cast.Caster.IsInWorld)
        {
            Finish(cast);
            return;
        }

        // A held instant left its cast slot; while it goes off it is the caster's running cast again, as it was without batching,
        // for everything that looks it up there (procs, scripts, melee pauses). Finish clears the slot (Cast finishes on every path).
        // A slot the caster filled meanwhile stays as it is: _resolvingBatchedGo still names this cast (CurrentGenericCast).
        // A triggered cast (an item's later use spell) never held the slot, so it does not take it now either.
        if (!cast.IsTriggered && GetOrCreateState(cast.Caster) is { CurrentCast: null } state)
        {
            state.CurrentCast = cast;
        }

        SpellCast? outer = _resolvingBatchedGo;
        _resolvingBatchedGo = cast;
        try
        {
            Cast(cast);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cast already recorded the fault ("spell.script") and finished the cast; the rest of the batch still goes off.
            FaultLedger.Shared.Record("spell.batch", ex);
            _logger.LogFault("spell.batch", ex, "spell {Spell} of {Caster} failed in the spell batch", cast.Spell.Id, cast.Caster.Guid);
        }
        finally
        {
            _resolvingBatchedGo = outer;
        }
    }
}
