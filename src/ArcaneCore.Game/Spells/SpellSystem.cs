using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The spell system of the world: cast pipeline (vmangos Spell::prepare → cast → update →
/// finish/cancel), cooldowns and global cooldowns, power costs, effect dispatch and the aura
/// framework. One instance serves every map; all members run on the world thread.
/// <para>
/// Per-unit state lives here, keyed by GUID, instead of on <see cref="Unit"/> so the shared
/// object model stays untouched (docs/integration/spells.md); a unit's state is dropped once it
/// leaves the world.
/// </para>
/// </summary>
public sealed partial class SpellSystem
{
    private volatile SpellStore _store;
    private readonly Dictionary<ObjectGuid, UnitSpellState> _states = [];
    private readonly Func<uint> _clock;
    private readonly ILogger _logger;
    private readonly HashSet<(string Kind, uint Value)> _reportedUnsupported = [];
    private readonly Dictionary<SpellEffectName, SpellEffectHandler> _builtInEffectHandlers;

    public SpellSystem(
        SpellStore store,
        Func<uint> clock,
        ISpellUnitResolver? units = null,
        IDamageSink? damage = null,
        ITeleportSink? teleport = null,
        ISpellbook? spellbook = null,
        Random? random = null,
        ILogger? logger = null,
        IItemEnchantmentCatalog? itemEnchantments = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Units = units ?? new MapPlayerResolver();
        Damage = damage ?? new HealthOnlyDamageSink();
        Teleports = teleport ?? new NearTeleportSink();
        Spellbook = spellbook;
        Random = random ?? Random.Shared;
        _logger = logger ?? NullLogger.Instance;
        ItemEnchantments = itemEnchantments ?? new EmptyItemEnchantmentCatalog();
        EffectHandlers = CreateEffectHandlers();
        _builtInEffectHandlers = new Dictionary<SpellEffectName, SpellEffectHandler>(EffectHandlers);
        AuraHandlers = CreateAuraHandlers();
        RegisterModules(SpellHandlerModules.BuiltIn);
    }

    /// <summary>
    /// The spell table, replaced as a whole by <c>.reload spell_template</c> (docs/areas/hot-reload.md).
    /// The store is immutable, so a reader sees either the old table or the new one; the volatile
    /// field publishes the swap to the session tasks that look spells up off the world thread.
    /// </summary>
    public SpellStore Store
    {
        get => _store;
        set => _store = value;
    }

    public ISpellUnitResolver Units { get; set; }

    public IDamageSink Damage { get; set; }

    public ITeleportSink Teleports { get; set; }

    /// <summary>
    /// A unit temporarily detached from its map during a player transfer (vmangos
    /// STATUS_TRANSFER). Retain its spell state while map simulation is paused.
    /// </summary>
    public Func<Unit, bool> IsInTransit { get; set; } = _ => false;

    public ISpellbook? Spellbook { get; set; }

    public Random Random { get; set; }

    public IItemEnchantmentCatalog ItemEnchantments { get; set; }

    /// <summary>World-owned trade/lifecycle veto for item casts; closed by default.</summary>
    public Func<Player, Item, bool> ItemUseTradeGuard { get; set; } = static (_, _) => false;

    /// <summary>
    /// The map update interval subtracted from global cooldowns (vmangos Player::AddGCD: "spell
    /// packets are handled on Map update so substract the update interval"; CONFIG_UINT32_INTERVAL_MAPUPDATE).
    /// </summary>
    public uint MapUpdateIntervalMs { get; set; } = 50;

    /// <summary>
    /// Raised on the world thread after a cast applied its effects to a target (caster, target,
    /// spell): vmangos Spell::DoAllEffectOnTarget → CreatureAI::SpellHit. Creature AI subscribes;
    /// a throwing handler is not caught here.
    /// </summary>
    public event Action<Unit, Unit, SpellInfo>? SpellHit;

    /// <summary>Units with live spell state.</summary>
    public int TrackedUnitCount => _states.Count;

    public uint NowMs => _clock();

    /// <summary>
    /// Whole Unix seconds (vmangos <c>time(nullptr)</c>) stamped on a holder when it is applied
    /// (<see cref="SpellAuraHolder.AppliedAtUnixSeconds"/>). The default is the system clock; a feature that compares
    /// the stamp with another wall-clock reading (the duel service) installs the clock it reads, so both sides agree.
    /// </summary>
    public Func<long> UnixSecondsClock { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The spell state of a unit, if it has any.</summary>
    public UnitSpellState? GetState(ObjectGuid guid) => _states.GetValueOrDefault(guid);

    // --- client requests ---------------------------------------------------------------

    /// <summary>
    /// CMSG_CAST_SPELL (vmangos WorldSession::HandleCastSpellOpcode): unknown spells, spells the
    /// player does not know and passive spells are ignored (logged, no reply); a negative spell
    /// explicitly aimed at oneself fails with BAD_TARGETS; everything else is prepared.
    /// </summary>
    /// <summary>World-owned deferred trade request; no ordinary cast executes before acceptance.</summary>
    public Func<Player, uint, SpellCastTargets, SpellCastResult>? TradeEnchantmentRequest { get; set; }

    public SpellCastResult HandleCastRequest(Player player, uint spellId, SpellCastTargets targets)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(targets);
        SpellInfo? spell = Store.Get(spellId);
        if (spell is null)
        {
            return SpellCastResult.NotFound;
        }

        if (spell.IsPassive || (Spellbook is { } book && !book.HasSpell(player, spellId)))
        {
            _logger.LogWarning("{Player} cast spell {Spell} which they should not have", player.Name, spellId);
            return SpellCastResult.NotKnown;
        }

        if ((targets.Mask & SpellCastTargetFlags.Unit) != 0 && targets.Unit == player.Guid
            && IsExplicitUnitTarget(spell.Effects[0].TargetA) && !spell.IsPositive)
        {
            player.Session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(spellId, SpellCastResult.BadTargets));
            return SpellCastResult.BadTargets;
        }

        if ((targets.Mask & SpellCastTargetFlags.TradeItem) != 0 && TradeEnchantmentRequest is { } deferred)
        {
            SpellCastResult result = deferred(player, spellId, targets);
            SendCastResult(player, spell, result, triggered: false);
            return result;
        }

        return Prepare(player, spell, targets, triggered: false);
    }

    /// <summary>Cast a spell for the server (scripts, triggered spells, GM commands).</summary>
    public SpellCastResult CastSpell(Unit caster, uint spellId, SpellCastTargets targets, bool triggered)
        => CastSpell(caster, spellId, targets, triggered, triggeringSpell: null);

    /// <summary>Cast with the originating spell/aura's reagent policy (vmangos IgnoreItemRequirements).</summary>
    public SpellCastResult CastSpell(Unit caster, uint spellId, SpellCastTargets targets, bool triggered, SpellInfo? triggeringSpell)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(targets);
        SpellInfo? spell = Store.Get(spellId);
        return spell is null ? SpellCastResult.NotFound : Prepare(caster, spell, targets, triggered, triggeringSpell: triggeringSpell);
    }

    /// <summary>
    /// A triggered cast made by an aura (vmangos CastSpell with <c>triggeredByAura</c>, which sets Spell::m_triggeredByAuraSpell):
    /// it takes no power (Spell::TakePower, Spell.cpp:5053).
    /// </summary>
    internal SpellCastResult CastSpellTriggeredByAura(Unit caster, uint spellId, SpellCastTargets targets, SpellInfo aura)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(targets);
        SpellInfo? spell = Store.Get(spellId);
        return spell is null ? SpellCastResult.NotFound
            : Prepare(caster, spell, targets, triggered: true, triggeringSpell: aura, triggeredByAura: true);
    }

    /// <summary>
    /// CMSG_CANCEL_CAST (vmangos HandleCancelCastOpcode → InterruptNonMeleeSpells(false, spellId)):
    /// interrupt the cast in progress when it is <paramref name="spellId"/> (0 = any). Channels are
    /// not interrupted by this packet (withDelayed = false keeps vmangos' channel slot). A queued next-swing
    /// spell is cancelled as well, whatever <paramref name="spellId"/> is (SpellHandler.cpp:329-330 has no id
    /// filter on <c>InterruptSpell(CURRENT_MELEE_SPELL)</c>); this method is the only owner of that behaviour.
    /// </summary>
    public void CancelCast(Unit caster, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (GetState(caster.Guid)?.CurrentCast is { State: SpellCastState.Preparing } cast
            && (spellId == 0 || cast.Spell.Id == spellId))
        {
            Cancel(cast);
        }

        // ranged (autorepeat lane): InterruptNonMeleeSpells(false, spellId) also takes the auto-repeat slot when the id matches.
        if (GetState(caster.Guid)?.AutoRepeatCast is { } autoRepeat && (spellId == 0 || autoRepeat.Spell.Id == spellId))
        {
            CancelAutoRepeat(caster);
        }

        CancelQueuedMeleeSpell(caster);
    }

    /// <summary>CMSG_CANCEL_CHANNELLING (vmangos HandleCancelChanneling): stop a non-triggered channel.</summary>
    public void CancelChannel(Unit caster)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (GetState(caster.Guid)?.CurrentCast is { State: SpellCastState.Casting, IsTriggered: false } cast)
        {
            Cancel(cast);
        }
    }

    // --- world tick ---------------------------------------------------------------------

    /// <summary>Advance casts, channels, auras and cooldowns by <paramref name="diffMs"/> (world thread).</summary>
    public void Update(uint diffMs)
    {
        ProcessDeathAuraRemovals();
        UpdateDynamicObjects(diffMs); // persistent area auras (PersistentAreaAuras/SpellSystem.PersistentAreaAuras.cs)
        if (_states.Count == 0)
        {
            return;
        }

        uint now = NowMs;
        foreach (UnitSpellState state in _states.Values.ToArray())
        {
            if (!state.Unit.IsInWorld)
            {
                if (IsInTransit(state.Unit))
                {
                    continue;
                }

                Forget(state);
                continue;
            }

            if (IsQuestSettlementPending(state.Unit))
            {
                continue;
            }

            // vmangos Unit::Update runs the unit's events (ChannelResetEvent) before _UpdateSpells.
            UpdatePendingChannelReset(state, diffMs);

            if (state.AutoRepeatCast is { } autoRepeat)
            {
                UpdateAutoRepeat(state, autoRepeat); // before the casts, as Unit::_UpdateSpells does (Unit.cpp:2673-2674)
            }

            if (state.CurrentCast is { } cast)
            {
                UpdateCast(cast, diffMs);
            }

            UpdateAuras(state, diffMs);
            UpdateAreaAuras(state);
            ExpireCooldowns(state, now);
            if (state.IsIdle)
            {
                _states.Remove(state.Unit.Guid);
            }
        }
    }

    /// <summary>
    /// A unit leaves the world (logout, despawn): its cast stops without packets and its state is
    /// dropped. A player's saveable auras and cooldowns are captured first by the world daemon
    /// (<see cref="CaptureState"/>, character_aura / character_spell_cooldown).
    /// </summary>
    public void RemoveUnit(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ForgetFoodDrinkHeartbeat(unit);
        RevokeAuraCaster(unit);
        if (_states.TryGetValue(unit.Guid, out UnitSpellState? state) && ReferenceEquals(state.Unit, unit))
        {
            Forget(state);
        }
    }

    // --- cast pipeline ------------------------------------------------------------------

    private SpellCastResult Prepare(Unit caster, SpellInfo spell, SpellCastTargets targets, bool triggered, bool autoRepeatShot = false, Item? castItem = null,
        SpellInfo? triggeringSpell = null, byte itemSpellIndex = 0, int? itemCooldownMs = null, int? itemCategoryCooldownMs = null,
        uint? itemCategory = null, bool itemEquipCast = false, bool itemTriggeredCast = false,
        IReadOnlyDictionary<int, int>? customAuraAmounts = null, bool triggeredByAura = false)
    {
        if (castItem is not null && (IsQuestSettlementPending(caster) || IsInTransit(caster)
            || (caster is Player player && !player.IsInWorld)))
        {
            SendCastResult(caster, spell, SpellCastResult.NotReady, triggered);
            return SpellCastResult.NotReady;
        }

        UnitSpellState state = GetOrCreateState(caster);
        if (!triggered && spell.IsNextMeleeSwing)
        {
            // Own slot: neither blocked by nor interrupting a generic cast or a channel.
            return QueueNextSwing(state, caster, spell, targets, ResolveUnitTarget(caster, targets, spell),
                triggeringSpell, castItem, itemSpellIndex, itemCooldownMs, itemCategoryCooldownMs, itemCategory);
        }

        if (!triggered && RangedSpellFacts.IsAutoRepeatRanged(spell))
        {
            // ranged (autorepeat lane): Auto Shot / wand Shoot toggle in their own slot (SpellSystem.AutoRepeat.cs).
            return PrepareAutoRepeat(state, caster, spell, targets);
        }

        if (!triggered && state.CurrentCast is { } current)
        {
            if (current.State == SpellCastState.Preparing)
            {
                SendCastResult(caster, spell, SpellCastResult.SpellInProgress, triggered);
                return SpellCastResult.SpellInProgress;
            }

            // A new cast ends a running channel (vmangos Unit::SetCurrentCastedSpell: generic
            // spells interrupt the channeled slot).
            Cancel(current);
        }

        Unit? unitTarget = ResolveUnitTarget(caster, targets, spell);
        SpellCastResult result = CheckCast(state, spell, targets, unitTarget, triggered, strict: true,
            castItem: castItem, triggeringSpell: triggeringSpell, itemCategory: itemCategory);
        if (result != SpellCastResult.CastOk)
        {
            SendCastResult(caster, spell, result, triggered);
            return result;
        }

        // vmangos Spell::prepare (Spell.cpp:3395, :3436): the cost is read without spending mod charges, the cast time after the
        // first CheckCast with them ("to prevent charge counting for first CheckCast fail"), the duration never. An item cast has
        // no power cost (Spell::CheckPower and TakePower return at once for m_CastItem, Spell.cpp:5053,7050-7052).
        SpellModScope? modScope = ModEngine?.CreateScope(caster, spell);
        int castTime;
        using (BeginModWindow(modScope))
        {
            castTime = triggered ? 0 : CastTimeFor(caster, spell);
        }

        var cast = new SpellCast(spell, caster, targets, triggered, castTime,
            castItem is null ? PowerCostFor(caster, spell) : 0, DurationFor(caster, spell), triggeringSpell, castItem, itemSpellIndex,
            castItem is null ? (byte)0 : castItem.BagSlot, castItem is null ? (byte)0 : castItem.Slot,
            itemCooldownMs, itemCategoryCooldownMs, itemCategory, customAuraAmounts)
            { ModScope = modScope, IsItemEquipCast = itemEquipCast, IsItemTriggeredCast = itemTriggeredCast, IsTriggeredByAura = triggeredByAura };
        cast.AutoRepeatShot = autoRepeatShot;
        if (!triggered)
        {
            OnGenericCastStarted(state, spell); // ranged (autorepeat lane): a wand is broken, Auto Shot gets a new wind-up (Unit::SetCurrentCastedSpell)
            state.CurrentCast = cast;
            SendToSet(caster, WorldOpcode.SmsgSpellStart, SpellPackets.BuildSpellStart(
                castItem?.Guid ?? caster.Guid, caster.Guid, spell.Id, WithAmmoFlag(SpellCastFlags.Unknown2, spell), (uint)castTime, targets,
                RangedSpellFacts.IsRanged(spell) ? GetAmmoVisual(caster) : default), includeSelf: true); // ranged (hunter lane): ammo trailer
            AddGlobalCooldown(state, spell);
        }

        // vmangos Spell::prepare removes ACTION/LOOTING auras (and Stealth) before the cast bar runs (Spell.cpp:3443-3456).
        InterruptAtCastStart(cast);

        NotifyPrepared(cast);
        if (castTime == 0)
        {
            return Cast(cast);
        }

        return SpellCastResult.CastOk;
    }

    /// <summary>
    /// vmangos Spell::cast: re-check (with landing range leeway), start the cooldown, take the
    /// power, report success, send SMSG_SPELL_GO, start a channel, apply the effects, finish.
    /// </summary>
    private SpellCastResult Cast(SpellCast cast)
    {
        using ProcEventScope castEvent = BeginProcEvent(); // the cast-end procs, every target's hit and the casts they trigger are one event
        Unit caster = cast.Caster;
        SpellInfo spell = cast.Spell;
        UnitSpellState state = GetOrCreateState(caster);
        if (cast.CastItem is { } castItem && (caster is not Player castOwner || !castOwner.Inventory.OwnsItemAt(castItem, cast.ItemBag, cast.ItemSlot)))
        {
            Finish(cast);
            return SpellCastResult.ItemNotReady;
        }

        if (!cast.IsItemEquipCast && !cast.IsItemTriggeredCast && cast.CastItem is { } liveItem && caster is Player liveOwner
            && !CanStartItemUse(liveOwner, liveItem, cast.ItemSpellIndex, out _, requireCharges: !cast.IsTriggered))
        {
            Finish(cast);
            return SpellCastResult.ItemNotReady;
        }

        Unit? unitTarget = ResolveUnitTarget(caster, cast.Targets, spell);
        SpellCastResult result = CheckCast(state, spell, cast.Targets, unitTarget, cast.IsTriggered, strict: false, skipCooldown: true,
            castItem: cast.CastItem, triggeringSpell: cast.TriggeringSpell, itemCategory: cast.ItemCategory);
        InventoryRewardStage? reagentStage = null;
        if (result == SpellCastResult.CastOk)
        {
            InterruptAtCastCompletion(cast); // rogue lane: ACTION_LATE / ATTACKING half (vmangos Spell.cpp:3697-3714), docs/integration/rogue-aura-interrupt.md
            result = StageCastReagents(cast, out reagentStage);
        }
        if (result != SpellCastResult.CastOk)
        {
            SendCastResult(caster, spell, result, cast.IsTriggered);
            if (!cast.IsTriggered)
            {
                SendInterrupted(cast);
            }

            Finish(cast);
            return result;
        }

        // vmangos Spell::cast (Spell.cpp:3646-3658): the cost is read again, now spending mod charges, and everything the cast
        // reads from here until it returns (effects, crit rolls) spends the charges of the mods it uses.
        using SpellModWindow modWindow = BeginModWindow(cast.ModScope);
        if (cast.ModScope is not null && !cast.IsTriggered && cast.CastItem is null)
        {
            cast.PowerCost = PowerCostFor(caster, spell);
        }

        if (!cast.IsItemEquipCast)
        {
            // ranged (autorepeat lane): shots tell the client nothing; an item spell may carry its own category and times.
            AddCooldown(state, spell, cast.IsTriggered && !cast.AutoRepeatShot, cast.ItemCooldownMs, cast.ItemCategoryCooldownMs, cast.ItemCategory,
                cast.CastItem?.Entry ?? 0, castItem: cast.CastItem);
        }

        if (cast.CastItem is null)
        {
            TakePower(caster, spell, cast.PowerCost, cast.IsTriggeredByAura); // vmangos Spell::TakePower returns at once for an item cast (Spell.cpp:5053)
        }

        if (reagentStage is not null)
        {
            // Spell.cpp:3716-3718: power, reagents, then ammunition. Apply the
            // whole cost before callbacks, preserving every existing item identity.
            reagentStage.Inventory.ApplyQuestRewardInventory(reagentStage);
            reagentStage.Inventory.NotifyQuestRewardInventory(reagentStage);

            // Spell.cpp:7268-7290 clears m_CastItem whenever a reagent entry
            // matches, even when another stack supplied the reagent. This is
            // what prevents TakeCastItem from charging/deleting the same item
            // a second time. Matching item targets are cleared as well.
            if (cast.CastItem is { } paidCastItem
                && cast.Spell.Reagents.Any(r => r.Item > 0 && (uint)r.Item == paidCastItem.Entry))
            {
                cast.CastItem = null;
                if (CurrentItemUseDispatch(paidCastItem) is { } dispatch)
                    dispatch.ReagentCleared = true;
                if ((cast.Targets.Mask & SpellCastTargetFlags.Item) != 0
                    && caster is Player reagentPlayer
                    && reagentPlayer.Inventory.GetItemByGuid(cast.Targets.Item) is { } targetItem
                    && cast.Spell.Reagents.Any(r => r.Item > 0 && (uint)r.Item == targetItem.Entry))
                {
                    cast.Targets.Item = default;
                    cast.Targets.Mask &= ~SpellCastTargetFlags.Item;
                }
            }
        }

        TakeCosts(cast); // registered cost takers (ISpellCostTaker), after the reagents and before TakeAmmo
        TakeAmmo(caster, spell); // ranged (hunter lane): vmangos order TakePower, TakeReagents, TakeAmmo (Spell.cpp:3716-3718)
        SendCastResult(caster, spell, SpellCastResult.CastOk, cast.IsTriggered);
        cast.Completed = true;
        NotifyCast(cast);
        if (spell.IsChanneled)
        {
            ResetPendingChannelBeforeStart(state); // vmangos Spell::cast, after OnSpellLaunch and before the spell go (Spell.cpp:3460-3469)
        }

        Dictionary<Unit, SpellTargetEntry> targetEffects = SelectTargets(cast, unitTarget);
        // Magnet selection changes the explicit target; a channel must track that selected unit.
        unitTarget = ResolveUnitTarget(caster, cast.Targets, spell);
        var hits = new List<ObjectGuid>();
        var misses = new List<(ObjectGuid Guid, SpellMissInfo Reason)>();
        foreach ((Unit target, SpellTargetEntry entry) in targetEffects)
        {
            // Spell::AddUnitTarget removes immune effects before WriteSpellGoTargets. A target
            // with no surviving effects is listed as IMMUNE2 rather than a successful hit.
            for (int i = 0; i < SpellConstants.MaxEffects; i++)
            {
                if ((entry.EffectMask & (1 << i)) != 0
                    && spell.Effects[i].Effect != SpellEffectName.ActivateObject
                    && ImmunityRules.IsImmuneToSpellEffect(this, target, spell, i, ReferenceEquals(target, caster)))
                {
                    entry.EffectMask &= ~(1 << i);
                }
            }

            // vmangos Spell::AddUnitTarget → Unit::SpellHitResult, once per target.
            // A self cast skips the roll but not the immunity test: vmangos SpellHitResult asks IsImmuneToSpell(spell, victim == this) before the
            // "victim == this" return (SpellCaster.cpp:175-180), so a self bandage on a Recently Bandaged player lands immune (crafting lane).
            bool objectOnly = entry.EffectMask != 0;
            for (int i = 0; i < SpellConstants.MaxEffects && objectOnly; i++)
            {
                if ((entry.EffectMask & (1 << i)) != 0 && spell.Effects[i].Effect != SpellEffectName.ActivateObject)
                    objectOnly = false;
            }
            entry.Miss = objectOnly ? SpellMissInfo.None
                : entry.EffectMask == 0 ? SpellMissInfo.Immune2
                : ReferenceEquals(target, caster)
                    ? (Rules.Immunity.ImmunityRules.IsImmuneToSpell(this, target, spell, castOnSelf: true) ? SpellMissInfo.Immune : SpellMissInfo.None)
                    : CombatRules.RollHit(this, caster, target, spell);
            if (entry.Miss == SpellMissInfo.None)
            {
                if (!objectOnly) hits.Add(target.Guid); // a carrier for GO effects is not itself a hit
            }
            else
            {
                misses.Add((target.Guid, entry.Miss));
            }
        }

        // Spell::WriteSpellGoTargets includes selected game objects alongside unit hits.
        foreach (ObjectGuid guid in cast.ObjectTargetsByEffect.Values.SelectMany(guids => guids).Distinct())
        {
            if (!hits.Contains(guid)) hits.Add(guid);
        }

        SendToSet(caster, WorldOpcode.SmsgSpellGo, SpellPackets.BuildSpellGo(
            cast.CastItem?.Guid ?? caster.Guid, caster.Guid, spell.Id, WithAmmoFlag(SpellCastFlags.Unknown9, spell), hits, misses, cast.Targets,
            RangedSpellFacts.IsRanged(spell) ? GetAmmoVisual(caster) : default), includeSelf: true); // ranged (hunter lane): ammo trailer

        // Proc engine: the cast-end procs before the effects (vmangos Spell::cast, Spell.cpp:3724-3749), and for a channel the target triggers
        // as soon as the targets are known (Spell.cpp:3682-3683).
        Unit castEndTarget = unitTarget ?? caster;
        FireCastEndProcs(cast, castEndTarget, targetEffects.TryGetValue(castEndTarget, out SpellTargetEntry? mainEntry) ? mainEntry.Miss : null,
            targetEffects.Count == 0);
        var procOutcomes = targetEffects.Select(pair => (pair.Key, pair.Value.Miss)).ToList();
        if (spell.IsChanneled && !cast.IsTriggered)
        {
            HandleAddTargetTriggerAuras(cast, procOutcomes);
        }

        int duration = cast.Duration;
        if (spell.IsChanneled && duration > 0 && !cast.IsTriggered)
        {
            cast.State = SpellCastState.Casting;
            cast.Timer = duration;
            SealModScope(cast); // vmangos Spell.cpp:3834: a channel ends its mods when it starts, not when it ends
            cast.CastX = caster.X;
            cast.CastY = caster.Y;
            cast.CastZ = caster.Z;
            cast.CastO = caster.Orientation;
            if (caster is Player player)
            {
                player.Session.Send(WorldOpcode.MsgChannelStart, SpellPackets.BuildChannelStart(spell.Id, (uint)duration));
            }

            caster.SetUInt64(UpdateFields.UnitFieldChannelObject, (unitTarget ?? caster).Guid.Value);
            caster.SetUInt32(UpdateFields.UnitChannelSpell, spell.Id);
        }

        foreach ((Unit target, SpellTargetEntry entry) in targetEffects)
        {
            // vmangos Spell::DoAllEffectOnTarget (Spell.cpp:1129-1137): a persistent area aura runs once on the ground (HandleGroundEffects below),
            // never on the units its selector listed in SMSG_SPELL_GO.
            int unitMask = WithoutGroundEffects(spell, entry.EffectMask);
            if (unitMask != entry.EffectMask)
            {
                if (unitMask == 0)
                {
                    continue;
                }

                entry.EffectMask = unitMask;
            }

            if (entry.Miss == SpellMissInfo.Reflect)
            {
                // vmangos Spell::DoAllEffectOnTarget (Spell.cpp:1209-1224): the reflected spell lands on its caster instead, unless the caster is
                // immune to it (reflectResult) or a game object cast it (hunter traps are not reflected back after 1.10, Spell.cpp:944-957).
                if (_objectCastDepth == 0 && caster.IsAlive && !ImmunityRules.IsImmuneToSpell(this, caster, spell, castOnSelf: true)
                    && ApplyEffects(cast, caster, entry.EffectMask, entry.Multipliers, reflected: true) is { } reflectedOutcome)
                {
                    NotifyOutcome(cast, reflectedOutcome with { Miss = SpellMissInfo.Reflect });
                }

                continue;
            }

            if (entry.Miss != SpellMissInfo.None)
            {
                FireSpellHitProcs(cast, target, entry.Miss, 0, 0, false, 0, entry.EffectMask, reflected: false);
                SendNextMeleeSpellNoDamage(cast, target, entry.Miss);
                // vmangos SpellCaster::SendSpellMiss; a missed hostile spell still starts combat (zero damage).
                SendToSet(caster, WorldOpcode.SmsgSpelllogmiss, SpellPackets.BuildSpellLogMiss(spell.Id, caster.Guid, target.Guid, entry.Miss), includeSelf: true);
                InterruptTargetOfHostileSpell(cast, target, hit: false, dealsDamage: false); // rogue lane (vmangos Spell.cpp:1893-1897)
                if (!IsQuestSettlementPending(caster) && !IsQuestSettlementPending(target) && target.IsAlive && Relations.IsHostile(caster, target))
                {
                    Damage.DealSpellDamage(caster, target, spell, 0, periodic: false, startsCombat: StartsCombat(caster, target));
                }

                HandleItemSpecialProc(cast, target, entry.Miss);
                NotifyOutcome(cast, new SpellTargetOutcome(target, entry.Miss, 0, 0, false, entry.EffectMask));
                continue;
            }

            bool nextMeleeTarget = spell.IsNextMeleeSwing && ReferenceEquals(caster.Combat.Victim, target);
            if (ApplyEffects(cast, target, entry.EffectMask, entry.Multipliers) is { } outcome)
            {
                ApplySpellThreat(cast, target, outcome); // SpellSystem.Threat.cs: combat on a harmless hostile hit, assist, flat spell_threat
                if (outcome.Damage == 0)
                    SendNextMeleeSpellNoDamage(cast, target, eligible: nextMeleeTarget);
                HandleItemSpecialProc(cast, target, SpellMissInfo.None);
                // vmangos Spell.cpp:1534-1537: a weapon ability that dealt damage triggers the target's damage shields.
                if (outcome.Damage > 0 && !ReferenceEquals(caster, target) && CanTriggerWeaponProcs(spell))
                    TriggerDamageShields(caster, target);
                NotifyOutcome(cast, outcome);
            }
        }

        HandleGroundEffects(cast, unitTarget); // vmangos "process ground" (Spell.cpp:3971-3976)
        if (!spell.IsChanneled || cast.IsTriggered)
        {
            HandleAddTargetTriggerAuras(cast, procOutcomes); // vmangos Spell::finish (Spell.cpp:4368-4369)
        }

        // ranged (autorepeat lane): a non-triggered cast with the combat interrupt bit restarts the melee swing (vmangos Spell.cpp:3805-3810).
        if (!cast.IsTriggered && spell.InterruptFlags.HasFlag(SpellInterruptFlags.Combat) && !RangedSpellFacts.DoesNotResetCombatTimers(spell))
        {
            caster.Map?.FindUpdater<Combat.MapCombat>()?.ResetMeleeTimersAfterCast(caster);
        }

        // vmangos Spell::TakeCastItem, after the effects and SMSG_SPELL_GO (Spell.cpp:3876-3878). Cancellation and failed
        // completion therefore leave both charges and stack count untouched; a multi-spell item use consumes once.
        if (!cast.IsTriggered && cast.CastItem is { } consumed && caster is Player owner)
        {
            if (CurrentItemUseDispatch(consumed) is { } dispatch)
                dispatch.PendingConsume = true;
            else
                owner.Inventory.ConsumeItemUse(consumed);
        }

        if (cast.State != SpellCastState.Casting)
        {
            Finish(cast);
        }

        return SpellCastResult.CastOk;
    }

    private void UpdateCast(SpellCast cast, uint diffMs)
    {
        if (cast.CastItem is { } castItem && (cast.Caster is not Player owner
            || !owner.Inventory.OwnsItemAt(castItem, cast.ItemBag, cast.ItemSlot)))
        {
            Cancel(cast);
            return;
        }

        if ((!cast.Targets.Unit.IsEmpty || HasResurrectionCorpseTarget(cast.Spell, cast.Targets))
            && ResolveUnitTarget(cast.Caster, cast.Targets, cast.Spell) is null)
        {
            Cancel(cast);
            return;
        }

        if (cast.State == SpellCastState.Casting && cast.MagnetTarget is { } magnet
            && (!magnet.IsAlive || !magnet.IsInWorld || !ReferenceEquals(magnet.Map, cast.Caster.Map)
                || !ReferenceEquals(Units.Find(cast.Caster, magnet.Guid), magnet)))
        {
            Cancel(cast);
            return;
        }

        if (cast.Caster is Player && cast.Timer != 0 && cast.HasMoved
            && (cast.Spell.InterruptFlags.HasFlag(SpellInterruptFlags.Movement)
                || cast.Spell.AuraInterruptFlags.HasFlag(SpellAuraInterruptFlags.Moving)
                || cast.Spell.ChannelInterruptFlags.HasFlag(SpellAuraInterruptFlags.Moving)))
        {
            // vmangos Spell::update: channels always cancel; casts only with the movement interrupt flag.
            if (cast.State == SpellCastState.Casting
                || (!cast.IsTriggered && cast.Spell.InterruptFlags.HasFlag(SpellInterruptFlags.Movement)))
            {
                Cancel(cast);
                return;
            }
        }

        // ranged (hunter lane): a player's cast bar does not run while feigning death (Spell.cpp:4082-4090).
        if (cast.State == SpellCastState.Preparing && cast.Timer != 0 && cast.Caster is Player && IsFeigningDeath(cast.Caster))
        {
            Cancel(cast);
            return;
        }

        if (cast.State == SpellCastState.Casting && cast.Timer > 0)
        {
            // vmangos Spell::update (Spell.cpp:4130-4139): a jump cancels every player channel, and a turn cancels a channel with
            // the turning channel interrupt flag (the orientation is compared with the one stored when the channel started).
            if (cast.Caster is Player channeller
                && (channeller.Movement.HasFlag(MovementFlags.Jumping)
                    || (cast.Spell.ChannelInterruptFlags.HasFlag(SpellAuraInterruptFlags.Turning) && channeller.Orientation != cast.CastO)))
            {
                Cancel(cast);
                return;
            }

            // vmangos Spell.cpp:4142-4147: no target the channel needs is left alive (HasValidUnitPresentInTargetList).
            if (!HasValidChannelTarget(cast))
            {
                EndChannelWithoutTargets(cast);
                return;
            }

            if (IsChannelTargetOutOfRange(cast))
            {
                Cancel(cast);
                return;
            }
        }

        cast.Timer = diffMs >= cast.Timer ? 0 : cast.Timer - (int)diffMs;
        if (cast.Timer > 0)
        {
            return;
        }

        if (cast.State == SpellCastState.Preparing)
        {
            Cast(cast);
        }
        else if (cast.State == SpellCastState.Casting)
        {
            EndChannel(cast, interrupted: false);
            Finish(cast);
        }
    }

    /// <summary>
    /// vmangos Spell::cancel: a preparing cast resets its global cooldown, tells the set it was
    /// interrupted (SMSG_SPELL_FAILED_OTHER, self included) and reports INTERRUPTED to the caster;
    /// a channel removes its auras, sends a zero channel update and the interrupt (no cast result).
    /// </summary>
    private void Cancel(SpellCast cast)
    {
        if (cast.State == SpellCastState.Finished)
        {
            return;
        }

        UnitSpellState state = GetOrCreateState(cast.Caster);
        if (cast.State == SpellCastState.Preparing)
        {
            state.GlobalCooldowns.Remove(cast.Spell.StartRecoveryCategory);
            SendInterrupted(cast);
            SendCastResult(cast.Caster, cast.Spell, SpellCastResult.Interrupted, cast.IsTriggered);
        }
        else
        {
            RemoveChannelAuras(cast);
            EndChannel(cast, interrupted: true);
            SendInterrupted(cast);
        }

        Finish(cast);
    }

    /// <summary>The auras a channel placed on its caster and its target (vmangos SendChannelUpdate(0), Spell.cpp: AURA_REMOVE_BY_CHANNEL).</summary>
    private void RemoveChannelAuras(SpellCast cast)
    {
        RemoveAurasByCaster(cast.Caster, cast.Spell.Id, cast.Caster.Guid);
        if (ResolveUnitTarget(cast.Caster, cast.Targets, cast.Spell) is { } target && target != cast.Caster)
        {
            RemoveAurasByCaster(target, cast.Spell.Id, cast.Caster.Guid);
        }

        // Arcane Missiles redirects its aura while retaining the original request target.
        if (cast.MagnetTarget is { } magnet && !ReferenceEquals(magnet, cast.Caster))
        {
            RemoveAurasByCaster(magnet, cast.Spell.Id, cast.Caster.Guid);
        }
    }

    /// <summary>
    /// vmangos Spell.cpp:4142-4147: a channel with no valid target left ends with SendChannelUpdate(0, true) and finish(): its
    /// auras go and the channel fields clear, but no interrupt is reported (it is not Spell::cancel).
    /// </summary>
    private void EndChannelWithoutTargets(SpellCast cast)
    {
        RemoveChannelAuras(cast);
        EndChannel(cast, interrupted: true);
        Finish(cast);
    }

    /// <summary>
    /// vmangos Spell::HasValidUnitPresentInTargetList (Spell.cpp:1957-1990): a channel that applies an aura to a unit other than
    /// its caster needs one of those units in a state the spell can target (alive, or dead for a spell that targets the dead).
    /// Only the explicit (or redirected) unit target is tracked here, which covers the single-target channels (Drain Life, Mind
    /// Flay, Arcane Missiles); a channel whose auras all sit on its caster needs no target.
    /// </summary>
    private bool HasValidChannelTarget(SpellCast cast)
    {
        bool needsTarget = cast.Spell.Effects.Any(e => e.Effect == SpellEffectName.ApplyAura && e.AuraType != AuraType.None
            && e.TargetA != SpellImplicitTarget.UnitCaster);
        if (!needsTarget)
        {
            return true;
        }

        Unit? target = cast.MagnetTarget ?? ResolveUnitTarget(cast.Caster, cast.Targets, cast.Spell);
        if (target is null || ReferenceEquals(target, cast.Caster))
        {
            return true;
        }

        return target.IsAlive ? !cast.Spell.IsDeathOnly : cast.Spell.CanTargetDead;
    }

    /// <summary>
    /// vmangos SpellAuraHolder::UpdateHolder (SpellAuras.cpp:7338-7376): while the channel's aura sits on its channel target,
    /// the channel is interrupted when the combat distance passes the spell's maximum range times 1.33 for a hostile target, or
    /// plus 1.25 yd otherwise, with SPELLMOD_RANGE applied after. SPELL_CUSTOM_CHAN_NO_DIST_LIMIT channels have no limit.
    /// </summary>
    private bool IsChannelTargetOutOfRange(SpellCast cast)
    {
        const uint customChanNoDistLimit = 0x008; // vmangos SpellDefines.h:1005
        Unit caster = cast.Caster;
        ulong channelObject = caster.GetUInt64(UpdateFields.UnitFieldChannelObject);
        if ((cast.Spell.CustomFlags & customChanNoDistLimit) != 0 || channelObject == 0 || channelObject == caster.Guid.Value
            || Units.Find(caster, new ObjectGuid(channelObject)) is not { } target
            || !GetAuras(target).Any(h => h.Spell.Id == cast.Spell.Id && h.CasterGuid == caster.Guid))
        {
            return false;
        }

        float maxRange = Relations.IsHostile(target, caster) ? cast.Spell.Range.Max * 1.33f : cast.Spell.Range.Max + 1.25f;
        maxRange = SpellModifiers.Apply(caster, cast.Spell, SpellModOp.Range, maxRange);
        float combatDistance = Math.Max(0.0f, Distance3D(caster, target.X, target.Y, target.Z) - (CombatReach(caster) + CombatReach(target)));
        return combatDistance > maxRange;
    }

    /// <summary>How long the channel fields outlive a channel that ended normally (vmangos <c>AddEventAtOffset(ChannelResetEvent, 1000)</c>).</summary>
    internal const uint ChannelResetDelayMs = 1000;

    /// <summary>
    /// vmangos Spell::SendChannelUpdate(0, interrupted) (Spell.cpp:4801-4823): when the caster still shows a channel, an interrupt
    /// clears it at once (<see cref="CancelChannelingAnimation"/>), a normal end ("else, we have some visual bugs (arcane
    /// projectile, last tick)") leaves it for <see cref="ChannelResetDelayMs"/> (ChannelResetEvent, run by <see cref="UpdatePendingChannelReset"/>).
    /// </summary>
    private void EndChannel(SpellCast cast, bool interrupted)
    {
        Unit caster = cast.Caster;
        if (caster.GetUInt32(UpdateFields.UnitChannelSpell) == 0 && caster.GetUInt64(UpdateFields.UnitFieldChannelObject) == 0)
        {
            return;
        }

        UnitSpellState state = GetOrCreateState(caster);
        if (interrupted)
        {
            state.PendingChannelResetMs = 0;
            CancelChannelingAnimation(caster);
        }
        else
        {
            state.PendingChannelResetMs = ChannelResetDelayMs;
        }
    }

    /// <summary>
    /// vmangos ChannelResetEvent::Execute / Abort (Spell.cpp:8341-8357): when the second is up the values go, unless a channel is
    /// running again by then (its start already reset them, <see cref="ResetPendingChannelBeforeStart"/>).
    /// </summary>
    private static void UpdatePendingChannelReset(UnitSpellState state, uint diffMs)
    {
        if (state.PendingChannelResetMs == 0)
        {
            return;
        }

        if (diffMs < state.PendingChannelResetMs)
        {
            state.PendingChannelResetMs -= diffMs;
            return;
        }

        state.PendingChannelResetMs = 0;
        if (state.CurrentCast is not { State: SpellCastState.Casting } running || !running.Spell.IsChanneled)
        {
            CancelChannelingAnimation(state.Unit);
        }
    }

    /// <summary>"Prevent animation from disappearing if casting another channel too soon after previous ends" (vmangos Spell.cpp:3463-3469).</summary>
    private static void ResetPendingChannelBeforeStart(UnitSpellState state)
    {
        if (state.PendingChannelResetMs != 0)
        {
            state.PendingChannelResetMs = 0;
            CancelChannelingAnimation(state.Unit);
        }
    }

    /// <summary>vmangos Unit::CancelSpellChannelingAnimationInstantly (Unit.cpp:10747-10755): the zero channel update for a player, then the fields.</summary>
    private static void CancelChannelingAnimation(Unit caster)
    {
        if (caster is Player player)
        {
            player.Session.Send(WorldOpcode.MsgChannelUpdate, SpellPackets.BuildChannelUpdate(0));
        }

        caster.SetUInt64(UpdateFields.UnitFieldChannelObject, 0);
        caster.SetUInt32(UpdateFields.UnitChannelSpell, 0);
    }

    private void Finish(SpellCast cast)
    {
        bool alreadyFinished = cast.State == SpellCastState.Finished;
        cast.State = SpellCastState.Finished;
        if (_states.TryGetValue(cast.Caster.Guid, out UnitSpellState? state))
        {
            if (ReferenceEquals(state.CurrentCast, cast))
            {
                state.CurrentCast = null;
            }

            if (ReferenceEquals(state.MeleeCast, cast))
            {
                state.MeleeCast = null;
            }

            if (ReferenceEquals(state.AutoRepeatCast, cast))
            {
                state.AutoRepeatCast = null;
            }
        }

        if (!alreadyFinished)
        {
            NotifyFinished(cast);
        }
    }

    private void Forget(UnitSpellState state)
    {
        RevokeAuraCaster(state.Unit);
        foreach (SpellCast? slot in new[] { state.CurrentCast, state.MeleeCast, state.AutoRepeatCast })
        {
            if (slot is { State: not SpellCastState.Finished } cast)
            {
                cast.State = SpellCastState.Finished;
                NotifyFinished(cast);
            }
        }

        state.CurrentCast = null;
        state.MeleeCast = null;
        state.AutoRepeatCast = null;

        // The unit's events die with it; ChannelResetEvent::Abort still clears the values (the unit is leaving, so no packet).
        if (state.PendingChannelResetMs != 0)
        {
            state.PendingChannelResetMs = 0;
            state.Unit.SetUInt64(UpdateFields.UnitFieldChannelObject, 0);
            state.Unit.SetUInt32(UpdateFields.UnitChannelSpell, 0);
        }

        foreach (SpellAuraHolder holder in state.Auras)
        {
            holder.IsRemoved = true;
        }

        state.Auras.Clear();
        _states.Remove(state.Unit.Guid);
    }

    // --- checks -------------------------------------------------------------------------

    /// <summary>
    /// The subset of vmangos Spell::CheckCast this area owns: caster alive, cooldowns, stun,
    /// movement, explicit target presence and liveness, range (CheckRange) and power (CheckPower).
    /// Line of sight is delegated to the vmap-los seam. Reagents, items, shapeshift, facing and area restrictions belong to other
    /// areas (docs/areas/spells.md).
    /// </summary>
    private SpellCastResult CheckCast(UnitSpellState state, SpellInfo spell, SpellCastTargets targets, Unit? unitTarget, bool triggered, bool strict,
        bool skipCooldown = false, Item? castItem = null, SpellInfo? triggeringSpell = null, uint? itemCategory = null)
    {
        Unit caster = state.Unit;
        if (IsQuestSettlementPending(caster))
        {
            return SpellCastResult.NotReady;
        }

        // Registered first checks (stand state): vmangos Spell.cpp:5309.
        SpellCastResult start = RunCastChecks(SpellCheckPhase.Start, caster, spell, targets, unitTarget, triggered, strict, castItem);
        if (start != SpellCastResult.CastOk)
        {
            return start;
        }

        // vmangos Spell.cpp:5320 also lets a dead unit cast a triggered spell no aura triggered; here only a creature's script cast takes that
        // exception (EventAI "cast on death"), a dead player still needs SPELL_ATTR_ALLOW_CAST_WHILE_DEAD. A passive spell is exempt, as in
        // vmangos (`!(Attributes & SPELL_ATTR_PASSIVE)`): a character that logs in as a ghost must still get its passives (languages,
        // proficiencies), or it knows no language and every chat line, '.' commands included, is refused (wave-10 rehearsal).
        if (!caster.IsAlive && !spell.IsPassive && !spell.HasAttribute(SpellAttributes.AllowCastWhileDead) && _objectCastDepth == 0
            && !(caster is Creatures.Creature && triggered && triggeringSpell is null))
        {
            return SpellCastResult.CasterDead;
        }

        if (!triggered && !skipCooldown && !IsSpellReady(state, spell, itemCategory, castItem))
        {
            return SpellCastResult.NotReady;
        }

        // Registered caster-state checks (shapeshift, caster aura state): vmangos Spell.cpp:5349-5392.
        SpellCastResult casterState = RunCastChecks(SpellCheckPhase.Caster, caster, spell, targets, unitTarget, triggered, strict, castItem);
        if (casterState != SpellCastResult.CastOk)
        {
            return casterState;
        }

        // vmangos Spell::ValidateExplicitTargetMask (Spell.cpp:6757-6761, asked for a client's own cast at :5346): a spell whose Targets
        // expect a corpse (the player resurrections, Remove Insignia) needs a unit or a corpse in the client's target block.
        if (!triggered && strict && caster is Player
            && (spell.Targets & (uint)(SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.CorpseEnemy)) != 0
            && (targets.Mask & (SpellCastTargetFlags.Unit | SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.CorpseEnemy)) == 0)
        {
            return SpellCastResult.BadTargets;
        }

        if (!triggered)
        {
            // The caster's own state (stun, confuse, fear, silence, pacify) and the immunity-granting-spell bypass (vmangos Spell::CheckCasterAuras).
            SpellCastResult stateResult = Rules.Gating.CasterAuraGate.Check(this, caster, spell, CastTimeFor(caster, spell));
            if (stateResult != SpellCastResult.CastOk)
            {
                return stateResult;
            }
        }

        // ranged (autorepeat lane): an auto-repeat spell cast while moving is delayed, not refused (vmangos Spell.cpp:5395-5403).
        if (!triggered && caster is Player moving && RangedSpellFacts.IsAutoRepeatRanged(spell) && IsMoving(moving))
        {
            return SpellCastResult.Moving;
        }

        if (!triggered && strict && caster is Player mover && CastTimeFor(caster, spell) > 0
            && spell.InterruptFlags.HasFlag(SpellInterruptFlags.Movement) && IsMoving(mover))
        {
            return SpellCastResult.Moving;
        }

        // ranged (hunter lane): vmangos Spell::CheckItems (weapon and ammunition) runs before CheckRange (Spell.cpp:5694-5702).
        SpellCastResult rangedItems = CheckRangedItems(caster, spell);
        if (rangedItems != SpellCastResult.CastOk)
        {
            return rangedItems;
        }

        SpellCastResult reagents = CheckReagents(caster, spell, targets, triggered, triggeringSpell, castItem);
        if (reagents != SpellCastResult.CastOk)
        {
            return reagents;
        }

        // ranged (hunter lane): Hunter's Mark needs an attackable unit (Spell.cpp:6436-6447).
        SpellCastResult stalked = CheckStalkedTarget(caster, spell, unitTarget);
        if (stalked != SpellCastResult.CastOk)
        {
            return stalked;
        }

        bool needsUnit = NeedsUnitTarget(spell);
        Unit? checkedTarget = null;
        if (needsUnit)
        {
            Unit? target = unitTarget ?? (targets.Mask == SpellCastTargetFlags.Self ? caster : null);
            if (target is null)
            {
                return SpellCastResult.BadTargets;
            }

            if (IsQuestSettlementPending(target))
            {
                return SpellCastResult.BadTargets;
            }

            // vmangos Spell::CheckCast (Spell.cpp:5572) asks SpellEntry::CanTargetAliveState (SpellEntry.h:944-950):
            // a dead explicit target is fine for a spell that can target the dead.
            if (!target.IsAlive && !spell.CanTargetDead)
            {
                return SpellCastResult.TargetsDead;
            }

            checkedTarget = target;

            // Registered target-state checks (target aura state): vmangos Spell.cpp:5636.
            SpellCastResult targetState = RunCastChecks(SpellCheckPhase.Target, caster, spell, targets, target, triggered, strict, castItem);
            if (targetState != SpellCastResult.CastOk)
            {
                return targetState;
            }
        }

        // Registered equipment checks: vmangos CheckItems (Spell.cpp:5698), before CheckRange (:5707) and CheckPower (:5721).
        if (!triggered && castItem is { } resourceItem && caster is Player itemUser)
        {
            SpellCastResult resources = CheckItemResources(itemUser, resourceItem, spell, targets,
                checkedTarget ?? unitTarget ?? caster, triggered);
            if (resources != SpellCastResult.CastOk) return resources;
        }
        SpellCastResult items = RunCastChecks(SpellCheckPhase.Items, caster, spell, targets, checkedTarget, triggered, strict, castItem, triggeringSpell);
        if (items != SpellCastResult.CastOk)
        {
            return items;
        }

        if (needsUnit)
        {
            Unit target = checkedTarget!;
            // vmangos Spell.cpp:3112-3117 resolves a resurrection's body to its owner, and 5780-5788 checks the body's line of sight
            // (the resurrect effect checks, ResurrectEffects.CheckCorpse): the range is never measured to the ghost, which may have
            // left for a graveyard.
            if (!HasResurrectionCorpseTarget(spell, targets))
            {
                // ranged (hunter lane): a cast from a game object (trap) ignores range and the owner being far or dead (vmangos triggered casts).
                SpellCastResult range = _objectCastDepth > 0 ? SpellCastResult.CastOk
                    : CheckRange(caster, spell, target, strict, RangedOptions.Range.Leeway == RangeLeewayMode.Retail, SpellModifiers);
                if (range != SpellCastResult.CastOk)
                {
                    return range;
                }

                // Line of sight (vmap-los seam, docs/integration/vmap-los.md).
                SpellCastResult sight = Maps.Collision.SpellLineOfSight.Check(caster, spell, target, triggered);
                if (sight != SpellCastResult.CastOk)
                {
                    return sight;
                }
            }
        }
        else if (targets.HasDest)
        {
            SpellCastResult range = CheckDestRange(caster, spell, targets, strict, SpellModifiers);
            if (range != SpellCastResult.CastOk)
            {
                return range;
            }

            SpellCastResult sight = Maps.Collision.SpellLineOfSight.CheckDest(caster, spell, targets.Dest.X, targets.Dest.Y, targets.Dest.Z, triggered);
            if (sight != SpellCastResult.CastOk)
            {
                return sight;
            }
        }

        SpellCastResult targetRules = CheckTargetRules(caster, spell, targets, unitTarget, strict);
        if (targetRules != SpellCastResult.CastOk)
        {
            return targetRules;
        }

        SpellCastResult effectChecks = CheckEffects(caster, spell, targets, unitTarget, triggered, strict, castItem);
        if (effectChecks != SpellCastResult.CastOk)
        {
            return effectChecks;
        }

        // vmangos Spell::CheckPower returns SPELL_CAST_OK at once for an item cast ("item cast not used power", Spell.cpp:7050-7052), so neither its
        // registered checks nor the amounts run; the take half skips the power the same way (TakePower, Spell.cpp:5053).
        if (castItem is null)
        {
            // Registered checks inside vmangos CheckPower, before the amounts (combo points, Spell.cpp:7035-7038).
            SpellCastResult beforePower = RunCastChecks(SpellCheckPhase.Power, caster, spell, targets, unitTarget, triggered, strict, castItem);
            if (beforePower != SpellCastResult.CastOk)
            {
                return beforePower;
            }

            SpellCastResult power = CheckPower(caster, spell, triggered);
            if (power != SpellCastResult.CastOk)
            {
                return power;
            }
        }

        // Registered checks after power and caster auras: the target aura state (Spell.cpp:5733-5742).
        return RunCastChecks(SpellCheckPhase.Final, caster, spell, targets, unitTarget, triggered, strict, castItem);
    }

    /// <summary>
    /// vmangos Spell::CheckRange: self-only range always passes; combat-range spells use melee
    /// reach (5 yd + both combat reaches); otherwise the SpellRange maximum plus the player leeway
    /// (1.25 yd at cast start, 6.25 yd on landing) against the combat distance (3D distance minus
    /// both combat reaches), with the minimum range giving TOO_CLOSE.
    /// </summary>
    internal static SpellCastResult CheckRange(Unit caster, SpellInfo spell, Unit target, bool strict, bool movementLeeway = true, Rules.ISpellModifiers? modifiers = null)
    {
        if (spell.RangeIndex == SpellConstants.RangeIndexSelfOnly || ReferenceEquals(caster, target))
        {
            return SpellCastResult.CastOk;
        }

        float distance = Distance3D(caster, target.X, target.Y, target.Z);
        float reach = CombatReach(caster) + CombatReach(target);
        if (spell.RangeIndex == SpellConstants.RangeIndexCombat)
        {
            // vmangos Spell::CheckRange (Spell.cpp:6882): a next-melee-swing spell passes; the swing itself checks reach.
            if (spell.IsNextMeleeSwing)
            {
                return SpellCastResult.CastOk;
            }

            // vmangos WorldObject::CanReachWithMeleeSpellAttack with Spell::CheckRange's range_mod
            // 1.0: reach = both combat reaches + 1.0 + BASE_MELEERANGE_OFFSET (4/3), at least
            // ATTACK_DISTANCE, compared in 2D ("melee spells ignore Z-axis checks").
            // The SPELLMOD_RANGE spell mod is read on ATTACK_DISTANCE and its difference is added to the 1.0 (Spell.cpp:6890-6899:
            // "range_mod += ApplySpellMod(..., base = ATTACK_DISTANCE)"): a vmangos quirk reproduced, not corrected.
            float rangeMod = 1.0f;
            if (modifiers is not null)
            {
                rangeMod += modifiers.Apply(caster, spell, SpellModOp.Range, SpellConstants.AttackDistance) - SpellConstants.AttackDistance;
            }

            float meleeRange = Math.Max(SpellConstants.AttackDistance, reach + rangeMod + (4.0f / 3.0f));
            float dx = caster.X - target.X;
            float dy = caster.Y - target.Y;
            return (dx * dx) + (dy * dy) < meleeRange * meleeRange ? SpellCastResult.CastOk : SpellCastResult.OutOfRange;
        }

        float leeway = caster is Player
            ? (strict ? SpellConstants.PlayerStrictRangeLeeway : SpellConstants.PlayerLandingRangeLeeway)
            : (strict ? 0.0f : 2.25f);

        // ranged (hunter lane): + 2.66 yd when a player is involved and both run (Spell.cpp:6911, Object.cpp:1890-1912).
        if (movementLeeway)
        {
            leeway += RangeLeeway.Bonus(caster, target);
        }

        // vmangos Spell.cpp:6915-6917: SPELLMOD_RANGE on the maximum range, before the leeway is added.
        float maxRange = modifiers?.Apply(caster, spell, SpellModOp.Range, spell.Range.Max) ?? spell.Range.Max;
        float combatDistance = Math.Max(0.0f, distance - reach);
        if (combatDistance > maxRange + leeway)
        {
            return SpellCastResult.OutOfRange;
        }

        return spell.Range.Min > 0 && combatDistance < spell.Range.Min ? SpellCastResult.TooClose : SpellCastResult.CastOk;
    }

    private static SpellCastResult CheckDestRange(Unit caster, SpellInfo spell, SpellCastTargets targets, bool strict, Rules.ISpellModifiers? modifiers = null)
    {
        if (spell.RangeIndex == SpellConstants.RangeIndexSelfOnly || spell.Range.Max <= 0)
        {
            return SpellCastResult.CastOk;
        }

        float leeway = caster is Player ? (strict ? SpellConstants.PlayerStrictRangeLeeway : SpellConstants.PlayerLandingRangeLeeway) : 0.0f;
        float distance = Distance3D(caster, targets.Dest.X, targets.Dest.Y, targets.Dest.Z);
        float maxRange = modifiers?.Apply(caster, spell, SpellModOp.Range, spell.Range.Max) ?? spell.Range.Max;
        if (distance > maxRange + leeway)
        {
            return SpellCastResult.OutOfRange;
        }

        return spell.Range.Min > 0 && distance < spell.Range.Min ? SpellCastResult.TooClose : SpellCastResult.CastOk;
    }

    /// <summary>
    /// vmangos Spell::CheckPower (Spell.cpp:7029-7066): enough of the spell's power type (health costs must leave the caster alive).
    /// A triggered cast passes at once (:7032), and a creature that is not a pet passes a spell of a power it cannot have: any
    /// power but mana, or mana without create mana (:7055-7059).
    /// </summary>
    private SpellCastResult CheckPower(Unit caster, SpellInfo spell, bool triggered)
    {
        if (triggered)
        {
            return SpellCastResult.CastOk;
        }

        uint cost = PowerCostFor(caster, spell);
        if (cost == 0)
        {
            return SpellCastResult.CastOk;
        }

        if (spell.PowerType == SpellMath.PowerHealth)
        {
            return caster.Health <= cost ? SpellCastResult.CasterAurastate : SpellCastResult.CastOk;
        }

        if (spell.PowerType is < 0 or > (int)PowerType.Happiness)
        {
            return SpellCastResult.Unknown;
        }

        if (caster is Creatures.Creature { IsPet: false }
            && (spell.PowerType != (int)PowerType.Mana || caster.GetUInt32(UpdateFields.UnitFieldBaseMana) == 0))
        {
            return SpellCastResult.CastOk;
        }

        return GetPower(caster, (PowerType)spell.PowerType) < cost ? SpellCastResult.NoPower : SpellCastResult.CastOk;
    }

    /// <summary>
    /// vmangos SpellCaster::IsSpellReady + HasGCD: no running spell cooldown, category cooldown or
    /// global cooldown of the spell's StartRecoveryCategory.
    /// </summary>
    public bool IsSpellReady(Unit unit, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(spell);
        return GetState(unit.Guid) is not { } state || IsSpellReady(state, spell);
    }

    private bool IsSpellReady(UnitSpellState state, SpellInfo spell, uint? itemCategory = null, Item? castItem = null)
    {
        ItemSpellCooldown cooldown = ItemSpellCooldowns.Pick(spell, castItem);
        // ranged (hunter lane): a COOLDOWN_ON_EVENT spell waits while the object it created lives (Unit::AddGameObject).
        if (spell.HasAttribute(SpellAttributes.CooldownOnEvent) && SpellObjects.IsCreatedBySpell(state.Unit, spell.Id))
        {
            return false;
        }

        uint now = NowMs;
        if (state.SpellCooldowns.TryGetValue(spell.Id, out uint until) && until > now)
        {
            return false;
        }

        // crafting lane: an item spell may carry its own category (Player.cpp:22139-22160); an explicit item use passes it.
        uint category = itemCategory ?? cooldown.Category;
        if (category != 0 && state.CategoryCooldowns.TryGetValue(category, out until) && until > now)
        {
            return false;
        }

        if (spell.PreventionType == SpellConstants.PreventionTypeSilence && state.SchoolLockouts.TryGetValue(spell.School, out until) && until > now)
        {
            return false;
        }

        return !(state.GlobalCooldowns.TryGetValue(spell.StartRecoveryCategory, out until) && until > now);
    }

    /// <summary>
    /// The explicit-target rules this round adds after range and line of sight (vmangos
    /// Spell::CheckCast / CheckTarget): party- and raid-only targets must be in the caster's
    /// group, and a client-requested dispel-only spell needs something to dispel.
    /// </summary>
    private SpellCastResult CheckTargetRules(Unit caster, SpellInfo spell, SpellCastTargets targets, Unit? unitTarget, bool strict)
    {
        if (!NeedsUnitTarget(spell) || (unitTarget ?? (targets.Mask == SpellCastTargetFlags.Self ? caster : null)) is not { } target)
        {
            return SpellCastResult.CastOk;
        }

        SpellCastResult helpful = CheckExplicitHelpfulTargetRules(caster, spell, target);
        if (helpful != SpellCastResult.CastOk)
        {
            return helpful;
        }

        SpellCastResult group = CheckGroupTarget(caster, spell, target);
        if (group != SpellCastResult.CastOk)
        {
            return group;
        }

        if (strict && IsDispelOnly(spell)
            && !spell.Effects.Any(e => e.Effect == SpellEffectName.Dispel && DispellableAuras(caster, target, (uint)e.MiscValue).Count > 0))
        {
            return SpellCastResult.NothingToDispel;
        }

        return SpellCastResult.CastOk;
    }

    /// <summary>Party/raid-only explicit targets (vmangos Spell::CheckTarget for TARGET_SINGLE_PARTY / TARGET_SINGLE_FRIEND_2).</summary>
    private SpellCastResult CheckGroupTarget(Unit caster, SpellInfo spell, Unit target)
    {
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.IsEmpty)
            {
                continue;
            }

            if ((effect.TargetA == SpellImplicitTarget.UnitParty && !IsGroupMember(caster, target, raid: false))
                || (effect.TargetA == SpellImplicitTarget.UnitRaid && !IsGroupMember(caster, target, raid: true)))
            {
                return SpellCastResult.BadTargets;
            }
        }

        return SpellCastResult.CastOk;
    }

    private static bool IsDispelOnly(SpellInfo spell)
        => spell.Effects.Any(e => e.Effect == SpellEffectName.Dispel) && spell.Effects.All(e => e.IsEmpty || e.Effect == SpellEffectName.Dispel);

    // --- costs and cooldowns ------------------------------------------------------------

    /// <summary>
    /// Power cost including UNIT_FIELD_POWER_COST_MODIFIER for the spell's school (vmangos Spell::CalculatePowerCost).
    /// <paramref name="modifyCost"/> is the stage hook for the caster's cost spell mod (see below); null leaves the cost unmodified.
    /// </summary>
    public static uint CalculatePowerCost(Unit caster, SpellInfo spell, Func<int, int>? modifyCost = null)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        uint current = 0;
        uint max = 0;
        if (spell.PowerType is >= 0 and <= (int)PowerType.Happiness)
        {
            current = GetPower(caster, (PowerType)spell.PowerType);
            max = caster.GetUInt32(UpdateFields.UnitFieldMaxpower1 + spell.PowerType);
        }

        int cost = spell.CalculatePowerCost(
            caster.Level, current, max,
            caster.GetUInt32(UpdateFields.UnitFieldBaseHealth), caster.GetUInt32(UpdateFields.UnitFieldBaseMana), caster.Health);
        if (!spell.HasAttribute(SpellAttributesEx.UseAllMana))
        {
            cost += caster.GetInt32(UpdateFields.UnitFieldPowerCostModifier + (int)spell.School);

            // vmangos Spell.cpp:7019-7023: the SPELLMOD_COST spell mod runs after the school flat modifier and before the
            // creature-level scaling and the school percent multiplier below (spell-modifier-engine).
            if (modifyCost is not null)
            {
                cost = modifyCost(cost);
            }

            // vmangos Spell::CalculatePowerCost (Spell.cpp:7026-7036): creature-level scaling, then the school
            // percent multiplier (aura 72: Clearcasting, cost talents). Casters lane, mana-spend-rule.
            if (((uint)spell.Attributes & Casters.CasterAttributes.ScalesWithCreatureLevel) != 0 && caster.Level > 0)
            {
                cost = (int)(cost / ((1.117f * spell.SpellLevel / caster.Level) - 0.1327f));
            }

            cost = (int)(cost * (1.0f + caster.GetFloat(UpdateFields.UnitFieldPowerCostMultiplier + (int)spell.School)));
        }

        return (uint)Math.Max(cost, 0);
    }

    /// <summary>
    /// vmangos Spell::TakePower (Spell.cpp:5051-5080): a cast triggered by an aura (a periodic trigger tick, a proc) takes no
    /// power at all; every other cast, triggered or not, pays its cost.
    /// </summary>
    private static void TakePower(Unit caster, SpellInfo spell, uint cost, bool triggeredByAura)
    {
        if (cost == 0 || triggeredByAura)
        {
            return;
        }

        if (spell.PowerType == SpellMath.PowerHealth)
        {
            caster.Health -= Math.Min(cost, caster.Health);
            return;
        }

        if (spell.PowerType is >= 0 and <= (int)PowerType.Happiness)
        {
            var power = (PowerType)spell.PowerType;
            SetPower(caster, power, GetPower(caster, power) - Math.Min(cost, GetPower(caster, power)));

            // vmangos Spell::TakePower (Spell.cpp:5077-5079): paying mana starts the five second timer unless the
            // spell has SPELL_ATTR_EX2_DONT_BLOCK_MANA_REGEN. A triggered cast that was not triggered by an aura pays and
            // starts it too; an aura-triggered cast returned above. Casters lane, mana-spend-rule.
            if (power == PowerType.Mana && ((uint)spell.AttributesEx2 & Casters.CasterAttributes.Ex2DontBlockManaRegen) == 0)
            {
                caster.Combat.NoteManaUsed(spell.Id);
            }
        }
    }

    /// <summary>vmangos Player::AddGCD (see <see cref="SpellConstants.GlobalCooldownCategory"/>).</summary>
    private void AddGlobalCooldown(UnitSpellState state, SpellInfo spell)
    {
        int duration = (int)spell.StartRecoveryTime;
        if (spell.StartRecoveryCategory == 0 && duration == 0)
        {
            return;
        }

        // vmangos Player::AddGCD (Player.cpp:22101-22105): SPELLMOD_GLOBAL_COOLDOWN before the haste scaling, so a modified
        // duration no longer equals 1500 and is not scaled.
        duration = ModInt(state.Unit, spell, SpellModOp.GlobalCooldown, duration);

        if (spell.StartRecoveryCategory == SpellConstants.GlobalCooldownCategory && duration == 1500
            && spell.DamageClass is not (SpellDamageClass.Melee or SpellDamageClass.Ranged)
            && !spell.HasAttribute(SpellAttributes.UsesRangedSlot) && !spell.HasAttribute(SpellAttributes.IsAbility))
        {
            duration = Math.Clamp((int)(duration * CastSpeed(state.Unit)), 1000, 1500);
        }

        if (duration < 1)
        {
            return;
        }

        duration = Math.Max(1, duration - (int)MapUpdateIntervalMs);
        state.GlobalCooldowns[spell.StartRecoveryCategory] = NowMs + (uint)duration;
    }

    /// <summary>
    /// vmangos Spell::SendSpellCooldown → SpellCaster::AddCooldown: start the spell and category
    /// cooldowns unless the spell is passive. The client starts its own timers from SMSG_SPELL_GO
    /// for casts it requested (vmangos sends nothing then); for server-initiated (triggered) casts
    /// SMSG_SPELL_COOLDOWN tells the client — decision recorded in docs/areas/spells.md.
    /// COOLDOWN_ON_EVENT spells are not started here (the event that starts them is not modelled yet).
    /// </summary>
    private void AddCooldown(UnitSpellState state, SpellInfo spell, bool triggered, int? itemCooldownMs = null,
        int? itemCategoryCooldownMs = null, uint? itemCategory = null, uint itemId = 0, bool onEvent = false, Item? castItem = null)
    {
        if (spell.IsPassive || (spell.HasAttribute(SpellAttributes.CooldownOnEvent) && !onEvent))
        {
            return;
        }

        // ranged (hunter lane): a ranged-slot spell also waits out the weapon speed (Player.cpp:22193-22197).
        // crafting lane: an item spell may carry its own category and times (Player.cpp:22139-22160): the explicit item-use values
        // when given, else the cast item's template; an ItemPrototype cooldown of -1 means the Spell.dbc recovery (Player.cpp:22139).
        ItemSpellCooldown picked = ItemSpellCooldowns.Pick(spell, castItem);
        uint recovery = itemCooldownMs is { } itemRecovery ? (itemRecovery < 0 ? spell.RecoveryTime : (uint)itemRecovery) : picked.RecoveryTime;
        recovery += RangedRecoveryMs(state.Unit, spell);
        uint categoryRecovery = itemCategoryCooldownMs is { } itemCategoryRecovery
            ? (itemCategoryRecovery < 0 ? spell.CategoryRecoveryTime : (uint)itemCategoryRecovery)
            : picked.CategoryRecoveryTime;
        uint category = itemCategory ?? picked.Category;
        bool itemOwned = itemCategory is not null || itemCooldownMs is not null || itemCategoryCooldownMs is not null || castItem is not null;
        if (recovery == 0 && categoryRecovery == 0)
        {
            return;
        }

        // vmangos Player::AddCooldown (Player.cpp:22200-22206): the cooldown spell mod applies to the spell's own time when it
        // has one, otherwise to the category time ("blizzlike code for choosing which is recTime > categoryRecTime").
        if (recovery > 0)
        {
            recovery = (uint)Math.Max(ModInt(state.Unit, spell, SpellModOp.Cooldown, (int)recovery), 0);
        }
        else if (category != 0 && categoryRecovery > 0)
        {
            categoryRecovery = (uint)Math.Max(ModInt(state.Unit, spell, SpellModOp.Cooldown, (int)categoryRecovery), 0);
        }

        uint now = NowMs;
        if (recovery > 0)
        {
            state.SpellCooldowns[spell.Id] = now + recovery;
        }

        if (category != 0 && categoryRecovery > 0)
        {
            state.CategoryCooldowns[category] = now + categoryRecovery;
        }

        if (itemOwned)
        {
            state.CooldownOwners[spell.Id] = new ItemCooldownOwner(itemId != 0 ? itemId : castItem?.Entry ?? 0, category, spell.Id);
        }
        else
        {
            state.CooldownOwners.Remove(spell.Id);
        }

        if (triggered && state.Unit is Player player)
        {
            uint ms = Math.Max(recovery, categoryRecovery);
            player.Session.Send(WorldOpcode.SmsgSpellCooldown, SpellPackets.BuildSpellCooldown(player.Guid, [(spell.Id, ms)]));
        }
    }

    /// <summary>Clear a spell's cooldown and its category cooldown, and tell the client (vmangos Player::RemoveSpellCooldown → SMSG_CLEAR_COOLDOWN).</summary>
    public void ClearCooldown(Unit unit, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state)
        {
            return;
        }

        bool removed = state.SpellCooldowns.Remove(spellId);
        if (Store.Get(spellId) is { } spell)
        {
            uint category = state.CooldownOwners.GetValueOrDefault(spellId).Category;
            removed |= state.CategoryCooldowns.Remove(category != 0 ? category : spell.Category);
            state.CooldownOwners.Remove(spellId);
        }

        if (removed && unit is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgClearCooldown, SpellPackets.BuildClearCooldown(spellId, player.Guid));
        }
    }

    /// <summary>Running cooldowns for SMSG_INITIAL_SPELLS (vmangos Player::SendInitialSpells cooldown block).</summary>
    public IReadOnlyList<InitialSpellCooldown> GetActiveCooldowns(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state)
        {
            return [];
        }

        uint now = NowMs;
        var result = new List<InitialSpellCooldown>();
        foreach ((uint spellId, uint until) in state.SpellCooldowns)
        {
            if (until > now && Store.Get(spellId) is { } spell)
            {
                ItemCooldownOwner owner = state.CooldownOwners.GetValueOrDefault(spellId);
                uint categoryId = owner.Category != 0 ? owner.Category : spell.Category;
                uint category = categoryId != 0 && state.CategoryCooldowns.TryGetValue(categoryId, out uint catUntil) && catUntil > now
                    ? catUntil - now : 0;
                result.Add(new InitialSpellCooldown(spellId, owner.ItemId, categoryId, until - now, category));
            }
        }

        foreach ((uint spellId, ItemCooldownOwner owner) in state.CooldownOwners)
        {
            if ((state.SpellCooldowns.TryGetValue(spellId, out uint spellUntil) && spellUntil > now)
                || owner.Category == 0 || !state.CategoryCooldowns.TryGetValue(owner.Category, out uint until) || until <= now
                || Store.Get(spellId) is not { } spell)
                continue;
            result.Add(new InitialSpellCooldown(spellId, owner.ItemId, owner.Category, 0, until - now));
        }

        return result;
    }

    private static void ExpireCooldowns(UnitSpellState state, uint now)
    {
        Expire(state.SpellCooldowns, now);
        Expire(state.CategoryCooldowns, now);
        Expire(state.GlobalCooldowns, now);
        foreach ((SpellSchool school, uint until) in state.SchoolLockouts.ToArray())
        {
            if (until <= now)
            {
                state.SchoolLockouts.Remove(school);
            }
        }

        static void Expire(Dictionary<uint, uint> cooldowns, uint now)
        {
            foreach ((uint key, uint until) in cooldowns.ToArray())
            {
                if (until <= now)
                {
                    cooldowns.Remove(key);
                }
            }
        }
    }

    // --- packets ------------------------------------------------------------------------

    /// <summary>
    /// vmangos Spell::SendCastResult: players only; nothing for triggered casts or spells with
    /// DO_NOT_REPORT_SPELL_FAILURE; passive spells fail with DONT_REPORT.
    /// </summary>
    internal static void SendCastResult(Unit caster, SpellInfo spell, SpellCastResult result, bool triggered)
    {
        if (caster is not Player player || triggered || spell.HasAttribute(SpellAttributesEx2.DoNotReportSpellFailure))
        {
            return;
        }

        if (result != SpellCastResult.CastOk && spell.IsPassive)
        {
            result = SpellCastResult.DontReport;
        }

        player.Session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(spell.Id, result));
    }

    /// <summary>
    /// vmangos Spell::SendInterrupted: SMSG_SPELL_FAILED_OTHER to the caster's set, self included.
    /// cmangos-classic also sends SMSG_SPELL_FAILURE first; vmangos dropped it as useless
    /// ("This first packet is apparently useless") — vmangos followed, recorded.
    /// </summary>
    private static void SendInterrupted(SpellCast cast)
        => SendToSet(cast.Caster, WorldOpcode.SmsgSpellFailedOther, SpellPackets.BuildSpellFailedOther(cast.Caster.Guid, cast.Spell.Id), includeSelf: true);

    /// <summary>vmangos WorldObject::SendMessageToSet: the unit's own client (when asked) and every player that sees it.</summary>
    internal static void SendToSet(Unit unit, WorldOpcode opcode, byte[] payload, bool includeSelf)
    {
        if (includeSelf && unit is Player self)
        {
            self.Session.Send(opcode, payload);
        }

        unit.Map?.BroadcastToObservers(unit, opcode, payload);
    }

    // --- helpers ------------------------------------------------------------------------

    private static bool IsQuestSettlementPending(Unit? unit)
        => unit is Player { IsQuestSettlementPending: true };

    private UnitSpellState GetOrCreateState(Unit unit)
    {
        if (!_states.TryGetValue(unit.Guid, out UnitSpellState? state) || !ReferenceEquals(state.Unit, unit))
        {
            state = new UnitSpellState(unit);
            _states[unit.Guid] = state;
        }

        return state;
    }

    private Unit? ResolveUnitTarget(Unit caster, SpellCastTargets targets, SpellInfo? spell = null)
    {
        // A resurrect effect aimed at a corpse targets the corpse's owner, even when the client also names the unit: a released spirit may be a
        // ghost on another map, which no unit lookup on the caster's map finds (vmangos Spell.cpp:3109-3118).
        if (spell is not null && HasResurrectionCorpseTarget(spell, targets))
        {
            return ResolveResurrectionCorpseTarget(caster, targets);
        }

        if ((targets.Mask & (SpellCastTargetFlags.Unit | SpellCastTargetFlags.UnitEnemy)) != 0)
        {
            return Units.Find(caster, targets.Unit);
        }

        // A corpse target is its owner (vmangos Spell.cpp:3109-3118 adds the owner of the corpse to the targets of the resurrect effects).
        return (targets.Mask & (SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.CorpseEnemy)) != 0 ? ResolveCorpseOwner(caster, targets.Corpse) : null;
    }

    /// <summary>
    /// The spell is aimed at one explicit unit (or the owner of an explicit corpse), which CheckCast then checks: present, alive or dead as
    /// the spell allows, in range and in sight (vmangos Spell::CheckCast and CheckRange measure <c>m_targets.getUnitTarget()</c> whatever the
    /// implicit target). A resurrect effect always is: the real player rows (Resurrection 2006, Redemption 7328, Ancestral Spirit 2008,
    /// Rebirth 20484) name no implicit target at all (TARGET_NONE) and the corpse flag in <c>Targets</c>, and the effect takes the explicit unit
    /// or corpse owner (Spell.cpp:3106-3117); without one the client sent nothing to resurrect (vmangos ValidateExplicitTargetMask).
    /// </summary>
    private static bool NeedsUnitTarget(SpellInfo spell)
        => spell.Effects.Any(e => !e.IsEmpty && (IsExplicitUnitTarget(e.TargetA)
            || (e.TargetA is SpellImplicitTarget.LocationCasterDest or SpellImplicitTarget.None
                && e.Effect is SpellEffectName.Resurrect or SpellEffectName.ResurrectNew)));

    internal static bool IsExplicitUnitTarget(SpellImplicitTarget target)
        => target is SpellImplicitTarget.UnitEnemy or SpellImplicitTarget.UnitFriend or SpellImplicitTarget.Unit or SpellImplicitTarget.UnitParty
            or SpellImplicitTarget.UnitRaid or SpellImplicitTarget.UnitFriendChainHeal;

    private static bool IsMoving(Player player)
        => (player.Movement.Flags & (MovementFlags.Forward | MovementFlags.Backward | MovementFlags.StrafeLeft
            | MovementFlags.StrafeRight | MovementFlags.Jumping | MovementFlags.FallingFar)) != 0;

    internal static float CastSpeed(Unit unit)
    {
        float speed = unit.GetFloat(UpdateFields.UnitModCastSpeed);
        return speed > 0 ? speed : 1.0f;
    }

    private static float CombatReach(Unit unit)
    {
        float reach = unit.GetFloat(UpdateFields.UnitFieldCombatreach);
        return reach > 0 ? reach : SpellConstants.DefaultCombatReach;
    }

    private static float Distance3D(Unit unit, float x, float y, float z)
    {
        float dx = unit.X - x;
        float dy = unit.Y - y;
        float dz = unit.Z - z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>UNIT_FIELD_POWER1 + power type (vmangos Unit::GetPower).</summary>
    public static uint GetPower(Unit unit, PowerType power)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.GetUInt32(UpdateFields.UnitFieldPower1 + (int)power);
    }

    /// <summary>vmangos Unit::SetPower: clamped to UNIT_FIELD_MAXPOWER1 + power type.</summary>
    public static void SetPower(Unit unit, PowerType power, uint value)
    {
        ArgumentNullException.ThrowIfNull(unit);
        uint max = unit.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power);
        unit.SetUInt32(UpdateFields.UnitFieldPower1 + (int)power, Math.Min(value, max));
    }

    private void ReportUnsupported(string kind, uint value, uint spellId)
    {
        if (_reportedUnsupported.Add((kind, value)))
        {
            _logger.LogInformation("Spell {Spell}: {Kind} {Value} is not implemented yet", spellId, kind, value);
        }
    }
}
