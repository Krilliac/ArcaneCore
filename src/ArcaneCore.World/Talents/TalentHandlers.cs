using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Talents;

/// <summary>
/// CMSG_LEARN_TALENT and MSG_TALENT_WIPE_CONFIRM. Client layouts (build 5875, gtker/wow_messages
/// wowm/world/spell): cmsg_learn_talent = u32 talent id, u32 requested rank; msg_talent_wipe_confirm_client = u64 trainer
/// guid. Lengths are exact; anything else disconnects. CMSG_UNLEARN_TALENTS is deliberately not handled: vmangos marks it
/// INVALID_PACKET (Opcodes.cpp:622) and wow_messages lists it for versions 2 and 3 only.
/// </summary>
public sealed class TalentHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgLearnTalent, LearnTalent);
        table.OnWorld(WorldOpcode.MsgTalentWipeConfirm, WipeConfirm);
    }

    private static TalentFeature Feature(WorldSession session) => session.Services.GetRequiredService<TalentFeature>();

    private static void LearnTalent(WorldSession session, Player player, byte[] payload)
    {
        RequireLength(payload, 8);
        var reader = new PacketReader(payload);
        uint talent = reader.ReadUInt32();
        uint rank = reader.ReadUInt32();
        Feature(session).Service?.LearnTalent(player, talent, rank);
    }

    private static void WipeConfirm(WorldSession session, Player player, byte[] payload)
    {
        RequireLength(payload, 8);
        var reader = new PacketReader(payload);
        Feature(session).OnWipeConfirm(player, new ObjectGuid(reader.ReadUInt64()));
    }

    private static void RequireLength(byte[] payload, int expected)
    {
        if (payload.Length != expected)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"talent request requires exactly {expected} bytes");
        }
    }
}
