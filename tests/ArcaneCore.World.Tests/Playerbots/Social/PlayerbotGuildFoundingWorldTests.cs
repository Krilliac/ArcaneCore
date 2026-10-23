using ArcaneCore.Game.Guilds;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// Bots founding and filling guilds in a real world (real handlers and clock; the feature's own quick passes): the founding by
/// CMSG_GUILD_CREATE and by charter (buy, offer, sign, turn in), the caps on guilds and on members, the live switch, the limits of a
/// charter founding, and the master handing out ranks. Every test asserts the guild and charter state the server holds and the client
/// opcodes the bots' managed actions dispatched. Founding is switched on only after the bots are in and watched, through the live option.
/// </summary>
public sealed class PlayerbotGuildFoundingWorldTests
{
    private static readonly string[] SixBots = ["Oakleaf", "Pinecone", "Ashgrove", "Elmwood", "Birchbark", "Cedarwind"];

    private static async Task<SocialWorld> StartAsync(Action<PlayerbotSocialGuildOptions> guilds, bool petitioner = false, int minPetitionSigns = -1)
    {
        SocialWorld world = await SocialWorld.StartAsync(options =>
        {
            options.Social.Guilds.Enabled = false; // switched on once the bots are in
            guilds(options.Social.Guilds);
        }, petitioner: petitioner);
        if (minPetitionSigns >= 0) await world.Host.OnWorldAsync(() => world.Guilds.Options.MinPetitionSigns = minPetitionSigns);
        return world;
    }

    private static Task SwitchOnAsync(SocialWorld world, bool on = true) => world.Host.OnWorldAsync(() => world.Options.Social.Guilds.Enabled = on);

    private static Task<List<Guild>> GuildsAsync(SocialWorld world) => world.Host.OnWorldAsync(() => world.Guilds.All.ToList());

    [Fact]
    public async Task ClientCreate_FoundsAGuildLedByABot_WithAScreenedName_FillsItAndSetsItsMotd()
    {
        await using SocialWorld world = await StartAsync(guilds => { guilds.Creation = PlayerbotGuildCreation.ClientCreate; guilds.TargetSize = 2; });
        await world.AddBotAsync("Oakleaf");
        await world.AddBotAsync("Pinecone");
        IReadOnlySet<uint> botIds = await world.BotCharacterIdsAsync();
        Assert.Equal(0, await world.GuildCountAsync());
        Assert.Equal(0, world.Log.Count(WorldOpcode.CmsgGuildCreate)); // nothing is founded while the option is off

        await SwitchOnAsync(world);
        await world.Host.WaitForWorldAsync(() => world.Guilds.All.Any(guild => guild.MemberCount == 2 && guild.Motd != Guild.DefaultMotd), "a bot guild of two with its motd");

        Guild guild = Assert.Single(await GuildsAsync(world));
        Assert.Contains(guild.LeaderId, botIds);
        Assert.True(CharterNameRules.IsValid(guild.Name, new GuildOptions(), null), guild.Name);
        Assert.All(guild.Members, member => Assert.Contains(member.CharacterId, botIds));
        Assert.Contains(guild.Name, guild.Motd);
        Assert.True(CharterNameRules.CodePointCount(guild.Motd) <= Guild.MaxMotdLength);

        // The packets a client sends, from the right bots: the leader created it and set the motd, the other joined by its own accept.
        string leader = guild.LeaderId == await SocialTestKit.CharacterIdAsync(world.Host, "Oakleaf") ? "Oakleaf" : "Pinecone";
        string other = leader == "Oakleaf" ? "Pinecone" : "Oakleaf";
        Assert.Equal([leader], world.Log.Who(WorldOpcode.CmsgGuildCreate));
        Assert.Equal([leader], world.Log.Who(WorldOpcode.CmsgGuildMotd));
        Assert.Equal([other], world.Log.Who(WorldOpcode.CmsgGuildAccept));
        Assert.True(world.Log.Count(leader, WorldOpcode.CmsgGuildInvite) >= 1);
        Assert.Equal(0, world.Log.Count(WorldOpcode.CmsgPetitionBuy));
        Assert.Equal(0, world.Log.Count(WorldOpcode.CmsgTurnInPetition));

        // Nothing more happens: no second founding, no growth past the two bots there are.
        await SocialTestKit.WaitPassesAsync(world.Host, 10);
        Assert.Equal(1, await world.GuildCountAsync());
        Assert.Equal(1, world.Log.Count(WorldOpcode.CmsgGuildCreate));
    }

