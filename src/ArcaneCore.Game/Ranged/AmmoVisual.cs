namespace ArcaneCore.Game.Ranged;

/// <summary>
/// The projectile trailer of SMSG_SPELL_START / SMSG_SPELL_GO when CAST_FLAG_AMMO is set: the
/// display id of the arrow, bullet or thrown weapon and its inventory type (vmangos
/// Spell::WriteAmmoToPacket, Spell.cpp:4563-4620). The client draws the flying projectile from it.
/// </summary>
public readonly record struct AmmoVisual(uint DisplayId, uint InventoryType);
