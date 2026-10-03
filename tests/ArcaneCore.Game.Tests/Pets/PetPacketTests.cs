using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>The pet wire formats against the gtker/wow_messages vectors and layouts (docs/integration/pets.md).</summary>
public sealed class PetPacketTests
{
    [Fact]
    public void NameQuery_MatchesTheWowMessagesVectors()
    {
        // wow_messages queries/cmsg_pet_name_query.wowm test (1.12): pet_number 0xDEADBEEF, guid 0xFACADEDEADBEEF
        byte[] request = [0xEF, 0xBE, 0xAD, 0xDE, 0xEF, 0xBE, 0xAD, 0xDE, 0xDE, 0xCA, 0xFA, 0x00];
        (uint number, ObjectGuid guid) = PetPackets.ReadNameQuery(request);
        Assert.Equal(0xDEADBEEFu, number);
        Assert.Equal(0x00FACADEDEADBEEFul, guid.Value);

        // smsg_pet_name_query_response.wowm test (1.12): "ABCDEF", timestamp 0xFACADE
        byte[] response = [0xEF, 0xBE, 0xAD, 0xDE, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x00, 0xDE, 0xCA, 0xFA, 0x00];
        Assert.Equal(response, PetPackets.BuildNameQueryResponse(0xDEADBEEF, "ABCDEF", 0xFACADE));
    }

    [Fact]
    public void ClientPackets_ParseTheirLayouts()
    {
        // CMSG_PET_ACTION: guid, data (spell | type << 24), target
        byte[] action = [1, 0, 0, 0, 0, 0, 0x30, 0xF1, 0x02, 0x00, 0x00, 0x07, 9, 0, 0, 0, 0, 0, 0, 0];
        PetActionRequest request = PetPackets.ReadAction(action);
        Assert.Equal(0xF130000000000001ul, request.Pet.Value);
        Assert.Equal(2u, request.Action);
        Assert.Equal((byte)ActionType.Command, request.Type);
        Assert.Equal(9ul, request.Target.Value);

        // CMSG_PET_SPELL_AUTOCAST: guid, spell, bool
        byte[] autocast = [5, 0, 0, 0, 0, 0, 0, 0, 0x39, 0x30, 0, 0, 1];
        Assert.Equal(new PetAutocastRequest(new ObjectGuid(5), 12345, true), PetPackets.ReadAutocast(autocast));

        // CMSG_PET_SET_ACTION: one action is 8 + 8 bytes, two actions exactly 24 (vmangos Packets/Pet.cpp:52-62)
        byte[] one = new byte[16];
        Assert.Single(PetPackets.ReadSetAction(one).Actions);
        byte[] two = new byte[24];
        Assert.Equal(2, PetPackets.ReadSetAction(two).Actions.Count);
    }

    [Fact]
    public void ServerPackets_HaveTheirLayouts()
    {
        Assert.Equal(new byte[8], PetPackets.BuildRemoveActionBar());
        Assert.Equal([4], PetPackets.BuildActionFeedback(PetFeedback.NoPathTo));
        Assert.Equal([0x34, 0x12, 0, 0, 2, (byte)SpellCastResult.NotReady], PetPackets.BuildCastFailed(0x1234, SpellCastResult.NotReady));
        Assert.Equal([5, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0], PetPackets.BuildActionSound(new ObjectGuid(5), PetTalk.Attack));
        Assert.Equal([5, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0], PetPackets.BuildAiReaction(new ObjectGuid(5)));
    }
}
