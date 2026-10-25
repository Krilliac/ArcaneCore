using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Tests.Playerbots.Groups;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.WorldBossRealRows;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// Kill talk for world bosses on a continent (p7b design 4.6, review item 8): <c>PlayerbotGuildChat.OnUnitKilled</c> and
/// <c>PlayerbotConversations.OnUnitKilled</c> used to return for every map that is not a dungeon, so no open-world boss kill was ever remarked on.
/// They now talk for the entries of the world-boss catalogue snapshot (<see cref="PlayerbotWorldBossCatalogue.IsWorldBossEntry"/>) and not for
/// rank 3 as such: the faction leaders and Archbishop Benedictus are rank 3 on the continents and must stay silent. The catalogue is built from the
/// real world database rows (<see cref="WorldBossRealRows"/>); the creatures are spawned from the real Azuregos, Bolvar and Benedictus templates.
/// The zone name needs the terrain's area table, which the test world does not have, so the remark names the map (the fallback of the design).
/// </summary>
public sealed class PlayerbotWorldBossKillTalkTests
{
    private const string GuildName = "Quiet Owls";

    private static readonly PlayerbotWorldBossCatalogue Catalogue = PlayerbotWorldBossCatalogue.Build(new PlayerbotWorldBossOptions(), Content(), Factions(), new HashSet<uint>());

    private sealed class Rig(SocialWorld world, Guild guild, PlayerbotGuildChat chat, PlayerbotConversations conversations) : IAsyncDisposable
    {
        public SocialWorld World { get; } = world;

        public Guild Guild { get; } = guild;

        public PlayerbotGuildChat Chat { get; } = chat;

        public PlayerbotConversations Conversations { get; } = conversations;

        public PlayerbotSocialTimings Timings { get; } = new() { SpeakerGapMs = 0, IdleChatter = false };

        public static async Task<Rig> StartAsync(bool catalogue = true)
        {
            SocialWorld world = await SocialWorld.StartAsync(frozen: true);
            try
            {
                await world.AddBotAsync("Quilla");
                await world.AddBotAsync("Quillb");
                Guild guild = await world.FoundGuildAsync("Quilla", GuildName);
                var chat = new PlayerbotGuildChat(NullLogger.Instance);
                var conversations = new PlayerbotConversations();
                if (catalogue)
                {
                    chat.IsWorldBossEntry = Catalogue.IsWorldBossEntry;
                    conversations.IsWorldBossEntry = Catalogue.IsWorldBossEntry;
                }

                return new Rig(world, guild, chat, conversations);
            }
            catch
            {
                await world.DisposeAsync();
                throw;
            }
        }

        /// <summary>World thread: <paramref name="killer"/> kills a fresh creature of <paramref name="template"/> beside it, and both talk paths see the death.</summary>
        public Task<(int GuildTopics, IReadOnlyList<PlayerbotGuildTopic> Queued, int Noticed, string? NoticedWhere)> KillAsync(string killer, CreatureTemplate template) => World.Host.OnWorldAsync(() =>
        {
            Player player = World.Host.World.FindOnlinePlayer(killer)!;
            var victim = new Creature(template.Entry, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
            PlayerbotSocialPass pass = SocialTestKit.BotPass(World.Host, 1_000, Timings);
            Chat.OnUnitKilled(pass, player.Map!, player, victim);
            Conversations.OnUnitKilled(player.Map!, player, victim);
            IReadOnlyList<PlayerbotGuildTopic> queued = Chat.Queued(Guild.Id);
            return (Chat.Waiting(Guild.Id, "guild.boss"), queued, Conversations.Noticed.Count, Conversations.Noticed.FirstOrDefault().Where);
        });

        public ValueTask DisposeAsync() => World.DisposeAsync();
    }

