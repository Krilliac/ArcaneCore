using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.GridTerrain;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Teleport;

/// <summary>
/// Area-trigger entry requirements over real sessions (docs/areas/area-triggers.md): an attunement-gated dungeon entrance refuses a
/// player without the key with the retail-style message and teleports one who carries it; a quest-done and a conditions-table
/// requirement are enforced through the quest feature's gate and the condition feature; the row's own message replaces the generated
/// one; a game master in GM mode passes. The triggers share the test dungeon entrance's box (map test data).
/// </summary>
public sealed class TeleportRequirementWorldTests
{
    private const uint KeyEntry = 60001;
    private const uint AttunementQuest = 9301;
    private const uint KeyTrigger = InMemoryMapDataStore.DeadminesTrigger;
    private const uint QuestTrigger = 981;
    private const uint MessageTrigger = 982;
    private const uint ConditionTrigger = 983;
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    // Inside the 10 x 10 x 10 box of the test dungeon entrance (map test data).
    private const float InsideX = -8962f;
    private const float InsideY = -130f;
    private const float InsideZ = 84f;

    [Fact]
    public async Task AttunementTrigger_WithoutTheKey_IsRefusedWithTheMessage_AndWithTheKeyTeleports()
    {
        await using WorldTestHost host = Start(new QuestJournalFixture());
        await using WorldTestClient player = await host.EnterWorldAsync("TPKEY", "Tpkey");
        await player.CollectAsync(Quiet);
        await host.PlaceAsync("Tpkey", InsideX, InsideY, InsideZ);

        await player.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(KeyTrigger));
        List<(WorldOpcode Opcode, byte[] Payload)> refused = [.. await player.CollectAsync(Quiet)];

        Assert.Equal("You must have item Test Attunement Key to enter.", MessageText(refused.Single(p => p.Opcode == WorldOpcode.SmsgAreaTriggerMessage).Payload));
        Assert.DoesNotContain(refused, p => p.Opcode == WorldOpcode.SmsgTransferPending);
        Assert.Equal(0u, await host.PlayerStateAsync("Tpkey", p => p.MapId));

