using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// Real friend, ignore and guild-invitation packets from the bots' memory (docs/areas/playbots-social.md, Lists and Guild invitations): the
/// thresholds, the one change per bot per minute, never ignoring a game master, the weekly guild invitation per target and the affinity
/// acceptance. The passes are driven by hand on a frozen feature; the asserted facts are the lists and guilds on the server and the packets dispatched.
/// </summary>
public sealed class PlayerbotSocialListsWorldTests
{
    private static readonly PlayerbotSocialListsOptions Defaults = new();

    [Theory]
    [InlineData(50, 5, false, false, false, false, false, PlayerbotListChange.Friend)]
    [InlineData(49, 9, false, false, false, false, false, PlayerbotListChange.None)]
    [InlineData(80, 4, false, false, false, false, false, PlayerbotListChange.None)]
    [InlineData(80, 9, false, false, false, true, false, PlayerbotListChange.None)]
    [InlineData(20, 9, false, false, false, true, false, PlayerbotListChange.None)]
    [InlineData(19, 9, false, false, false, true, false, PlayerbotListChange.Unfriend)]
    [InlineData(-70, 1, false, false, false, false, false, PlayerbotListChange.Ignore)]
    [InlineData(-69, 1, false, false, false, false, false, PlayerbotListChange.None)]
    [InlineData(-90, 1, false, false, true, false, false, PlayerbotListChange.None)]      // a game master is never ignored
    [InlineData(-55, 1, true, true, false, false, false, PlayerbotListChange.Ignore)]     // a rival with a chat strike
    [InlineData(-55, 1, true, false, false, false, false, PlayerbotListChange.None)]      // a rival without one
    [InlineData(-31, 1, false, false, false, false, true, PlayerbotListChange.None)]      // ignored, not yet forgiven
    [InlineData(-29, 1, false, false, false, false, true, PlayerbotListChange.Unignore)]
    [InlineData(-29, 1, true, true, false, false, true, PlayerbotListChange.None)]        // ignored, still a rival with a strike
    public void TheListRules_FollowTheThresholds(int affinity, int familiarity, bool rival, bool strike, bool gm, bool isFriend, bool isIgnored, PlayerbotListChange expected)
        => Assert.Equal(expected, PlayerbotSocialLists.Decide(Defaults, affinity, familiarity, rival, strike, gm, isFriend, isIgnored));

    [Fact]
    public void ARelationToIgnoreWinsOverOneToFriend_TheOrderIsIgnoreUnignoreUnfriendFriend()
        => Assert.True(PlayerbotListChange.Ignore < PlayerbotListChange.Unignore && PlayerbotListChange.Unignore < PlayerbotListChange.Unfriend && PlayerbotListChange.Unfriend < PlayerbotListChange.Friend);

    private static Task RememberAsync(SocialWorld world, string bot, string other, short affinity, int familiarity, PlayerbotRelationFlags flags = PlayerbotRelationFlags.None)
        => world.Host.OnWorldAsync(() =>
        {
            Player self = world.Host.World.FindOnlinePlayer(bot)!, them = world.Host.World.FindOnlinePlayer(other)!;
            PlayerbotSocialMemory memory = PartyTestHost.Feature(world.Host).Social.Memory;
            memory.Load([new PlayerbotRelation(world.Bots[bot], (int)self.Guid.Low, them.Guid.Value, true, affinity, familiarity, 500, flags, memory.NowMs, "", "")]);
        });

    private static Task RunListsAsync(SocialWorld world, PlayerbotSocialLists lists, long nowMs)
        => world.Host.OnWorldAsync(() => lists.Run(SocialTestKit.BotPass(world.Host, nowMs)));

    /// <summary>World thread only.</summary>
    private static bool HasNow(SocialWorld world, string bot, string other, SocialFlags flag)
        => SocialTestKit.Social(world.Host).Context.Friends.Get(world.Host.World.FindOnlinePlayer(bot)!)
            .Has(world.Host.World.FindOnlinePlayer(other)!.Guid.Low, flag);

    private static Task<bool> HasAsync(SocialWorld world, string bot, string other, SocialFlags flag)
        => world.Host.OnWorldAsync(() => HasNow(world, bot, other, flag));

