using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Tests.GridTerrain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>
/// Exploration quests credited by the imported <c>areatrigger_involvedrelation</c> table over real sessions (docs/areas/area-triggers.md):
/// the relation arrives through the quest content store, <c>Quests:AreaTriggerQuests</c> stays empty, and stepping on the trigger sends
/// SMSG_QUESTUPDATE_COMPLETE for a quest in the log and does nothing for one that is not.
/// </summary>
public sealed class QuestExplorationWorldTests
{
    private const uint Exploration = 9311;
    private const uint Unrelated = 9312;
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private static QuestTemplate Explore(uint id) => new()
    {
        Entry = id, Method = 2, QuestLevel = 1, MinLevel = 1, Title = $"Explore {id}", Details = "Look around.", Objectives = "Find the entrance.",
        SpecialFlags = (byte)QuestSpecialFlags.ExplorationOrEvent,
    };

    [Fact]
    public async Task EnteringTheRelatedTrigger_CreditsTheLoggedExplorationQuest_FromTheImportedTable()
    {
        var fixture = new QuestJournalFixture
        {
            Templates = [Explore(Exploration), Explore(Unrelated)],
            AreaTriggerQuests = [new CreatureQuestRelation { Id = InMemoryMapDataStore.DeadminesTrigger, Quest = Exploration }],
        };
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await EnterAsync(host, fixture, "EXPLTBL", "Expltbl");
        AssertNoConfiguredTriggers(host);
        await client.CollectAsync(Quiet);
        await host.PlaceAsync("Expltbl", -8962f, -130f, 84f);

        await client.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(InMemoryMapDataStore.DeadminesTrigger));

        byte[] complete = await client.ReadUntilAsync(WorldOpcode.SmsgQuestupdateComplete);
        Assert.Equal(Exploration, BinaryPrimitives.ReadUInt32LittleEndian(complete));
        Assert.True(await QuestState(host, "Expltbl", Exploration, d => d.Explored));
        Assert.False(await QuestState(host, "Expltbl", Unrelated, d => d.Explored));
    }

    [Fact]
    public async Task ATriggerWithoutARelation_CreditsNothing()
    {
        var fixture = new QuestJournalFixture
        {
            Templates = [Explore(Exploration)],
            AreaTriggerQuests = [new CreatureQuestRelation { Id = 5555, Quest = Exploration }],
        };
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await EnterAsync(host, fixture, "EXPLNONE", "Explnone");
        await client.CollectAsync(Quiet);
        await host.PlaceAsync("Explnone", -8962f, -130f, 84f);

        await client.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(InMemoryMapDataStore.DeadminesTrigger));
        List<(WorldOpcode Opcode, byte[] Payload)> packets = await client.CollectAsync(Quiet);

        Assert.DoesNotContain(packets, p => p.Opcode == WorldOpcode.SmsgQuestupdateComplete);
        Assert.False(await QuestState(host, "Explnone", Exploration, d => d.Explored));
    }

    private static void AssertNoConfiguredTriggers(WorldTestHost host)
        => Assert.Empty(host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.AreaTriggerQuests);

    private static Task<bool> QuestState(WorldTestHost host, string name, uint quest, Func<QuestStatusData, bool> read)
        => host.PlayerStateAsync(name, player =>
        {
            QuestNpcFeature feature = ((WorldSession)player.Session).Services.GetRequiredService<QuestNpcFeature>();
            return feature.Services.StateOf(player)?.Quests.Get(quest) is { } data && read(data);
        });

    private static WorldTestHost Start(QuestJournalFixture fixture)
    {
        QuestNpcTestServices.Current.Value = fixture;
        try { return WorldTestHost.Start(); }
        finally { QuestNpcTestServices.Current.Value = null; }
    }

    // Both quests are in the log, incomplete; only the relation decides which one a trigger credits.
    private static async Task<WorldTestClient> EnterAsync(WorldTestHost host, QuestJournalFixture fixture, string account, string name)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
        var stored = (await host.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(stored.Id)).Single();
        fixture.Characters.Seed([.. fixture.Templates!.Select(t => fixture.Progress(record.Id, t.Entry, 0, 0))]);
        await client.LoginAsync((ulong)record.Id);
        return client;
    }

    private static byte[] AreaTrigger(uint id)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, id);
        return payload;
    }
}
