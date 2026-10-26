using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Chooses and submits one ordinary spell, item or pet command for a managed player. A bot of a playable class follows its
/// class rotation (<see cref="PlayerbotClassRotation"/>, vmangos PartyBotAI); a spellbook with none of the class's abilities
/// falls back to the first usable hostile spell. All decisions and packet dispatch happen on the world thread; the ordinary
/// CMSG_CAST_SPELL / CMSG_USE_ITEM / CMSG_PET_ACTION handlers remain the authoritative validators and executors.
/// </summary>
internal sealed class PlayerbotCombatSpells(WorldSession session)
{
    private const uint FailureBackoffMs = 750;

    /// <summary>How long a rotation spell the server refused is left out before it is tried again.</summary>
    internal const uint RefusalBackoffMs = 4000;
    private const uint LowManaConserveMs = 10_000;

    private uint _failureBackoffMs;
    private uint _lastRefusedSpell;
    private readonly Dictionary<uint, uint> _refusedUntil = [];
    private uint _itemBlockedUntil;
    private PlayerbotAbilities? _abilities;
    private PlayerbotRole _role;
    private readonly PlayerbotCombatWatch _watch = new();
    private uint _combatLowManaSinceMs;
    private bool _wasInCombat;
    private ObjectGuid _autocastPet;
    private readonly HashSet<uint> _autocastSent = [];

    /// <summary>
    /// The outcome check of the bot's item uses (<see cref="PlayerbotConsumableWatch"/>). A caller that sends CMSG_USE_ITEM itself takes
    /// <see cref="PlayerbotConsumableWatch.Begin"/> before the packet and hands the result to <see cref="PlayerbotConsumableWatch.Sent"/>
    /// when the server took it.
    /// </summary>
    internal PlayerbotConsumableWatch Consumables { get; } = new();

    /// <summary>
    /// The creature the bot is closing in on before a fight (set by its controller): the out-of-combat upkeep prepares for it
    /// (rogue stealth, a pet sent ahead). Not part of the fight itself; null between pulls.
    /// </summary>
    internal Unit? PullTarget { get; set; }

    /// <summary>
    /// The role the bot's group gave it (<see cref="Groups.PlayerbotGroupCoordinator"/>; a warrior without Shield Slam still tanks for
    /// a group that has no other tank). Null: the role its talents give (vmangos AutoAssignRole).
    /// </summary>
    internal PlayerbotRole? GroupRole { get; set; }

    /// <summary>Retire retry state when the player leaves a combat lifetime (death/reclaim).</summary>
    internal void Reset()
    {
        _failureBackoffMs = 0;
        _lastRefusedSpell = 0;
        _refusedUntil.Clear();
        _itemBlockedUntil = 0;
        _abilities = null;
        PullTarget = null;
        _watch.Reset();
        _combatLowManaSinceMs = 0;
        _wasInCombat = false;
        _autocastPet = default;
        _autocastSent.Clear();
        Consumables.Reset();
    }

    /// <summary>
    /// One fight decision against <paramref name="target"/> (a pull when the bot is not in combat yet). True: an action was
    /// accepted or a cast is in progress (the caller keeps still); false: the caller chases or swings as usual.
    /// </summary>
    internal bool Update(Player player, Unit target, uint elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(target);

        if (elapsedMs > 0 && _failureBackoffMs > 0)
        {
            _failureBackoffMs = elapsedMs >= _failureBackoffMs ? 0 : _failureBackoffMs - elapsedMs;
            if (_failureBackoffMs != 0)
            {
                return false;
            }
        }

        if (!player.IsInWorld || !player.IsAlive || !target.IsInWorld || !target.IsAlive
            || !ReferenceEquals(player.Map, target.Map)
            || player.Map is null || !player.Map.Combat.Hooks.CanAttack(player, target))
        {
            return false;
        }

        SpellFeature? feature = session.Services.GetService<SpellFeature>();
        if (feature is null)
        {
            return false;
        }

        uint nowMs = session.World.NowMs;
        Consumables.Poll(player, feature.System, nowMs);
        if (player.Combat.IsInCombat)
        {
            _wasInCombat = true;
            _watch.Fight(player, nowMs);
            uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
            if (player.PowerType == PowerType.Mana && maxMana > 0
                && (ulong)SpellSystem.GetPower(player, PowerType.Mana) * 100UL < (ulong)maxMana * 10UL)
            {
                if (_combatLowManaSinceMs == 0) _combatLowManaSinceMs = Math.Max(1u, nowMs);
            }
            else
            {
                _combatLowManaSinceMs = 0;
            }
        }
        else
        {
            _combatLowManaSinceMs = 0;
            if (_wasInCombat)
            {
                _wasInCombat = false;
                _watch.Reset();
            }
        }
        UnitSpellState? state = feature.System.GetState(player.Guid);
        if (state?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting } running)
        {
            _watch.Casting(player, running, nowMs);
            PlayerbotMovementControl.Stop(session, player);
            return true;
        }