        await host.PlayerStateAsync("Tpkey", p => p.Inventory.AddItem(KeyEntry, 1, out _));
        await player.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(KeyTrigger));

        byte[] pending = await player.ReadUntilAsync(WorldOpcode.SmsgTransferPending);
        Assert.Equal(36u, BinaryPrimitives.ReadUInt32LittleEndian(pending));
    }

    [Fact]
    public async Task TheRowsOwnMessage_IsWhatTheClientSees()
    {
        await using WorldTestHost host = Start(new QuestJournalFixture());
        await using WorldTestClient player = await host.EnterWorldAsync("TPMSG", "Tpmsg");
        await player.CollectAsync(Quiet);
        await host.PlaceAsync("Tpmsg", InsideX, InsideY, InsideZ);

        await player.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(MessageTrigger));

        Assert.Equal("Bring the Test Attunement Key.", MessageText(await player.ReadUntilAsync(WorldOpcode.SmsgAreaTriggerMessage)));
    }

    [Fact]
    public async Task AGameMaster_InGmMode_PassesTheItemRequirement_ButNotWithoutGmMode()
    {
        await using WorldTestHost host = Start(new QuestJournalFixture());
        await using WorldTestClient gm = await host.EnterWorldAsync("TPGM", "Tpgm", AccountSecurity.GameMaster);
        await gm.CollectAsync(Quiet);
        await host.PlaceAsync("Tpgm", InsideX, InsideY, InsideZ);

        await gm.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(KeyTrigger));
        Assert.Contains("You must have item", MessageText(await gm.ReadUntilAsync(WorldOpcode.SmsgAreaTriggerMessage)), StringComparison.Ordinal);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm on");
        await gm.CollectAsync(Quiet);
        await gm.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(KeyTrigger));

        Assert.Equal(36u, BinaryPrimitives.ReadUInt32LittleEndian(await gm.ReadUntilAsync(WorldOpcode.SmsgTransferPending)));
    }

    [Fact]
    public async Task QuestDoneRequirement_RefusesSilently_UntilTheQuestIsTurnedIn()
    {
        var fixture = new QuestJournalFixture { Templates = [Quest(AttunementQuest)] };
        await using WorldTestHost undone = Start(fixture);
        await using WorldTestClient without = await EnterAsync(undone, fixture, "TPQNO", "Tpqno", _ => []);
        await without.CollectAsync(Quiet);
        await undone.PlaceAsync("Tpqno", InsideX, InsideY, InsideZ);

        await without.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(QuestTrigger));

        // The reference sends no text for an unfinished quest: nothing at all, and no transfer.
        Assert.Empty(await without.CollectAsync(Quiet));

        var done = new QuestJournalFixture { Templates = [Quest(AttunementQuest)] };
        await using WorldTestHost host = Start(done);
        await using WorldTestClient with = await EnterAsync(host, done, "TPQYES", "Tpqyes",
            id => [new CharacterQuestStatus(id, AttunementQuest, (byte)QuestStatus.Complete, true, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)]);
        await with.CollectAsync(Quiet);
        await host.PlaceAsync("Tpqyes", InsideX, InsideY, InsideZ);

        await with.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(QuestTrigger));

        Assert.Equal(36u, BinaryPrimitives.ReadUInt32LittleEndian(await with.ReadUntilAsync(WorldOpcode.SmsgTransferPending)));
    }

    [Fact]
    public async Task AQuestInTheLogButNotTurnedIn_DoesNotSatisfyTheQuestDoneRequirement()
    {
        var fixture = new QuestJournalFixture { Templates = [Quest(AttunementQuest)] };
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient player = await EnterAsync(host, fixture, "TPQOPEN", "Tpqopen",
            id => [new CharacterQuestStatus(id, AttunementQuest, (byte)QuestStatus.Complete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)]);
        await player.CollectAsync(Quiet);
        await host.PlaceAsync("Tpqopen", InsideX, InsideY, InsideZ);

        await player.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(QuestTrigger));

        Assert.Empty(await player.CollectAsync(Quiet));
    }

    [Fact]
    public async Task ConditionRequirement_WithoutASatisfiedConditionRow_RefusesSilently()
    {
        await using WorldTestHost host = Start(new QuestJournalFixture());
        await using WorldTestClient player = await host.EnterWorldAsync("TPCOND", "Tpcond");
        await player.CollectAsync(Quiet);
        await host.PlaceAsync("Tpcond", InsideX, InsideY, InsideZ);

        await player.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(ConditionTrigger));

        Assert.Empty(await player.CollectAsync(Quiet));
        Assert.Equal(0u, await host.PlayerStateAsync("Tpcond", p => p.MapId));
    }

    private static MapContent Content()
    {
        AreaTriggerTemplate box = InMemoryMapDataStore.Content.AreaTriggers.Single(t => t.Id == InMemoryMapDataStore.DeadminesTrigger);
        return InMemoryMapDataStore.Content with
        {
            AreaTriggers = [.. InMemoryMapDataStore.Content.AreaTriggers,
                box with { Id = QuestTrigger }, box with { Id = MessageTrigger }, box with { Id = ConditionTrigger }],
            AreaTriggerTeleports =
            [
                new AreaTriggerTeleport(KeyTrigger, "Attunement entrance", "", 0, 36, -16.4f, -383.07f, 61.78f, 1.86f, RequiredItem: KeyEntry),
                new AreaTriggerTeleport(QuestTrigger, "Quest entrance", "", 0, 36, -16.4f, -383.07f, 61.78f, 1.86f, RequiredQuestDone: AttunementQuest),
                new AreaTriggerTeleport(MessageTrigger, "Message entrance", "Bring the Test Attunement Key.", 0, 36, -16.4f, -383.07f, 61.78f, 1.86f, RequiredItem: KeyEntry),
                new AreaTriggerTeleport(ConditionTrigger, "Condition entrance", "", 0, 36, -16.4f, -383.07f, 61.78f, 1.86f, RequiredCondition: 5),
            ],
        };
    }

    private static QuestTemplate Quest(uint id) => new() { Entry = id, Method = 2, QuestLevel = 1, MinLevel = 1, Title = "Attunement", Details = "Prove yourself.", Objectives = "Done." };

    private static WorldTestHost Start(QuestJournalFixture fixture)
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = KeyEntry, Class = 15, SubClass = 0, Name = "Test Attunement Key", DisplayId = 1, Quality = 1 });
        QuestNpcTestServices.Current.Value = fixture;
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start(configureServices: services => services.AddSingleton<IMapDataStore>(new FixedMapStore(Content())));
            }
        }
        finally
        {
            QuestNpcTestServices.Current.Value = null;
        }
    }

    private static async Task<WorldTestClient> EnterAsync(WorldTestHost host, QuestJournalFixture fixture, string account, string name, Func<int, CharacterQuestStatus[]> seed)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
        Account stored = (await host.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(stored.Id)).Single();
        fixture.Characters.Seed(seed(record.Id));
        await client.LoginAsync((ulong)record.Id);
        return client;
    }

    private static byte[] AreaTrigger(uint id)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, id);
        return payload;
    }

    // SMSG_AREA_TRIGGER_MESSAGE: u32 length (with the NUL), then the NUL-terminated text.
    private static string MessageText(byte[] payload)
    {
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return System.Text.Encoding.UTF8.GetString(payload, 4, length - 1);
    }

    private sealed class FixedMapStore(MapContent content) : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }
}