    [Fact]
    public async Task MaxBotGuilds_StopsNewFoundings_WhileBotsAreStillFree()
    {
        await using SocialWorld world = await StartAsync(guilds =>
        {
            guilds.Creation = PlayerbotGuildCreation.ClientCreate;
            guilds.MaxBotGuilds = 1;
            guilds.TargetSize = 3;
        });
        foreach (string name in SixBots) await world.AddBotAsync(name);
        IReadOnlySet<uint> botIds = await world.BotCharacterIdsAsync();

        await SwitchOnAsync(world);
        await world.Host.WaitForWorldAsync(() => world.Guilds.All.Any(guild => guild.MemberCount == 3), "the one bot guild filled to its target size");
        await SocialTestKit.WaitPassesAsync(world.Host, 25); // ~6 founding attempts' worth of passes

        Guild guild = Assert.Single(await GuildsAsync(world));
        Assert.Equal(3, guild.MemberCount); // TargetSize holds as well: three bots stay free
        Assert.Equal(1, world.Log.Count(WorldOpcode.CmsgGuildCreate));
        int unguilded = 0;
        foreach (string name in SixBots)
            if (await world.GuildOfAsync(name) is null) unguilded++;
        Assert.Equal(3, unguilded);

        // The rule that counts a bot guild: led by a registered bot (a player's guild, or one led by an unregistered character, does not count).
        Assert.Equal(1, await world.Host.OnWorldAsync(() => PlayerbotGuildMembership.CountBotGuilds(world.Guilds.All, botIds)));
        Assert.Equal(0, await world.Host.OnWorldAsync(() => PlayerbotGuildMembership.CountBotGuilds(world.Guilds.All, new HashSet<uint>())));
    }

    [Fact]
    public async Task WithRoomForTwoGuilds_TheSameBotsFoundTwo_SoTheCapAboveIsTheLimit()
    {
        await using SocialWorld world = await StartAsync(guilds =>
        {
            guilds.Creation = PlayerbotGuildCreation.ClientCreate;
            guilds.MaxBotGuilds = 2;
            guilds.TargetSize = 3;
        });
        foreach (string name in SixBots) await world.AddBotAsync(name);

        await SwitchOnAsync(world);
        await world.Host.WaitForWorldAsync(() => world.Guilds.All.Count() == 2 && world.Guilds.All.All(guild => guild.MemberCount == 3), "two bot guilds of three");
        await SocialTestKit.WaitPassesAsync(world.Host, 15);

        List<Guild> guilds = await GuildsAsync(world);
        Assert.Equal(2, guilds.Count);
        Assert.All(guilds, guild => Assert.Equal(3, guild.MemberCount));
        Assert.Equal(2, world.Log.Count(WorldOpcode.CmsgGuildCreate));
    }

    [Fact]
    public async Task ZeroBotGuilds_FoundsNone()
    {
        await using SocialWorld world = await StartAsync(guilds => { guilds.Creation = PlayerbotGuildCreation.ClientCreate; guilds.MaxBotGuilds = 0; });
        foreach (string name in SixBots.Take(3)) await world.AddBotAsync(name);

        await SwitchOnAsync(world);
        await SocialTestKit.WaitPassesAsync(world.Host, 20);

        Assert.Equal(0, await world.GuildCountAsync());
        Assert.Equal(0, world.Log.Count(WorldOpcode.CmsgGuildCreate));
    }

    [Fact]
    public async Task WhenSwitchedOff_NoGuildForms_AndSwitchingItOnLiveStartsOne()
    {
        await using SocialWorld world = await StartAsync(guilds => guilds.Creation = PlayerbotGuildCreation.ClientCreate);
        foreach (string name in SixBots.Take(3)) await world.AddBotAsync(name);

        await SocialTestKit.WaitPassesAsync(world.Host, 20); // Enabled = false from the start
        Assert.Equal(0, await world.GuildCountAsync());
        Assert.Equal(0, world.Log.Count(WorldOpcode.CmsgGuildCreate));
        Assert.Equal(0, world.Log.Count(WorldOpcode.CmsgGuildInvite));

        await SwitchOnAsync(world); // the control: the same bots, the same passes, only the option differs
        await world.Host.WaitForWorldAsync(() => world.Guilds.All.Any(), "a guild once the option is on");
        Assert.Equal(1, world.Log.Count(WorldOpcode.CmsgGuildCreate));
    }

