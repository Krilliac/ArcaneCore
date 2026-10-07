using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// The unit-to-unit links vmangos keeps in update fields (Unit.h:1195-1233): UNIT_FIELD_SUMMONEDBY
/// (owner), UNIT_FIELD_CREATEDBY (creator), UNIT_FIELD_SUMMON (the owner's pet) and
/// UNIT_FIELD_CHARMEDBY. The fields are the source of truth, as in vmangos, so a create block
/// carries them without a second copy.
/// </summary>
public static class OwnerLinks
{
    extension(Unit unit)
    {
        /// <summary>vmangos Unit::GetOwnerGuid (UNIT_FIELD_SUMMONEDBY).</summary>
        public ObjectGuid OwnerGuid => new(unit.GetUInt64(UpdateFields.UnitFieldSummonedby));

        /// <summary>vmangos Unit::GetCreatorGuid (UNIT_FIELD_CREATEDBY).</summary>
        public ObjectGuid CreatorGuid => new(unit.GetUInt64(UpdateFields.UnitFieldCreatedby));

        /// <summary>vmangos Unit::GetPetGuid (UNIT_FIELD_SUMMON).</summary>
        public ObjectGuid PetGuid => new(unit.GetUInt64(UpdateFields.UnitFieldSummon));

        /// <summary>vmangos Unit::GetCharmerGuid (UNIT_FIELD_CHARMEDBY).</summary>
        public ObjectGuid CharmerGuid => new(unit.GetUInt64(UpdateFields.UnitFieldCharmedby));

        /// <summary>vmangos Unit::GetCharmGuid (UNIT_FIELD_CHARM): the unit this one charms or possesses.</summary>
        public ObjectGuid CharmGuid => new(unit.GetUInt64(UpdateFields.UnitFieldCharm));

        /// <summary>vmangos Unit::GetCharmerOrOwnerGuid: the charmer when charmed, else the owner (the faction lane's owner resolution).</summary>
        public ObjectGuid CharmerOrOwnerGuid => unit.CharmerGuid.IsEmpty ? unit.OwnerGuid : unit.CharmerGuid;

        /// <summary>vmangos Unit::SetOwnerGuid: also forces the health fields so a client re-reads them for the owner.</summary>
        public void SetOwnerGuid(ObjectGuid owner)
        {
            unit.SetUInt64(UpdateFields.UnitFieldSummonedby, owner.Value);
            unit.ForceFieldUpdate(UpdateFields.UnitFieldHealth);
            unit.ForceFieldUpdate(UpdateFields.UnitFieldMaxhealth);
        }

        /// <summary>vmangos Unit::SetCreatorGuid.</summary>
        public void SetCreatorGuid(ObjectGuid creator) => unit.SetUInt64(UpdateFields.UnitFieldCreatedby, creator.Value);

        /// <summary>vmangos Unit::SetPetGuid.</summary>
        public void SetPetGuid(ObjectGuid pet) => unit.SetUInt64(UpdateFields.UnitFieldSummon, pet.Value);

        /// <summary>vmangos Unit::GetOwner: the owner unit in the same map, or null.</summary>
        public Unit? GetOwner() => unit.OwnerGuid is { IsEmpty: false } guid ? unit.Map?.FindObject(guid) as Unit : null;

        /// <summary>vmangos Unit::GetCharmer: the charmer unit in the same map, or null.</summary>
        public Unit? GetCharmer() => unit.CharmerGuid is { IsEmpty: false } guid ? unit.Map?.FindObject(guid) as Unit : null;

        /// <summary>vmangos Unit::GetCharmerOrOwner.</summary>
        public Unit? GetCharmerOrOwner() => unit.CharmerGuid.IsEmpty ? unit.GetOwner() : unit.GetCharmer();

        /// <summary>vmangos Unit::GetCharmerOrOwnerOrSelf.</summary>
        public Unit GetCharmerOrOwnerOrSelf() => unit.GetCharmerOrOwner() ?? unit;

        /// <summary>vmangos Unit::IsCharmerOrOwnerPlayerOrPlayerItself: a player, or something a player owns or charms (by GUID, so the owner need not be loaded).</summary>
        public bool IsCharmerOrOwnerPlayerOrPlayerItself => unit is Player || unit.CharmerOrOwnerGuid.IsPlayer;

        /// <summary>vmangos Unit::GetCharmerOrOwnerPlayerOrPlayerItself: the player behind the unit (a pet's master), the unit itself when it is a player, else null.</summary>
        public Player? GetCharmerOrOwnerPlayerOrSelf()
            => unit.CharmerOrOwnerGuid.IsPlayer ? unit.Map?.FindPlayer(unit.CharmerOrOwnerGuid) : unit as Player;

        /// <summary>vmangos Unit::GetCharmerOrOwnerPlayer: the player that owns or charms the unit, null for a player itself.</summary>
        public Player? GetCharmerOrOwnerPlayer()
            => unit.CharmerOrOwnerGuid.IsPlayer ? unit.Map?.FindPlayer(unit.CharmerOrOwnerGuid) : null;

        /// <summary>
        /// vmangos Unit::GetAffectingPlayer (Unit.cpp:4824-4834): the player a unit acts for in combat
        /// credit, PvP flags and threat: the unit itself when it is a player, a pet's owner (a pet of a
        /// charmed creature counts for the player controlling that creature), null for everything else.
        /// Loot, experience, quest kill credit and PvP rules of the other areas ask this of the killer.
        /// </summary>
        public Player? GetAffectingPlayer()
        {
            if (unit.CharmerOrOwnerGuid.IsEmpty)
            {
                return unit as Player;
            }

            // no charmer: a pet of a charmed creature should still be attackable by the player
            if (unit.GetCharmerOrOwner() is not { } master)
            {
                return null;
            }

            return master.OwnerGuid.IsPlayer ? master.Map?.FindPlayer(master.OwnerGuid) : master as Player;
        }

        /// <summary>vmangos Unit::GetPet: the pet in UNIT_FIELD_SUMMON (a mini pet or guardian is not it), or null.</summary>
        public Creature? GetPet() => unit.PetGuid is { IsEmpty: false } guid ? unit.Map?.FindObject(guid) as Creature : null;
    }
}
