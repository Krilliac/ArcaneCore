using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Crafting;

namespace ArcaneCore.Game.Crafting.Enchanting;

/// <summary>
/// The enchantment engine of one player (crafting lane), after vmangos Player::ApplyEnchantment (Objects/Player.cpp:11739-11884) and the
/// duration list (UpdateEnchantTime :11619-11636, AddEnchantmentDuration(s) :11641-11731, SendEnchantmentDurations :11910).
/// <para>
/// <b>Apply and remove.</b> An enchantment counts only while its item is worn and unbroken. Per enchantment effect: DAMAGE adds flat damage to the hand
/// (main hand, off hand or ranged slot); EQUIP_SPELL casts the enchant's spell triggered at the owner with the item as cast item and removes the item's
/// aura again (most 1.12 enchantments work this way: the stat is an aura of that spell); RESISTANCE and STAT move the update fields as deltas (the
/// repository convention of <see cref="EquipmentStatsApplier"/>, derived values follow from the stat system); TOTEM adds the Rockbiter weapon damage
/// (<c>amount * weapon delay / 1000</c>, shamans only); COMBAT_SPELL is inert here (the proc needs a melee outcome event, a documented limit).
/// What was applied is remembered per (item, slot) so a removal undoes exactly that, even after the slot changed.
/// </para>
/// <para>
/// <b>Durations.</b> A temporary enchantment's remaining time is a ledger kept off the item field (see <c>Item.LiveEnchantDuration</c>), because the
/// field is what the client shows; it is written back when the timer stops and saved with the item. Every item the player holds is tracked, equipped or not
/// (vmangos adds the durations whenever an item enters the inventory). A time update goes to the client when a timer starts and at login after the player
/// entered the world. An expired enchantment is removed from the stats and cleared with a fade log.
/// </para>
/// Thread affinity: world thread.
/// </summary>
public sealed class PlayerEnchantments
{
    /// <summary>How often the item set is compared with the tracked timers (items arrive and leave without a hook).</summary>
    private const uint SweepIntervalMs = 1000;

    private sealed class Tracked(Item item, int slot, uint left)
    {
        public Item Item { get; } = item;

        public int Slot { get; } = slot;

        public uint Left { get; set; } = left;
    }

    /// <summary>What was applied for one (item, slot): the enchantment id and the equipment slot it was applied in.</summary>
    private readonly record struct AppliedEnchant(uint EnchantId, byte EquipmentSlot);

    private readonly Player _player;
    private readonly EnchantCatalog _catalog;
    private readonly SpellSystem? _spells;
    private readonly Dictionary<(Item Item, int Slot), AppliedEnchant> _applied = [];
    private readonly List<Tracked> _durations = [];
    private readonly List<(Item Item, uint SpellId)> _pendingSpells = [];
    private uint _sinceSweepMs = SweepIntervalMs;
    private bool _loginSent;

