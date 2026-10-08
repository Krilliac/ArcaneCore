using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>
/// The creature systems' <see cref="CreatureAiServices.ScriptQuests"/> as the world binds it (<see cref="CreatureAiServicesBinder"/>): the
/// DB-script quest commands (QUEST_EXPLORED 7, KILL_CREDIT 8, also reached by relay scripts) and a player-linked escort's failure reach the
/// live quest log of a logged-in player.
/// </summary>
public sealed class QuestDbScriptWiringTests
{
    private const uint Explored = 9401;
    private const uint Escorted = 9402;

    private static QuestTemplate Event(uint id) => new()
    {
        Entry = id, Method = 2, QuestLevel = 1, MinLevel = 1, Title = $"Event {id}", Details = "Do it.", Objectives = "Do it.",
        SpecialFlags = (byte)QuestSpecialFlags.ExplorationOrEvent,
    };

    [Fact]
    public async Task ScriptQuests_CreditsAndFailsTheLoggedQuests_ThroughTheQuestService()
    {
        var fixture = new QuestJournalFixture { Templates = [Event(Explored), Event(Escorted)] };
        QuestNpcTestServices.Current.Value = fixture;
        WorldTestHost started;
        try { started = WorldTestHost.Start(); }
        finally { QuestNpcTestServices.Current.Value = null; }

        await using WorldTestHost host = started;
        byte[] key = await host.AddAccountAsync("DBSWIRE");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("DBSWIRE", key);
        await client.CreateCharacterAsync("Dbswire");
        var stored = (await host.Accounts.FindByUsernameAsync("DBSWIRE"))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(stored.Id)).Single();
        fixture.Characters.Seed([.. fixture.Templates!.Select(t => fixture.Progress(record.Id, t.Entry, 0, 0))]);
        await client.LoginAsync((ulong)record.Id);

        IScriptQuestEvents quests = CreatureAiServicesBinder.Build(host.WorldServices, new CreatureOptions()).ScriptQuests!;
        (bool explored, QuestStatus escorted) = await host.PlayerStateAsync("Dbswire", player =>
        {
            quests.AreaExploredOrEventHappens(player, Explored);
            quests.GroupEventFailHappens(player, Escorted);
            QuestNpcFeature feature = ((WorldSession)player.Session).Services.GetRequiredService<QuestNpcFeature>();
            PlayerQuestLog log = feature.Services.StateOf(player)!.Quests;
            return (log.Get(Explored)!.Explored, log.Get(Escorted)!.Status);
        });

        Assert.True(explored);
        Assert.Equal(QuestStatus.Failed, escorted);
    }
}
