using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Ranged;

/// <summary>Unit dynamic-flag bits (vmangos SharedDefines.h UnitDynFlags) the hunter mechanics touch.</summary>
public static class UnitDynFlags
{
    /// <summary>UNIT_DYNFLAG_TRACK_UNIT: the unit is marked (Hunter's Mark); the client shows it on the minimap.</summary>
    public const uint TrackUnit = 0x0002;

    /// <summary>UNIT_DYNFLAG_DEAD: the unit appears dead (Feign Death).</summary>
    public const uint Dead = 0x0020;
}

/// <summary>
/// Tracking auras and Hunter's Mark (vmangos SpellAuras.cpp). The minimap dots are drawn by the
/// client from the player's tracking fields; the server only keeps the bits.
/// <list type="bullet">
/// <item>TRACK_CREATURES / TRACK_RESOURCES (HandleAuraTrackCreatures / TrackResources, 2909-2931):
/// players only; applying removes the other tracking auras (SPELL_TRACKER stacking, SpellEntry.cpp:148-157);
/// bit (MiscValue - 1) of PLAYER_TRACK_CREATURES / PLAYER_TRACK_RESOURCES is set or cleared.</item>
/// <item>TRACK_STEALTHED (2933-2940): the same exclusivity; sets or clears the TRACK_STEALTHED bit
/// (0x02) of byte 0 of PLAYER_FIELD_BYTES (Player.h:360,378).</item>
/// <item>MOD_STALKED (HandleAuraModStalked, 4217-4226): sets UNIT_DYNFLAG_TRACK_UNIT and forces a values update.</item>
/// </list>
/// </summary>
internal static class TrackingAuras
{
    /// <summary>PLAYER_FIELD_BYTE_TRACK_STEALTHED.</summary>
    private const byte TrackStealthedBit = 0x02;

    /// <summary>The aura types SPELL_TRACKER covers (SpellEntry.cpp:148-157): they exclude one another.</summary>
    internal static readonly AuraType[] TrackerTypes = [AuraType.TrackCreatures, AuraType.TrackResources, AuraType.TrackStealthed];

    internal static void Apply(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        switch (aura.Type)
        {
            case AuraType.TrackCreatures:
            case AuraType.TrackResources:
                if (target is not Player)
                {
                    return;
                }

                if (apply)
                {
                    system.RemoveOtherHolders(target, holder, TrackerTypes);
                }

                SetTrackBit(target, aura.Type == AuraType.TrackCreatures ? UpdateFields.PlayerTrackCreatures : UpdateFields.PlayerTrackResources, aura.MiscValue, apply);
                break;

            case AuraType.TrackStealthed:
                if (target is not Player)
                {
                    return;
                }

                if (apply)
                {
                    system.RemoveOtherHolders(target, holder, TrackerTypes);
                }

                byte flags = target.GetByte(UpdateFields.PlayerFieldBytes, 0);
                target.SetByte(UpdateFields.PlayerFieldBytes, 0, apply ? (byte)(flags | TrackStealthedBit) : (byte)(flags & ~TrackStealthedBit));
                break;

            case AuraType.ModStalked:
                if (apply)
                {
                    target.SetFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.TrackUnit);
                    target.ForceFieldUpdate(UpdateFields.UnitDynamicFlags);
                }
                else
                {
                    target.RemoveFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.TrackUnit);
                }

                break;
        }
    }

    private static void SetTrackBit(Unit target, int field, int miscValue, bool apply)
    {
        if (miscValue is < 1 or > 32)
        {
            return; // 1 << (misc - 1) would leave the 32-bit field
        }

        uint bit = 1u << (miscValue - 1);
        if (apply)
        {
            target.SetFlag(field, bit);
        }
        else
        {
            target.RemoveFlag(field, bit);
        }
    }
}
