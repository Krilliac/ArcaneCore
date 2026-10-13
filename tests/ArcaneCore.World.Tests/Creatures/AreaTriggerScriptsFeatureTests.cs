using ArcaneCore.Game.Entities;
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

    private static bool Run(WorldTestHost host, Player player, uint trigger)
        => AreaTriggerScriptsFeature.Run(host.WorldServices.GetRequiredService<QuestNpcFeature>().Services, player, trigger);

    private static async Task WaitLoadedAsync(WorldTestHost host, Player player)
    {
        QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();
        await host.WaitForWorldAsync(() => feature.Services.StateOf(player) is { Loaded: true }, "the quest journal is loaded");
    }

    private static Task<QuestStatus> Status(WorldTestHost host, string name, uint quest)
        => host.PlayerStateAsync(name, player =>
            host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.StateOf(player)?.Quests.GetStatus(quest) ?? QuestStatus.None);

    private static WorldTestHost Start(QuestJournalFixture fixture)
    {
        QuestNpcTestServices.Current.Value = fixture;
        try { return WorldTestHost.Start(); }
        finally { QuestNpcTestServices.Current.Value = null; }
    }

    private static async Task<WorldTestClient> EnterAsync(WorldTestHost host, QuestJournalFixture fixture, string account, string name, bool seed)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
        var stored = (await host.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(stored.Id)).Single();
        if (seed)
        {
            fixture.Characters.Seed([.. fixture.Templates!.Select(t => fixture.Progress(record.Id, t.Entry, 0, 0))]);
        }

        await client.LoginAsync((ulong)record.Id);
        return client;
    }
}