    [Fact]
    public async Task ACatalogueBossKilledOnAContinent_TalksInTheKillersGuild_NamingTheBossAndThePlace()
    {
        await using Rig rig = await Rig.StartAsync();
        var result = await rig.KillAsync("Quilla", Of(Azuregos).Template);

        Assert.Equal(1, result.GuildTopics);
        PlayerbotGuildTopic topic = Assert.Single(result.Queued, t => t.Key == "guild.boss");
        Assert.Equal("Azuregos", topic.Values["boss"]);
        string place = topic.Values["instance"];
        Assert.False(string.IsNullOrWhiteSpace(place));
        Assert.NotEqual("Azuregos", place);
        Assert.Contains(place, topic.Sentence, StringComparison.Ordinal);
        Assert.Equal(GuildName, topic.Values["guild"]);
    }

    [Fact]
    public async Task TheConversationNews_ForTheSameKill_NamesTheBossAndThePlace()
    {
        await using Rig rig = await Rig.StartAsync();
        var result = await rig.KillAsync("Quilla", Of(Azuregos).Template);

        Assert.Equal(1, result.Noticed);
        var notice = rig.Conversations.Noticed.Single();
        Assert.Equal(BotChatEventKind.BossKill, notice.Kind);
        Assert.Equal("Azuregos", notice.Value);
        Assert.False(string.IsNullOrWhiteSpace(notice.Where));
    }

    /// <summary>The faction leader and Benedictus are rank 3 with a continent spawn in the world database: rank 3 alone must not make a boss.</summary>
    [Theory]
    [InlineData(1748u)]   // Highlord Bolvar Fordragon
    [InlineData(1284u)]   // Archbishop Benedictus
    [InlineData(4949u)]   // Thrall
    [InlineData(2425u)]   // Varimathras
    public async Task ARankThreeContinentCreatureOutsideTheCatalogue_IsNotTalkedAbout(uint entry)
    {
        await using Rig rig = await Rig.StartAsync();
        Row row = Of(entry);
        Assert.Equal(3u, row.Rank);
        var result = await rig.KillAsync("Quilla", row.Template);

        Assert.Equal(0, result.GuildTopics);
        Assert.Equal(0, result.Noticed);
    }

    [Fact]
    public async Task AnOrdinaryCreature_IsNotTalkedAbout()
    {
        await using Rig rig = await Rig.StartAsync();
        var result = await rig.KillAsync("Quilla", new CreatureTemplate { Entry = 990860, Name = "Test Boar", Faction = 14, MinLevel = 5, MaxLevel = 5, Rank = 0 });
        Assert.Equal(0, result.GuildTopics);
        Assert.Equal(0, result.Noticed);
    }

    [Fact]
    public async Task ABossKilledByABotWithNoGuild_QueuesNoGuildTopic()
    {
        await using Rig rig = await Rig.StartAsync();
        var result = await rig.KillAsync("Quillb", Of(Azuregos).Template); // Quillb never joined the guild
        Assert.Equal(0, result.GuildTopics);
    }

    [Fact]
    public async Task AZoneLookupThatFailsOnAHostWithoutAreaData_IsRecordedUnderItsFaultSite_AndNamesNoZone()
    {
        await using Rig rig = await Rig.StartAsync();
        PlayerbotSocialFeature feature = SocialTestKit.Feature(rig.World.Host);
        feature.ZoneLookup = (_, _) => throw new InvalidOperationException("no area data");
        string? zone = await rig.World.Host.OnWorldAsync(() =>
        {
            Player player = rig.World.Host.World.FindOnlinePlayer("Quilla")!;
            CreatureTemplate template = Of(Azuregos).Template;
            var boss = new Creature(template.Entry, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
            return feature.ZoneNameOf(player.Map!, boss);
        });

        Assert.Null(zone);
        Assert.Contains(FaultLedger.Shared.Snapshot(), f => f.Site == "playerbot.social.bosskill");
    }

    [Fact]
    public async Task WithNoCatalogueWired_ContinentKillsStaySilent_AsBefore()
    {
        await using Rig rig = await Rig.StartAsync(catalogue: false);
        var result = await rig.KillAsync("Quilla", Of(Azuregos).Template);
        Assert.Equal(0, result.GuildTopics);
        Assert.Equal(0, result.Noticed);
    }
}
