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

    /// <summary>CORPSE_FLAG_LOOTABLE: a battleground body whose insignia can be taken (vmangos Player::CreateCorpse, Player.cpp:4753-4754).</summary>
    public const uint FlagLootable = 0x20;

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

    /// <summary>
    /// The map instance the body lies in (vmangos <c>Corpse::GetInstanceId</c>, saved as <c>corpse.instance</c>): set when it
    /// enters a map and kept when it leaves one (a logout takes the body out of its map before the character is saved), or the
    /// stored instance of a restored body whose instance map is not loaded (<see cref="MapCombat.RestoreGhost"/>).
    /// </summary>
    public uint InstanceId { get; internal set; }

    public ObjectGuid Owner => new(GetUInt64(UpdateFields.CorpseFieldOwner));

    /// <summary>
    /// Build the corpse of <paramref name="player"/> at its position (vmangos
    /// Player::CreateCorpse, Player.cpp:4719-4782): owner, position and facing, the native display id,
    /// CORPSE_FIELD_BYTES_1 = (0, race, gender, skin), CORPSE_FIELD_BYTES_2 = (face, hair
    /// style, hair color, facial hair), CORPSE_FLAG_UNK2 with the hide helm and hide cloak flags of
    /// PLAYER_FLAGS, the guild id and the equipment (CORPSE_FIELD_ITEM + slot = display id |
    /// inventory type &lt;&lt; 24). <paramref name="lootable"/> adds CORPSE_FLAG_LOOTABLE, which vmangos sets
    /// when the player is in a battleground ("to be able to remove insignia").
    /// </summary>
    public static Corpse CreateFor(Player player, bool pvpDeath, bool lootable = false)
    {
        ArgumentNullException.ThrowIfNull(player);
        Corpse corpse = CreateAt(player, player.MapId, player.X, player.Y, player.Z, player.Orientation,
            pvpDeath ? CorpseType.ResurrectablePvp : CorpseType.ResurrectablePve);
        if (lootable)
        {
            corpse.SetUInt32(UpdateFields.CorpseFieldFlags, corpse.GetUInt32(UpdateFields.CorpseFieldFlags) | FlagLootable);
            corpse.ClearChangedFields();
        }

        return corpse;
    }

    /// <summary>
    /// The corpse of <paramref name="player"/> at an explicit place and of an explicit type: a body
    /// that was left in the world by an earlier session (vmangos loads it from the corpse table,
    /// Corpse::LoadFromDB, Corpse.cpp:157-226, which reads the appearance, the equipment cache, the guild
    /// and the player flags of the character row); appearance, gear, guild and the hide flags come from
    /// the owner like <see cref="CreateFor"/>. Never lootable: vmangos saves no battleground body.
    /// </summary>
    public static Corpse CreateAt(Player player, uint mapId, float x, float y, float z, float orientation, CorpseType type)
    {
        ArgumentNullException.ThrowIfNull(player);

        uint counter = (uint)Interlocked.Increment(ref s_nextCounter);
        var corpse = new Corpse(new ObjectGuid(((ulong)HighGuid.Corpse << 48) | counter))
        {
            Type = type,
            MapId = mapId,
            X = x,
            Y = y,
            Z = z,
            Orientation = orientation,
        };

        corpse.SetUInt64(UpdateFields.CorpseFieldOwner, player.Guid.Value);
        corpse.SetFloat(UpdateFields.CorpseFieldFacing, orientation);
        corpse.SetFloat(UpdateFields.CorpseFieldPosX, x);
        corpse.SetFloat(UpdateFields.CorpseFieldPosY, y);
        corpse.SetFloat(UpdateFields.CorpseFieldPosZ, z);
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
        uint flags = FlagUnk2;
        if ((player.Flags & PlayerFlags.HideHelm) != 0)
        {
            flags |= FlagHideHelm;
        }

        if ((player.Flags & PlayerFlags.HideCloak) != 0)
        {
            flags |= FlagHideCloak;
        }

        corpse.SetUInt32(UpdateFields.CorpseFieldFlags, flags);
        corpse.SetUInt32(UpdateFields.CorpseFieldGuild, player.GetUInt32(UpdateFields.PlayerGuildid));
        foreach ((byte slot, Items.Item item) in player.Inventory.Equipped)
        {
            corpse.SetUInt32(UpdateFields.CorpseFieldItem + slot, item.Template.DisplayId | (item.Template.InventoryType << 24));
        }

        // A create block carries every value; nothing is pending.
        corpse.ClearChangedFields();
        return corpse;
    }
}
