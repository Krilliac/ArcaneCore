using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// vmangos <c>Unit::GetSpellModOwner</c> (Unit.cpp:9008-9023): a player is its own mod owner; a pet (any summoned Pet object:
/// pet, guardian, mini pet) or a totem uses the player that owns it; everything else, including a creature that merely has an
/// owner field, has none. A pet whose owner left the map has none either (the lookup is by the owner's GUID in the pet's own map).
/// Follows <see cref="SpellModOptions.OwnerModsForPetsAndTotems"/> on every call.
/// </summary>
public sealed class PetTotemModOwnerResolver(SpellModOptions options) : ISpellModOwnerResolver
{
    public Player? GetModOwner(Unit caster)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (caster is Player player)
        {
            return player;
        }

        if (!options.OwnerModsForPetsAndTotems || caster is not Creature { IsPet: true } and not Creature { IsTotem: true })
        {
            return null;
        }

        ObjectGuid owner = caster.OwnerGuid;
        return owner.IsPlayer ? caster.Map?.FindPlayer(owner) : null;
    }
}
