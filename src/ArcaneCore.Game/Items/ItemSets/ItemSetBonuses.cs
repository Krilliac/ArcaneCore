using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items.ItemSets;

/// <summary>
/// The set bonuses one player currently has: for each set, how many worn pieces count and which bonus spells are active (mangos
/// <c>ItemSetEffect</c>, Player::ItemSetEff). Owned by the player's equip binding (<see cref="ItemEquipSpells"/>), touched on the world
/// thread only, and changed only when a piece is worn or removed, so it allocates per equip event, never per tick.
/// </summary>
public sealed class PlayerItemSets
{
    private readonly Dictionary<uint, SetEffect> _effects = [];

    /// <summary>The pieces of set <paramref name="setId"/> that count (0 when none, or when the set's skill requirement was not met).</summary>
    public int PieceCount(uint setId) => _effects.TryGetValue(setId, out SetEffect? effect) ? effect.Pieces : 0;

    /// <summary>The bonus spells of set <paramref name="setId"/> in force, ascending by spell id.</summary>
    public IReadOnlyList<uint> ActiveSpells(uint setId)
        => _effects.TryGetValue(setId, out SetEffect? effect) ? [.. effect.Spells.Order()] : [];

    internal SetEffect GetOrAdd(uint setId)
    {
        if (!_effects.TryGetValue(setId, out SetEffect? effect))
        {
            _effects[setId] = effect = new SetEffect();
        }

        return effect;
    }

    internal SetEffect? Find(uint setId) => _effects.GetValueOrDefault(setId);

    internal void Remove(uint setId) => _effects.Remove(setId);

    internal sealed class SetEffect
    {
        /// <summary>The worn pieces that count: a piece worn while the set's skill requirement was not met is not one of them.</summary>
        public HashSet<Item> Counted { get; } = new(ReferenceEqualityComparer.Instance);

        public int Pieces => Counted.Count;

        public HashSet<uint> Spells { get; } = [];
    }
}

/// <summary>
/// Item set bonuses (mangos AddItemsSetItem / RemoveItemsSetItem, Item.cpp): each worn piece of a set counts once, a bonus spell is cast on
/// the wearer when its piece threshold is reached and removed as soon as the count drops below it, so unequipping one piece removes the
/// highest bonus. A piece counts while worn even when broken. A set whose required skill the wearer lacks at the time a piece is worn does
/// not count that piece (mangos checks only then). Taking off a piece that was never counted changes nothing: the counted pieces are
/// remembered (mangos RemoveItemsSetItem decrements the count for any piece once the set has an effect, so an early uncounted piece
/// would take a bonus away from the counted ones).
/// <para>
/// Set content is the immutable <see cref="ItemSetCatalog"/> read from ItemSet.dbc. Spells are cast triggered on the player with no cast item,
/// so removal is by spell id. Form-dependent re-evaluation (mangos UpdateEquipSpellsAtFormChange) is not implemented, see docs/areas/items.md.
/// </para>
/// </summary>
public sealed class ItemSetBonuses(ItemSetCatalog catalog, SpellSystem spells, Action<uint, uint>? onUnknownSet = null)
{
    private readonly ItemSetCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly SpellSystem _spells = spells ?? throw new ArgumentNullException(nameof(spells));

    /// <summary>The catalog in force.</summary>
    public ItemSetCatalog Catalog => _catalog;

    /// <summary>
    /// A piece of <paramref name="item"/>'s set was put on. <paramref name="replay"/> (login) first drops any bonus aura restored from the
    /// saved aura list, so the replay never stacks on it.
    /// </summary>
    public void ItemWorn(Player player, PlayerItemSets state, Item item, bool replay)
    {
        uint setId = item.Template.SetId;
        if (setId == 0)
        {
            return;
        }

        if (_catalog.Find(setId) is not { } set)
        {
            onUnknownSet?.Invoke(setId, item.Template.Entry);
            return;
        }

        if (set.RequiredSkill != 0 && player.Inventory.Requirements.SkillValue(player.Inventory, set.RequiredSkill) < set.RequiredSkillRank)
        {
            return;
        }

        PlayerItemSets.SetEffect effect = state.GetOrAdd(setId);
        if (!effect.Counted.Add(item))
        {
            return; // already counted (a replay of a piece that is still counted)
        }

        for (int slot = 0; slot < ItemSetRecord.BonusSlots; slot++)
        {
            uint spellId = set.SpellIds[slot];
            if (spellId == 0 || set.Thresholds[slot] > effect.Pieces || effect.Spells.Contains(spellId))
            {
                continue;
            }

            if (_spells.Store.Get(spellId) is null)
            {
                continue;   // mangos: "unknown spell id in items set effects", the slot stays free
            }

            if (replay)
            {
                _spells.RemoveAuras(player, spellId);
            }

            _spells.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true);
            effect.Spells.Add(spellId);
        }
    }

    /// <summary>A piece of <paramref name="item"/>'s set was taken off: every bonus whose threshold is no longer met is removed.</summary>
    public void ItemRemoved(Player player, PlayerItemSets state, Item item)
    {
        uint setId = item.Template.SetId;
        if (setId == 0 || _catalog.Find(setId) is not { } set || state.Find(setId) is not { } effect || !effect.Counted.Remove(item))
        {
            return;   // an unknown set was never applied; a piece worn without the set's skill was never counted
        }

        for (int slot = 0; slot < ItemSetRecord.BonusSlots; slot++)
        {
            uint spellId = set.SpellIds[slot];
            if (spellId == 0 || set.Thresholds[slot] <= effect.Pieces || !effect.Spells.Remove(spellId))
            {
                continue;
            }

            _spells.RemoveAuras(player, spellId);
        }

        if (effect.Pieces <= 0)
        {
            state.Remove(setId);
        }
    }
}
