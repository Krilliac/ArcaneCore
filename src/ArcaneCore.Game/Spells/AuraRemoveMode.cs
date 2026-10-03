namespace ArcaneCore.Game.Spells;

/// <summary>
/// Why an aura holder left its target (vmangos AuraRemoveMode, UnitDefines.h:259-271). Handlers and rules that observe a
/// removal (<see cref="SpellSystem.HolderRemoved"/>, <see cref="SpellAuraHolder.RemoveMode"/>) can tell an expiry from a
/// cancel, a dispel or a replacement. Members that no code in this lane produces yet (shield break, channel end, range,
/// group) are reserved for the lanes that own those removals; docs/areas/aura-engine.md lists which modes are produced.
/// </summary>
public enum AuraRemoveMode
{
    /// <summary>AURA_REMOVE_BY_DEFAULT: any removal without a more specific cause.</summary>
    Default = 0,

    /// <summary>AURA_REMOVE_BY_STACK: replaced by a similar aura.</summary>
    Stack,

    /// <summary>AURA_REMOVE_BY_CANCEL: CMSG_CANCEL_AURA.</summary>
    Cancel,

    /// <summary>AURA_REMOVE_BY_DISPEL.</summary>
    Dispel,

    /// <summary>AURA_REMOVE_BY_DEATH: the target died.</summary>
    Death,

    /// <summary>AURA_REMOVE_BY_DELETE: removed on logout or unsummon after the save.</summary>
    Delete,

    /// <summary>AURA_REMOVE_BY_SHIELD_BREAK: an absorb shield was used up.</summary>
    ShieldBreak,

    /// <summary>AURA_REMOVE_BY_EXPIRE: the duration ran out.</summary>
    Expire,

    /// <summary>AURA_REMOVE_BY_CHANNEL: the channel finished or was cancelled.</summary>
    Channel,

    /// <summary>AURA_REMOVE_BY_RANGE: an area aura target left the radius.</summary>
    Range,

    /// <summary>AURA_REMOVE_BY_GROUP: the target left the caster's group.</summary>
    Group,
}
