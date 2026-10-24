using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// H1 hardening: <c>bot-social/guild-action-failed</c>, reported when a bot's guild packet (create, charter buy / turn-in / sign, invite, accept,
/// promote) did not have the effect the bot read back from the server. Real handlers throughout; the server is made to refuse by its own switches
/// (<c>AllowClientGuildCreate</c>, a master rank stripped of its rights, a charter stall timer set short). The ledger is process-wide, so every
/// capture filters on the subjects of the bots of its own test (names unique to this class).
/// </summary>
public sealed class PlayerbotGuildAnomalyTests
{
    private static readonly string GuildFailed = PlayerbotAnomalies.Key("social", "guild-action-failed");

    private static AnomalyCapture CaptureOf(params string[] bots)
        => AnomalyLedger.Shared.Capture(n => n.Key == GuildFailed && bots.Any(b => n.Subject.Contains($"bot {b} ", StringComparison.Ordinal)));

    private static Task<SocialWorld> StartAsync(Action<PlayerbotSocialGuildOptions> guilds, bool petitioner = false, Action<PlayerbotSocialTimings>? tune = null)
        => SocialWorld.StartAsync(options =>
        {
            options.Social.Guilds.Enabled = false; // switched on once the bots are in
            guilds(options.Social.Guilds);
        }, petitioner: petitioner, tune: tune);

    private static Task SwitchAsync(SocialWorld world, bool on) => world.Host.OnWorldAsync(() => world.Options.Social.Guilds.Enabled = on);

