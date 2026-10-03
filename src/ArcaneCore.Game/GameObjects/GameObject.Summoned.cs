using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.GameObjects;

// What a spell-created game object (a fishing bobber, a ritual, a trap) remembers about its creator.
public sealed partial class GameObject
{
    /// <summary>The unit that created the object (vmangos GameObject::GetOwnerGuid), or empty.</summary>
    public ObjectGuid OwnerGuid { get; private set; }

    /// <summary>The spell that created the object (vmangos GameObject::GetSpellId), or 0.</summary>
    public uint SpellId { get; set; }

    /// <summary>
    /// vmangos GameObject::SetOwnerGuid: remembers the owner and publishes it in OBJECT_FIELD_CREATED_BY
    /// (update field 0x6, a GUID visible to everyone).
    /// </summary>
    public void SetOwner(ObjectGuid owner)
    {
        OwnerGuid = owner;
        SetUInt64(UpdateFields.ObjectFieldCreatedBy, owner.Value);
    }
}