    [Fact]
    public async Task WhenTheServerRefusesClientCreate_NothingIsFounded_AndAutoFallsBackToTheCharter()
    {
        await using SocialWorld world = await StartAsync(guilds => guilds.Creation = PlayerbotGuildCreation.ClientCreate);
        foreach (string name in SixBots.Take(3)) await world.AddBotAsync(name);
        await world.Host.OnWorldAsync(() => world.Guilds.Options.AllowClientGuildCreate = false);

        await SwitchOnAsync(world);
        await world.Host.WaitForWorldAsync(() => world.Log.Count(WorldOpcode.CmsgGuildCreate) >= 1, "the bot to try CMSG_GUILD_CREATE");
        await SocialTestKit.WaitPassesAsync(world.Host, 10);
        Assert.Equal(0, await world.GuildCountAsync()); // the server ignored it (World:Guild:AllowClientGuildCreate), and the bot founds nothing else
        Assert.Equal(0, world.Log.Count(WorldOpcode.CmsgGuildInvite));

        // Auto reads the same server option: with it off the bots take the charter way and never send CMSG_GUILD_CREATE again.
        await world.Host.OnWorldAsync(() => world.Options.Social.Guilds.Creation = PlayerbotGuildCreation.Auto);
        await SocialTestKit.WaitPassesAsync(world.Host, 3);
        int creates = world.Log.Count(WorldOpcode.CmsgGuildCreate);
        await SocialTestKit.WaitPassesAsync(world.Host, 15);
        Assert.Equal(creates, world.Log.Count(WorldOpcode.CmsgGuildCreate));
        Assert.Equal(0, world.Log.Count(WorldOpcode.CmsgPetitionBuy)); // and with no petitioner in reach, no charter is bought
        Assert.Equal(0, await world.GuildCountAsync());
    }

    [Fact]
    public async Task ACharter_IsBought_Offered_Signed_AndTurnedIn_ByTheBots()
    {
        await using SocialWorld world = await StartAsync(guilds => guilds.Creation = PlayerbotGuildCreation.Charter, petitioner: true, minPetitionSigns: 2);
        string[] names = ["Oakleaf", "Pinecone", "Ashgrove"];
        foreach (string name in names) await world.AddBotAsync(name, money: 5000);
        IReadOnlySet<uint> botIds = await world.BotCharacterIdsAsync();

        await SwitchOnAsync(world);
        await world.Host.WaitForWorldAsync(() => world.Guilds.All.Any(guild => guild.MemberCount == 3 && guild.Motd != Guild.DefaultMotd), "the charter guild with its three members");

        Guild guild = Assert.Single(await GuildsAsync(world));
        Assert.Contains(guild.LeaderId, botIds);
        Assert.All(guild.Members, member => Assert.Contains(member.CharacterId, botIds));
        Assert.True(CharterNameRules.IsValid(guild.Name, new GuildOptions(), null), guild.Name);

        // The founder alone bought (1000 copper), offered to both signers, turned in, and set the motd; each signer signed once.
        string founder = Assert.Single(world.Log.Who(WorldOpcode.CmsgPetitionBuy));
        Assert.Equal(guild.LeaderId, await SocialTestKit.CharacterIdAsync(world.Host, founder));
        Assert.Equal([founder], world.Log.Who(WorldOpcode.CmsgTurnInPetition));
        Assert.Equal([founder], world.Log.Who(WorldOpcode.CmsgGuildMotd));
        Assert.Equal(2, world.Log.Count(WorldOpcode.CmsgPetitionSign));
        Assert.DoesNotContain(founder, world.Log.Who(WorldOpcode.CmsgPetitionSign));
        Assert.True(world.Log.Count(founder, WorldOpcode.CmsgOfferPetition) >= 2);
        Assert.Equal(0, world.Log.Count(WorldOpcode.CmsgGuildCreate));
        Assert.Equal(4000u, await world.Host.PlayerStateAsync(founder, p => p.Money));
        foreach (string name in names.Where(name => name != founder))
            Assert.Equal(5000u, await world.Host.PlayerStateAsync(name, p => p.Money));

        // The charter is used up: no petition is left, and no second founding starts (no bot is free).
        Assert.Empty(await world.Host.OnWorldAsync(() => SocialTestKit.Social(world.Host).Context.Petitions.All.ToList()));
        await SocialTestKit.WaitPassesAsync(world.Host, 10);
        Assert.Equal(1, await world.GuildCountAsync());
        Assert.Equal(1, world.Log.Count(WorldOpcode.CmsgPetitionBuy));
    }