    [Fact]
    public async Task CreationRefusedByTheServer_Fires_OncePerCreatePacketTheBotSent()
    {
        string[] bots = ["Quillfeather", "Mossbrook", "Thistledown"];
        await using SocialWorld world = await StartAsync(guilds => guilds.Creation = PlayerbotGuildCreation.ClientCreate);
        foreach (string name in bots) await world.AddBotAsync(name);
        await world.Host.OnWorldAsync(() => world.Guilds.Options.AllowClientGuildCreate = false);
        using AnomalyCapture capture = CaptureOf(bots);

        await SwitchAsync(world, true);
        await world.Host.WaitForWorldAsync(() => world.Log.Count(WorldOpcode.CmsgGuildCreate) >= 1, "the bot to try CMSG_GUILD_CREATE");
        await SwitchAsync(world, false);
        await SocialTestKit.WaitPassesAsync(world.Host, 5);

        int creates = world.Log.Count(WorldOpcode.CmsgGuildCreate);
        Assert.Equal(0, await world.GuildCountAsync());
        Assert.Equal(creates, capture.Count(GuildFailed));
        AnomalyNotice notice = capture.Notices.First(n => n.Key == GuildFailed);
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.Contains("guild creation for '", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("failed", notice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FoundingRecruitingAndPromoting_AllSucceed_AndReportNothing()
    {
        string[] bots = ["Duskhollow", "Larkspur"];
        await using SocialWorld world = await StartAsync(guilds => { guilds.Creation = PlayerbotGuildCreation.ClientCreate; guilds.TargetSize = 2; });
        foreach (string name in bots) await world.AddBotAsync(name);
        using AnomalyCapture capture = CaptureOf(bots);

        await SwitchAsync(world, true);
        await world.Host.WaitForWorldAsync(() => world.Guilds.All.Any(guild => guild.MemberCount == 2 && guild.Motd != Guild.DefaultMotd), "the two-bot guild");
        Guild guild = Assert.Single(await world.Host.OnWorldAsync(() => world.Guilds.All.ToList()));
        string member = guild.LeaderId == await SocialTestKit.CharacterIdAsync(world.Host, bots[0]) ? bots[1] : bots[0];
        uint memberId = await SocialTestKit.CharacterIdAsync(world.Host, member);
        await world.Host.OnWorldAsync(() => world.Host.World.FindOnlinePlayer(member)!.Level = 10);
        await world.Host.WaitForWorldAsync(() => guild.Find(memberId)!.Rank == Guild.OfficerRank, "the member to reach Officer");
        await SocialTestKit.WaitPassesAsync(world.Host, 10);

        Assert.True(world.Log.Count(WorldOpcode.CmsgGuildCreate) == 1 && world.Log.Count(WorldOpcode.CmsgGuildPromote) == 3);
        Assert.False(capture.Any(GuildFailed), capture.Describe());
    }

    [Fact]
    public async Task PromotionTheServerRefuses_Fires_OncePerPromotePacket()
    {
        string[] bots = ["Hazelwick", "Rowanberry"];
        await using SocialWorld world = await StartAsync(guilds => { guilds.Creation = PlayerbotGuildCreation.ClientCreate; guilds.TargetSize = 2; });
        foreach (string name in bots) await world.AddBotAsync(name);
        using AnomalyCapture capture = CaptureOf(bots);

        await SwitchAsync(world, true);
        await world.Host.WaitForWorldAsync(() => world.Guilds.All.Any(guild => guild.MemberCount == 2), "the two-bot guild");
        Guild guild = Assert.Single(await world.Host.OnWorldAsync(() => world.Guilds.All.ToList()));
        string member = guild.LeaderId == await SocialTestKit.CharacterIdAsync(world.Host, bots[0]) ? bots[1] : bots[0];
        uint memberId = await SocialTestKit.CharacterIdAsync(world.Host, member);

        // The master loses the right to promote (the Promote handler reads the rank's rights), so each CMSG_GUILD_PROMOTE is refused.
        await world.Host.OnWorldAsync(() => guild.Ranks[Guild.GuildMasterRank].Rights = GuildRights.Empty);
        await world.Host.OnWorldAsync(() => world.Host.World.FindOnlinePlayer(member)!.Level = 10);
        await world.Host.WaitForWorldAsync(() => world.Log.Count(WorldOpcode.CmsgGuildPromote) >= 1, "the master to try a promotion");
        // Switching guilds off does not stop promotions (the guilds that exist go on), so stop them for a reason the bot honours: the member drops below the promotion level.
        // Then both counts are read on the world thread, where no pass can sit between the packet being logged and the anomaly being reported.
        await SwitchAsync(world, false);
        await world.Host.OnWorldAsync(() => world.Host.World.FindOnlinePlayer(member)!.Level = 1);
        await SocialTestKit.WaitPassesAsync(world.Host, 5); // a promotion chosen before the reset has been logged by now
        int settled = await world.Host.OnWorldAsync(() => world.Log.Count(WorldOpcode.CmsgGuildPromote));
        await SocialTestKit.WaitPassesAsync(world.Host, 5);
        (int promotes, int reported) = await world.Host.OnWorldAsync(() => (world.Log.Count(WorldOpcode.CmsgGuildPromote), capture.Count(GuildFailed)));

        Assert.Equal(PlayerbotGuildMembership.RankInitiate, await world.Host.OnWorldAsync(() => guild.Find(memberId)!.Rank));
        Assert.Equal(settled, promotes);
        Assert.True(promotes >= 1);
        Assert.Equal(promotes, reported);
        AnomalyNotice notice = capture.Notices.First(n => n.Key == GuildFailed);
        Assert.Contains($"guild promotion of {member}", notice.Detail, StringComparison.Ordinal);
        Assert.Contains($"before={PlayerbotGuildMembership.RankInitiate}", notice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecruitInviteTheServerRefuses_Fires_OncePerInvitePacket()
    {
        string[] bots = ["Fernglade", "Willowmere"];
        await using SocialWorld world = await StartAsync(guilds => { guilds.Creation = PlayerbotGuildCreation.ClientCreate; guilds.TargetSize = 2; });
        foreach (string name in bots) await world.AddBotAsync(name);
        Guild guild = await world.FoundGuildAsync(bots[0], "Anomaly Rangers");
        // The only candidate ignores the leader: the Invite handler refuses (IgnoringYouS) although the bot sees nothing wrong beforehand.
        await world.Host.OnWorldAsync(() => SocialTestKit.Social(world.Host).Context.Friends.AddIgnore(world.Host.World.FindOnlinePlayer(bots[1])!, bots[0]));
        using AnomalyCapture capture = CaptureOf(bots);

        await SwitchAsync(world, true);
        await world.Host.WaitForWorldAsync(() => world.Log.Count(bots[0], WorldOpcode.CmsgGuildInvite) >= 1, "the leader to try a recruit invite");
        await SwitchAsync(world, false);
        await SocialTestKit.WaitPassesAsync(world.Host, 5);

        Assert.Equal(1, guild.MemberCount);
        Assert.True(capture.Count(GuildFailed) >= 1);
        Assert.Equal(world.Log.Count(bots[0], WorldOpcode.CmsgGuildInvite), capture.Count(GuildFailed));
        Assert.Contains($"guild recruitment invite from {bots[0]} to {bots[1]}", capture.Notices.First(n => n.Key == GuildFailed).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CharterThatNeverCompletes_Fires_WhenItStalls_AndASignedCharterIsSilent()
    {
        // Stall: the stall timer is 1 ms, so the charter is abandoned on the first pass after it starts, before the signatures are in.
        string[] stuck = ["Brackenfell", "Ivyreach", "Sedgewood"];
        await using (SocialWorld world = await StartAsync(guilds => guilds.Creation = PlayerbotGuildCreation.Charter, petitioner: true,
            tune: timings => timings.CharterStallMs = 1))
        {
            foreach (string name in stuck) await world.AddBotAsync(name, money: 5000);
            await world.Host.OnWorldAsync(() => world.Guilds.Options.MinPetitionSigns = 2);
            using AnomalyCapture capture = CaptureOf(stuck);

            await SwitchAsync(world, true);
            await world.Host.WaitForWorldAsync(() => capture.Any(GuildFailed), "the stalled charter to be reported");
            await SwitchAsync(world, false);

            AnomalyNotice notice = capture.Notices.First(n => n.Key == GuildFailed);
            Assert.Contains("charter founding for '", notice.Detail, StringComparison.Ordinal);
            Assert.Contains("stalled after", notice.Detail, StringComparison.Ordinal);
            Assert.Equal(0, await world.GuildCountAsync());
        }

        // Silent: the charter is bought, signed and turned in inside the default stall window.
        string[] fine = ["Cinderpath", "Marshlight", "Wrenfield"];
        await using SocialWorld healthy = await StartAsync(guilds => guilds.Creation = PlayerbotGuildCreation.Charter, petitioner: true);
        foreach (string name in fine) await healthy.AddBotAsync(name, money: 5000);
        await healthy.Host.OnWorldAsync(() => healthy.Guilds.Options.MinPetitionSigns = 2);
        using AnomalyCapture quiet = CaptureOf(fine);
        await SwitchAsync(healthy, true);
        await healthy.Host.WaitForWorldAsync(() => healthy.Guilds.All.Any(guild => guild.MemberCount == 3), "the charter guild with its three members");
        await SocialTestKit.WaitPassesAsync(healthy.Host, 5);
        Assert.False(quiet.Any(GuildFailed), quiet.Describe());
    }

    [Fact]
    public async Task InviteAcceptanceTheServerRefuses_Fires_AndAnAcceptedInviteIsSilent()
    {
        string[] bots = ["Orcannoy", "Humannoy"];
        await using SocialWorld world = await SocialWorld.StartAsync(options =>
        {
            options.Social.Guilds.Enabled = false;
            options.Social.Guilds.InvitePolicy = PlayerbotInvitePolicy.Anyone;
        }, frozen: true);
        await world.AddBotAsync(bots[0], race: 2); // an Orc: the other side
        await world.AddBotAsync(bots[1]);
        WorldTestClient player = await world.AddPlayerAsync("Annoymaker");
        await world.Host.OnWorldAsync(() => SocialTestKit.Social(world.Host).Context.Options.AllowTwoSideGuild = true);
        await player.SendAsync(WorldOpcode.CmsgGuildCreate, SocialTestKit.CString("Anomaly Mixed"));
        await world.Host.WaitForWorldAsync(() => world.Guilds.GetByName("Anomaly Mixed") is not null, "the player's guild");
        using AnomalyCapture capture = CaptureOf(bots);

        foreach (string bot in bots)
        {
            await player.SendAsync(WorldOpcode.CmsgGuildInvite, SocialTestKit.CString(bot));
            await world.Host.WaitForWorldAsync(() => world.Guilds.InvitedTo(world.Host.World.FindOnlinePlayer(bot)!) != 0, $"{bot} to be invited");
        }

        // Between the invitation and the answer the server stops allowing two-side guilds: the Orc's accept is refused, the human's is not.
        await world.Host.OnWorldAsync(() =>
        {
            SocialTestKit.Social(world.Host).Context.Options.AllowTwoSideGuild = false;
            var membership = new PlayerbotGuildMembership(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            PlayerbotSocialPass pass = SocialTestKit.BotPass(world.Host, nowMs: 0);
            SocialTestKit.TakeInvites(pass, membership);
            membership.AnswerInvites(pass);
        });

        Assert.Equal(1, world.Log.Count(bots[0], WorldOpcode.CmsgGuildAccept));
        Assert.Null(await world.GuildOfAsync(bots[0]));
        Assert.NotNull(await world.GuildOfAsync(bots[1]));
        Assert.Equal(1, capture.Count(GuildFailed));
        AnomalyNotice notice = capture.Notices.Single();
        Assert.Contains("bot Orcannoy ", notice.Subject, StringComparison.Ordinal);
        Assert.Contains("guild invite acceptance for guild", notice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedGuildInvitePacket_AndSocialPassFault_RecordFaultAndAnomaly()
    {
        static long SiteCount() => FaultLedger.Shared.Snapshot()
            .Where(s => s.Site == "playerbot.social")
            .Sum(s => s.Count);

        long beforePacket = SiteCount();
        using (AnomalyCapture packetCapture = AnomalyLedger.Shared.Capture(
            n => n.Key == "bot-social/packet-decode-failed" && n.Subject == "guild-invite"))
        {
            Assert.False(PlayerbotSocialPackets.TryReadGuildInvite(
                Enumerable.Repeat((byte)0x41, PacketReader.MaxCStringBytes + 1).ToArray(),
                out _,
                out _));
            Assert.True(SiteCount() > beforePacket);
            Assert.True(packetCapture.Count("bot-social/packet-decode-failed") >= 1);
        }

        await using SocialWorld world = await StartAsync(guilds => guilds.Creation = PlayerbotGuildCreation.ClientCreate);
        await world.AddBotAsync("Faultbot");
        await world.AddBotAsync("Faultally");
        long beforeFeature = SiteCount();
        using AnomalyCapture featureCapture = PlayerbotAnomalies.CaptureIn(
            world.Host.World,
            n => n.Key == "bot-social/feature-fault");

        await world.Host.OnWorldAsync(() =>
        {
            SocialTestKit.Feature(world.Host).Random = null!;
            world.Options.Social.Guilds.Enabled = true;
        });

        await WorldTestHost.WaitForAsync(
            () => featureCapture.Count("bot-social/feature-fault") >= 1,
            "social feature fault anomaly");
        Assert.True(SiteCount() > beforeFeature);
        AnomalyNotice notice = featureCapture.Notices.First();
        Assert.Equal("social", notice.Subject);
        Assert.Equal(world.Host.World.Identity, notice.Origin);
    }
}
