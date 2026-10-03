using System.Buffers.Binary;
using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// Social end to end over the real world socket: friends (with persistence across a relog),
/// group invite/accept and party chat, a GM-founded guild with guild chat and roster, and a
/// custom chat channel with join notices, channel chat and the member list.
/// </summary>
public sealed class SocialEndToEndTests
{
    [Fact]
    public async Task Friend_IsAdded_NotifiedOffline_AndListedAfterRelog()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("ALICE");
        WorldTestClient alice = await host.ConnectAsync();
        await alice.AuthenticateAsync("ALICE", key);
        await alice.CreateCharacterAsync("Alice");
        await alice.LoginAsync(1);
        await using (WorldTestClient bob = await host.EnterWorldAsync("BOB", "Bob"))
        {
            await alice.SendAsync(WorldOpcode.CmsgAddFriend, CString("bob"));
            byte[] added = await alice.ReadUntilAsync(WorldOpcode.SmsgFriendStatus);
            Assert.Equal((byte)FriendsResult.AddedOnline, added[0]);
            Assert.Equal(2ul, BinaryPrimitives.ReadUInt64LittleEndian(added.AsSpan(1)));
            Assert.Equal((byte)FriendStatus.Online, added[9]);

            ISocialStore store = await StoreAsync(host, "Alice");
            await WorldTestHost.WaitForAsync(() => store.GetSocialAsync(1).Result.Count == 1, "the friend to be saved");
        }

