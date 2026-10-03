namespace ArcaneCore.Game.Creatures;

// The skinning state of a corpse (vmangos Creature::skinningForOthersTimer and lootForSkin).
public sealed partial class Creature
{
    /// <summary>The share of a fresh corpse (and of a corpse that was just looted out) reserved for the tapper: 5 s (Creature.cpp:251, :3357).</summary>
    public const uint SkinningForOthersDefaultMs = 5000;

    /// <summary>
    /// vmangos <c>skinningForOthersTimer</c>: while it runs only a tapper may skin the corpse (<c>IsSkinnableBy</c>, Creature.h:308); it counts down
    /// while the creature is a corpse (Creature.cpp:911-914) and restarts when the corpse is looted out (:3357) and at death.
    /// </summary>
    public uint SkinningForOthersMs { get; set; } = SkinningForOthersDefaultMs;

    /// <summary>vmangos <c>lootForSkin</c>: the skinning loot was already generated (a corpse can be skinned once).</summary>
    public bool LootedForSkin { get; internal set; }
}