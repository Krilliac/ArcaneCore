using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The bank errand of a bot staying at its capital home (<see cref="PlayerbotBank"/>, salvaged from the capital trip work): which items are
/// deposited and which are kept, the bank slot gate, the capital's banker spawns, and a visit through the real banker and bank handlers.
/// </summary>
public sealed class PlayerbotBankTests
{
    private const float Sx = -8949.95f, Sy = -132.49f, Sz = 83.53f;

    [Theory]
    [InlineData(100u, 1000u, 2, true, true)]    // nearly full, cheap
    [InlineData(100u, 1000u, 5, true, false)]   // enough room left
    [InlineData(500u, 1000u, 0, true, false)]   // more than 40% of the gold
    [InlineData(400u, 1000u, 0, true, true)]    // exactly 40%
    [InlineData(100u, 1000u, 0, false, false)]  // no slot left to buy
    [InlineData(0u, 1000u, 0, true, false)]     // no price known
    public void BankSlotGate(uint price, uint money, int freeBankSlots, bool exists, bool expected)
        => Assert.Equal(expected, PlayerbotBank.ShouldBuySlot(money, price, freeBankSlots, exists));

    [Fact]
    public void BankOptions_AreOnByDefault()
    {
        var economy = new PlayerbotEconomyOptions();
        Assert.True(economy.BankDeposit);
        Assert.True(economy.BuyBankSlots);
    }

    private static ItemTemplate Item(uint entry, ItemClass @class, uint stack = 1, uint quality = 1, InventoryType type = InventoryType.NonEquip)
        => new()
        {
            Entry = entry, Name = $"item-{entry}", Class = (uint)@class, InventoryType = (uint)type, Quality = quality, Stackable = stack,
            SellPrice = 5, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
        };

    private static IEnumerable<ItemTemplate> BankItems() =>
    [
        Item(9401, ItemClass.TradeGoods, stack: 20),
        Item(9402, ItemClass.Armor, type: InventoryType.Head),
        Item(9403, ItemClass.Consumable, stack: 20),
        Item(9404, ItemClass.Quest, stack: 1),
        Item(PlayerbotHearthstone.ItemEntry, ItemClass.Junk),
        Item(9405, ItemClass.Gem, stack: 20),
        Item(9406, ItemClass.Junk, stack: 20),
        Item(9407, ItemClass.TradeGoods, stack: 20, quality: 0),
        Item(9408, ItemClass.Weapon),
        Item(9409, ItemClass.Junk), // not stackable
    ];

    private static readonly uint[] Deposited = [9401, 9405, 9406];
    private static readonly uint[] Kept = [9402, 9403, 9404, PlayerbotHearthstone.ItemEntry, 9407, 9408, 9409];

    private sealed class Bot : IAsyncDisposable
    {
        private readonly WorldTestHost _host;

        private Bot(WorldTestHost host, WorldSession session, PlayerbotOptions options)
        {
            _host = host;
            Session = session;
            Options = options;
            Bank = new PlayerbotBank(session, options);
        }

        public WorldSession Session { get; }
        public PlayerbotOptions Options { get; }
        public PlayerbotBank Bank { get; }

