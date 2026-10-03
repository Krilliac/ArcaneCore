using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// The tabard designer and the guild emblem (vmangos GuildHandler.cpp:684-735, NPCHandler.cpp:49-69,
/// Guild.cpp:883-892). Results are the u32 of wow_messages GuildEmblemResult.
/// </summary>
public sealed class GuildEmblemTests
{
    private const uint Success = 0, NoGuild = 2, NotGuildMaster = 3, NotEnoughMoney = 4, NoMessage = 5;

    private static uint ReadResult(PetitionKit k, Player p)
    {
        var reader = new PacketReader(Assert.Single(k.Sent(p, WorldOpcode.MsgSaveGuildEmblem)));
        uint result = reader.ReadUInt32();
        Assert.Equal(0, reader.Remaining);
        return result;
    }

    private static (Player Leader, Player Member, Guild Guild) Guild(PetitionKit k, uint leaderMoney = 200_000)
    {
        Player leader = k.Add(1, money: leaderMoney);
        Player member = k.Add(2);
        Assert.Equal(GuildAdminResult.Ok, k.Guilds.Create(1, "Arcane", out Guild? guild));
        k.Guilds.Invite(leader, member.Name);
        k.Guilds.Accept(member);
        k.ClearAll();
        return (leader, member, guild!);
    }

    [Fact] // :686-692: the silent variant is SENT as result 5 (the client prints nothing)
    public void NoTabardDesignerInReach_AnswersNoMessage()
    {
        using var k = new PetitionKit(NpcFlags.Petitioner); // a petitioner that is not a tabard designer
        (Player leader, _, _) = Guild(k);

        k.Guilds.SaveEmblem(leader, k.Npc.Guid, 1, 2, 3, 4, 5);

        Assert.Equal(NoMessage, ReadResult(k, leader));
        Assert.Equal(200_000u, leader.Money);
        Assert.Empty(k.Sent(leader, WorldOpcode.SmsgGuildQueryResponse));
    }

    [Fact] // :686-692 out of range
    public void ADistantTabardDesigner_AnswersNoMessage()
    {
        using var k = new PetitionKit();
        (Player leader, _, _) = Guild(k);
        k.Npc = k.Npc with { X = 60 };

        k.Guilds.SaveEmblem(leader, k.Npc.Guid, 1, 2, 3, 4, 5);

        Assert.Equal(NoMessage, ReadResult(k, leader));
    }

    [Fact] // :698-703
    public void WithoutAGuild_AnswersNoGuild()
    {
        using var k = new PetitionKit();
        Player loner = k.Add(1);

        k.Guilds.SaveEmblem(loner, k.Npc.Guid, 1, 2, 3, 4, 5);

        Assert.Equal(NoGuild, ReadResult(k, loner));
    }

    [Fact] // :705-710
    public void ANonLeader_AnswersNotGuildMaster()
    {
        using var k = new PetitionKit();
        (_, Player member, Guild guild) = Guild(k);
        member.Money = 500_000;

        k.Guilds.SaveEmblem(member, k.Npc.Guid, 1, 2, 3, 4, 5);

        Assert.Equal(NotGuildMaster, ReadResult(k, member));
        Assert.Equal(500_000u, member.Money);
        Assert.Equal(-1, guild.EmblemStyle);
    }

    [Fact] // :712-717 — 10 gold is 100000 copper
    public void ALeaderWith99999Copper_AnswersNotEnoughMoney_AndNothingIsCharged()
    {
        using var k = new PetitionKit();
        (Player leader, _, Guild guild) = Guild(k, leaderMoney: 99_999);

        k.Guilds.SaveEmblem(leader, k.Npc.Guid, 1, 2, 3, 4, 5);

        Assert.Equal(NotEnoughMoney, ReadResult(k, leader));
        Assert.Equal(99_999u, leader.Money);
        Assert.Equal(-1, guild.EmblemStyle);
    }

    [Fact] // :719-734
    public void ALeaderWith100000Copper_PaysExactlyTenGold_StoresTheFiveValues_AndOnlyTheLeaderGetsTheQuery()
    {
        using var k = new PetitionKit();
        (Player leader, Player member, Guild guild) = Guild(k, leaderMoney: 100_000);

        k.Guilds.SaveEmblem(leader, k.Npc.Guid, 7, 8, 9, 10, 11);

        Assert.Equal(Success, ReadResult(k, leader));
        Assert.Equal(0u, leader.Money);
        Assert.Equal((7, 8, 9, 10, 11), (guild.EmblemStyle, guild.EmblemColor, guild.BorderStyle, guild.BorderColor, guild.BackgroundColor));
        Assert.Equal((7, 8, 9, 10, 11), (k.Persistence.Guilds[guild.Id].EmblemStyle, k.Persistence.Guilds[guild.Id].EmblemColor,
            k.Persistence.Guilds[guild.Id].BorderStyle, k.Persistence.Guilds[guild.Id].BorderColor, k.Persistence.Guilds[guild.Id].BackgroundColor));

        // SMSG_GUILD_QUERY_RESPONSE with the new emblem, to the leader only.
        var query = new PacketReader(Assert.Single(k.Sent(leader, WorldOpcode.SmsgGuildQueryResponse)));
        Assert.Equal((uint)guild.Id, query.ReadUInt32());
        Assert.Equal("Arcane", query.ReadCString());
        for (int i = 0; i < 10; i++)
        {
            query.ReadCString();
        }

        Assert.Equal([7, 8, 9, 10, 11], [query.ReadInt32(), query.ReadInt32(), query.ReadInt32(), query.ReadInt32(), query.ReadInt32()]);
        Assert.Empty(k.F.Session(member).Sent); // neither vmangos nor cmangos broadcasts GE_TABARDCHANGE
        Assert.Empty(k.Sent(leader, WorldOpcode.SmsgGuildEvent));
    }

    [Fact] // the values are raw int32s; vmangos neither validates ranges nor sends INVALID_TABARD_COLORS (1)
    public void NegativeAndHugeValues_AreStoredRaw_WithoutValidation()
    {
        using var k = new PetitionKit();
        (Player leader, _, Guild guild) = Guild(k);

        k.Guilds.SaveEmblem(leader, k.Npc.Guid, -1, int.MaxValue, int.MinValue, 0, 99999);

        Assert.Equal(Success, ReadResult(k, leader));
        Assert.Equal((-1, int.MaxValue, int.MinValue, 0, 99999), (guild.EmblemStyle, guild.EmblemColor, guild.BorderStyle, guild.BorderColor, guild.BackgroundColor));
    }

    [Fact] // NPCHandler.cpp:49-69
    public void TabardVendorActivate_EchoesTheGuid_OnlyForAnInteractableTabardDesigner()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);

        k.Guilds.ActivateTabardVendor(a, k.Npc.Guid);

        var reader = new PacketReader(Assert.Single(k.Sent(a, WorldOpcode.MsgTabardvendorActivate)));
        Assert.Equal(k.Npc.Guid.Value, reader.ReadUInt64());
        Assert.Equal(0, reader.Remaining);

        k.ClearAll();
        k.Npc = k.Npc with { NpcFlags = NpcFlags.Petitioner };
        k.Guilds.ActivateTabardVendor(a, k.Npc.Guid);
        k.Npc = k.Npc with { NpcFlags = NpcFlags.TabardDesigner, X = 90 };
        k.Guilds.ActivateTabardVendor(a, k.Npc.Guid);
        Assert.Empty(k.F.Session(a).Sent);
    }
}
