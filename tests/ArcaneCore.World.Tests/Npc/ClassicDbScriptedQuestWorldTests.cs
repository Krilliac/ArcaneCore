using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>
/// The daemon's wiring of the quest DB scripts (QuestNpcFeature attaches CreatureQuestScripts to the creature feature's systems): a client takes
/// classic-db quest 2843 from Scooty over the socket, with the imported z2815 rows as the world's quest and creature content, and the quest's
/// start script (QUEST_EXPLORED at 10 s) is scheduled on the creature system of the map. The quest is an event quest that only its script
/// completes, so it is offered at all only because the script's credit counts (DbScriptQuestCredit).
/// </summary>
public sealed class ClassicDbScriptedQuestWorldTests
{
    private static readonly ObjectGuid Scooty = ObjectGuid.WithEntry(HighGuid.Unit, 7853, 2);

    [Fact]
    public async Task TakingQuest2843OverTheSocket_SchedulesItsStartScript()
    {
        (CreatureContent creatures, QuestContent quests) = await ClassicDbScriptedQuestTests.Content;
        var fixture = new ScriptedQuestWorldFixture(creatures, quests);
        ScriptedQuestWorldServices.Current.Value = fixture;
        WorldTestHost started;
        try { started = WorldTestHost.Start(); }
        finally { ScriptedQuestWorldServices.Current.Value = null; }

        await using WorldTestHost host = started;
        byte[] key = await host.AddAccountAsync("DBSQUEST");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("DBSQUEST", key);
        await client.CreateCharacterAsync("Dbsquest", race: 2);
        Account account = (await host.Accounts.FindByUsernameAsync("DBSQUEST"))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        fixture.Characters.Seed(new CharacterQuestStatus(record.Id, 2842, (byte)QuestStatus.Complete, true, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        await client.LoginAsync((ulong)record.Id);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Dbsquest")!.Level = 30);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Dbsquest")!.VisibleObjects.Contains(Scooty), "Scooty visible");

        var body = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(body, Scooty.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 2843);
        await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, body);
        await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);

        CreatureWorldFeature creatureFeature = host.WorldServices.GetRequiredService<CreatureWorldFeature>();
        (QuestStatus status, int pending) = await host.PlayerStateAsync("Dbsquest", player =>
        {
            QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();
            return (feature.Services.StateOf(player)!.Quests.GetStatus(2843), creatureFeature.FindSystem(player.Map!)!.PendingDbScriptSteps);
        });
        Assert.Equal(QuestStatus.Incomplete, status);
        Assert.Equal(1, pending); // dbscripts_on_quest_start 2843: QUEST_EXPLORED 2843 at 10 s, waiting on the map clock
    }
}

internal sealed class ScriptedQuestWorldServices : IWorldTestServices
{
    public static readonly AsyncLocal<ScriptedQuestWorldFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<IQuestContentStore>(fixture);
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<ICharacterQuestStore>(fixture.Characters);
        services.AddSingleton<IWorldDataStore>(new NextToScooty());
        // The orc player (template 2) and every creature faction of the excerpt are on speaking terms.
        services.AddSingleton(new FactionTemplateCatalog([
            new(2, 1, 0, 1, 0, 0), .. fixture.CreatureFactions.Select(f => new FactionTemplateRecord(f, 0, 0, 8, 0, 0))]));
    }
}

/// <summary>The imported content as the world's stores (it was loaded through EfCreatureDataStore and EfQuestContentStore).</summary>
internal sealed class ScriptedQuestWorldFixture(CreatureContent creatures, QuestContent quests) : IQuestContentStore, ICreatureDataStore
{
    public MemoryQuestStore Characters { get; } = new();

    public IEnumerable<uint> CreatureFactions => creatures.Templates.Select(t => t.Faction).Distinct();

    Task<QuestContent> IQuestContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(quests);

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(creatures);
}

/// <summary>Orc warriors start two yards from Scooty's spawn (classic-db creature guid 2, Booty Bay).</summary>
internal sealed class NextToScooty : IWorldDataStore
{
    private readonly InMemoryWorldDataStore _defaults = new();

    public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => Task.FromResult<StartPosition?>(new StartPosition(0, 33, -14462.9f, 459.585f, 15.2488f, 0f));

    public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default)
        => _defaults.GetRaceInfoAsync(race, gender, cancellationToken);

    public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default)
        => _defaults.GetClassInfoAsync(cls, cancellationToken);

    public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => _defaults.IsValidRaceClassAsync(race, cls, cancellationToken);
}
