using ArcaneCore.Game;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Party;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// How bots answer guild invitations and charter offers in a real world: a real player's by the invite policy and the allowlist, another
/// bot's only when a founding of ours expects it, and with no SMSG_GUILD_INVITE packet in the capture queue (the 128-entry queue dropped
/// it) a decline. The asserted facts are the guild and charter state on the server and the CMSG_GUILD_ACCEPT / _DECLINE / _PETITION_SIGN the
/// bot dispatched.
/// </summary>
public sealed class PlayerbotGuildInviteWorldTests
{
    private const string RealGuild = "Real Guild";

    /// <summary>A world with the bots' own founding off (they still answer invitations), a real player and the guild it founded.</summary>
    private static async Task<(SocialWorld World, WorldTestClient Player, Guild Guild)> StartAsync(
        Action<PlayerbotOptions> configure, bool frozen, params string[] bots)
    {
        SocialWorld world = await SocialWorld.StartAsync(options =>
        {
            options.Social.Guilds.Enabled = false;
            configure(options);
        }, frozen);
        try
        {
            foreach (string name in bots) await world.AddBotAsync(name);
            WorldTestClient player = await world.AddPlayerAsync("Realmaker");
            await player.SendAsync(WorldOpcode.CmsgGuildCreate, SocialTestSupport.CString(RealGuild));
            await world.Host.WaitForWorldAsync(() => world.Guilds.GetByName(RealGuild) is not null, "the player's guild");
            Guild guild = (await world.Host.OnWorldAsync(() => world.Guilds.GetByName(RealGuild)))!;
            return (world, player, guild);
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    /// <summary>The player invites the bot; with the feature frozen, also wait until the server holds the invitation (a live bot answers within a pass).</summary>
    private static async Task InviteAsync(SocialWorld world, WorldTestClient player, string bot, bool held = false)
    {
        await player.SendAsync(WorldOpcode.CmsgGuildInvite, SocialTestSupport.CString(bot));
        if (held) await world.Host.WaitForWorldAsync(() => world.Guilds.InvitedTo(world.Host.World.FindOnlinePlayer(bot)!) != 0, $"{bot} to be invited");
    }

    private static Task AnsweredAsync(SocialWorld world, string bot)
        => WorldTestHost.WaitForAsync(() => world.Log.Count(bot, WorldOpcode.CmsgGuildAccept) + world.Log.Count(bot, WorldOpcode.CmsgGuildDecline) > 0, $"{bot} to answer the invitation");

    [Theory]
    [InlineData(PlayerbotInvitePolicy.Anyone, false, true)]
    [InlineData(PlayerbotInvitePolicy.GuildOrFriends, false, false)]
    [InlineData(PlayerbotInvitePolicy.None, false, false)]
    [InlineData(PlayerbotInvitePolicy.None, true, true)]
    public async Task ARealPlayersInvitation_IsAnsweredByTheInvitePolicy_AndTheAllowlist(PlayerbotInvitePolicy policy, bool allowlisted, bool accepted)
    {
        (SocialWorld world, WorldTestClient player, Guild guild) = await StartAsync(options =>
        {
            options.Social.Guilds.InvitePolicy = policy;
            if (allowlisted) options.Party.Allowlist = ["Realmaker"];
        }, frozen: false, "Oakleaf");
        await using (world)
        {
            await InviteAsync(world, player, "Oakleaf");
            await AnsweredAsync(world, "Oakleaf");

            Assert.Equal(accepted ? 1 : 0, world.Log.Count(WorldOpcode.CmsgGuildAccept));
            Assert.Equal(accepted ? 0 : 1, world.Log.Count(WorldOpcode.CmsgGuildDecline));
            Assert.Equal(accepted ? guild : null, await world.GuildOfAsync("Oakleaf"));
            Assert.Equal(accepted ? 2 : 1, await world.Host.OnWorldAsync(() => guild.MemberCount));
            Assert.Equal(0, await world.Host.OnWorldAsync(() => world.Guilds.InvitedTo(world.Host.World.FindOnlinePlayer("Oakleaf")!))); // answered either way
        }
    }

    [Fact]
    public async Task UnderGuildOrFriends_AFriendOfTheBotIsAccepted_AStrangerIsNot()
    {
        (SocialWorld world, WorldTestClient player, Guild guild) = await StartAsync(
            options => options.Social.Guilds.InvitePolicy = PlayerbotInvitePolicy.GuildOrFriends, frozen: false, "Oakleaf", "Pinecone");
        await using (world)
        {
            // Oakleaf puts the player on its own friend list (the inviter's list would show no consent); Pinecone does not.
            Assert.True(await world.SendAsync("Oakleaf", WorldOpcode.CmsgAddFriend, SocialTestSupport.CString("Realmaker")));
            await world.Host.WaitForWorldAsync(() => PlayerbotGroupInvites.OnBotsFriendList(
                SocialTestSupport.Social(world.Host).Context, world.Host.World.FindOnlinePlayer("Oakleaf")!, world.Host.World.FindOnlinePlayer("Realmaker")!), "the friend entry");

            await InviteAsync(world, player, "Oakleaf");
            await InviteAsync(world, player, "Pinecone");
            await AnsweredAsync(world, "Oakleaf");
            await AnsweredAsync(world, "Pinecone");

            Assert.Equal(guild, await world.GuildOfAsync("Oakleaf"));
            Assert.Null(await world.GuildOfAsync("Pinecone"));
            Assert.Equal(1, world.Log.Count("Oakleaf", WorldOpcode.CmsgGuildAccept));
            Assert.Equal(1, world.Log.Count("Pinecone", WorldOpcode.CmsgGuildDecline));
        }
    }

    [Fact]
    public async Task WithNoPacketInTheCaptureQueue_TheInvitationIsDeclined_EvenUnderAnyone()
    {
        (SocialWorld world, WorldTestClient player, Guild guild) = await StartAsync(
            options => options.Social.Guilds.InvitePolicy = PlayerbotInvitePolicy.Anyone, frozen: true, "Oakleaf", "Pinecone");
        await using (world)
        {
            await InviteAsync(world, player, "Oakleaf", held: true);
            await InviteAsync(world, player, "Pinecone", held: true);

            // SMSG_GUILD_INVITE tells a bot who invited it; the capture queue keeps 128 packets and drops the oldest. Flood Pinecone's.
            bool dropped = await world.Host.OnWorldAsync(() =>
            {
                var session = (ArcaneCore.World.Net.WorldSession)world.Host.World.FindOnlinePlayer("Pinecone")!.Session;
                for (int i = 0; i < 130; i++) session.Send(WorldOpcode.SmsgMessagechat, new byte[8]);
                return session.DrainManagedPackets(WorldOpcode.SmsgGuildInvite).Count == 0;
            });
            Assert.True(dropped, "the flood did not push the invitation out of the queue; the test would prove nothing");

            await world.Host.OnWorldAsync(() =>
            {
                var membership = new PlayerbotGuildMembership(NullLogger.Instance);
                PlayerbotSocialPass pass = SocialTestSupport.BotPass(world.Host, nowMs: 0);
                SocialTestSupport.TakeInvites(pass, membership);
                membership.AnswerInvites(pass);
            });

            // Oakleaf's packet was there: accepted. Pinecone's was not: declined, though the policy accepts anybody.
            Assert.Equal(guild, await world.GuildOfAsync("Oakleaf"));
            Assert.Equal(["Oakleaf"], world.Log.Who(WorldOpcode.CmsgGuildAccept));
            Assert.Null(await world.GuildOfAsync("Pinecone"));
            Assert.Equal(["Pinecone"], world.Log.Who(WorldOpcode.CmsgGuildDecline));
            Assert.Equal(0, await world.Host.OnWorldAsync(() => world.Guilds.InvitedTo(world.Host.World.FindOnlinePlayer("Pinecone")!)));
        }
    }

    [Fact]
    public async Task AnotherBotsInvitation_IsDeclinedUnlessAFoundingOfOursExpectsIt()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(
            options => options.Social.Guilds.InvitePolicy = PlayerbotInvitePolicy.Anyone, frozen: true);
        foreach (string name in new[] { "Oakleaf", "Pinecone", "Ashgrove" }) await world.AddBotAsync(name);
        Guild guild = await world.FoundGuildAsync("Oakleaf", "Bot Guild");

        // Oakleaf invites Pinecone by hand: the packet is there, the policy says Anyone, yet no founding expected it.
        Assert.True(await world.SendAsync("Oakleaf", WorldOpcode.CmsgGuildInvite, SocialTestSupport.CString("Pinecone")));
        await world.Host.OnWorldAsync(() =>
        {
            var membership = new PlayerbotGuildMembership(NullLogger.Instance);
            PlayerbotSocialPass pass = SocialTestSupport.BotPass(world.Host, nowMs: 0);
            SocialTestSupport.TakeInvites(pass, membership);
            membership.AnswerInvites(pass);
        });
        Assert.Equal(["Pinecone"], world.Log.Who(WorldOpcode.CmsgGuildDecline));
        Assert.Null(await world.GuildOfAsync("Pinecone"));
        Assert.Equal(1, await world.Host.OnWorldAsync(() => guild.MemberCount));

        // The control: the guild's own recruiting (an invitation it expects) is accepted by the same bot, under the same policy.
        (int pending, string[] joined) = await world.Host.OnWorldAsync(() =>
        {
            world.Options.Social.Guilds.Enabled = true;
            var membership = new PlayerbotGuildMembership(NullLogger.Instance);
            PlayerbotSocialPass pass = SocialTestSupport.BotPass(world.Host, nowMs: 0);
            membership.Recruit(pass, new PlayerbotGuildFounding(NullLogger.Instance));
            int expected = membership.PendingCount;
            membership.AnswerInvites(pass);
            return (expected, guild.Members.Select(member => pass.Social.Characters.Find(member.CharacterId)!.Name).ToArray());
        });
        Assert.Equal(1, pending);
        Assert.Equal(2, joined.Length);
        Assert.Single(world.Log.Who(WorldOpcode.CmsgGuildAccept));
        Assert.Equal(["Oakleaf", "Oakleaf"], world.Log.Who(WorldOpcode.CmsgGuildInvite)); // the hand-made invitation and the recruit, both from the master
    }

    [Theory]
    [InlineData(PlayerbotInvitePolicy.Anyone, false, true)]
    [InlineData(PlayerbotInvitePolicy.None, false, false)]
    [InlineData(PlayerbotInvitePolicy.None, true, true)]
    public async Task ARealPlayersCharter_IsSignedByThePolicy_AndNoOtherCharterIs(PlayerbotInvitePolicy policy, bool allowlisted, bool signed)
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options =>
        {
            options.Social.Guilds.Enabled = false;
            options.Social.Guilds.InvitePolicy = policy;
            if (allowlisted) options.Party.Allowlist = ["Realmaker"];
        }, petitioner: true);
        await world.AddBotAsync("Oakleaf");
        WorldTestClient player = await world.AddPlayerAsync("Realmaker");
        var npc = ObjectGuid.WithEntry(HighGuid.Unit, 900030, 900031);
        await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer("Realmaker")!.VisibleObjects.Contains(npc), "the petitioner to become visible");
        await world.Host.OnWorldAsync(() => world.Host.World.FindOnlinePlayer("Realmaker")!.Money = 10_000);

        await player.SendAsync(WorldOpcode.CmsgPetitionBuy, PlayerbotSocialPackets.PetitionBuy(npc, "Open Doors"));
        await world.Host.WaitForWorldAsync(
            () => SocialTestSupport.Social(world.Host).Context.Petitions.GetByOwner(world.Host.World.FindOnlinePlayer("Realmaker")!.Guid.Low) is not null, "the charter");
        ObjectGuid charter = await world.Host.PlayerStateAsync("Realmaker", p => p.Inventory.AllItems.Single(item => item.Entry == SocialTestSupport.CharterItem).Guid);
        ObjectGuid bot = await world.Host.PlayerStateAsync("Oakleaf", p => p.Guid);
        await player.SendAsync(WorldOpcode.CmsgOfferPetition, PlayerbotSocialPackets.PetitionOffer(charter, bot));

        if (signed)
        {
            await world.Host.WaitForWorldAsync(() => SocialTestSupport.Social(world.Host).Context.Petitions
                .GetByOwner(world.Host.World.FindOnlinePlayer("Realmaker")!.Guid.Low)!.ForPlayer(bot.Low) is not null, "the bot to sign");
        }
        else
        {
            await SocialTestSupport.WaitPassesAsync(world.Host, 20);
        }

        Assert.Equal(signed ? 1 : 0, world.Log.Count("Oakleaf", WorldOpcode.CmsgPetitionSign));
        Assert.Equal(signed, await world.Host.OnWorldAsync(() => SocialTestSupport.Social(world.Host).Context.Petitions
            .GetByOwner(world.Host.World.FindOnlinePlayer("Realmaker")!.Guid.Low)!.ForPlayer(bot.Low) is not null));
    }
}
