using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Pets;

/// <summary>
/// The stable master opcodes (vmangos NPCHandler.cpp): MSG_LIST_STABLED_PETS, CMSG_STABLE_PET, CMSG_UNSTABLE_PET, CMSG_BUY_STABLE_SLOT,
/// CMSG_STABLE_SWAP_PET (u64 stable master, plus u32 pet number for unstable and swap), and CMSG_STABLE_REVIVE_PET, which vmangos reads and
/// ignores (HandleStableRevivePet is empty in 1.12). World thread; the work is <see cref="PetStableController"/>'s.
/// </summary>
public sealed class PetStableHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.MsgListStabledPets, (session, player, payload) => Controller(session)?.HandleList(player, Npc(payload)));
        table.OnWorld(WorldOpcode.CmsgStablePet, (session, player, payload) => Controller(session)?.HandleStable(player, Npc(payload)));
        table.OnWorld(WorldOpcode.CmsgUnstablePet, (session, player, payload) =>
        {
            (ObjectGuid npc, uint number) = NpcAndPet(payload);
            Controller(session)?.HandleUnstable(player, npc, number);
        });
        table.OnWorld(WorldOpcode.CmsgBuyStableSlot, (session, player, payload) => Controller(session)?.HandleBuySlot(player, Npc(payload)));
        table.OnWorld(WorldOpcode.CmsgStableSwapPet, (session, player, payload) =>
        {
            (ObjectGuid npc, uint number) = NpcAndPet(payload);
            Controller(session)?.HandleSwap(player, npc, number);
        });
        table.OnWorld(WorldOpcode.CmsgStableRevivePet, static (_, _, _) => { });
    }

    private static PetStableController? Controller(WorldSession session) => session.Services.GetService<PetStableFeature>()?.Controller;

    private static ObjectGuid Npc(byte[] payload) => new(new PacketReader(payload).ReadUInt64());

    private static (ObjectGuid Npc, uint PetNumber) NpcAndPet(byte[] payload)
    {
        var reader = new PacketReader(payload);
        return (new ObjectGuid(reader.ReadUInt64()), reader.ReadUInt32());
    }
}