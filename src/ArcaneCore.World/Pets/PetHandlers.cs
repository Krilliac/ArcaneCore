using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Pets;

/// <summary>
/// The pet opcodes (vmangos PetHandler.cpp, MiscHandler.cpp HandleRequestPetInfoOpcode,
/// SpellHandler.cpp HandlePetCancelAuraOpcode, QueryHandler.cpp HandlePetNameQueryOpcode). All run
/// on the world thread against the session's player and hand off to <see cref="PetController"/>;
/// the layouts are in <see cref="PetPackets"/>.
/// <para>
/// Not registered: CMSG_PET_RENAME and CMSG_PET_UNLEARN (hunter pets only: they need the pet
/// store and the training-point rules of the class-hunter lane).
/// </para>
/// </summary>
public sealed class PetHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgPetAction, (session, player, payload) => Controller(session).HandleAction(player, PetPackets.ReadAction(payload)));
        table.OnWorld(WorldOpcode.CmsgPetSetAction, (session, player, payload) => Controller(session).HandleSetAction(player, PetPackets.ReadSetAction(payload)));
        table.OnWorld(WorldOpcode.CmsgPetSpellAutocast, (session, player, payload) => Controller(session).HandleAutocast(player, PetPackets.ReadAutocast(payload)));
        table.OnWorld(WorldOpcode.CmsgPetStopAttack, (session, player, payload) => Controller(session).HandleStopAttack(player, PetPackets.ReadGuid(payload)));
        table.OnWorld(WorldOpcode.CmsgPetCastSpell, (session, player, payload) => Controller(session).HandleCast(player, PetPackets.ReadCast(payload)));
        table.OnWorld(WorldOpcode.CmsgPetCancelAura, HandleCancelAura);
        table.OnWorld(WorldOpcode.CmsgPetNameQuery, HandleNameQuery);
        table.OnWorld(WorldOpcode.CmsgPetAbandon, (session, player, payload) => Controller(session).HandleAbandon(player, PetPackets.ReadGuid(payload)));
        table.OnWorld(WorldOpcode.CmsgRequestPetInfo, (session, player, _) => Controller(session).HandleRequestPetInfo(player));
    }

    private static PetController Controller(WorldSession session) => session.Services.GetRequiredService<PetsFeature>().Controller;

    private static void HandleCancelAura(WorldSession session, Player player, byte[] payload)
    {
        (ObjectGuid pet, uint spell) = PetPackets.ReadCancelAura(payload);
        Controller(session).HandleCancelAura(player, pet, spell);
    }

    private static void HandleNameQuery(WorldSession session, Player player, byte[] payload)
    {
        (uint number, ObjectGuid pet) = PetPackets.ReadNameQuery(payload);
        Controller(session).HandleNameQuery(player, number, pet);
    }
}
