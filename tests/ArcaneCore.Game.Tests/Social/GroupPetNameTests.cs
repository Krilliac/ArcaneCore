using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;

namespace ArcaneCore.Game.Tests.Social;

public sealed class GroupPetNameTests
{
    [Fact]
    public void PartyMemberStats_PetNameUsesCurrentPetCharmName()
    {
        using var kit = new PetTestKit();
        (var owner, _) = kit.AddPlayer(1);
        kit.Cast(owner, PetSpell);
        var pet = Assert.Single(kit.Creatures.Creatures);
        pet.Summon!.Charm!.Name = "Fang";

        var reader = new PacketReader(GroupPackets.BuildPartyMemberStats(owner, GroupUpdateFlags.PetGuid | GroupUpdateFlags.PetName));
        Assert.Equal(owner.Guid.Value, reader.ReadPackedGuid());
        Assert.Equal((uint)(GroupUpdateFlags.PetGuid | GroupUpdateFlags.PetName), reader.ReadUInt32());
        Assert.Equal(pet.Guid.Value, reader.ReadUInt64());
        Assert.Equal("Fang", reader.ReadCString());
    }

    [Fact]
    public void MarkPetNameChanged_SendsOnlyToOutOfRangeGroupMembers()
    {
        using var fixture = new SocialFixture();
        var leader = fixture.AddPlayer(1, x: 0);
        var member = fixture.AddPlayer(2, x: 5000);
        fixture.Context.Groups.Invite(leader, member.Name);
        fixture.Context.Groups.Accept(member);
        fixture.Context.Groups.UpdateOutOfRangeStats();
        fixture.ClearAll();

        fixture.Context.Groups.MarkPetNameChanged(leader);
        fixture.Context.Groups.UpdateOutOfRangeStats();

        byte[] packet = fixture.Single(member, WorldOpcode.SmsgPartyMemberStats);
        var reader = new PacketReader(packet);
        Assert.Equal(leader.Guid.Value, reader.ReadPackedGuid());
        Assert.Equal((uint)GroupUpdateFlags.PetName, reader.ReadUInt32());
        Assert.Equal(string.Empty, reader.ReadCString());
        Assert.Empty(fixture.Sent(leader, WorldOpcode.SmsgPartyMemberStats));
    }

    [Fact]
    public void PetNameAndHealthChanges_ShareOneStatsPacket()
    {
        using var fixture = new SocialFixture();
        var leader = fixture.AddPlayer(1, x: 0);
        var member = fixture.AddPlayer(2, x: 5000);
        fixture.Context.Groups.Invite(leader, member.Name);
        fixture.Context.Groups.Accept(member);
        fixture.Context.Groups.UpdateOutOfRangeStats();
        fixture.ClearAll();
        leader.Health = leader.Health - 1;
        fixture.Context.Groups.MarkPetNameChanged(leader);
        fixture.Context.Groups.UpdateOutOfRangeStats();
        var reader = new PacketReader(fixture.Single(member, WorldOpcode.SmsgPartyMemberStats));
        Assert.Equal(leader.Guid.Value, reader.ReadPackedGuid());
        Assert.Equal((uint)(GroupUpdateFlags.CurrentHp | GroupUpdateFlags.PetName), reader.ReadUInt32());
        Assert.Equal((ushort)leader.Health, reader.ReadUInt16());
        Assert.Equal(string.Empty, reader.ReadCString());
    }

    [Fact]
    public void MarkPetNameChanged_IsNoOpInRangeAndWithoutAGroup()
    {
        using var fixture = new SocialFixture();
        var leader = fixture.AddPlayer(1, x: 0);
        var member = fixture.AddPlayer(2, x: 0);
        fixture.Context.Groups.Invite(leader, member.Name);
        fixture.Context.Groups.Accept(member);
        fixture.ClearAll();

        fixture.Context.Groups.MarkPetNameChanged(leader);
        fixture.Context.Groups.UpdateOutOfRangeStats();
        Assert.Empty(fixture.Sent(leader, WorldOpcode.SmsgPartyMemberStats));
        Assert.Empty(fixture.Sent(member, WorldOpcode.SmsgPartyMemberStats));

        var ungrouped = fixture.AddPlayer(3);
        fixture.Context.Groups.MarkPetNameChanged(ungrouped);
        fixture.Context.Groups.UpdateOutOfRangeStats();
        Assert.Empty(fixture.Sent(ungrouped, WorldOpcode.SmsgPartyMemberStats));
    }
}
