namespace ArcaneCore.Game.Spells;

/// <summary>Persisted owner metadata for an item-caused spell/category cooldown.</summary>
public readonly record struct ItemCooldownOwner(uint ItemId, uint Category, uint SpellId);
