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

    [Fact]
    public async Task ATip_IsARealTrade_ThatMovesTheGold()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true,
            services: services => services.AddScoped<ArcaneCore.Kernel.Economy.IEconomyStore>(_ => new Party.PlayerbotPartyTests.StubEconomyStore()));
        await using WorldTestClient friend = await world.AddPlayerAsync("Tipfriend");
        Guid bot = await world.AddBotAsync("Tipbot", money: 50_000);
        await HelpTestKit.StandAsync(world, "Tipbot", ("Tipfriend", 3f));
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Tipbot", p => p.Guid);
        ObjectGuid friendGuid = await world.Host.PlayerStateAsync("Tipfriend", p => p.Guid);
        uint before = await world.Host.PlayerStateAsync("Tipfriend", p => p.Money);
        await world.Host.OnWorldAsync(() =>
        {
            HelpTestKit.Online(world, "Tipbot").Inventory.Load([]);
            HelpTestKit.Online(world, "Tipfriend").Inventory.Load([]);
        });
        uint copper = PlayerbotGratitude.TipCopper(50_000, 1, 10_000);
        Assert.True(copper > 0);
        await world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Gratitude.StartTip(bot, friendGuid, copper, HelpTestKit.Online(world, "Tipbot").Money, 1_000));

        long now = 2_000;
        for (int i = 0; i < 40 && await world.Host.PlayerStateAsync("Tipfriend", p => p.Money) == before; i++)
        {
            await PassAsync(world, now += 1_000);
            await Task.Delay(1100); // a trade ignores an accept that follows a change by less than a second (scam prevention)
            (bool exists, bool opened, bool goldSet) = await world.Host.OnWorldAsync(() =>
            {
                var economy = (ArcaneCore.World.Economy.EconomyFeature)world.Host.WorldServices.GetService(typeof(ArcaneCore.World.Economy.EconomyFeature))!;
                return economy.TradeOf(HelpTestKit.Online(world, "Tipfriend")) is { } t ? (true, t.Opened, t.SideOf(HelpTestKit.Online(world, "Tipbot")).Gold == copper) : (false, false, false);
            });
            if (exists && !opened) await friend.SendAsync(WorldOpcode.CmsgBeginTrade, []);
            else if (exists && goldSet) await world.Host.OnWorldAsync(() =>
            {
                Player p = HelpTestKit.Online(world, "Tipfriend");
                ((ArcaneCore.World.Economy.EconomyFeature)world.Host.WorldServices.GetService(typeof(ArcaneCore.World.Economy.EconomyFeature))!).AcceptTrade((ArcaneCore.World.Net.WorldSession)p.Session, p);
            });
        }

        Assert.Equal(before + copper, await world.Host.PlayerStateAsync("Tipfriend", p => p.Money));
        Assert.Equal(50_000u - copper, await world.Host.PlayerStateAsync("Tipbot", p => p.Money));
        await PassAsync(world, now + 1_000);
        Assert.Equal(1, SocialTestKit.Feature(world.Host).Gratitude.TipsPaid);
        Assert.Equal(0, SocialTestKit.Feature(world.Host).Gratitude.TipsPending);
        _ = botGuid;
    }
}
