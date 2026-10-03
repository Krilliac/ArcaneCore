using System.Text;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Reload.ReloadContentFixture;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// <c>.reload quest_template</c> (vmangos ServerCommands.cpp:1145-1156 → ObjectMgr::LoadQuests, ObjectMgr.cpp:5523, and the
/// relation loaders, ObjectMgr.cpp:9172-9262): the quest store is rebuilt from the content tables off the world thread and
/// swapped in, so a client, a quest giver and an online journal see the changed rows at once. Every test changes a row
/// between the first load and the reload and looks at it through a live holder.
/// </summary>
public sealed class QuestReloadTests
{
    private const uint Ordinary = 9301;
    private const uint Second = 9302;
    private const uint Added = 9399;
    private const uint Giver = 4000;

    private static ReloadContentFixture Content()
    {
        var content = new ReloadContentFixture();
        content.Templates.AddRange([Quest(Ordinary, "Saved journal"), Quest(Second, "Second quest")]);
        content.Starters.Add(new CreatureQuestRelation { Id = Giver, Quest = Ordinary });
        content.Enders.Add(new CreatureQuestRelation { Id = Giver, Quest = Ordinary });
        return content;
    }

    private static ReloadCoordinator Coordinator(WorldTestHost host) => host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator;

    private static QuestNpcServices Services(WorldTestHost host) => host.WorldServices.GetRequiredService<QuestNpcFeature>().Services;

