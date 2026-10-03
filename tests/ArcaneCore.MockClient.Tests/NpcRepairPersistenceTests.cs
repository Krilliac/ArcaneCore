using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.Protocol;
using ArcaneCore.World.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Repair snapshots through the real world save queue and SQLite, before any logout save.</summary>
public sealed class NpcRepairPersistenceTests : IDisposable
{
    private const uint Sword = 91002;
    private const uint Helm = 91004;
    private readonly OwnedFixtureDirectory _directory = OwnedFixtureDirectory.Create();

    [Theory]
    [InlineData(false, 970u)]
    [InlineData(true, 810u)]
    public async Task Repair_SaveQueueReloadsChargedMoneyAndRepairedDurability(bool repairAll, uint expectedMoney)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory.Path, "repair.sqlite"), Pooling = false,
            Cache = SqliteCacheMode.Private, DefaultTimeout = 1,
        }.ToString()).Options;
        var templates = new ItemTemplateStore(
        [
            new ItemTemplate { Entry = Sword, Class = 2, SubClass = 7, Name = "Repair sword", Quality = 2,
                ItemLevel = 10, InventoryType = 13, MaxDurability = 50 },
            new ItemTemplate { Entry = Helm, Class = 4, SubClass = 1, Name = "Repair helm", Quality = 1,
                ItemLevel = 10, InventoryType = 1, MaxDurability = 40 },
        ], []);
        InventoryItemData[] initialItems =
        [
            new(0, 0, new ItemInstanceData { Guid = 8, Entry = Helm, Count = 1, Durability = repairAll ? 0u : 40u }),
            new(0, 23, new ItemInstanceData { Guid = 7, Entry = Sword, Count = 1, Durability = repairAll ? 0u : 40u }),
        ];
        CharacterRecord character;
        await using (var seed = new CharacterDbContext(options))
        {
            await SchemaBootstrapper.EnsureAsync(seed, CharacterDbContext.Schema, cancellationToken: token);
            character = await new EfCharacterStore(seed).CreateAsync(new CharacterRecord
            {
                AccountId = 1, Name = "Repairhero", Race = 1, Class = 1, Level = 1,
                MapId = 0, ZoneId = 12, Z = 83.5f, Money = 1000,
            }, token);
            await new EfItemStore(seed).SaveInventoryAsync(character.Id, new InventorySnapshot(initialItems), token);
        }

        var registrations = new ServiceCollection();
        registrations.AddScoped(_ => new CharacterDbContext(options));
        registrations.AddScoped<ICharacterStore, EfCharacterStore>();
        await using ServiceProvider provider = registrations.BuildServiceProvider();
        var queue = new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, queue, NullLogger<WorldRuntime>.Instance);
        var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new Session());
        player.Inventory.Templates = templates;
        player.Inventory.Load(initialItems);
        try
        {
            world.AddPlayer(player);
            world.RunTick(0);
            var npc = new NpcInfo(ObjectGuid.WithEntry(HighGuid.Unit, 500, 77), 500, 77, NpcFlags.Repair,
                0, player.X + 1, player.Y, player.Z, 0.5f, true, false, false, false, 0);
            uint[] multipliers = new uint[RepairCostTable.MultiplierCount];
            multipliers[7] = 3;
            multipliers[22] = 2;
            var repair = new RepairCostTable([(10u, multipliers)], [(6u, 1.0f), (4u, 0.5f)]);
            var items = new InventoryItemService(() => templates, repair, BankBagSlotPriceTable.Empty, () => 1000);
            var services = new QuestNpcServices(new QuestStore(QuestContent.Empty), new NpcStore(NpcContent.Empty),
                new QuestNpcDependencies(Creatures: new Lookup(npc), Items: items), new QuestNpcOptions(),
                new SaveSink(world), () => 1000, NullLogger.Instance);
            services.CompleteLoad(services.Track(player), new CharacterQuestData([], []));

            services.RepairItem(player, npc.Guid, repairAll ? ObjectGuid.Empty : ObjectGuid.Item(7));
            await queue.FlushCharacterAsync(character.Id, token);

            // Fresh context and player: no live item references and no logout/autosave can hide an old snapshot.
            await using var read = new CharacterDbContext(options);
            CharacterRecord stored = Assert.IsType<CharacterRecord>(await new EfCharacterStore(read).GetByIdAsync(character.Id, token));
            IReadOnlyList<InventoryItemData> storedItems = await new EfItemStore(read).GetInventoryAsync(character.Id, token);
            Assert.Equal(expectedMoney, stored.Money);
            Assert.Equal(50u, Assert.Single(storedItems, row => row.Item.Entry == Sword).Item.Durability);
            Assert.Equal(40u, Assert.Single(storedItems, row => row.Item.Entry == Helm).Item.Durability);
            var reloaded = new Player(stored, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new Session());
            reloaded.Inventory.Templates = templates;
            reloaded.Inventory.Load(storedItems);
            Assert.Equal(expectedMoney, reloaded.Money);
            Assert.Equal(50u, reloaded.Inventory.GetItemByGuid(ObjectGuid.Item(7))!.Durability);
        }
        finally
        {
            // Only after the assertions may cleanup take an ordinary logout snapshot.
            world.RemovePlayer(player);
            world.RunTick(0);
            await queue.StopAsync();
        }
    }

    public void Dispose() => _directory.Delete();

    private sealed class Lookup(NpcInfo npc) : ICreatureLookup
    {
        public NpcInfo? Find(Player player, ObjectGuid guid) => guid == npc.Guid ? npc : null;
    }

    private sealed class SaveSink(WorldRuntime world) : IQuestNpcSink
    {
        public void CharacterChanged(Player player) => world.SavePlayer(player);
        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows) { }
        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask) { }
    }

    private sealed class Session : IPlayerSession
    {
        public int AccountId => 1;
        public AccountSecurity Security => AccountSecurity.Player;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }
}
