using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Ranged;

/// <summary>
/// A game object a spell created and its caster owns (vmangos Unit::m_spellGameObjects with the
/// object's owner guid and spell id, Unit.cpp:4075-4140). The game object class has no owner,
/// so the ownership lives here (see <see cref="SpellObjectRegistry"/>).
/// </summary>
public sealed class SpellCreatedObject
{
    internal SpellCreatedObject(GameObject gameObject, Unit owner, uint spellId, int slot)
    {
        Object = gameObject;
        Owner = owner;
        SpellId = spellId;
        Slot = slot;
    }

    public GameObject Object { get; }

    public Unit Owner { get; }

    /// <summary>The spell that created the object (vmangos GameObject::GetSpellId).</summary>
    public uint SpellId { get; }

    /// <summary>The owner's object slot 0-3 (SPELL_EFFECT_SUMMON_OBJECT_SLOT1-4).</summary>
    public int Slot { get; }

    /// <summary>Map updates since the object was created (the spawn animation waits one tick for the client to know the object).</summary>
    internal int Age { get; set; }

    internal bool SpawnAnimSent { get; set; }

    /// <summary>Whether the trap system has applied the arming delay (a trap is armed on its first update).</summary>
    internal bool Armed { get; set; }
}

/// <summary>
/// The objects created by spells and who owns them: a list per owner and four slots per owner
/// (vmangos Unit::m_ObjectSlotGuid). World thread only.
/// </summary>
public sealed class SpellObjectRegistry
{
    /// <summary>MAX_OBJECT_SLOT (SummonObjectSlot1-4).</summary>
    public const int SlotCount = 4;

    private readonly Dictionary<ObjectGuid, SpellCreatedObject> _byObject = [];
    private readonly Dictionary<ObjectGuid, SpellCreatedObject?[]> _slots = [];

    public IReadOnlyCollection<SpellCreatedObject> All => _byObject.Values;

    public int Count => _byObject.Count;

    public SpellCreatedObject? Find(ObjectGuid objectGuid) => _byObject.GetValueOrDefault(objectGuid);

    public SpellCreatedObject? Find(GameObject gameObject)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        return _byObject.GetValueOrDefault(gameObject.Guid) is { } entry && ReferenceEquals(entry.Object, gameObject) ? entry : null;
    }

    /// <summary>The objects <paramref name="owner"/> owns.</summary>
    public IReadOnlyList<SpellCreatedObject> OwnedBy(Unit owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return [.. _byObject.Values.Where(e => ReferenceEquals(e.Owner, owner))];
    }

    /// <summary>The object in an owner's slot, if any.</summary>
    public SpellCreatedObject? InSlot(Unit owner, int slot)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return slot is >= 0 and < SlotCount && _slots.TryGetValue(owner.Guid, out SpellCreatedObject?[]? slots) ? slots[slot] : null;
    }

    /// <summary>Whether <paramref name="owner"/> owns a live object created by <paramref name="spellId"/> (the creating spell's cooldown waits for it).</summary>
    public bool IsCreatedBySpell(Unit owner, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        foreach (SpellCreatedObject entry in _byObject.Values)
        {
            if (entry.SpellId == spellId && ReferenceEquals(entry.Owner, owner))
            {
                return true;
            }
        }

        return false;
    }

    internal SpellCreatedObject Add(GameObject gameObject, Unit owner, uint spellId, int slot)
    {
        var entry = new SpellCreatedObject(gameObject, owner, spellId, slot);
        _byObject[gameObject.Guid] = entry;
        if (slot is >= 0 and < SlotCount)
        {
            if (!_slots.TryGetValue(owner.Guid, out SpellCreatedObject?[]? slots))
            {
                _slots[owner.Guid] = slots = new SpellCreatedObject?[SlotCount];
            }

            slots[slot] = entry;
        }

        return entry;
    }

    internal bool Remove(SpellCreatedObject entry)
    {
        if (!_byObject.Remove(entry.Object.Guid))
        {
            return false;
        }

        if (_slots.TryGetValue(entry.Owner.Guid, out SpellCreatedObject?[]? slots) && entry.Slot is >= 0 and < SlotCount
            && ReferenceEquals(slots[entry.Slot], entry))
        {
            slots[entry.Slot] = null;
            if (slots.All(s => s is null))
            {
                _slots.Remove(entry.Owner.Guid);
            }
        }

        return true;
    }
}