    [Fact]
    public async Task ACharterIsBoughtOnlyWhereAPetitionerIsAlreadyInReach_AndOnlyByABotThatCanPayForIt()
    {
        // No petitioner anywhere: the bots have the money and the signers, and still buy nothing (they are never moved to find one).
        await using (SocialWorld bare = await StartAsync(guilds => guilds.Creation = PlayerbotGuildCreation.Charter, petitioner: false, minPetitionSigns: 2))
        {
            foreach (string name in SixBots.Take(3)) await bare.AddBotAsync(name, money: 5000);
            await SwitchOnAsync(bare);
            await SocialTestKit.WaitPassesAsync(bare.Host, 20);

            Assert.Equal(0, bare.Log.Count(WorldOpcode.CmsgPetitionBuy));
            Assert.Equal(0, bare.Log.Count(WorldOpcode.CmsgGuildCreate));
            Assert.Equal(0, await bare.GuildCountAsync());
            Assert.Empty(await bare.Host.OnWorldAsync(() => SocialTestKit.Social(bare.Host).Context.Petitions.All.ToList()));
        }

        // A petitioner at hand but 999 copper per bot: no purchase. One more copper for one bot and the charter is bought.
        await using SocialWorld poor = await StartAsync(guilds => guilds.Creation = PlayerbotGuildCreation.Charter, petitioner: true, minPetitionSigns: 2);
        foreach (string name in SixBots.Take(3)) await poor.AddBotAsync(name, money: 999);
        await SwitchOnAsync(poor);
        await SocialTestKit.WaitPassesAsync(poor.Host, 20);
        Assert.Equal(0, poor.Log.Count(WorldOpcode.CmsgPetitionBuy));
        Assert.Equal(0, await poor.GuildCountAsync());

        await poor.Host.OnWorldAsync(() => poor.Host.World.FindOnlinePlayer("Pinecone")!.Money = 1000);
        await poor.Host.WaitForWorldAsync(() => poor.Log.Count(WorldOpcode.CmsgPetitionBuy) >= 1, "the one bot that can pay buys the charter");
        Assert.Equal(["Pinecone"], poor.Log.Who(WorldOpcode.CmsgPetitionBuy).Distinct());
    }

    [Fact]
    public async Task TheMaster_PromotesAMemberOneRankAtATime_NeverPastOfficer()
    {
        await using SocialWorld world = await StartAsync(guilds => { guilds.Creation = PlayerbotGuildCreation.ClientCreate; guilds.TargetSize = 2; });
        await world.AddBotAsync("Oakleaf");
        await world.AddBotAsync("Pinecone");
        await SwitchOnAsync(world);
        await world.Host.WaitForWorldAsync(() => world.Guilds.All.Any(guild => guild.MemberCount == 2), "the two-bot guild");
        Guild guild = Assert.Single(await GuildsAsync(world));
        string member = guild.LeaderId == await SocialTestKit.CharacterIdAsync(world.Host, "Oakleaf") ? "Pinecone" : "Oakleaf";
        uint memberId = await SocialTestKit.CharacterIdAsync(world.Host, member);

        // A level 1 Initiate is not promoted (the control: the master has had passes to do it).
        await SocialTestKit.WaitPassesAsync(world.Host, 10);
        Assert.Equal(PlayerbotGuildMembership.RankInitiate, await world.Host.OnWorldAsync(() => guild.Find(memberId)!.Rank));
        Assert.Equal(0, world.Log.Count(WorldOpcode.CmsgGuildPromote));

        // At level 10 the one wanted officer is the member, and the server moves it up one rank per CMSG_GUILD_PROMOTE: 4 -> 3 -> 2 -> 1.
        await world.Host.OnWorldAsync(() => world.Host.World.FindOnlinePlayer(member)!.Level = 10);
        await world.Host.WaitForWorldAsync(() => guild.Find(memberId)!.Rank == Guild.OfficerRank, "the member to reach Officer");
        await SocialTestKit.WaitPassesAsync(world.Host, 15);

        Assert.Equal(Guild.OfficerRank, await world.Host.OnWorldAsync(() => guild.Find(memberId)!.Rank));
        Assert.Equal(Guild.GuildMasterRank, await world.Host.OnWorldAsync(() => guild.Find(guild.LeaderId)!.Rank));
        Assert.Equal(3, world.Log.Count(WorldOpcode.CmsgGuildPromote));
        Assert.All(world.Log.Who(WorldOpcode.CmsgGuildPromote), who => Assert.NotEqual(member, who)); // only the master promotes
    }
}