    public PlayerEnchantments(Player player, EnchantCatalog catalog, SpellSystem? spells)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _spells = spells;
    }

    /// <summary>The temporary enchantments with a running timer.</summary>
    public int TrackedCount => _durations.Count;

    /// <summary>The remaining ms of a running timer, or null.</summary>
    public uint? LeftMs(Item item, int slot) => _durations.FirstOrDefault(t => ReferenceEquals(t.Item, item) && t.Slot == slot)?.Left;

    /// <summary>Whether the enchantment of (item, slot) is currently applied to the stats.</summary>
    public bool IsApplied(Item item, int slot) => _applied.ContainsKey((item, slot));

    /// <summary>
    /// Start maintaining a player whose items were loaded before this attached (the stat hook only sees later equips and removals): apply the
    /// enchantments of every worn item and start the timers of every item that has one.
    /// </summary>
    public void Attach()
    {
        foreach ((byte slot, Item item) in _player.Inventory.Equipped)
        {
            Apply(item, apply: true, applyDuration: false);
        }

        SweepTimers(send: false);
    }

    /// <summary>vmangos <c>Player::ApplyEnchantment(Item*, bool)</c>: every slot of the item.</summary>
    public void Apply(Item item, bool apply, bool applyDuration = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        for (int slot = 0; slot < EnchantSlots.Count; slot++)
        {
            Apply(item, slot, apply, applyDuration);
        }
    }

    /// <summary>
    /// vmangos <c>Player::ApplyEnchantment(Item*, slot, apply, apply_dur)</c> (Player.cpp:11739-11884). Applying twice or removing what was not applied
    /// does nothing, so the pair stays balanced however the callers interleave.
    /// </summary>
    public void Apply(Item item, int slot, bool apply, bool applyDuration = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (slot is < 0 or >= EnchantSlots.Count)
        {
            return;
        }

        uint currentId = ItemEnchantments.Id(item, slot);
        if (apply)
        {
            if (currentId == 0)
            {
                return;
            }

            if (_catalog.Find(currentId) is { } enchant && IsEquipped(item) && !IsBroken(item) && !_applied.ContainsKey((item, slot)))
            {
                _applied[(item, slot)] = new AppliedEnchant(currentId, item.Slot);
                ApplyEffects(item, enchant, item.Slot, +1);
            }
        }
        else if (_applied.Remove((item, slot), out AppliedEnchant previous))
        {
            if (_catalog.Find(previous.EnchantId) is { } enchant)
            {
                ApplyEffects(item, enchant, previous.EquipmentSlot, -1);
            }
        }
        else if (currentId == 0)
        {
            return;
        }

        // Visualize the enchantments of worn items (mostly weapon glows): the inspect slots only.
        if (IsEquipped(item) && slot < EnchantSlots.MaxInspected)
        {
            _player.SetUInt32(UpdateFields.PlayerVisibleItem10 + (item.Slot * VisibleItemStride) + 1 + slot, apply ? currentId : 0);
        }

        if (applyDuration)
        {
            AddDuration(item, slot, apply ? ItemEnchantments.Duration(item, slot) : 0, send: true);
        }
    }

    private const int VisibleItemStride = 12;

    private static bool IsEquipped(Item item) => item.Container is null && item.Slot < InventorySlots.EquipmentEnd;

    private static bool IsBroken(Item item) => item.MaxDurability > 0 && item.Durability == 0;

    /// <summary>The effect switch of Player::ApplyEnchantment; <paramref name="sign"/> is +1 to apply, -1 to remove.</summary>
    private void ApplyEffects(Item item, SpellItemEnchantment enchant, byte equipmentSlot, int sign)
    {
        for (int s = 0; s < EnchantCatalog.EffectCount; s++)
        {
            int amount = enchant.Amounts[s];
            uint arg = enchant.Args[s];
            switch ((EnchantEffectType)enchant.Types[s])
            {
                case EnchantEffectType.Damage:
                    if (HandOf(equipmentSlot) is { } hand)
                    {
                        _player.StatState.AddTotalDamage(hand, amount * sign);
                    }

                    break;
                case EnchantEffectType.EquipSpell:
                    if (arg != 0 && _spells is not null)
                    {
                        EquipSpell(item, arg, sign > 0);
                    }

                    break;
                case EnchantEffectType.Resistance:
                    if (arg < PlayerResistanceCount)
                    {
                        AddField(UpdateFields.UnitFieldResistances + (int)arg, amount * sign);
                    }

                    break;
                case EnchantEffectType.Stat:
                    ApplyStat((ItemStatType)arg, amount, sign);
                    break;
                case EnchantEffectType.Totem:
                    // Rockbiter Weapon: shamans only, main and off hand (Player.cpp:11846-11863).
                    if (_player.Class == Class.Shaman && HandOf(equipmentSlot) is { } totemHand and not WeaponAttackType.RangedAttack)
                    {
                        _player.StatState.AddTotalDamage(totemHand, amount * item.Template.Delay / 1000.0f * sign);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// The spell of an EQUIP_SPELL enchantment. The spell system belongs to the world thread: while the player is not in the world (login loads the
    /// equipment on the session task) the cast waits in a list and the first world tick settles it (<see cref="SettleEquipSpells"/>).
    /// </summary>
    private void EquipSpell(Item item, uint spellId, bool apply)
    {
        if (!_player.IsInWorld)
        {
            if (apply)
            {
                _pendingSpells.Add((item, spellId));
            }
            else
            {
                _pendingSpells.RemoveAll(p => ReferenceEquals(p.Item, item) && p.SpellId == spellId);
            }

            return;
        }

        if (apply)
        {
            _spells!.CastItemSpell(_player, item, spellId, SpellCastTargets.ForSelf(), triggered: true);
        }
        else
        {
            _spells!.RemoveAurasDueToItemSpell(_player, item, spellId);
        }
    }

    /// <summary>
    /// The first world tick after login: every deferred equip spell either already exists as a restored aura of the player (the character's saved auras are put
    /// back on login, and an equip aura is permanent so it was saved): that aura is adopted by the item, so it ends with the item; or it is cast now.
    /// </summary>
    private void SettleEquipSpells()
    {
        if (_spells is null || _pendingSpells.Count == 0)
        {
            return;
        }

        foreach ((Item item, uint spellId) in _pendingSpells.ToArray())
        {
            SpellAuraHolder? existing = _spells.GetAuras(_player).FirstOrDefault(h => !h.IsRemoved && h.Spell.Id == spellId && h.CasterGuid == _player.Guid);
            if (existing is not null)
            {
                existing.CastItemGuid = item.Guid;
            }
            else
            {
                _spells.CastItemSpell(_player, item, spellId, SpellCastTargets.ForSelf(), triggered: true);
            }
        }

        _pendingSpells.Clear();
    }

    private const int PlayerResistanceCount = 7;

    private static WeaponAttackType? HandOf(byte equipmentSlot) => equipmentSlot switch
    {
        InventorySlots.MainHand => WeaponAttackType.BaseAttack,
        InventorySlots.OffHand => WeaponAttackType.OffAttack,
        InventorySlots.Ranged => WeaponAttackType.RangedAttack,
        _ => null,
    };

    /// <summary>The ITEM_MOD_* branch (Player.cpp:11792-11840): mana and health move the maximum, the five stats move the stat and its buff counter.</summary>
    private void ApplyStat(ItemStatType type, int amount, int sign)
    {
        int delta = amount * sign;
        switch (type)
        {
            case ItemStatType.Mana:
                AddField(UpdateFields.UnitFieldMaxpower1, delta);
                break;
            case ItemStatType.Health:
                AddField(UpdateFields.UnitFieldMaxhealth, delta);
                break;
            case ItemStatType.Strength:
            case ItemStatType.Agility:
            case ItemStatType.Stamina:
            case ItemStatType.Intellect:
            case ItemStatType.Spirit:
            {
                int index = type switch
                {
                    ItemStatType.Strength => 0,
                    ItemStatType.Agility => 1,
                    ItemStatType.Stamina => 2,
                    ItemStatType.Intellect => 3,
                    _ => 4,
                };
                AddField(UpdateFields.UnitFieldStat0 + index, delta);
                AddField((amount > 0 ? UpdateFields.PlayerFieldPosstat0 : UpdateFields.PlayerFieldNegstat0) + index, delta);   // the counter follows the enchantment amount, not the direction
                break;
            }
        }
    }

    private void AddField(int field, int delta) => _player.SetUInt32(field, unchecked((uint)((int)_player.GetUInt32(field) + delta)));

    // --- durations ---------------------------------------------------------------------------------------------

    /// <summary>
    /// vmangos <c>Player::AddEnchantmentDuration</c> (Player.cpp:11709-11731): an existing timer of the slot is written back and dropped; a positive
    /// <paramref name="durationMs"/> starts a new one and tells the client. Zero just stops it.
    /// </summary>
    public void AddDuration(Item item, int slot, uint durationMs, bool send = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (slot is < 0 or >= EnchantSlots.Count)
        {
            return;
        }

        int existing = _durations.FindIndex(t => ReferenceEquals(t.Item, item) && t.Slot == slot);
        if (existing >= 0)
        {
            Stop(_durations[existing]);
            _durations.RemoveAt(existing);
        }

        if (durationMs == 0)
        {
            return;
        }

        if (send && _player.IsInWorld)
        {
            EnchantPackets.SendTimeUpdate(_player, item, slot, durationMs);
        }

        var tracked = new Tracked(item, slot, durationMs);
        _durations.Add(tracked);
        Mirror(tracked);
    }

    /// <summary>The timer stops: its remaining time goes back into the item field (vmangos SetEnchantmentDuration) and the ledger entry is dropped.</summary>
    private static void Stop(Tracked tracked)
    {
        ItemEnchantments.SetDuration(tracked.Item, tracked.Slot, tracked.Left);
        if (tracked.Item.LiveEnchantDuration is { } live && tracked.Slot < live.Length)
        {
            live[tracked.Slot] = null;
        }
    }

    private static void Mirror(Tracked tracked)
    {
        uint?[] live = tracked.Item.LiveEnchantDuration ??= new uint?[EnchantSlots.Count];
        live[tracked.Slot] = tracked.Left;
    }

    /// <summary>
    /// vmangos <c>Player::UpdateEnchantTime</c> (Player.cpp:11619-11636) once per update: a timer whose enchantment is gone is dropped, one that has
    /// <paramref name="diffMs"/> or less left expires (removed from the stats, cleared with a fade log), the rest count down. Also compares the held
    /// items with the timers once a second. A player held by a pending quest or economy settlement is skipped entirely so a staged inventory
    /// snapshot stays valid; the elapsed time is charged on the first pass after.
    /// </summary>
    public void Update(uint diffMs)
    {
        if (!_player.CanMutateQuestSettlementState)
        {
            return;
        }

        if (!_loginSent && _player.IsInWorld)
        {
            _loginSent = true;
            SettleEquipSpells();
            SendDurations();
        }

        for (int i = _durations.Count - 1; i >= 0; i--)
        {
            Tracked tracked = _durations[i];
            if (ItemEnchantments.Id(tracked.Item, tracked.Slot) == 0)
            {
                Stop(tracked);
                _durations.RemoveAt(i);
            }
            else if (tracked.Left <= diffMs)
            {
                _durations.RemoveAt(i);
                Apply(tracked.Item, tracked.Slot, apply: false, applyDuration: false);
                ItemEnchantments.Clear(tracked.Item, tracked.Slot, sendToClient: true);
                Stop(tracked);
            }
            else
            {
                tracked.Left -= diffMs;
                Mirror(tracked);
            }
        }

        _sinceSweepMs += diffMs;
        if (_sinceSweepMs >= SweepIntervalMs)
        {
            _sinceSweepMs = 0;
            SweepTimers(send: true);
        }
    }

    /// <summary>vmangos <c>Player::SendEnchantmentDurations</c> (Player.cpp:11910): the remaining whole seconds of every running timer.</summary>
    public void SendDurations()
    {
        foreach (Tracked tracked in _durations)
        {
            EnchantPackets.SendTimeUpdate(_player, tracked.Item, tracked.Slot, tracked.Left);
        }
    }

    /// <summary>
    /// Items arrive and leave the inventory without a hook here (vmangos calls AddEnchantmentDurations / RemoveEnchantmentDurations at every
    /// store, remove and destroy): start the timer of every held item that has one and stop the timers of items the player no longer holds.
    /// </summary>
    private void SweepTimers(bool send)
    {
        for (int i = _durations.Count - 1; i >= 0; i--)
        {
            Item item = _durations[i].Item;
            if (!ReferenceEquals(item.Inventory, _player.Inventory) || item.Slot == InventorySlots.NullSlot && item.Container is null)
            {
                Apply(item, _durations[i].Slot, apply: false, applyDuration: false);
                Stop(_durations[i]);
                _durations.RemoveAt(i);
            }
        }

        foreach (Item item in _player.Inventory.AllItems)
        {
            for (int slot = 0; slot < EnchantSlots.Count; slot++)
            {
                if (ItemEnchantments.Id(item, slot) != 0 && ItemEnchantments.Duration(item, slot) > 0 && !IsTracked(item, slot))
                {
                    AddDuration(item, slot, ItemEnchantments.Duration(item, slot), send);
                }
            }
        }
    }

    private bool IsTracked(Item item, int slot) => _durations.Exists(t => ReferenceEquals(t.Item, item) && t.Slot == slot);
}
