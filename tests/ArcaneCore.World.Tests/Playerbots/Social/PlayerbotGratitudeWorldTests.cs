using ArcaneCore.Kernel.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.BotSocial;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>Gratitude (P2 section 3): the pure rules, the thank emote (across factions, once per fight) and the tip through a real trade.</summary>
public sealed class PlayerbotGratitudeWorldTests
{
    private static Task PassAsync(SocialWorld world, long nowMs)
        => world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Gratitude.Run(SocialTestKit.BotPass(world.Host, nowMs), SocialTestKit.Feature(world.Host).Help));

    [Fact]
    public void TheRules_CapTheTip_GateTheFavour_AndAreDeterministic()
    {
        Assert.Equal(0u, PlayerbotGratitude.TipCopper(1_000_000, 60, 0));
        Assert.Equal(10_000u, PlayerbotGratitude.TipCopper(100_000_000, 60, 10_000)); // 1% would be 1_000_000: capped at 1 gold
        Assert.Equal(5_000u, PlayerbotGratitude.TipCopper(100_000_000, 30, 10_000)); // half the level, half the cap
        Assert.Equal(50u, PlayerbotGratitude.TipCopper(5_000, 60, 10_000)); // 1% of its gold
        Assert.Equal(0u, PlayerbotGratitude.TipCopper(99, 60, 10_000));
        Assert.False(PlayerbotGratitude.Generous(0.59f, 100));
        Assert.False(PlayerbotGratitude.Generous(0.9f, 29));
        Assert.True(PlayerbotGratitude.Generous(0.6f, 30));
        Assert.Equal(PlayerbotFavour.None, PlayerbotGratitude.FavourOf(1, 2, 3, 0.5f, 100, 1f, true, true, true));
        for (uint fight = 0; fight < 50; fight++)
        {
            Assert.Equal(PlayerbotFavour.Tip, PlayerbotGratitude.FavourOf(1, 2, fight, 1f, 100, 1f, true, true, true));
            Assert.Equal(PlayerbotFavour.None, PlayerbotGratitude.FavourOf(1, 2, fight, 1f, 100, 1f, false, false, false));
            Assert.Equal(PlayerbotGratitude.FavourOf(1, 2, fight, 0.8f, 50, 0f, true, true, true), PlayerbotGratitude.FavourOf(1, 2, fight, 0.8f, 50, 0f, true, true, true));
        }
    }

    [Fact]
    public async Task AHelpedBot_ThanksWithAnEmote_EvenAcrossFactions_OncePerFight()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await using WorldTestClient horde = await world.AddPlayerAsync("Thankhorde", SocialTestKit.Orc);
        await world.AddBotAsync("Thankbot");
        await HelpTestKit.StandAsync(world, "Thankbot", ("Thankhorde", 5f));
        await world.Host.OnWorldAsync(() => HelpTestKit.RecorderOf(world).NoteHelp(HelpTestKit.Online(world, "Thankhorde"), HelpTestKit.Online(world, "Thankbot")));

        await PassAsync(world, 10_000);
        await world.Host.WaitForWorldAsync(() => world.Log.Count("Thankbot", WorldOpcode.CmsgTextEmote) == 1, "the bot's thank emote");
        Assert.Equal(1, SocialTestKit.Feature(world.Host).Gratitude.Emotes);

        // The same fight again (the pair is credited once), and a second report inside the window: no second thanks.
        await world.Host.OnWorldAsync(() => HelpTestKit.RecorderOf(world).NoteHelp(HelpTestKit.Online(world, "Thankhorde"), HelpTestKit.Online(world, "Thankbot")));
        await PassAsync(world, 20_000);
        Assert.Equal(1, SocialTestKit.Feature(world.Host).Gratitude.Emotes);
    }

    private static ArcaneCore.World.Economy.EconomyFeature EconomyOf(SocialWorld world)
        => (ArcaneCore.World.Economy.EconomyFeature)world.Host.WorldServices.GetService(typeof(ArcaneCore.World.Economy.EconomyFeature))!;

    /// <summary>
    /// A generous bot (Generosity 0.6 or more) that likes a real player and owes them thanks, so that a thank at the right world time rolls a tip. The
    /// tip goes through the real thanks path: the help report, the thanks queue, <c>FavourOf</c> and the lane's own trade steps.
    /// </summary>
    private sealed class TipScene : IAsyncDisposable
    {
        private readonly WorldTestClient _friend;

        private TipScene(SocialWorld world, WorldTestClient friend, string botName, Guid bot, ObjectGuid botGuid, ObjectGuid friendGuid, float generosity, uint copper)
        {
            World = world;
            _friend = friend;
            BotName = botName;
            Bot = bot;
            BotGuid = botGuid;
            FriendGuid = friendGuid;
            Generosity = generosity;
            Copper = copper;
        }

        public SocialWorld World { get; }

        public string BotName { get; }

        public Guid Bot { get; }

        public ObjectGuid BotGuid { get; }

        public ObjectGuid FriendGuid { get; }

        public float Generosity { get; }

        public uint Copper { get; }

        public long Now { get; private set; } = 1_000;

        public static async Task<TipScene> StartAsync()
        {
            SocialWorld world = await SocialWorld.StartAsync(frozen: true,
                services: services => services.AddScoped<ArcaneCore.Kernel.Economy.IEconomyStore>(_ => new Party.PlayerbotPartyTests.StubEconomyStore()));
            WorldTestClient friend = await world.AddPlayerAsync("Tipfriend");
            await world.Host.OnWorldAsync(() =>
            {
                world.Options.Social.Gratitude.TipChance = 1f;
                world.Options.Social.Memory.MaxDailyAffinityGainPerPair = 0; // the daily cap would hold a pair under the tipping affinity
            });
            for (int i = 0; i < 14; i++)
            {
                string name = "Tipbot" + (char)('a' + i);
                Guid bot = await world.AddBotAsync(name, money: 50_000);
                ObjectGuid botGuid = await world.Host.PlayerStateAsync(name, p => p.Guid);
                float generosity = PlayerbotTraits.For(botGuid.Value).Generosity;
                if (generosity < PlayerbotGratitude.GenerousAt) continue;
                await HelpTestKit.StandAsync(world, name, ("Tipfriend", 3f));
                ObjectGuid friendGuid = await world.Host.PlayerStateAsync("Tipfriend", p => p.Guid);
                await world.Host.OnWorldAsync(() =>
                {
                    HelpTestKit.Online(world, name).Inventory.Load([]);
                    HelpTestKit.Online(world, "Tipfriend").Inventory.Load([]);
                });
                return new TipScene(world, friend, name, bot, botGuid, friendGuid, generosity, PlayerbotGratitude.TipCopper(50_000, 1, 10_000));
            }

            throw new InvalidOperationException("no generous bot among fourteen");
        }

        /// <summary>The friend helps the bot; a pass at a world time where the thanks roll a tip runs.</summary>
        public async Task ThankAsync()
        {
            await World.Host.OnWorldAsync(() =>
            {
                PlayerbotSocialRecorder recorder = HelpTestKit.RecorderOf(World);
                for (int i = 0; i < 60 && !(recorder.Memory.TryGet(Bot, FriendGuid.Value, out PlayerbotRelation r) && r.Affinity >= PlayerbotGratitude.FavourAffinity); i++)
                    recorder.NoteById(Bot, BotGuid, FriendGuid, PlayerbotSocialEvent.GroupSucceeded);
                recorder.NoteHelp(HelpTestKit.Online(World, "Tipfriend"), HelpTestKit.Online(World, BotName));
            });
            Now = HelpTestKit.NowWhere(t => PlayerbotGratitude.FavourOf(BotGuid.Value, FriendGuid.Value, (uint)(t / 1000), Generosity, 100, 1f, true, true, true) == PlayerbotFavour.Tip);
            await PassAsync(World, Now);
        }

        public async Task PassNextAsync() => await PassAsync(World, Now += 1_000);

        /// <summary>The trade the friend is in: whether there is one, it opened, and the bot's gold is set.</summary>
        public Task<(bool Exists, bool Opened, bool GoldSet)> TradeAsync() => World.Host.OnWorldAsync(() =>
            EconomyOf(World).TradeOf(HelpTestKit.Online(World, "Tipfriend")) is { } t
                ? (true, t.Opened, t.SideOf(HelpTestKit.Online(World, BotName)).Gold == Copper)
                : (false, false, false));

        public Task BeginTradeAsync() => _friend.SendAsync(WorldOpcode.CmsgBeginTrade, []);

        public Task AcceptTradeAsync() => _friend.SendAsync(WorldOpcode.CmsgAcceptTrade, []);

        public async ValueTask DisposeAsync()
        {
            await _friend.DisposeAsync();
            await World.DisposeAsync();
        }
    }

    [Fact]
    public async Task ATip_ComesThroughTheThanksPath_AndIsAcceptedByTheClient_AndCountsFromTheTradeCompletion()
    {
        await using TipScene scene = await TipScene.StartAsync();
        uint before = await scene.World.Host.PlayerStateAsync("Tipfriend", p => p.Money);
        Assert.True(scene.Copper > 0);

        await scene.ThankAsync();
        Assert.Equal(1, SocialTestKit.Feature(scene.World.Host).Gratitude.TipsStarted);

        for (int i = 0; i < 40 && await scene.World.Host.PlayerStateAsync("Tipfriend", p => p.Money) == before; i++)
        {
            await scene.PassNextAsync();
            await Task.Delay(1100); // a trade ignores an accept that follows a change by less than a second (scam prevention)
            (bool exists, bool opened, bool goldSet) = await scene.TradeAsync();
            if (exists && !opened) await scene.BeginTradeAsync();
            else if (exists && goldSet) await scene.AcceptTradeAsync();
        }

        Assert.Equal(before + scene.Copper, await scene.World.Host.PlayerStateAsync("Tipfriend", p => p.Money));
        await scene.PassNextAsync();
        Assert.Equal(1, SocialTestKit.Feature(scene.World.Host).Gratitude.TipsPaid);
        Assert.Equal(0, SocialTestKit.Feature(scene.World.Host).Gratitude.TipsPending);
    }

    [Fact]
    public async Task DisablingGratitudeLive_MidTip_CancelsTheTrade()
    {
        await using TipScene scene = await TipScene.StartAsync();
        await scene.ThankAsync();
        for (int i = 0; i < 20; i++)
        {
            await scene.PassNextAsync();
            (bool exists, bool opened, _) = await scene.TradeAsync();
            if (exists && opened) break;
            if (exists) await scene.BeginTradeAsync();
            await Task.Delay(300);
        }

        Assert.True((await scene.TradeAsync()).Opened, "the trade window opened");
        Assert.Equal(1, SocialTestKit.Feature(scene.World.Host).Gratitude.TipsPending);

        await scene.World.Host.OnWorldAsync(() => scene.World.Options.Social.Gratitude.Enabled = false);
        await scene.PassNextAsync();

        Assert.Equal(0, SocialTestKit.Feature(scene.World.Host).Gratitude.TipsPending);
        await HelpTestKit.UntilAsync(() => !scene.TradeAsync().Result.Exists, "the bot's cancel to close the friend's trade window");
    }
}
