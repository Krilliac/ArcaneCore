using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The WTS half of the trade gifts lane through a real world: a player's WTS line heard by a bot that lacks a reagent of a recipe it wants (<see cref="PlayerbotWorldChatter.WtsAnswer"/>)
/// opens a buy deal on the trade desk and is answered by a whisper (react.wts). The bot, its spellbook and its bags are real; the line is handed to the answer as the world chatter would.
/// </summary>
public sealed class PlayerbotTradeGiftsWtsWorldTests
{
    private const uint Potion = 996_601, Dust = 996_611, ThreadItem = 996_612, RecipeSpell = 996_621;
    private const string BotName = "Wtsbot", SellerName = "Wtsseller";

    private static ItemTemplate Reagent(uint entry, string name) => new()
    {
        Entry = entry, Name = name, Class = (uint)ItemClass.TradeGoods, DisplayId = 1, Quality = 1, SellPrice = 500, BuyPrice = 4000, Stackable = 20,
        InventoryType = (uint)InventoryType.NonEquip, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
    };

    /// <summary>
    /// The bot wants a recipe that uses 3 Moonpetal Dust and carries 1, so it lacks 2. A WTS line for Ashen Thread (no recipe uses it) leaves nothing on the desk and draws no whisper. A WTS line
    /// for the dust leaves one Quoted deal for that seller, a buyer deal for the missing 2 (the same price the desk bids for 2 and not for 3) and the react.wts whisper to the seller. Broken: an answer
    /// that never quotes, quotes the other role or the wrong count, or quotes for goods the bot does not need.
    /// </summary>
    [Fact]
    public async Task AHeardWtsLine_QuotesABuyerDealOnlyForAReagentTheBotLacks()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.AddRange([
            new ItemTemplate { Entry = Potion, Name = "wtspotion", Class = (uint)ItemClass.Consumable, InventoryType = (uint)InventoryType.NonEquip, Stackable = 20, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue },
            Reagent(Dust, "Moonpetal Dust"), Reagent(ThreadItem, "Ashen Thread")]);
        SocialWorld world;
        using (items.Use())
        {
            world = await SocialWorld.StartAsync(
                options =>
                {
                    options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
                    options.Chat.Enabled = false;
                },
                frozen: true,
                services: services => services.AddScoped<ArcaneCore.Kernel.Economy.IEconomyStore>(_ => new PlayerbotPartyTests.StubEconomyStore()));
        }

        await using SocialWorld worldScope = world;
        Guid botId = await world.AddBotAsync(BotName, money: 100_000);
        await using WorldTestClient seller = await world.AddPlayerAsync(SellerName);
        ObjectGuid sellerGuid = await world.Host.PlayerStateAsync(SellerName, p => p.Guid);
        PlayerbotTradeDesk desk = PartyTestHost.Feature(world.Host).TradeDesk;

        await world.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(world, BotName);
            bot.Inventory.Load([]);
            SpellFeature feature = ((WorldSession)bot.Session).Services.GetRequiredService<SpellFeature>();
            SpellStore store = feature.System.Store = new SpellStore([
                new SpellInfo
                {
                    Id = RecipeSpell,
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.CreateItem, ItemType = Potion }, new(), new()],
                    Reagents = new[] { new SpellReagent(Dust, 3) },
                    Totems = new uint[] { 0, 0 },
                },
            ], [], []);
            feature.Spellbook.LearnSpell(bot, RecipeSpell);
            Assert.Equal(InventoryResult.Ok, bot.Inventory.AddItem(Dust, 1, out _));
            var needs = new PlayerbotVendorSupplyNeeds();
            needs.Refresh(bot, store, feature.Spellbook);
            Assert.Equal([(Dust, 2u)], needs.Shortfalls(bot, 8)); // the precondition: the bot lacks exactly 2 dust
            return true;
        });

        Task<BotSpeech?> HearAsync(string text) => world.Host.OnWorldAsync(() =>
        {
            PlayerbotSocialPass pass = SocialTestKit.BotPass(world.Host, world.Host.World.NowMs);
            PlayerbotSocialBot bot = pass.Bots.Single();
            var line = new BotHeardLine(bot.Id, bot.Player.Guid, bot.Player.Name, ChatType.Channel, "World", sellerGuid, SellerName, false, text, false, true);
            return PlayerbotWorldChatter.WtsAnswer(pass, bot, line, PlayerbotWorldChatter.Normalize(text));
        });

        // An item no wanted recipe uses: no whisper and no deal.
        Assert.Null(await HearAsync("WTS Ashen Thread x20, pst"));
        Assert.Null(desk.StateOf(botId, sellerGuid));
        Assert.Equal(0, desk.Count);

        // The reagent the bot lacks: the whisper and a Quoted buyer deal.
        BotSpeech? speech = await HearAsync("WTS Moonpetal Dust x20, pst");
        Assert.NotNull(speech);
        Assert.Equal(BotSpeechChannel.Whisper, speech.Channel);
        Assert.Equal((SellerName, "react.wts"), (speech.Target, speech.Topic));
        Assert.Contains("Moonpetal Dust", speech.Text);
        Assert.Contains("2", speech.Text); // the missing count
        Assert.Equal(PlayerbotDealState.Quoted, desk.StateOf(botId, sellerGuid));
        Assert.Equal(1, desk.Count);
        long price = desk.PriceOf(botId, sellerGuid)!.Value;

        // The deal is the buyer's, for 2: the desk bids exactly that for 2 and something else for 3 (and a seller's quote of 2 is refused, the bot holds one).
        (PlayerbotDealQuote two, PlayerbotDealQuote three, PlayerbotDealQuote asSeller) = await world.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(world, BotName), other = HelpTestKit.Online(world, SellerName);
            PlayerbotDealQuote bidTwo = desk.Quote(botId, bot, other, PlayerbotDealRole.Buyer, Dust, 2);
            PlayerbotDealQuote bidThree = desk.Quote(botId, bot, other, PlayerbotDealRole.Buyer, Dust, 3);
            PlayerbotDealQuote ask = desk.Quote(botId, bot, other, PlayerbotDealRole.Seller, Dust, 2);
            return (bidTwo, bidThree, ask);
        });
        Assert.True(two.Ok && three.Ok);
        Assert.Equal(price, two.Price);
        Assert.NotEqual(two.Price, three.Price);
        Assert.Equal(PlayerbotDealRefusal.NoGoods, asSeller.Refusal);
    }
}
