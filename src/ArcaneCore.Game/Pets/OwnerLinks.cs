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
    }
}