    private static byte[] UInt32(uint value)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(value);
        return writer.ToArray();
    }

    private static async Task<string> QueryAsync(WorldTestClient client, uint quest)
    {
        await client.SendAsync(WorldOpcode.CmsgQuestQuery, UInt32(quest));
        return Encoding.UTF8.GetString(await client.ReadUntilAsync(WorldOpcode.SmsgQuestQueryResponse));
    }

    [Fact]
    public async Task AChangedRow_IsSeenByAClientThatAlreadyLoggedIn()
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("RQCLIENT", "Rqclient");
        await client.CollectAsync();
        Assert.Contains("Saved journal", await QueryAsync(client, Ordinary));
        content.Templates[0] = Quest(Ordinary, "Renamed by reload", killCount: 9);

        ReloadResult result = await Coordinator(host).ReloadAsync("quest_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        string after = await QueryAsync(client, Ordinary);
        Assert.Contains("Renamed by reload", after);
        Assert.DoesNotContain("Saved journal", after);
        Assert.Equal(9u, Services(host).Quests.Get(Ordinary)!.Template.ReqCreatureOrGOCount1);
        Assert.Contains($"{content.Templates.Count} quest templates", result.Message);
    }

    [Fact]
    public async Task AnAddedQuest_AndItsRelations_AreLive_AndARemovedRelationIsCleared()
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        QuestNpcServices services = Services(host);
        Assert.Null(services.Quests.Get(Added));
        content.Templates.Add(Quest(Added, "Added by reload"));
        content.Starters.Add(new CreatureQuestRelation { Id = Giver, Quest = Added });
        content.Enders.Clear(); // ObjectMgr.cpp:9174: the relation maps are cleared and re-read

        ReloadResult result = await Coordinator(host).ReloadAsync("quest_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal("Added by reload", services.Quests.Get(Added)?.Template.Title);
        Assert.Equal([Ordinary, Added], services.Quests.StartersOf(Giver));
        Assert.Empty(services.Quests.EndersOf(Giver));
    }

    [Fact]
    public async Task ARowThatLeftTheTable_StaysLoaded_AndIsReported()
    {
        // ObjectMgr.cpp:5590-5594: LoadQuests inserts or overwrites map entries and never removes one.
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        QuestNpcServices services = Services(host);
        content.Templates.RemoveAt(1);

        ReloadResult result = await Coordinator(host).ReloadAsync("quest_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal("Second quest", services.Quests.Get(Second)?.Template.Title);
        Assert.Equal(2, services.Quests.Count);
        Assert.Contains(result.Notes, n => n.Contains("1 quest(s) no longer in quest_template", StringComparison.Ordinal) && n.Contains($"{Second}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyTable_KeepsTheLoadedQuests()
    {
        // ObjectMgr.cpp:5568-5577: an empty result returns before any template is touched.
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        QuestNpcServices services = Services(host);
        content.Templates.Clear();

        ReloadResult result = await Coordinator(host).ReloadAsync("quest_template");

        Assert.Equal(ReloadStatus.KeptCurrent, result.Status);
        Assert.Equal(2, services.Quests.Count);
        Assert.NotNull(services.Quests.Get(Ordinary));
    }

    [Fact]
    public async Task DuplicateEntries_AreRejected_AndNothingChanges()
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        QuestNpcServices services = Services(host);
        QuestStore before = services.Quests;
        content.Templates.Add(Quest(Ordinary, "Twice"));

        ReloadResult result = await Coordinator(host).ReloadAsync("quest_template");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Contains(result.Notes, n => n.Contains($"{Ordinary}", StringComparison.Ordinal));
        Assert.Same(before, services.Quests);
    }

    [Fact]
    public async Task AFailingStore_ChangesNothing()
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        QuestNpcServices services = Services(host);
        QuestStore before = services.Quests;
        content.Failure = new InvalidOperationException("world database unavailable");

        ReloadResult result = await Coordinator(host).ReloadAsync("quest_template");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("world database unavailable", result.Message);
        Assert.Same(before, services.Quests);
    }

    [Fact]
    public async Task WithoutAQuestContentStore_TheReloadFails()
    {
        await using var host = WorldTestHost.Start();

        ReloadResult result = await Coordinator(host).ReloadAsync("quest_template");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("quest content store", result.Message);
    }

    [Fact]
    public async Task AJournalAlreadyOnline_KeepsItsQuest_WhenTheTemplateChanges()
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        await using WorldTestClient client = await host.ConnectAsync();
        byte[] key = await host.AddAccountAsync("RQJOURNAL");
        await client.AuthenticateAsync("RQJOURNAL", key);
        await client.CreateCharacterAsync("Rqjournal");
        var account = (await host.Accounts.FindByUsernameAsync("RQJOURNAL"))!;
        CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        content.Characters.Seed(new CharacterQuestStatus(character.Id, Ordinary, (byte)QuestStatus.Incomplete, false, false, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0));
        await client.LoginAsync((ulong)character.Id);
        QuestNpcServices services = Services(host);
        Assert.Equal(QuestStatus.Incomplete, await host.PlayerStateAsync("Rqjournal", p => services.StateOf(p)!.Quests.GetStatus(Ordinary)));
        content.Templates[0] = Quest(Ordinary, "Renamed by reload", killCount: 9);

        ReloadResult result = await Coordinator(host).ReloadAsync("quest_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(QuestStatus.Incomplete, await host.PlayerStateAsync("Rqjournal", p => services.StateOf(p)!.Quests.GetStatus(Ordinary)));
        Assert.Equal(Ordinary, await host.PlayerStateAsync("Rqjournal", p => services.StateOf(p)!.Quests.SlotQuestId(0)));
        Assert.Equal(9u, services.Quests.Get(Ordinary)!.Template.ReqCreatureOrGOCount1);
    }

    [Fact]
    public async Task AFailureLaterInTheSwap_PutsTheOldStoreBack()
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        QuestNpcServices services = Services(host);
        QuestStore before = services.Quests;
        content.Templates[0] = Quest(Ordinary, "Never goes live");
        var reloadable = new QuestContentReloadable(host.WorldServices);
        ArcaneCore.Game.Reload.ContentCandidate candidate = await reloadable.BuildAsync(CancellationToken.None);

        await host.OnWorldAsync(() =>
        {
            var transaction = new ArcaneCore.Game.Reload.ReloadTransaction();
            candidate.Commit(host.World, transaction);
            Assert.NotSame(before, services.Quests);
            Assert.Throws<InvalidOperationException>(() => transaction.Step("later step", () => throw new InvalidOperationException("boom"), () => { }));
            Assert.Empty(transaction.Rollback());
        });

        Assert.Same(before, services.Quests);
        Assert.Equal("Saved journal", services.Quests.Get(Ordinary)!.Template.Title);
    }
}
