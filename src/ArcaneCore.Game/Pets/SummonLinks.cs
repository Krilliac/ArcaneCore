using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// The server-side record of a summoned creature (what vmangos spreads over Totem/Pet members:
/// m_duration, the totem slot, the summon spell). The owner GUID itself lives in the unit's
/// UNIT_FIELD_SUMMONEDBY field (<see cref="OwnerLinks"/>), the source of truth as in vmangos.
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class SummonLinks
{
    internal SummonLinks(SummonKind kind, ObjectGuid owner, uint spellId, int slot, int durationMs, float followAngle = PetConstants.FollowAngle)
    {
        Kind = kind;
        Owner = owner;
        SpellId = spellId;
        Slot = slot;
        RemainingMs = durationMs;
        HasTimer = durationMs > 0;
        FollowAngle = followAngle;
    }

    public SummonKind Kind { get; }

    /// <summary>The summoner when the creature was created (the field is authoritative afterwards).</summary>
    public ObjectGuid Owner { get; }

    /// <summary>The summoning spell (UNIT_CREATED_BY_SPELL).</summary>
    public uint SpellId { get; }

    /// <summary>The totem slot, <see cref="TotemSlots.None"/> for anything else.</summary>
    public int Slot { get; }

    /// <summary>Time left in ms; 0 or less on a non-totem means no limit (vmangos m_duration).</summary>
    public int RemainingMs { get; internal set; }

    /// <summary>The summon was created with a positive duration (a wild summon without one only ends with its death).</summary>
    public bool HasTimer { get; }

    /// <summary>The pet's command/react state and action bar (vmangos: "pets always have a charminfo"); null for totems and wild summons.</summary>
    public CharmInfo? Charm { get; internal set; }

    /// <summary>vmangos Pet::SetFollowAngle: where, relative to the owner's facing, the summon stands while following.</summary>
    public float FollowAngle { get; }
}