        public static async Task<Bot> StartAsync(BankFixture fixture)
        {
            var items = new ItemTestContent();
            foreach (ItemTemplate template in BankItems()) items.Templates.Templates.Add(template);
            BankServices.Current.Value = fixture;
            WorldTestHost host;
            using (items.Use()) host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var bot = new Bot(host, session, new PlayerbotOptions { Enabled = true });
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                return true;
            });
            return bot;
        }

        public Task<T> On<T>(Func<Player, T> action) => _host.OnWorldAsync(() => action(Session.Player!));

        public Task Advance(uint ms) => _host.World.AdvanceClockAsync(ms);

        /// <summary>Wait until the bot sees the near banker (the creatures are spawned around it after it enters).</summary>
        public async Task SeeBanker()
        {
            for (int i = 0; i < 100 && !await On(p => p.VisibleObjects.Contains(BankFixture.NearGuid)); i++) await Advance(100);
            Assert.True(await On(p => p.VisibleObjects.Contains(BankFixture.NearGuid)), "the banker never came into sight");
        }

        public Task Fill() => On(p =>
        {
            foreach (uint entry in new uint[] { 9401, 9402, 9403, 9404, PlayerbotHearthstone.ItemEntry, 9405, 9406, 9407, 9408, 9409 })
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, p.Inventory.AddItem(entry, entry is 9401 or 9403 or 9405 or 9406 ? 5u : 1u, out _));
            return true;
        });

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await _host.DisposeAsync();
            BankServices.Current.Value = null;
        }
    }

    private static PlayerbotHomeArea Capital(float x = Sx, float y = Sy) => new(0, new Vector3(x, y, Sz), 300f, "town:stormwind") { Stay = TownStay.Capital };

    private static uint Carried(Player player, uint entry)
        => (uint)player.Inventory.AllItems.Where(i => i.Entry == entry && !InventorySlots.IsBankPos(i.BagSlot, i.Slot)).Sum(i => i.Count);

    private static uint Banked(Player player, uint entry)
        => (uint)player.Inventory.AllItems.Where(i => i.Entry == entry && InventorySlots.IsBankPos(i.BagSlot, i.Slot)).Sum(i => i.Count);

    [Fact]
    public async Task Deposits_AreGoodsTheBotDoesNotNeed_GearConsumablesQuestItemsGraysAndTheStoneAreKept()
    {
        await using Bot bot = await Bot.StartAsync(new BankFixture());
        await bot.Fill();
        uint[] chosen = await bot.On(p => PlayerbotBank.SelectDeposits(p, new HashSet<uint>(), _ => false).Select(i => i.Entry).Order().ToArray());
        Assert.Equal(Deposited, chosen);

        // A trade good a known spell uses as a reagent stays; so does an item the town controller protects.
        uint[] kept = await bot.On(p => PlayerbotBank.SelectDeposits(p, new HashSet<uint> { 9401 }, i => i.Entry == 9405).Select(i => i.Entry).Order().ToArray());
        Assert.Equal([9406u], kept);
    }

    [Fact]
    public async Task HasWork_FollowsTheBagsAndTheOptions()
    {
        await using Bot bot = await Bot.StartAsync(new BankFixture());
        Assert.False(await bot.On(p => bot.Bank.HasWork(p, _ => false))); // nothing to deposit, no slot price known
        await bot.Fill();
        Assert.True(await bot.On(p => bot.Bank.HasWork(p, _ => false)));
        bot.Options.Economy.BankDeposit = false;
        Assert.False(await bot.On(p => bot.Bank.HasWork(p, _ => false)));
    }

    [Fact]
    public async Task Bankers_AreTheApproachableSpawnsNearTheCapital()
    {
        await using Bot bot = await Bot.StartAsync(new BankFixture());
        var bankers = await bot.On(p => bot.Bank.BankersOf(p, Capital()).Select(b => b.Spawn).ToArray());
        // The near and the walk-to banker; not the one 600 yards out, not the hostile one.
        Assert.Equal([BankFixture.NearSpawn, BankFixture.WalkSpawn], bankers);
    }

    [Fact]
    public async Task Visit_DepositsThroughTheRealBankerHandlers_ThenRests()
    {
        await using Bot bot = await Bot.StartAsync(new BankFixture());
        await bot.SeeBanker();
        await bot.Fill();
        PlayerbotHomeArea capital = Capital();
        bool working = true;
        for (int i = 0; i < 60 && working; i++)
        {
            await bot.Advance(500);
            working = await bot.On(p =>
            {
                bot.Session.ManagedBudget = new ManagedActionBudget(8);
                return bot.Bank.Update(p, capital, _ => false, 500);
            });
        }

        Assert.False(working);
        Assert.False(bot.Bank.Visiting);
        Assert.Equal(Deposited.Length, bot.Bank.Deposited);
        await bot.On(p =>
        {
            foreach (uint gone in Deposited) Assert.Equal(0u, Carried(p, gone));
            foreach (uint kept in Kept) Assert.True(Carried(p, kept) > 0, $"item {kept} was banked");
            Assert.Equal(5u, Banked(p, 9401));
            return true;
        });

        // The visit is over: the next one is RestMs away, even with new goods in the bags.
        await bot.On(p => p.Inventory.AddItem(9401, 3, out _));
        await bot.Advance(PlayerbotBank.CheckIntervalMs + 1_000);
        Assert.False(await bot.On(p => { bot.Session.ManagedBudget = new ManagedActionBudget(8); return bot.Bank.Update(p, capital, _ => false, 500); }));
        Assert.Equal(3u, await bot.On(p => Carried(p, 9401)));
    }

    [Fact]
    public async Task Visit_IsNotMade_OnAnotherMap_OrWithBothOptionsOff()
    {
        await using Bot bot = await Bot.StartAsync(new BankFixture());
        await bot.SeeBanker();
        await bot.Fill();
        var elsewhere = new PlayerbotHomeArea(1, new Vector3(Sx, Sy, Sz), 300f, "town:orgrimmar") { Stay = TownStay.Capital };
        Assert.False(await bot.On(p => { bot.Session.ManagedBudget = new ManagedActionBudget(8); return bot.Bank.Update(p, elsewhere, _ => false, 500); }));
        bot.Options.Economy.BankDeposit = false;
        bot.Options.Economy.BuyBankSlots = false;
        Assert.False(await bot.On(p => { bot.Session.ManagedBudget = new ManagedActionBudget(8); return bot.Bank.Update(p, Capital(), _ => false, 500); }));
        Assert.Equal(0, bot.Bank.Deposited);
        Assert.Equal(5u, await bot.On(p => Carried(p, 9401)));
    }

    [Fact]
    public async Task Visit_WalksToTheNearestBankerSpawn_WhenNoneIsInReach()
    {
        await using Bot bot = await Bot.StartAsync(new BankFixture { WithNear = false });
        await bot.Fill();
        float before = await bot.On(p => p.X);
        bool stepped = await bot.On(p => { bot.Session.ManagedBudget = new ManagedActionBudget(8); return bot.Bank.Update(p, Capital(), _ => false, 500); });
        Assert.True(stepped);
        Assert.True(bot.Bank.Visiting);
        Assert.Equal(BankFixture.Entry, bot.Bank.TargetEntry);
        await bot.On(p => { PlayerbotMotion.ElapseForTests(p, 500); return true; });
        Assert.True(await bot.On(p => p.X) > before, "the bot did not walk east towards the banker");
        Assert.Equal(0, bot.Bank.Deposited);
    }
}

