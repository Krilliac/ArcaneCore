using System.Text;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Creatures;

/// <summary>
/// <see cref="AreaTriggerScriptsFeature"/> (cmangos world/areatrigger_scripts.cpp, slice 1) in a running world: at_ravenholdt gives the Manor
/// Ravenholdt kill credit only while the quest is incomplete, and triggers without a script do nothing.
/// </summary>
public sealed class AreaTriggerScriptsFeatureTests
{
    private static QuestTemplate Ravenholdt() => new()
    {
        Entry = AreaTriggerScriptsFeature.QuestManorRavenholdt, Method = 2, QuestLevel = 24, MinLevel = 1, Title = "Manor Ravenholdt",
        Details = "Find the manor.", Objectives = "Reach Ravenholdt.",
        ReqCreatureOrGOId1 = (int)AreaTriggerScriptsFeature.NpcRavenholdt, ReqCreatureOrGOCount1 = 1,
    };

    [Fact]
    public void Feature_IsDiscoveredAsAnAreaTriggerListener()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var feature = new AreaTriggerScriptsFeature(services);
        Assert.IsAssignableFrom<IAreaTriggerListener>(feature);
        Assert.IsAssignableFrom<IWorldFeature>(feature);
        Assert.Throws<ArgumentNullException>(() => feature.Attach(null!));
    }

    [Fact]
    public async Task Ravenholdt_CreditsTheMarker_WhileTheQuestIsIncomplete()
    {
        var fixture = new QuestJournalFixture { Templates = [Ravenholdt()] };
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATRAVEN", "Atraven", seed: true);
        Player player = await host.PlayerAsync("Atraven");
        await WaitLoadedAsync(host, player);

        bool ran = false;
        await host.OnWorldAsync(() => ran = Run(host, player, AreaTriggerScriptsFeature.TriggerRavenholdt));

        Assert.True(ran);
        Assert.Equal(QuestStatus.Complete, await Status(host, "Atraven", AreaTriggerScriptsFeature.QuestManorRavenholdt));
    }

    [Fact]
    public async Task Ravenholdt_WithoutTheQuest_AndUnscriptedTriggers_DoNothing()
    {
        var fixture = new QuestJournalFixture { Templates = [Ravenholdt()] };
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATNONE", "Atnone", seed: false);
        Player player = await host.PlayerAsync("Atnone");
        await WaitLoadedAsync(host, player);

        bool ravenholdt = true, other = true;
        await host.OnWorldAsync(() =>
        {
            ravenholdt = Run(host, player, AreaTriggerScriptsFeature.TriggerRavenholdt);
            other = Run(host, player, 1);
        });

        Assert.False(ravenholdt);
        Assert.False(other);
        Assert.Equal(QuestStatus.None, await Status(host, "Atnone", AreaTriggerScriptsFeature.QuestManorRavenholdt));
    }

    private static QuestTemplate Plain(uint id) => new()
    {
        Entry = id, Method = 2, QuestLevel = 10, MinLevel = 1, Title = $"Quest {id}", Details = "d", Objectives = "o",
    };

    private static CreatureTemplate Template(uint entry) => new()
    {
        Entry = entry, Name = $"Npc{entry}", MinLevel = 60, MaxLevel = 60, DisplayIds = [903], Faction = 35, MinLevelHealth = 500, MaxLevelHealth = 500,
    };

    private static int Count(WorldTestHost host, Player player, uint entry)
        => player.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.Count(c => c.Entry == entry);

    [Fact]
    public async Task AncientLeaf_SummonsTheThreeAncients_Once_WhenTheQuestIsComplete()
    {
        var fixture = new QuestJournalFixture { Templates = [Plain(AreaTriggerScriptsFeature.QuestAncientLeaf)] };
        await using WorldTestHost host = Start(fixture, Template(AreaTriggerScriptsFeature.NpcVartrus), Template(AreaTriggerScriptsFeature.NpcStoma),
            Template(AreaTriggerScriptsFeature.NpcHastat));
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATLEAF", "Atleaf", seed: true, QuestStatus.Complete);
        Player player = await host.PlayerAsync("Atleaf");
        await WaitLoadedAsync(host, player);

        bool first = false, second = true;
        int vartrus = 0, all = 0;
        await host.OnWorldAsync(() =>
        {
            first = Run(host, player, AreaTriggerScriptsFeature.TriggerAncientLeaf);
            // The ancients stand at their Felwood spots, far from this test player, so the 50 yd check is made from one of them.
            Creature stoma = player.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.First(c => c.Entry == AreaTriggerScriptsFeature.NpcStoma);
            player.Relocate(stoma.X, stoma.Y, stoma.Z, 0f, 0);
            second = Run(host, player, AreaTriggerScriptsFeature.TriggerAncientLeaf);
            vartrus = Count(host, player, AreaTriggerScriptsFeature.NpcVartrus);
            all = vartrus + Count(host, player, AreaTriggerScriptsFeature.NpcStoma) + Count(host, player, AreaTriggerScriptsFeature.NpcHastat);
        });

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, vartrus);
        Assert.Equal(3, all);
    }

    [Fact]
    public async Task ScentOfLarkorwi_SummonsTheMateAtTheTriggerCentre_UnlessOneIsNear()
    {
        var fixture = new QuestJournalFixture { Templates = [Plain(AreaTriggerScriptsFeature.QuestScentOfLarkorwi)] };
        await using WorldTestHost host = Start(fixture, Template(AreaTriggerScriptsFeature.NpcLarkorwiMate));
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATLARK", "Atlark", seed: true);
        Player player = await host.PlayerAsync("Atlark");
        await WaitLoadedAsync(host, player);

        bool first = false, second = true;
        (float X, float Y) at = default;
        int count = 0;
        await host.OnWorldAsync(() =>
        {
            (float X, float Y, float Z) centre = (player.X + 5f, player.Y + 3f, player.Z);
            first = AreaTriggerScriptsFeature.Run(host.WorldServices.GetRequiredService<QuestNpcFeature>().Services, player,
                AreaTriggerScriptsFeature.TriggerLarkorwiFirst, centre);
            second = Run(host, player, AreaTriggerScriptsFeature.TriggerLarkorwiFirst + 1);
            Creature mate = player.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.Single(c => c.Entry == AreaTriggerScriptsFeature.NpcLarkorwiMate);
            at = (mate.X - centre.X, mate.Y - centre.Y);
            count = Count(host, player, AreaTriggerScriptsFeature.NpcLarkorwiMate);
        });

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, count);
        Assert.Equal(0f, at.X, 0.01f);
        Assert.Equal(0f, at.Y, 0.01f);
    }

    [Fact]
    public async Task TwilightGrove_SummonsTheCorrupter_WhoSpeaksToThePlayer()
    {
        var fixture = new QuestJournalFixture { Templates = [Plain(AreaTriggerScriptsFeature.QuestNightmareCorruption)] };
        await using WorldTestHost host = Start(fixture, Template(AreaTriggerScriptsFeature.NpcTwilightCorrupter));
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATGROVE", "Atgrove", seed: true);
        Player player = await host.PlayerAsync("Atgrove");
        await WaitLoadedAsync(host, player);

        bool first = false;
        int count = 0;
        await host.OnWorldAsync(() =>
        {
            // The corrupter appears at its Duskwood spot; the player stands next to it, as at the real trigger, to hear it.
            player.Relocate(-10320f, -485f, 50f, 0f, 0);
            first = Run(host, player, AreaTriggerScriptsFeature.TriggerTwilightGrove);
            count = Count(host, player, AreaTriggerScriptsFeature.NpcTwilightCorrupter);
        });

        Assert.True(first);
        Assert.Equal(1, count);
        byte[] chat = await client.ReadUntilAsync(WorldOpcode.SmsgMessagechat);
        Assert.Contains("Come, Atgrove.", Encoding.UTF8.GetString(chat), StringComparison.Ordinal);
    }

    private static bool Run(WorldTestHost host, Player player, uint trigger)
        => AreaTriggerScriptsFeature.Run(host.WorldServices.GetRequiredService<QuestNpcFeature>().Services, player, trigger);

    private static async Task WaitLoadedAsync(WorldTestHost host, Player player)
    {
        QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();
        await host.WaitForWorldAsync(() => feature.Services.StateOf(player) is { Loaded: true }, "the quest journal is loaded");
        await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().ContentInstalled, "the creature content is installed");
    }

    private static Task<QuestStatus> Status(WorldTestHost host, string name, uint quest)
        => host.PlayerStateAsync(name, player =>
            host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.StateOf(player)?.Quests.GetStatus(quest) ?? QuestStatus.None);

    private static WorldTestHost Start(QuestJournalFixture fixture, params CreatureTemplate[] templates)
    {
        // A bystander spawned by the human start, as in the smart trigger tests, so the start map runs a creature system.
        CreatureTemplate bystander = Template(302);
        var spawn = new CreatureSpawn { Guid = 4243, Entry = 302, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f };
        var content = new CreatureContent([.. templates, bystander], [spawn], [], [], [],
            new CreatureAiContent([], [], new BroadcastTextCatalog(
                [new BroadcastText(AreaTriggerScriptsFeature.TextTwilightCorrupterSpawn, "Come, $N. See what the Nightmare brings...", "", 0, 0, 0, [0, 0, 0], [0, 0, 0])]),
                [], EventAiDialect.CMangos, []));
        QuestNpcTestServices.Current.Value = fixture;
        CreatureTestStore.Current.Value = new CreatureTestContext(content);
        try { return WorldTestHost.Start(); }
        finally
        {
            QuestNpcTestServices.Current.Value = null;
            CreatureTestStore.Current.Value = null;
        }
    }

    private static async Task<WorldTestClient> EnterAsync(WorldTestHost host, QuestJournalFixture fixture, string account, string name, bool seed, QuestStatus status = QuestStatus.Incomplete)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
        var stored = (await host.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(stored.Id)).Single();
        if (seed)
        {
            fixture.Characters.Seed([.. fixture.Templates!.Select(t => fixture.Progress(record.Id, t.Entry, 0, 0) with { Status = (byte)status })]);
        }

        await client.LoginAsync((ulong)record.Id);
        return client;
    }
}
