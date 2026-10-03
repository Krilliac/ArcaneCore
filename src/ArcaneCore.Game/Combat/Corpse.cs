using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// A player's corpse (vmangos Corpse.cpp): a non-living object with a position. It exists
/// while the player is a ghost and is removed when the spirit reclaims it — vmangos turns it
/// into bones instead (see docs/areas/combat.md, discrepancies).
/// </summary>
public sealed class Corpse : WorldObject
{
    /// <summary>CORPSE_FLAG_UNK2 (vmangos Corpse.h CorpseFlags), set on every player corpse by Corpse::Create.</summary>
    public const uint FlagUnk2 = 0x04;

    /// <summary>CORPSE_FLAG_HIDE_HELM.</summary>
    public const uint FlagHideHelm = 0x08;

    /// <summary>CORPSE_FLAG_HIDE_CLOAK.</summary>
    public const uint FlagHideCloak = 0x10;

    private static int s_nextCounter;

    private Corpse(ObjectGuid guid)
        : base(guid, Game.TypeId.Corpse, TypeMask.Object | TypeMask.Corpse, UpdateFields.CorpseEnd)
    {
    }

    public override ReadOnlySpan<ushort> FieldFlags => UpdateFieldTables.CorpseVisibility;

    public override ReadOnlySpan<bool> GuidFieldStarts => UpdateFieldTables.CorpseGuidStarts;

    /// <summary>vmangos Corpse constructor: m_updateFlag = UPDATEFLAG_ALL | UPDATEFLAG_HAS_POSITION.</summary>
    public override ObjectUpdateFlags CreateUpdateFlags => ObjectUpdateFlags.All | ObjectUpdateFlags.HasPosition;

    public CorpseType Type { get; private init; }

    public ObjectGuid Owner => new(GetUInt64(UpdateFields.CorpseFieldOwner));

    /// <summary>
    /// Build the corpse of <paramref name="player"/> at its position (vmangos
    /// Player::CreateCorpse): owner, position and facing, the native display id,
    /// CORPSE_FIELD_BYTES_1 = (0, race, gender, skin), CORPSE_FIELD_BYTES_2 = (face, hair
    /// style, hair color, facial hair) and CORPSE_FLAG_UNK2. Equipment display ids
    /// (CORPSE_FIELD_ITEM) and the hide helm/cloak flags come from the items area.
    /// </summary>
    public static Corpse CreateFor(Player player, bool pvpDeath)
    {
        ArgumentNullException.ThrowIfNull(player);

        uint counter = (uint)Interlocked.Increment(ref s_nextCounter);
        var corpse = new Corpse(new ObjectGuid(((ulong)HighGuid.Corpse << 48) | counter))
        {
            Type = pvpDeath ? CorpseType.ResurrectablePvp : CorpseType.ResurrectablePve,
            MapId = player.MapId,
            X = player.X,
            Y = player.Y,
            Z = player.Z,
            Orientation = player.Orientation,
        };

        corpse.SetUInt64(UpdateFields.CorpseFieldOwner, player.Guid.Value);
        corpse.SetFloat(UpdateFields.CorpseFieldFacing, player.Orientation);
        corpse.SetFloat(UpdateFields.CorpseFieldPosX, player.X);
        corpse.SetFloat(UpdateFields.CorpseFieldPosY, player.Y);
        corpse.SetFloat(UpdateFields.CorpseFieldPosZ, player.Z);
        corpse.SetUInt32(UpdateFields.CorpseFieldDisplayId, player.NativeDisplayId);

        byte skin = player.GetByte(UpdateFields.PlayerBytes, 0);
        byte face = player.GetByte(UpdateFields.PlayerBytes, 1);
        byte hairStyle = player.GetByte(UpdateFields.PlayerBytes, 2);
        byte hairColor = player.GetByte(UpdateFields.PlayerBytes, 3);
        byte facialHair = player.GetByte(UpdateFields.PlayerBytes2, 0);
        corpse.SetUInt32(UpdateFields.CorpseFieldBytes1,
            ((uint)(byte)player.Race << 8) | ((uint)(byte)player.Gender << 16) | ((uint)skin << 24));
        corpse.SetUInt32(UpdateFields.CorpseFieldBytes2,
            face | ((uint)hairStyle << 8) | ((uint)hairColor << 16) | ((uint)facialHair << 24));
        corpse.SetUInt32(UpdateFields.CorpseFieldFlags, FlagUnk2);

        // A create block carries every value; nothing is pending.
        corpse.ClearChangedFields();
        return corpse;
    }
}
