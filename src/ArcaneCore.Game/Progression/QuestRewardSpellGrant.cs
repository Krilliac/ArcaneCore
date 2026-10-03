namespace ArcaneCore.Game.Progression;

/// <summary>An item a reward spell (SPELL_EFFECT_CREATE_ITEM) creates, with its count frozen at preparation.</summary>
public sealed record QuestRewardCreatedItem(uint Entry, uint Count);

/// <summary>
/// What a reward spell contributes to one quest settlement, decided on the world thread before
/// anything is held. A spell is either a pure grant (every effect is LearnSpell or CreateItem: the
/// grants are persisted atomically with the journal, and the spell is never cast) or transient
/// (nothing durable: cast once, after the commit, for the original live player only). The two
/// shapes are never mixed, because the transient half could not be cast without repeating the grant.
/// </summary>
public sealed record QuestRewardSpellGrant(
    bool Transient,
    IReadOnlyList<uint> LearnedSpells,
    IReadOnlyList<QuestRewardCreatedItem> CreatedItems)
{
    /// <summary>No reward spell.</summary>
    public static QuestRewardSpellGrant None { get; } = new(false, [], []);

    /// <summary>The spell grants something durable (spells and/or items).</summary>
    public bool HasDurableGrant => LearnedSpells.Count > 0 || CreatedItems.Count > 0;

    /// <summary>Anything to publish after the settlement released the character.</summary>
    public bool HasPostReleaseEffects => Transient || LearnedSpells.Count > 0;
}