    [Fact]
    public async Task ABotFriendsAndUnfriends_IgnoresAndUnignores_AtTheThresholds_OneChangeAMinute()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options => options.Social.Guilds.Enabled = false, frozen: true);
        await world.AddBotAsync("Listone");
        await world.AddBotAsync("Listtwo");
        var lists = new PlayerbotSocialLists();

        await RememberAsync(world, "Listone", "Listtwo", 55, 6);
        await RunListsAsync(world, lists, 100_000);
        Assert.Equal(1, world.Log.Count("Listone", WorldOpcode.CmsgAddFriend));
        await world.Host.WaitForWorldAsync(() => HasNow(world, "Listone", "Listtwo", SocialFlags.Friend), "the friend entry");

        // Within a minute nothing more happens, however the feeling changes.
        await RememberAsync(world, "Listone", "Listtwo", -90, 6);
        await RunListsAsync(world, lists, 130_000);
        Assert.Equal(0, world.Log.Count("Listone", WorldOpcode.CmsgAddIgnore) + world.Log.Count("Listone", WorldOpcode.CmsgDelFriend));

        await RunListsAsync(world, lists, 161_000);
        Assert.Equal(1, world.Log.Count("Listone", WorldOpcode.CmsgAddIgnore));
        await world.Host.WaitForWorldAsync(() => HasNow(world, "Listone", "Listtwo", SocialFlags.Ignored), "the ignore entry");

        // Forgiven: the ignore is lifted, then the friend entry (still there from before) goes below 20.
        await RememberAsync(world, "Listone", "Listtwo", 5, 6);
        await RunListsAsync(world, lists, 230_000);
        Assert.Equal(1, world.Log.Count("Listone", WorldOpcode.CmsgDelIgnore));
        await world.Host.WaitForWorldAsync(() => !HasNow(world, "Listone", "Listtwo", SocialFlags.Ignored), "the ignore lifted");
        await RunListsAsync(world, lists, 300_000);
        Assert.Equal(1, world.Log.Count("Listone", WorldOpcode.CmsgDelFriend));
    }

    [Fact]
    public async Task TheCursorAdvancesByTheBotsScanned_SoEveryBotIsLookedOverOnceInNOver8Passes()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options => options.Social.Guilds.Enabled = false, frozen: true);
        await world.AddBotAsync("Cursorone");
        var lists = new PlayerbotSocialLists();
        var seen = new Dictionary<Guid, int>();
        lists.OnExamine = id => seen[id] = seen.GetValueOrDefault(id) + 1;
        Guid[] ids = [.. Enumerable.Range(0, 24).Select(_ => Guid.NewGuid())];

        await world.Host.OnWorldAsync(() =>
        {
            PlayerbotSocialBot real = SocialTestKit.Gather(world.Host).Single();
            List<PlayerbotSocialBot> bots = [.. ids.Select(id => real with { Id = id })];
            for (int pass = 0; pass < ids.Length / PlayerbotSocialLists.BotsPerPass; pass++)
                lists.Run(new PlayerbotSocialPass(PartyTestHost.PlayerbotOptionsOf(world.Host), SocialTestKit.Social(world.Host).Context, world.Host.World,
                    world.Host.WorldServices, PartyTestHost.Feature(world.Host).Chat, new HashSet<uint>(), registryLoaded: true, new PlayerbotSocialTimings(),
                    new Random(1), 100_000 + pass, 1000, () => bots) { Recorder = PartyTestHost.Feature(world.Host).Social });
        });

        Assert.Equal(ids.Length, seen.Count);
        Assert.All(ids, id => Assert.Equal(1, seen[id]));
    }

    [Fact]
    public async Task ABotNeverIgnoresAGameMaster()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options => options.Social.Guilds.Enabled = false, frozen: true);
        await world.AddBotAsync("Listgmbot");
        await using WorldTestClient gm = await world.Host.EnterWorldAsync("LISTGM", "Listgm", AccountSecurity.GameMaster);
        var lists = new PlayerbotSocialLists();

        await RememberAsync(world, "Listgmbot", "Listgm", -95, 9);
        await RunListsAsync(world, lists, 100_000);
        await RunListsAsync(world, lists, 200_000);
        Assert.Equal(0, world.Log.Count("Listgmbot", WorldOpcode.CmsgAddIgnore));
        Assert.False(await HasAsync(world, "Listgmbot", "Listgm", SocialFlags.Ignored));
    }

    [Fact]
    public async Task ListsOff_NoPacketsGo()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options =>
        {
            options.Social.Guilds.Enabled = false;
            options.Social.Lists.Enabled = false;
        }, frozen: true);
        await world.AddBotAsync("Listoffa");
        await world.AddBotAsync("Listoffb");
        await RememberAsync(world, "Listoffa", "Listoffb", 90, 9);
        await RunListsAsync(world, new PlayerbotSocialLists(), 100_000);
        Assert.Equal(0, world.Log.Count(WorldOpcode.CmsgAddFriend));
    }

    // --- guild invitations by affinity -------------------------------------------------------------------------------------

    [Fact]
    public async Task AGuildLeaderInvitesALikedGuildlessBot_OnceAWeek_AndTheBotAcceptsByAffinity()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options => options.Social.Guilds.TargetSize = 10, frozen: true);
        foreach (string name in new[] { "Gleader", "Gfriend", "Gcold", "Gfew" }) await world.AddBotAsync(name);
        Guild guild = await world.FoundGuildAsync("Gleader", "Affinity Guild");
        var membership = new PlayerbotGuildMembership(NullLogger.Instance);

        await RememberAsync(world, "Gleader", "Gfriend", 65, 9);
        await RememberAsync(world, "Gfriend", "Gleader", 35, 3);
        await RememberAsync(world, "Gleader", "Gcold", 90, 12);   // liked by the leader, but Gcold does not care for the leader
        await RememberAsync(world, "Gcold", "Gleader", 5, 3);
        await RememberAsync(world, "Gleader", "Gfew", 90, 7);     // not met often enough

        async Task StepAsync(long nowMs, string? invitee)
        {
            await world.Host.OnWorldAsync(() =>
            {
                PlayerbotSocialPass pass = SocialTestKit.BotPass(world.Host, nowMs);
                membership.InviteByAffinity(pass);
            });
            // The invitation reaches the invited bot's capture queue on the next ticks: wait for it (when one was sent), then answer.
            if (invitee is not null)
                await world.Host.WaitForWorldAsync(() =>
                {
                    SocialTestKit.TakeInvites(SocialTestKit.BotPass(world.Host, nowMs), membership);
                    return membership.HasSeenInvite(world.Bots[invitee]);
                }, $"the invitation of {invitee} is noted");
            await world.Host.OnWorldAsync(() =>
            {
                PlayerbotSocialPass pass = SocialTestKit.BotPass(world.Host, nowMs);
                SocialTestKit.TakeInvites(pass, membership);
                membership.AnswerInvites(pass);
            });
        }

        // The leader likes Gcold best (90): it is asked first and declines, because it does not care for the leader (affinity 5 < 30).
        await StepAsync(1_000, "Gcold");
        Assert.Equal(1, world.Log.Count("Gleader", WorldOpcode.CmsgGuildInvite));
        Assert.Equal(["Gcold"], world.Log.Who(WorldOpcode.CmsgGuildDecline));
        Assert.Null(await world.GuildOfAsync("Gcold"));

        // Then Gfriend (65), who likes the leader enough (35 >= 30) and accepts by affinity alone: no founding expected the invitation.
        await StepAsync(1_000_000, "Gfriend");
        Assert.Equal(2, world.Log.Count("Gleader", WorldOpcode.CmsgGuildInvite));
        Assert.Equal(["Gfriend"], world.Log.Who(WorldOpcode.CmsgGuildAccept));
        await world.Host.WaitForWorldAsync(() => guild.MemberCount == 2, "Gfriend joins");
        Assert.Null(await world.GuildOfAsync("Gfew"));

        // A week has not passed: nobody is asked again (Gfew is below the familiarity bar, Gcold was asked).
        await StepAsync(3 * 86_400_000L, null);
        Assert.Equal(2, world.Log.Count("Gleader", WorldOpcode.CmsgGuildInvite));
        // After a week Gcold may be asked again.
        await StepAsync(8 * 86_400_000L, "Gcold");
        Assert.Equal(3, world.Log.Count("Gleader", WorldOpcode.CmsgGuildInvite));
    }

    [Fact]
    public async Task AffinityInvitesOff_NobodyIsInvited_AndABotAcceptsNothingByAffinity()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options => options.Social.Guilds.AffinityInvites = false, frozen: true);
        await world.AddBotAsync("Hleader");
        await world.AddBotAsync("Hfriend");
        await world.FoundGuildAsync("Hleader", "Quiet Guild");
        await RememberAsync(world, "Hleader", "Hfriend", 90, 12);
        await world.Host.OnWorldAsync(() => new PlayerbotGuildMembership(NullLogger.Instance).InviteByAffinity(SocialTestKit.BotPass(world.Host, 1_000)));
        Assert.Equal(0, world.Log.Count("Hleader", WorldOpcode.CmsgGuildInvite));
    }

    /// <summary>"invites off" (S4): a liked, guildless real player who opted out gets no affinity invitation; lifted, the same player is asked at once.</summary>
    [Fact]
    public async Task AnOptedOutPlayer_GetsNoAffinityGuildInvite_UntilTheyWhisperInvitesOn()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options => options.Social.Guilds.TargetSize = 10, frozen: true);
        await using WorldTestClient player = await world.AddPlayerAsync("Optedout");
        await world.AddBotAsync("Oleader");
        await world.FoundGuildAsync("Oleader", "Opt Guild");
        await RememberAsync(world, "Oleader", "Optedout", 90, 12);
        var store = new PlayerbotInviteOptOut();
        var membership = new PlayerbotGuildMembership(NullLogger.Instance) { InviteOptOut = store };
        (ulong guid, int account) = await world.Host.OnWorldAsync(() =>
        {
            Player real = world.Host.World.FindOnlinePlayer("Optedout")!;
            return (real.Guid.Value, real.AccountId);
        });
        var options = new PlayerbotSocialInviteOptions();
        Assert.NotNull(store.Handle(guid, account, "invites off", 0, options).Answer);

        await world.Host.OnWorldAsync(() => membership.InviteByAffinity(SocialTestKit.BotPass(world.Host, 1_000)));
        Assert.Equal(0, world.Log.Count("Oleader", WorldOpcode.CmsgGuildInvite));
        await world.Host.OnWorldAsync(() => membership.InviteByAffinity(SocialTestKit.BotPass(world.Host, 600_000)));
        Assert.Equal(0, world.Log.Count("Oleader", WorldOpcode.CmsgGuildInvite));

        Assert.NotNull(store.Handle(guid, account, "invites on", 700_000, options).Answer);
        await world.Host.OnWorldAsync(() => membership.InviteByAffinity(SocialTestKit.BotPass(world.Host, 1_200_000)));
        Assert.Equal(1, world.Log.Count("Oleader", WorldOpcode.CmsgGuildInvite)); // the control: the same player, asked once the opt-out is lifted
    }

    [Fact]
    public async Task ABotOfficer_InAGuildLedByARealPlayer_InvitesNobodyByAffinity()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options => options.Social.Guilds.TargetSize = 10, frozen: true);
        await using WorldTestClient leader = await world.AddPlayerAsync("Realleader");
        foreach (string name in new[] { "Pofficer", "Pfriend" }) await world.AddBotAsync(name);
        await world.Host.OnWorldAsync(() =>
        {
            uint leaderId = world.Host.World.FindOnlinePlayer("Realleader")!.Guid.Low, officerId = world.Host.World.FindOnlinePlayer("Pofficer")!.Guid.Low;
            Assert.Equal(GuildAdminResult.Ok, world.Guilds.Create(leaderId, "Player Run Guild", out Guild? guild));
            Assert.Equal(GuildAdminResult.Ok, world.Guilds.AdminInvite(officerId, "Player Run Guild"));
            Assert.Equal(GuildAdminResult.Ok, world.Guilds.AdminSetRank(officerId, Guild.OfficerRank));
            Assert.True(guild!.HasRight(Guild.OfficerRank, GuildRights.Invite), "an officer may invite: only the leader's kind keeps it from doing so");
        });
        await RememberAsync(world, "Pofficer", "Pfriend", 90, 12); // liked and known well: a bot-led guild would invite it

        await world.Host.OnWorldAsync(() => new PlayerbotGuildMembership(NullLogger.Instance).InviteByAffinity(SocialTestKit.BotPass(world.Host, 1_000)));
        Assert.Equal(0, world.Log.Count("Pofficer", WorldOpcode.CmsgGuildInvite));
        Assert.Null(await world.GuildOfAsync("Pfriend"));
    }

    [Fact]
    public async Task AnInvitationTheServerRefuses_StillStartsTheTargetsCooldown()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options => options.Social.Guilds.TargetSize = 10, frozen: true);
        await world.AddBotAsync("Rleader");
        await world.AddBotAsync("Rtarget");
        await world.FoundGuildAsync("Rleader", "Refusing Guild");
        await RememberAsync(world, "Rleader", "Rtarget", 90, 12);
        // The target ignores the leader: the server refuses the invitation ("is ignoring you").
        await world.Host.OnWorldAsync(() => SocialTestKit.Social(world.Host).Context.Friends.AddIgnore(world.Host.World.FindOnlinePlayer("Rtarget")!, "Rleader"));
        var membership = new PlayerbotGuildMembership(NullLogger.Instance);

        await world.Host.OnWorldAsync(() => membership.InviteByAffinity(SocialTestKit.BotPass(world.Host, 1_000)));
        Assert.Equal(1, world.Log.Count("Rleader", WorldOpcode.CmsgGuildInvite));
        Assert.Equal(0, await world.Host.OnWorldAsync(() => world.Guilds.InvitedTo(world.Host.World.FindOnlinePlayer("Rtarget")!)));

        // Long after the guild's own spacing, but within the target's week: it is not asked again.
        await world.Host.OnWorldAsync(() => membership.InviteByAffinity(SocialTestKit.BotPass(world.Host, 600_000)));
        Assert.Equal(1, world.Log.Count("Rleader", WorldOpcode.CmsgGuildInvite));
    }

    /// <summary>A real player leading a guild invites a guildless bot; the bot answers by the guild invite policy and its affinity.</summary>
    private static async Task<bool> PlayerInvitesBotAsync(SocialWorld world, WorldTestClient leader, string bot, Guild guild, short affinityToLeader, string leaderName)
    {
        await RememberAsync(world, bot, leaderName, affinityToLeader, 6);
        await leader.SendAsync(WorldOpcode.CmsgGuildInvite, SocialTestKit.CString(bot));
        var membership = new PlayerbotGuildMembership(NullLogger.Instance);
        await world.Host.WaitForWorldAsync(() =>
        {
            SocialTestKit.TakeInvites(SocialTestKit.BotPass(world.Host, 1_000), membership);
            return membership.HasSeenInvite(world.Bots[bot]);
        }, $"the invitation of {bot} is noted");
        await world.Host.OnWorldAsync(() =>
        {
            PlayerbotSocialPass pass = SocialTestKit.BotPass(world.Host, 1_000);
            membership.AnswerInvites(pass);
        });
        return world.Log.Count(bot, WorldOpcode.CmsgGuildAccept) == 1;
    }

    [Theory]
    [InlineData(PlayerbotInvitePolicy.None, false)]          // the operator said nobody: a liked player's invitation is declined
    [InlineData(PlayerbotInvitePolicy.GuildOrFriends, true)] // the default: affinity alone accepts
    public async Task TheGuildInvitePolicyNone_DeclinesEvenALikedPlayer_OtherPoliciesAcceptByAffinity(PlayerbotInvitePolicy policy, bool accepted)
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options => options.Social.Guilds.InvitePolicy = policy, frozen: true);
        await using WorldTestClient leader = await world.AddPlayerAsync("Likedleader");
        await world.AddBotAsync("Policybot");
        Guild guild = await world.Host.OnWorldAsync(() =>
        {
            Assert.Equal(GuildAdminResult.Ok, world.Guilds.Create(world.Host.World.FindOnlinePlayer("Likedleader")!.Guid.Low, "Liked Guild", out Guild? created));
            return created!;
        });

        Assert.Equal(accepted, await PlayerInvitesBotAsync(world, leader, "Policybot", guild, 50, "Likedleader"));
        Assert.Equal(accepted ? 1 : 0, world.Log.Count("Policybot", WorldOpcode.CmsgGuildAccept));
        Assert.Equal(accepted ? 0 : 1, world.Log.Count("Policybot", WorldOpcode.CmsgGuildDecline));
        Assert.Equal(accepted, (await world.GuildOfAsync("Policybot")) is not null);
    }
}