        byte[] offline = await alice.ReadUntilAsync(WorldOpcode.SmsgFriendStatus);
        Assert.Equal([(byte)FriendsResult.Offline, 2, 0, 0, 0, 0, 0, 0, 0], offline);
        await alice.DisposeAsync();

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "both sessions to leave");
        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("ALICE", key);
        await again.LoginAsync(1);
        Assert.Equal([0], again.LoginPacket(WorldOpcode.SmsgFriendList)); // the seam's empty list
        byte[] list = await again.ReadUntilAsync(WorldOpcode.SmsgFriendList);
        Assert.Equal([1, 2, 0, 0, 0, 0, 0, 0, 0, (byte)FriendStatus.Offline], list);
    }

    [Fact]
    public async Task Group_InviteAccept_ListsBothMembers_AndPartyChatReachesOnlyTheGroup()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient leader = await host.EnterWorldAsync("LEADER", "Leader");
        await using WorldTestClient member = await host.EnterWorldAsync("MEMBER", "Member");
        await using WorldTestClient outsider = await host.EnterWorldAsync("OUTSIDER", "Outsider");

        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, CString("member"));
        byte[] invite = await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        Assert.Equal(CString("Leader"), invite);

        await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        byte[] leaderList = await ReadGroupListAsync(leader, others: 1);
        byte[] memberList = await ReadGroupListAsync(member, others: 1);
        Assert.Equal(0, leaderList[0]); // party
        Assert.Equal(0, memberList[0]);
        Assert.Equal(1ul, await host.PlayerStateAsync("Member", p => Feature(p).Context.Groups.GetGroup(p.Guid)?.LeaderGuid.Value ?? 0));

        await Drain(leader, member, outsider);
        await member.SendChatAsync(ChatType.Party, Language.Common, "inc");
        ChatMessage heard = await leader.ReadChatAsync();
        Assert.Equal((ChatType.Party, Language.Common, 2ul, "inc"), (heard.Type, heard.Language, heard.Sender, heard.Text));
        await outsider.AssertSilentAsync(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Guild_FoundedByAGm_CarriesGuildChat_AndTheRoster()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Keeper", AccountSecurity.GameMaster);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Founder");
        SocialFeature feature = await FeatureAsync(host, "Keeper");
        await feature.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        await Drain(gm, player);

        Assert.Equal("Guild Arcane created.", await CommandAsync(gm, ".guild create Founder \"Arcane\""));
        Assert.Equal("Added to Arcane.", await CommandAsync(gm, ".guild invite Keeper \"Arcane\""));
        (int Id, byte Rank) founder = await host.PlayerStateAsync("Founder", p => GuildOf(p));
        (int Id, byte Rank) keeper = await host.PlayerStateAsync("Keeper", p => GuildOf(p));
        Assert.Equal((founder.Id, (byte)0), founder); // the leader is guild master
        Assert.Equal((founder.Id, (byte)4), keeper); // added members get the lowest of the five default ranks
        await Drain(gm, player);

        await player.SendChatAsync(ChatType.Guild, Language.Common, "welcome");
        ChatMessage heard = await gm.ReadChatAsync();
        Assert.Equal((ChatType.Guild, Language.Universal, 2ul, "welcome"), (heard.Type, heard.Language, heard.Sender, heard.Text));

        await player.SendAsync(WorldOpcode.CmsgGuildRoster, []);
        byte[] roster = await player.ReadUntilAsync(WorldOpcode.SmsgGuildRoster);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(roster));
    }

    [Fact]
    public async Task Channel_JoinNotices_ChannelChat_AndList()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient owner = await host.EnterWorldAsync("OWNER", "Owner");
        await using WorldTestClient guest = await host.EnterWorldAsync("GUEST", "Guest");
        await Drain(owner, guest);

        await owner.SendAsync(WorldOpcode.CmsgJoinChannel, [.. CString("Club"), .. CString(string.Empty)]);
        byte[] youJoined = await ReadNotifyAsync(owner, ChatNotify.YouJoined);
        Assert.Equal(CString("Club"), youJoined.AsSpan(1, 5).ToArray());

        await guest.SendAsync(WorldOpcode.CmsgJoinChannel, [.. CString("club"), .. CString(string.Empty)]);
        byte[] joined = await ReadNotifyAsync(owner, ChatNotify.Joined);
        Assert.Equal([(byte)ChatNotify.Joined, .. CString("Club"), 2, 0, 0, 0, 0, 0, 0, 0], joined);
        await ReadNotifyAsync(guest, ChatNotify.YouJoined);

        await guest.SendChatAsync(ChatType.Channel, Language.Common, "hello club", "Club");
        byte[] message = await owner.ReadUntilAsync(WorldOpcode.SmsgMessagechat);
        Assert.Equal(ChannelPackets.BuildChannelMessage("Club", Language.Common, new(2), "hello club", ChatTag.None), message);

        await guest.SendAsync(WorldOpcode.CmsgChannelList, CString("Club"));
        byte[] list = await guest.ReadUntilAsync(WorldOpcode.SmsgChannelList);
        Assert.Equal(CString("Club").Length + 1 + 4 + (2 * 9), list.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(CString("Club").Length + 1)));
    }

    private static (int Id, byte Rank) GuildOf(ArcaneCore.Game.Entities.Player player)
    {
        ArcaneCore.Game.Guilds.Guild guild = Feature(player).Context.Guilds.GetGuildOf(player)
            ?? throw new InvalidOperationException($"{player.Name} is not in a guild");
        return (guild.Id, guild.Members.Single(m => m.CharacterId == player.Guid.Low).Rank);
    }

    private static SocialFeature Feature(ArcaneCore.Game.Entities.Player player)
        => ((WorldSession)player.Session).Services.GetRequiredService<SocialFeature>();

    private static Task<SocialFeature> FeatureAsync(WorldTestHost host, string name) => host.PlayerStateAsync(name, Feature);

    private static Task<ISocialStore> StoreAsync(WorldTestHost host, string name)
        => host.PlayerStateAsync(name, p => ((WorldSession)p.Session).Services.GetRequiredService<ISocialStore>());

    /// <summary>Read SMSG_GROUP_LIST packets until one lists <paramref name="others"/> other members (u8 type, u8 flags, u32 count).</summary>
    private static async Task<byte[]> ReadGroupListAsync(WorldTestClient client, uint others)
    {
        while (true)
        {
            byte[] list = await client.ReadUntilAsync(WorldOpcode.SmsgGroupList);
            if (list.Length >= 6 && BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(2)) == others)
            {
                return list;
            }
        }
    }

    /// <summary>Read SMSG_CHANNEL_NOTIFY packets until one of <paramref name="type"/> arrives.</summary>
    private static async Task<byte[]> ReadNotifyAsync(WorldTestClient client, ChatNotify type)
    {
        while (true)
        {
            byte[] notify = await client.ReadUntilAsync(WorldOpcode.SmsgChannelNotify);
            if (notify[0] == (byte)type)
            {
                return notify;
            }
        }
    }

    private static async Task<string> CommandAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        ChatMessage reply = await client.ReadChatAsync();
        Assert.Equal(ChatType.System, reply.Type);
        return reply.Text;
    }

    private static async Task Drain(params WorldTestClient[] clients)
    {
        foreach (WorldTestClient client in clients)
        {
            await client.CollectAsync();
        }
    }

    private static byte[] CString(string value)
    {
        var writer = new PacketWriter(value.Length + 1);
        writer.WriteCString(value);
        return writer.ToArray();
    }
}