        _watch.NotCasting();

        // A queued next-swing spell is compatible with the ordinary melee action; let
        // the brain continue its normal swing/chase decision rather than suppressing it.
        if (state?.MeleeCast is not null)
        {
            return false;
        }

        if (Rotation(player, feature) is { } rotation)
        {
            RotationState view = View(player, feature, target);
            RotationAction? action = rotation.InCombat(view);
            bool acted = action is { } taken && Execute(player, feature, taken);
            CommandCompanionPet(player, feature, rotation, view, action);
            return acted;
        }

        SpellInfo? spell = Select(feature, player, target);
        if (spell is null || feature.System.CheckCasterState(player, spell) != SpellCastResult.CastOk)
        {
            return false;
        }

        if (player.StandState != StandState.Stand && !spell.HasAttribute(SpellAttributes.AllowWhileSitting))
        {
            session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Stand));
            if (player.StandState != StandState.Stand)
                return true;
        }

        // The bot stops (an unbudgeted report, as a client's own) and casts in this same think: a stop that waited for the next think
        // cost a gap of seconds with a thousand bots. Only a stop the server did not take leaves the cast to the next think.
        if (player.Movement.HasFlag(MovementFlags.MaskMoving)
            && (!PlayerbotMovementControl.Stop(session, player) || player.Movement.HasFlag(MovementFlags.MaskMoving)))
        {
            return true;
        }

        var writer = new PacketWriter(24);
        writer.WriteUInt32(spell.Id);
        SpellCastTargets.ForUnit(target.Guid).Write(writer);
        // A previous completed cast can leave GO packets in the bounded capture queue.
        // Consume prior notifications before submitting this request so they cannot
        // make an ordinary cooldown/resource refusal appear accepted.
        PlayerbotMount.CancelPendingCast(session);
        session.DrainManagedPackets(WorldOpcode.SmsgSpellStart, WorldOpcode.SmsgSpellGo,
            WorldOpcode.SmsgSpellFailure, WorldOpcode.SmsgSpellFailedOther, WorldOpcode.SmsgCastResult);

        // TryManagedAction is only transport/handler admission. Success is established
        // from the ordinary spell packets or authoritative cast state below.
        if (!session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray()))
        {
            return false;
        }

        bool accepted = Accepted(feature, player, spell);
        if (!accepted)
        {
            _failureBackoffMs = FailureBackoffMs;
            _lastRefusedSpell = spell.Id;
            _watch.Refused(player, spell, session.World.NowMs, _lastRefusalReason);
        }
        else
        {
            _lastRefusedSpell = 0;
            _watch.Accepted(spell);
        }

        // A refused cast permits ordinary chase/melee while spell retries back off.
        return accepted && feature.System.GetState(player.Guid)?.MeleeCast is null;
    }

    /// <summary>
    /// Upkeep between fights (vmangos UpdateOutOfCombatAI_&lt;Class&gt;): forms, auras and buffs on the bot and its group, the
    /// pet summoned, revived and called back, heals for the bot and its group, stealth before a pull, plus out-of-combat
    /// bandaging, temporary weapon enhancements, and buff consumables. True: an action was accepted or a cast is in progress.
    /// </summary>
    internal bool UpdateOutOfCombat(Player player, uint elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!player.IsInWorld || !player.IsAlive || player.Map is null
            || session.Services.GetService<SpellFeature>() is not { } feature)
            return false;

        if (!player.Combat.IsInCombat)
        {
            _combatLowManaSinceMs = 0;
            _wasInCombat = false;
            _watch.Reset();
        }
        Consumables.Poll(player, feature.System, session.World.NowMs);
        if (feature.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting })
        {
            PlayerbotMovementControl.Stop(session, player);
            return true;
        }

        if (MountService.IsMounted(player))
        {
            PlayerbotMount.CancelPendingCast(session);
            if (PullTarget is null)
                return false;
        }

        if (Rotation(player, feature) is { } rotation)
        {
            Unit? pull = PullTarget is { IsInWorld: true, IsAlive: true } target && ReferenceEquals(target.Map, player.Map)
                && player.Map.Combat.Hooks.CanAttack(player, target) ? target : null;
            RotationState view = View(player, feature, pull);
            RotationAction? action = rotation.OutOfCombat(view);
            bool acted = action is { } taken && Execute(player, feature, taken);
            CommandCompanionPet(player, feature, rotation, view, action);
            if (acted)
                return true;
        }

        return TryOutOfCombatConsumables(player, feature);
    }

    private bool TryOutOfCombatConsumables(Player player, SpellFeature feature)
    {
        if (unchecked((int)(_itemBlockedUntil - session.World.NowMs)) > 0)
            return false;
        if (ArcaneCore.Game.Items.ItemUse.ItemUseService.IsShapeShifted(player))
            return false;

        SpellSystem system = feature.System;
        var cooldownSpells = new HashSet<uint>();
        var cooldownCategories = new HashSet<uint>();
        foreach (InitialSpellCooldown cooldown in system.GetActiveCooldowns(player))
        {
            cooldownSpells.Add(cooldown.SpellId);
            if (cooldown.Category != 0) cooldownCategories.Add(cooldown.Category);
        }

        IReadOnlyList<SpellAuraHolder> ownAuras = system.GetAuras(player);

        // 1. Out-of-combat bandaging when below 65% HP, no food is available or active, and Recently Bandaged is not present.
        if (player.Health * 100UL < player.MaxHealth * 65UL
            && !PlayerbotConsumables.HasActiveFoodDrink(player, system)
            && PlayerbotConsumables.FindBestFood(player, system.Store) is null
            && !ownAuras.Any(h => !h.IsRemoved && (h.Spell.Id == 11196
                || string.Equals(h.Spell.Name, PlayerbotClassRotation.RecentlyBandaged, StringComparison.OrdinalIgnoreCase)))
            && PlayerbotConsumables.FindBestBandage(player, system.Store, cooldownSpells, cooldownCategories) is { } bandage)
        {
            if (player.Movement.HasFlag(MovementFlags.MaskMoving)
                && (!PlayerbotMovementControl.Stop(session, player) || player.Movement.HasFlag(MovementFlags.MaskMoving)))
                return true;

            _itemBlockedUntil = unchecked(session.World.NowMs + 2000);
            SpellInfo? bandageSpell = system.Store.Get(bandage.Template.Spells[0].SpellId);
            SpellCastTargets targets = bandageSpell is not null && IsExplicitUnit(bandageSpell)
                ? SpellCastTargets.ForUnit(player.Guid)
                : SpellCastTargets.ForSelf();
            return UseItemWatched(player, system, bandage, PlayerbotNavigation.UseItemPayload(bandage.BagSlot, bandage.Slot, targets));
        }

        // 2. Temporary weapon enhancement items (sharpening stones, weightstones, wizard/mana oils) on main-hand weapon.
        if (player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand) is { } mainHand
            && (ItemClass)mainHand.Template.Class == ItemClass.Weapon
            && mainHand.GetUInt32(UpdateFields.ItemFieldEnchantment + 3) == 0
            && FindBestTempWeaponEnhancement(player, mainHand, system.Store, cooldownSpells, cooldownCategories) is { } enhItem)
        {
            SpellInfo? enhSpell = system.Store.Get(enhItem.Template.Spells[0].SpellId);
            if (enhSpell is not null && enhSpell.GetCastTime(player.Level) > 0 && player.Movement.HasFlag(MovementFlags.MaskMoving)
                && (!PlayerbotMovementControl.Stop(session, player) || player.Movement.HasFlag(MovementFlags.MaskMoving)))
                return true;

            _itemBlockedUntil = unchecked(session.World.NowMs + 2000);
            var itemTarget = new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = mainHand.Guid };
            return UseItemWatched(player, system, enhItem, PlayerbotNavigation.UseItemPayload(enhItem.BagSlot, enhItem.Slot, itemTarget));
        }

        // 3. Buff scrolls and elixirs out of combat when the aura is not already present.
        if (PlayerbotConsumables.FindBestBuffConsumable(player, system.Store, ownAuras, cooldownSpells, cooldownCategories) is { } buffItem)
        {
            _itemBlockedUntil = unchecked(session.World.NowMs + 2000);
            SpellInfo? buffSpell = system.Store.Get(buffItem.Template.Spells[0].SpellId);
            SpellCastTargets targets = buffSpell is not null && IsExplicitUnit(buffSpell)
                ? SpellCastTargets.ForUnit(player.Guid)
                : SpellCastTargets.ForSelf();
            return UseItemWatched(player, system, buffItem, PlayerbotNavigation.UseItemPayload(buffItem.BagSlot, buffItem.Slot, targets));
        }

        return false;
    }

    /// <summary>CMSG_USE_ITEM for <paramref name="item"/>, with the outcome check (<see cref="Consumables"/>) around it.</summary>
    private bool UseItemWatched(Player player, SpellSystem system, Item item, byte[] payload)
    {
        SpellInfo? spell = item.Template.Spells[0].SpellId is var id and not 0 ? system.Store.Get(id) : null;
        PlayerbotConsumableWatch.Use use = PlayerbotConsumableWatch.Begin(player, item, spell);
        if (!session.TryManagedAction(WorldOpcode.CmsgUseItem, payload)) return false;
        Consumables.Sent(player, use, system, session.World.NowMs);
        return true;
    }

    private static Item? FindBestTempWeaponEnhancement(Player player, Item mainHand, SpellStore store,
        IReadOnlySet<uint> cooldownSpells, IReadOnlySet<uint> cooldownCategories)
    {
        Item? best = null;
        uint bestLevel = 0;
        foreach (Item item in player.Inventory.AllItems.Take(128))
        {
            if (!InventorySlots.IsInventoryPos(item.BagSlot, item.Slot)
                || InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot)
                || item.Count == 0
                || player.Inventory.CanUseItem(item) != InventoryResult.Ok)
                continue;

            ItemSpell use = item.Template.Spells[0];
            if (use.SpellId == 0 || use.Trigger != 0
                || cooldownSpells.Contains(use.SpellId)
                || (use.Category != 0 && cooldownCategories.Contains(use.Category))
                || store.Get(use.SpellId) is not { } spell)
                continue;

            if (!spell.Effects.Any(e => e.Effect == SpellEffectName.EnchantItemTemporary && e.MiscValue != 0))
                continue;

            if (!ArcaneCore.Game.Crafting.Enchanting.ItemTargetRules.IsFit(spell, mainHand.Template) || mainHand.Template.ItemLevel < spell.BaseLevel)
                continue;

            if (best is null || item.Template.ItemLevel >= bestLevel)
            {
                best = item;
                bestLevel = item.Template.ItemLevel;
            }
        }
        return best;
    }

    /// <summary>The pet command a class sends beside its chosen action (<see cref="PlayerbotClassRotation.PetCompanion"/>).</summary>
    private void CommandCompanionPet(Player player, SpellFeature feature, PlayerbotClassRotation rotation, RotationState view, RotationAction? chosen)
    {
        EnablePetAutocast(player, feature);
        if (rotation.PetCompanion(view, chosen) is { } pet) Execute(player, feature, pet);
    }

    /// <summary>
    /// A pet's spells start with autocast off (vmangos Pet::AddSpell, Pet.cpp:1934-1941) and a player turns them on from the pet bar, which a
    /// bot never does, so an Imp stood by with Firebolt and a Voidwalker never cast Torment. Once the pet's bar has reached the bot, send
    /// CMSG_PET_SPELL_AUTOCAST (u64 pet, u32 spell, u8 enable) for each harmful, non-passive spell it knows (damage and taunt spells; the pet AI
    /// only autocasts harmful ones). Each spell is sent once per pet, so a refusal is not repeated every think. World thread.
    /// </summary>
    private void EnablePetAutocast(Player player, SpellFeature feature)
    {
        if (player.GetPet() is not { IsAlive: true } pet || pet.GetCharmInfo() is not { } charm)
        {
            return;
        }

        if (_autocastPet != pet.Guid)
        {
            _autocastPet = pet.Guid;
            _autocastSent.Clear();
        }

        if (_autocastSent.Count >= charm.SpellStates.Count)
        {
            return; // every spell was handled for this pet: no allocation on the common think
        }

        // a snapshot: the handler may change the state while the loop runs
        foreach ((uint spellId, ActionType state) in charm.SpellStates.ToArray())
        {
            if (_autocastSent.Contains(spellId))
            {
                continue;
            }

            if (state != ActionType.Disabled || feature.System.Store.Get(spellId) is not { IsPassive: false, IsPositive: false })
            {
                _autocastSent.Add(spellId); // passive, positive or already on: nothing to send
                continue;
            }

            var writer = new PacketWriter(13);
            writer.WriteUInt64(pet.Guid.Value);
            writer.WriteUInt32(spellId);
            writer.WriteByte(1);
            if (session.TryManagedAction(WorldOpcode.CmsgPetSpellAutocast, writer.ToArray()))
            {
                _autocastSent.Add(spellId);
            }
        }
    }

    /// <summary>Where the bot fights from: melee distance, or a caster's or hunter's range (yards).</summary>
    internal float PreferredRange(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (session.Services.GetService<SpellFeature>() is not { } feature || Rotation(player, feature) is not { } rotation)
            return PlayerbotClassRotation.MeleeRange;
        return rotation.PreferredRange(View(player, feature, null));
    }

    /// <summary>The bot's role (vmangos AutoAssignRole) from the talent spells it knows.</summary>
    internal PlayerbotRole RoleOf(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (session.Services.GetService<SpellFeature>() is { } feature) Refresh(player, feature);
        else _role = PlayerbotRoles.Assign(player.Class, _ => false);
        return GroupRole ?? _role;
    }

    /// <summary>The resolved class abilities (rebuilt after a learned, superseded or removed spell).</summary>
    internal PlayerbotAbilities Abilities(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return session.Services.GetService<SpellFeature>() is { } feature ? Refresh(player, feature) : PlayerbotAbilities.Empty;
    }

    private PlayerbotAbilities? _escapes;
    private int _escapeBookCount = -1;

    /// <summary>
    /// The class escapes the bot knows (<see cref="PlayerbotEscapes.All"/>), resolved from its book at the highest rank; rebuilt when
    /// the book changes size.
    /// </summary>
    internal PlayerbotAbilities Escapes(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (session.Services.GetService<SpellFeature>() is not { } feature) return PlayerbotAbilities.Empty;
        IReadOnlyList<uint> book = feature.Spellbook.GetSpells(player);
        if (_escapes is not null && _escapeBookCount == book.Count) return _escapes;
        _escapeBookCount = book.Count;
        return _escapes = PlayerbotAbilities.Resolve([.. book.Select(feature.System.Store.Get).OfType<SpellInfo>()], PlayerbotEscapes.All);
    }

    /// <summary>Whether <paramref name="spell"/> can be cast at <paramref name="target"/> now (the rotation's castability check).</summary>
    internal bool CanCastNow(Player player, SpellInfo spell, Unit target)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (session.Services.GetService<SpellFeature>() is not { } feature || IsRefused(spell) || IsWithheldForManaRecovery(player, spell)) return false;
        Unit? victim = ReferenceEquals(target, player) ? null : target;
        RotationState view = View(player, feature, victim);
        RotationUnit? unit = victim is null ? view.Self : view.Victim;
        return unit is not null && view.CanCast(spell, unit);
    }

    /// <summary>
    /// The first of <paramref name="spells"/> the bot can cast at <paramref name="target"/> now (not refused lately, in range, affordable),
    /// judged on one combat view built for the whole list; null when none can be cast or the target is not in the view.
    /// </summary>
    internal SpellInfo? FirstCastable(Player player, IReadOnlyList<SpellInfo> spells, Unit target, Func<SpellInfo, bool>? accept = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(target);
        if (spells.Count == 0 || session.Services.GetService<SpellFeature>() is not { } feature) return null;
        RotationState view = View(player, feature, ReferenceEquals(target, player) ? null : target);
        RotationUnit? unit = ReferenceEquals(target, player) ? view.Self : view.Victim;
        if (unit is null) return null;
        for (int index = 0; index < spells.Count; index++)
            if ((accept is null || accept(spells[index])) && !IsRefused(spells[index]) && !IsWithheldForManaRecovery(player, spells[index]) && view.CanCast(spells[index], unit)) return spells[index];
        return null;
    }

    /// <summary>Submit <paramref name="spell"/> at <paramref name="target"/> through CMSG_CAST_SPELL; true when the server took it.</summary>
    internal bool CastAt(Player player, SpellInfo spell, Unit target)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(target);
        return session.Services.GetService<SpellFeature>() is { } feature && Cast(player, feature, spell, target.Guid);
    }

    /// <summary>
    /// Submit <paramref name="spell"/> at a player's corpse the way a 1.12 client does once that player released (the ghost cannot be
    /// targeted): CMSG_CAST_SPELL with TARGET_FLAG_CORPSE (0x8000, the resurrection rows' own <c>Targets</c>) and the corpse guid. The
    /// server resolves the corpse to its owner (vmangos Spell::SetTargetMap, Spell.cpp:3106-3117). True when the server took it.
    /// </summary>
    internal bool CastAtCorpse(Player player, SpellInfo spell, Corpse corpse)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(corpse);
        return session.Services.GetService<SpellFeature>() is { } feature
            && Cast(player, feature, spell, new SpellCastTargets { Mask = SpellCastTargetFlags.CorpseAlly, Corpse = corpse.Guid });
    }

    /// <summary>
    /// Submit <paramref name="spell"/> at a game object the way a 1.12 client does when the player clicks a locked object it opens with a
    /// spell (an Arathi Basin banner and spell 21651, vmangos BattleBotWaypoints.cpp:169-205): CMSG_CAST_SPELL with TARGET_FLAG_OBJECT
    /// (0x0800) and the object's guid (vmangos SpellCastTargets::read). True when the server took it.
    /// </summary>
    internal bool CastAtObject(Player player, SpellInfo spell, GameObject gameObject)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(gameObject);
        return session.Services.GetService<SpellFeature>() is { } feature
            && Cast(player, feature, spell, new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = gameObject.Guid });
    }

    private PlayerbotClassRotation? Rotation(Player player, SpellFeature feature)
        => PlayerbotRotations.For(player.Class) is { } rotation && Refresh(player, feature).Count > 0 ? rotation : null;

    private RotationState View(Player player, SpellFeature feature, Unit? victim)
        => PlayerbotCombatView.Build(session, feature, player, victim, Refresh(player, feature), GroupRole ?? _role, spell => IsRefused(spell) || IsWithheldForManaRecovery(player, spell));

    /// <summary>
    /// vmangos PartyBotAI::OnPacketReceived (PartyBotAI.cpp:544-553): a learned, superseded or removed spell marks the spell
    /// data stale; the next decision rebuilds it from the book. Only those opcodes are drained from the bounded capture queue.
    /// </summary>
    private PlayerbotAbilities Refresh(Player player, SpellFeature feature)
    {
        bool changed = session.DrainManagedPackets(WorldOpcode.SmsgLearnedSpell, WorldOpcode.SmsgSupercededSpell,
            WorldOpcode.SmsgRemovedSpell).Count > 0;
        if (_abilities is not null && !changed) return _abilities;

        PlayerbotClassRotation? rotation = PlayerbotRotations.For(player.Class);
        SpellInfo[] known = [.. feature.Spellbook.GetSpells(player).Select(feature.System.Store.Get).OfType<SpellInfo>()];
        _abilities = PlayerbotAbilities.Resolve(known, rotation?.Abilities ?? [], feature.System.Store);
        PlayerbotAbilities abilities = _abilities;
        _role = PlayerbotRoles.Assign(player.Class, id => abilities.HasSpellOrRank(id, feature.System.Store.Get));
        return _abilities;
    }

    private bool IsRefused(SpellInfo spell)
        => _refusedUntil.TryGetValue(spell.Id, out uint until) && unchecked((int)(until - session.World.NowMs)) > 0;

    private bool IsWithheldForManaRecovery(Player player, SpellInfo spell)
    {
        if (_combatLowManaSinceMs == 0 || !player.Combat.IsInCombat || spell.IsPositive) return false;
        if (spell.PowerType != (int)PowerType.Mana || (spell.ManaCost == 0 && spell.ManaCostPercentage == 0)) return false;
        if ((ulong)player.Health * 100UL <= (ulong)player.MaxHealth * 40UL) return false;
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        if (player.PowerType != PowerType.Mana || maxMana == 0
            || (ulong)SpellSystem.GetPower(player, PowerType.Mana) * 100UL >= (ulong)maxMana * 10UL)
            return false;
        uint elapsed = unchecked(session.World.NowMs - _combatLowManaSinceMs);
        return elapsed >= LowManaConserveMs && elapsed <= int.MaxValue;
    }

    private bool Execute(Player player, SpellFeature feature, RotationAction action)
    {
        switch (action.Kind)
        {
            case RotationActionKind.Cast when action.Spell is { } spell:
                return Cast(player, feature, spell, action.Target);

            case RotationActionKind.UseItem when action.Item is { } item:
                if (unchecked((int)(_itemBlockedUntil - session.World.NowMs)) > 0) return false;
                Item? carried = player.Inventory.GetItem(item.Bag, item.Slot);
                // A bandage is a channel with cast time 0, so CheckCast lets it start while the chase is still
                // walking; Spell::update then cancels it once that heartbeat moves the caster (ChannelInterruptFlags
                // include moving). vmangos DoCastSpell stops the mover first, as the out-of-combat bandage already
                // does. An instant potion is still used on the move. A stop the server did not take retries next think.
                if (carried is not null && ItemNeedsStillness(player, feature, carried)
                    && player.Movement.HasFlag(MovementFlags.MaskMoving)
                    && (!PlayerbotMovementControl.Stop(session, player) || player.Movement.HasFlag(MovementFlags.MaskMoving)))
                    return true;

                _itemBlockedUntil = unchecked(session.World.NowMs + 2000);
                return carried is not null
                    ? UseItemWatched(player, feature.System, carried, PlayerbotNavigation.UseItemPayload(item.Bag, item.Slot))
                    : session.TryManagedAction(WorldOpcode.CmsgUseItem, PlayerbotNavigation.UseItemPayload(item.Bag, item.Slot));

            case RotationActionKind.PetAttack:
                return PetAction(player, PetCommandAttack, action.Target);

            case RotationActionKind.PetFollow:
                return PetAction(player, PetCommandFollow, ObjectGuid.Empty);

            default:
                return false;
        }
    }

    /// <summary>CMSG_PET_ACTION data: ACT_COMMAND (7) in the top byte, the command below (vmangos UNIT_ACTION_BUTTON_*).</summary>
    private const uint PetCommandFollow = (7u << 24) | 1u;
    private const uint PetCommandAttack = (7u << 24) | 2u;

    /// <summary>A carried item use the bot must stand still for: a channel, or a cast time (vmangos DoCastSpell stops the mover).</summary>
    private static bool ItemNeedsStillness(Player player, SpellFeature feature, Item item)
    {
        if (item.Template.Spells.Count == 0) return false;
        ItemSpell use = item.Template.Spells[0];
        if (use.SpellId == 0 || use.Trigger != 0 || feature.System.Store.Get(use.SpellId) is not { } spell) return false;
        return spell.IsChanneled || spell.GetCastTime(player.Level) > 0;
    }

    private bool PetAction(Player player, uint data, ObjectGuid target)
    {
        if (player.PetGuid.IsEmpty) return false;
        var writer = new PacketWriter(20);
        writer.WriteUInt64(player.PetGuid.Value);
        writer.WriteUInt32(data);
        writer.WriteUInt64(target.Value);
        return session.TryManagedAction(WorldOpcode.CmsgPetAction, writer.ToArray());
    }

    private bool Cast(Player player, SpellFeature feature, SpellInfo spell, ObjectGuid target)
    {
        SpellCastTargets targets;
        if (target == player.Guid && spell.HasEffect(SpellEffectName.EnchantItemTemporary)
            && player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand) is { } mainHand)
        {
            targets = new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = mainHand.Guid };
        }
        else if (target == player.Guid)
        {
            targets = spell.IsPositive && IsExplicitUnit(spell) ? SpellCastTargets.ForUnit(target) : SpellCastTargets.ForSelf();
        }
        else
        {
            targets = SpellCastTargets.ForUnit(target);
        }

        return Cast(player, feature, spell, targets);
    }

    private bool Cast(Player player, SpellFeature feature, SpellInfo spell, SpellCastTargets targets)
    {
        // The server would refuse it (CastResult Stunned / Confused / Fleeing / Silenced / Pacified): send nothing.
        if (feature.System.CheckCasterState(player, spell) != SpellCastResult.CastOk) return false;

        if (player.StandState != StandState.Stand && !spell.HasAttribute(SpellAttributes.AllowWhileSitting))
        {
            session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Stand));
            if (player.StandState != StandState.Stand)
                return true;
        }

        // A spell with a cast time or channel needs the bot standing (vmangos DoCastSpell stops the mover); instants go while moving.
        if ((spell.IsChanneled || spell.GetCastTime(player.Level) > 0) && player.Movement.HasFlag(MovementFlags.MaskMoving)
            && (!PlayerbotMovementControl.Stop(session, player) || player.Movement.HasFlag(MovementFlags.MaskMoving)))
            return true;

        var beforePos = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        var writer = new PacketWriter(24);
        writer.WriteUInt32(spell.Id);
        targets.Write(writer);
        PlayerbotMount.CancelPendingCast(session);
        session.DrainManagedPackets(WorldOpcode.SmsgSpellStart, WorldOpcode.SmsgSpellGo,
            WorldOpcode.SmsgSpellFailure, WorldOpcode.SmsgSpellFailedOther, WorldOpcode.SmsgCastResult);
        if (!session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray())) return false;

        if (Accepted(feature, player, spell))
        {
            if (player.Locomotion.ActiveSpline is not null
                || System.Numerics.Vector3.DistanceSquared(beforePos, new System.Numerics.Vector3(player.X, player.Y, player.Z)) > 0.25f)
            {
                PlayerbotMotion.Reset(player);
            }

            _refusedUntil.Remove(spell.Id);
            _watch.Accepted(spell);
            return feature.System.GetState(player.Guid)?.MeleeCast is null;
        }

        // A refusal leaves that spell out for a while; the rest of the rotation goes on at the next decision.
        if (_refusedUntil.Count >= 64) _refusedUntil.Clear();
        _refusedUntil[spell.Id] = unchecked(session.World.NowMs + RefusalBackoffMs);
        _watch.Refused(player, spell, session.World.NowMs, _lastRefusalReason);
        return false;
    }

    private static bool IsExplicitUnit(SpellInfo spell) => spell.Effects[0].TargetA is SpellImplicitTarget.UnitFriend
        or SpellImplicitTarget.Unit or SpellImplicitTarget.UnitFriendAndParty or SpellImplicitTarget.UnitParty
        or SpellImplicitTarget.UnitFriendChainHeal or SpellImplicitTarget.UnitCasterPet;

    /// <summary>The server took the request: its SMSG_SPELL_START / GO, or the cast, next-swing or auto-repeat slot now holds it.</summary>
    private string? _lastRefusalReason;

    private bool Accepted(SpellFeature feature, Player player, SpellInfo spell)
    {
        IReadOnlyList<ManagedSessionPacket> packets = session.DrainManagedPackets(
            WorldOpcode.SmsgSpellStart,
            WorldOpcode.SmsgSpellGo,
            WorldOpcode.SmsgSpellFailure,
            WorldOpcode.SmsgSpellFailedOther,
            WorldOpcode.SmsgCastResult);
        UnitSpellState? state = feature.System.GetState(player.Guid);
        // The refusal's reason (SMSG_CAST_RESULT: u32 spell, u8 status, u8 SpellCastResult) for the cast-refused-repeatedly report (GitHub #827).
        _lastRefusalReason = null;
        foreach (ManagedSessionPacket packet in packets)
        {
            if (packet.Opcode == WorldOpcode.SmsgCastResult && packet.Payload.Length >= 6 && BitConverter.ToUInt32(packet.Payload, 0) == spell.Id
                && packet.Payload[4] != 0)
                _lastRefusalReason = ((SpellCastResult)packet.Payload[5]).ToString();
        }
        return packets.Any(packet =>
            (packet.Opcode is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo)
            && SpellIdOf(packet.Payload) == spell.Id)
            || state?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting } current && current.Spell.Id == spell.Id
            || state?.MeleeCast?.Spell.Id == spell.Id
            || state?.AutoRepeatCast?.Spell.Id == spell.Id;
    }

    private static uint SpellIdOf(byte[] payload)
    {
        var reader = new PacketReader(payload);
        _ = reader.ReadPackedGuid();
        _ = reader.ReadPackedGuid();
        return reader.ReadUInt32();
    }

    private SpellInfo? Select(SpellFeature feature, Player player, Unit target)
    {
        IReadOnlyList<uint> known = feature.Spellbook.GetSpells(player);
        // A normal refusal rotates the next attempt, so an expensive or conditionally
        // unavailable lowest-ID spell cannot starve the rest of the learned book.
        foreach (uint spellId in known.Where(id => id > _lastRefusedSpell)
            .Concat(known.Where(id => id <= _lastRefusedSpell)))
        {
            SpellInfo? spell = feature.System.Store.Get(spellId);
            if (spell is null || spell.IsPassive || spell.IsPositive || spell.CanTargetDead || IsWithheldForManaRecovery(player, spell))
            {
                continue;
            }

            // This first vertical slice deliberately supports only an explicit unit
            // target. AOE/cone/near-caster spells need a separate target contract.
            if (!spell.Effects.Any(static effect => effect.TargetA == SpellImplicitTarget.UnitEnemy))
            {
                continue;
            }

            float dx = player.X - target.X;
            float dy = player.Y - target.Y;
            float dz = player.Z - target.Z;
            float distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (spell.RangeIndex == SpellConstants.RangeIndexCombat && distance > 4f) continue;
            if (spell.Range.Max > 0 && distance > spell.Range.Max + player.BoundingRadius + target.BoundingRadius)
            {
                continue;
            }

            if (spell.Range.Min > 0 && distance < spell.Range.Min)
            {
                continue;
            }

            if (feature.System.GetActiveCooldowns(player).Any(cooldown => cooldown.SpellId == spell.Id
                    || (cooldown.Category != 0 && cooldown.Category == spell.Category)))
            {
                continue;
            }

            // Power cost includes aura modifiers and dynamic costs. Only the ordinary
            // handler decides affordability; refusal above advances the candidate cursor.
            return spell;
        }

        return null;
    }
}
