using System.Security.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Packets;

/// <summary>
/// The packets of the login sequence that are not character-screen packets. Order and
/// layouts follow vmangos WorldSession::HandlePlayerLogin and
/// Player::SendInitialPacketsBeforeAddToMap for build 5875.
/// </summary>
public static class LoginPackets
{
    /// <summary>SMSG_INITIALIZE_FACTIONS always lists 64 reputation slots in 1.12 (vmangos InitializeFactions::MAX_FACTION_COUNT).</summary>
    public const int FactionSlotCount = 64;

    /// <summary>
    /// SMSG_ACCOUNT_DATA_MD5 (0x209): one MD5 digest per account data type, 16 zero bytes for an
    /// empty type (vmangos WorldSession::SendAccountDataTimes, MD5::CreateEmpty). gtker names the
    /// opcode SMSG_ACCOUNT_DATA_TIMES (u32[32], the same 128 bytes) and notes the chat frame
    /// stays unusable unless it is sent.
    /// </summary>
    public static byte[] BuildAccountDataMd5(AccountSettings settings)
    {
        var writer = new PacketWriter(AccountSettings.DataTypeCount * MD5.HashSizeInBytes);
        Span<byte> digest = stackalloc byte[MD5.HashSizeInBytes];
        for (int type = 0; type < AccountSettings.DataTypeCount; type++)
        {
            byte[]? data = settings.Data[type]?.Data;
            if (data is { Length: > 0 })
            {
                MD5.HashData(data, digest);
            }
            else
            {
                digest.Clear();
            }

            writer.WriteBytes(digest);
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_FRIEND_LIST with no entries: u8 count (vmangos Social::FriendList).</summary>
    public static byte[] BuildEmptyFriendList() => [0];

    /// <summary>SMSG_IGNORE_LIST with no entries: u8 count (vmangos Social::IgnoreList).</summary>
    public static byte[] BuildEmptyIgnoreList() => [0];

    /// <summary>SMSG_SET_REST_START: u32 rest state time, always 0 (vmangos Misc::SetRestStart).</summary>
    public static byte[] BuildSetRestStart() => [0, 0, 0, 0];

    /// <summary>SMSG_BINDPOINTUPDATE: f32 x, y, z, u32 map, u32 area (vmangos Misc::BindpointUpdate, gtker).</summary>
    public static byte[] BuildBindPointUpdate(HomeBind home)
    {
        var writer = new PacketWriter(20);
        writer.WriteSingle(home.X);
        writer.WriteSingle(home.Y);
        writer.WriteSingle(home.Z);
        writer.WriteUInt32(home.MapId);
        writer.WriteUInt32(home.ZoneId);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_TUTORIAL_FLAGS: the account's eight tutorial words (vmangos
    /// WorldSession::SendTutorialsData). A set bit marks a tutorial as already shown.
    /// </summary>
    public static byte[] BuildTutorialFlags(ReadOnlySpan<uint> tutorials)
    {
        if (tutorials.Length != AccountSettings.TutorialWordCount)
        {
            throw new ArgumentException($"expected {AccountSettings.TutorialWordCount} tutorial words", nameof(tutorials));
        }

        var writer = new PacketWriter(AccountSettings.TutorialWordCount * 4);
        foreach (uint word in tutorials)
        {
            writer.WriteUInt32(word);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_ACTION_BUTTONS: 120 packed slots, action | type &lt;&lt; 24, 0 for an empty slot
    /// (vmangos MasterPlayer::SendInitialActionButtons, gtker u32[120]).
    /// </summary>
    public static byte[] BuildActionButtons(ReadOnlySpan<uint> buttons)
    {
        var writer = new PacketWriter(buttons.Length * 4);
        foreach (uint packed in buttons)
        {
            writer.WriteUInt32(packed);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_INITIALIZE_FACTIONS: u32 64, then 64 × (u8 flags, i32 standing) (vmangos
    /// ReputationMgr::SendInitialReputations). Until Faction.dbc is imported (M8) every slot is
    /// empty, so the reputation pane starts blank but initialised.
    /// </summary>
    public static byte[] BuildInitializeFactions()
    {
        var writer = new PacketWriter(4 + (FactionSlotCount * 5));
        writer.WriteUInt32(FactionSlotCount);
        for (int i = 0; i < FactionSlotCount; i++)
        {
            writer.WriteByte(0);
            writer.WriteInt32(0);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_INIT_WORLD_STATES: u32 map, u32 zone (builds &gt; 1.11.2), u16 count, then
    /// (u32 state, i32 value) pairs (vmangos Misc::InitWorldStates, gtker). This overload sends no
    /// states (cmangos-classic sends an empty list outside battlegrounds and outdoor PvP zones); the
    /// zone feature builds the real list from the world-state registry
    /// (<see cref="ArcaneCore.Game.WorldState.States.WorldStatePackets.BuildInit"/>, docs/areas/world-state.md).
    /// </summary>
    public static byte[] BuildInitWorldStates(uint mapId, uint zoneId)
        => ArcaneCore.Game.WorldState.States.WorldStatePackets.BuildInit(mapId, zoneId, []);
}