/// <summary>Bankers around the start position: one in reach (optional), one 150 yards east, one 600 yards east, and a hostile one.</summary>
internal sealed class BankFixture : ICreatureDataStore, INpcContentStore
{
    public const uint Entry = 951, HostileEntry = 952, NearSpawn = 95101, WalkSpawn = 95102, FarSpawn = 95103, HostileSpawn = 95201;
    public static readonly ObjectGuid NearGuid = ObjectGuid.WithEntry(HighGuid.Unit, Entry, NearSpawn);

    public bool WithNear { get; init; } = true;

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty);

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken)
    {
        var spawns = new List<CreatureSpawn>
        {
            new() { Guid = WalkSpawn, Entry = Entry, MapId = 0, X = -8799.95f, Y = -132.49f, Z = 83.53f },
            new() { Guid = FarSpawn, Entry = Entry, MapId = 0, X = -8349.95f, Y = -132.49f, Z = 83.53f },
            new() { Guid = HostileSpawn, Entry = HostileEntry, MapId = 0, X = -8939.95f, Y = -132.49f, Z = 83.53f },
        };
        if (WithNear) spawns.Insert(0, new() { Guid = NearSpawn, Entry = Entry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f });
        return Task.FromResult(new CreatureContent(
            [new CreatureTemplate { Entry = Entry, Name = "banker", Faction = 900011, NpcFlags = (uint)NpcFlags.Banker, DisplayIds = [49] },
             new CreatureTemplate { Entry = HostileEntry, Name = "enemy banker", Faction = 900012, NpcFlags = (uint)NpcFlags.Banker, DisplayIds = [49] }],
            spawns, [], [], []));
    }
}

internal sealed class BankServices : IWorldTestServices
{
    public static readonly AsyncLocal<BankFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<IWorldDataStore>(new FarTrainerWorldData());
        // The bot (template 1) is friends with 900011; 900012 is hostile to it.
        services.AddSingleton(new FactionTemplateCatalog([
            new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
            new FactionTemplateRecord(900011, 0, 0, 8, 0, 0),
            new FactionTemplateRecord(900012, 0, 0, 0, 0, 1)]));
    }
}
