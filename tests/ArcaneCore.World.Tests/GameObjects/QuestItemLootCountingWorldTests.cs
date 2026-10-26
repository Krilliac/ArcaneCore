using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// Quest 789 "Sting of the Scorpid" (10 x item 4862 from creature 3124, quest drop at 100 here so every kill is decisive) through the whole
/// host: looting a quest item must raise the quest's item counter by exactly the looted count. A loot that was counted twice (once by the
/// bag's ItemCountChanged listener, once by the loot service's own ItemLooted report) finishes the counter at 5 tails and stops the drop,
/// while the bags still hold only 5.
/// </summary>
public sealed class QuestItemLootCountingWorldTests
{
    private const uint QuestId = 789;
    private const uint Tail = 4862;
    private const uint WorkerEntry = 3124;
    private const uint Required = 10;

    private static WorldTestHost Start(QuestJournalFixture quests)
    {
        var loot = new LootContent([(LootTableKind.Creature, new LootStoreRow(WorkerEntry, Tail, -100f, 0, 1, 1))],
            [new CreatureLootInfo(WorkerEntry, WorkerEntry, 0, 0, 0)]);
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = Tail, Class = 12, Name = "Scorpid Worker Tail", DisplayId = 4002, Quality = 1, Stackable = 20, Bonding = 4 });
        GameObjectTestStore.Current.Value = new GameObjectTestContext(GameObjectContent.Empty, loot);
        QuestNpcTestServices.Current.Value = quests;
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start();
            }
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
            QuestNpcTestServices.Current.Value = null;
        }
    }

    private static async Task<ulong> KillAsync(WorldTestHost host)
        => await host.OnWorldAsync(() =>
        {
            Player killer = host.World.FindOnlinePlayer("Looter")!;
            var template = new CreatureTemplate { Entry = WorkerEntry, Name = "Scorpid Worker", MinLevelHealth = 10, MaxLevelHealth = 10 };
            CreatureMapSystem? system = killer.Map!.FindUpdater<CreatureMapSystem>();
            if (system is null)
            {
                system = new CreatureMapSystem(killer.Map, new CreatureContent([template], [], [], [], []), random: new Random(1));
                killer.Map.AddUpdater(system);
            }

            Creature creature = system.SpawnTemporary(template, killer.X + 1, killer.Y, killer.Z, 0);
            killer.Map.Combat.DealDamage(killer, creature, creature.Health, direct: false);
            return creature.Guid.Value;
        });

    /// <summary>Opens the corpse and returns how many items the window lists (SMSG_LOOT_RESPONSE: guid, type, gold, count).</summary>
    private static async Task<int> OpenAsync(WorldTestClient client, ulong corpse)
    {
        await client.SendAsync(WorldOpcode.CmsgLoot, BitConverter.GetBytes(corpse));
        byte[] response = await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse);
        return response[8 + 1 + 4];
    }

    [Fact]
    public async Task LootingTheQuestItem_RaisesTheQuestCounterByTheLootedCount_NotTwice()
    {
        var quests = new QuestJournalFixture
        {
            Templates = [new QuestTemplate { Entry = QuestId, Method = 2, QuestLevel = 3, MinLevel = 1, Title = "Sting of the Scorpid", ReqItemId1 = Tail, ReqItemCount1 = Required }],
        };
        await using WorldTestHost host = Start(quests);
        await using WorldTestClient client = await host.ConnectAsync();
        byte[] key = await host.AddAccountAsync("LOOTER");
        await client.AuthenticateAsync("LOOTER", key);
        await client.CreateCharacterAsync("Looter");
        var account = (await host.Accounts.FindByUsernameAsync("LOOTER"))!;
        CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        quests.Characters.Seed(new CharacterQuestStatus(character.Id, QuestId, (byte)QuestStatus.Incomplete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        await client.LoginAsync((ulong)character.Id);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Looter") is { } p
            && ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(p) is { Loaded: true }, "the quest journal to load");

        for (int kill = 1; kill <= (int)Required; kill++)
        {
            ulong corpse = await KillAsync(host);
            Assert.Equal(1, await OpenAsync(client, corpse));
            await client.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0]);
            await client.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
            await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(corpse));
            await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);

            (uint inBags, uint counter) = await host.OnWorldAsync(() =>
            {
                Player p = host.World.FindOnlinePlayer("Looter")!;
                var services = ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services;
                return (p.Inventory.GetItemCount(Tail), services.StateOf(p)!.Quests.Get(QuestId)!.ItemCount[0]);
            });
            Assert.Equal((uint)kill, inBags);
            Assert.Equal((uint)kill, counter);
        }
    }

    /// <summary>
    /// The 5875 client prints the centre-screen "Scorpid Worker Tail: n/10" line once per SMSG_QUESTUPDATE_ADD_ITEM it receives (n is its own bag
    /// count, so two packets print two identical lines); SMSG_ITEM_PUSH_RESULT only gives the chat "You receive loot" line
    /// (WoW-RE-Research protocol/quest-item-progress.md). One looted tail must therefore produce exactly one of each on the wire, carrying the
    /// item entry and the looted count.
    /// </summary>
    [Fact]
    public async Task LootingTheQuestItem_SendsOneQuestUpdateAddItem_AndOneItemPushResult()
    {
        var quests = new QuestJournalFixture
        {
            Templates = [new QuestTemplate { Entry = QuestId, Method = 2, QuestLevel = 3, MinLevel = 1, Title = "Sting of the Scorpid", ReqItemId1 = Tail, ReqItemCount1 = Required }],
        };
        await using WorldTestHost host = Start(quests);
        await using WorldTestClient client = await host.ConnectAsync();
        byte[] key = await host.AddAccountAsync("LOOTER");
        await client.AuthenticateAsync("LOOTER", key);
        await client.CreateCharacterAsync("Looter");
        var account = (await host.Accounts.FindByUsernameAsync("LOOTER"))!;
        CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        quests.Characters.Seed(new CharacterQuestStatus(character.Id, QuestId, (byte)QuestStatus.Incomplete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        await client.LoginAsync((ulong)character.Id);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Looter") is { } p
            && ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(p) is { Loaded: true }, "the quest journal to load");

        for (int kill = 1; kill <= 3; kill++)
        {
            ulong corpse = await KillAsync(host);
            Assert.Equal(1, await OpenAsync(client, corpse));
            await client.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0]);
            List<(WorldOpcode Opcode, byte[] Payload)> packets = await client.CollectFromAsync(WorldOpcode.SmsgLootRemoved);
            await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(corpse));
            packets.AddRange(await client.CollectFromAsync(WorldOpcode.SmsgLootReleaseResponse));

            byte[] update = Assert.Single(packets, p => p.Opcode == WorldOpcode.SmsgQuestupdateAddItem).Payload;
            var reader = new PacketReader(update);
            Assert.Equal(Tail, reader.ReadUInt32());
            Assert.Equal(1u, reader.ReadUInt32());
            Assert.Single(packets, p => p.Opcode == WorldOpcode.SmsgItemPushResult);
        }
    }
}
