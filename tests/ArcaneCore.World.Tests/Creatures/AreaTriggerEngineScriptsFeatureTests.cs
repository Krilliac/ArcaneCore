using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.World;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Creatures;

/// <summary>
/// <see cref="AreaTriggerEngineScriptsFeature"/> (cmangos world/areatrigger_scripts.cpp, slice 2) and the engine pieces under it: world map
/// script data (<see cref="KalimdorWorldMapData"/>), temporary factions, AI events sent to a creature, and closest-of-entry lookups.
/// </summary>
public sealed class AreaTriggerEngineScriptsFeatureTests
{
    private sealed class RecordingAI(Creature creature) : CreatureAI(creature)
    {
        public List<(uint Type, Unit Sender, Unit? Invoker)> Events { get; } = [];

        public override void OnReceiveAiEvent(uint eventType, Unit sender, Unit? invoker, uint miscValue) => Events.Add((eventType, sender, invoker));
    }

    [Fact]
    public void Feature_IsDiscoveredAsAnAreaTriggerListener()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var feature = new AreaTriggerEngineScriptsFeature(services);
        Assert.IsAssignableFrom<IAreaTriggerListener>(feature);
        Assert.IsAssignableFrom<IWorldFeature>(feature);
        Assert.Throws<ArgumentNullException>(() => feature.Attach(null!));
    }

    [Fact]
    public async Task HiveTower_SummonsThreeDrones_ThenWaitsForTheWorldMapTimer()
    {
        var fixture = new QuestJournalFixture { Templates = [Plain(1)] };
        await using WorldTestHost host = Start(fixture, Template(KalimdorWorldMapData.NpcHiveAshiDrone));
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATHIVE", "Athive", seed: false);
        Player player = await host.PlayerAsync("Athive");
        await WaitLoadedAsync(host, player);

        bool first = false, second = true, afterTimer = false;
        int drones = 0, dronesAfter = 0;
        uint during = 0;
        await host.OnWorldAsync(() =>
        {
            KalimdorWorldMapData data = InstallKalimdor(player);
            first = Run(host, player, AreaTriggerEngineScriptsFeature.TriggerHiveTower);
            drones = Count(player, KalimdorWorldMapData.NpcHiveAshiDrone);
            during = data.GetData(KalimdorWorldMapData.TypeHive);
            second = Run(host, player, AreaTriggerEngineScriptsFeature.TriggerHiveTower);
            data.Update(KalimdorWorldMapData.HiveDronesTimerMs);
            afterTimer = Run(host, player, AreaTriggerEngineScriptsFeature.TriggerHiveTower);
            dronesAfter = Count(player, KalimdorWorldMapData.NpcHiveAshiDrone);
        });

        Assert.True(first);
        Assert.Equal(3, drones);
        Assert.Equal(EncounterState.InProgress, during);
        Assert.False(second);
        Assert.True(afterTimer);
        Assert.Equal(6, dronesAfter);
    }

    [Fact]
    public async Task EasternKingdoms_GetsItsWorldMapScript_AndTheHiveTowerStaysKalimdors()
    {
        var fixture = new QuestJournalFixture { Templates = [Plain(1)] };
        await using WorldTestHost host = Start(fixture, Template(KalimdorWorldMapData.NpcHiveAshiDrone));
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATEKMAP", "Atekmap", seed: false);
        Player player = await host.PlayerAsync("Atekmap");
        await WaitLoadedAsync(host, player);

        InstanceData? data = null;
        await host.OnWorldAsync(() => data = player.Map!.FindUpdater<InstanceData>());

        Assert.IsType<EasternKingdomsWorldMapData>(data);
    }

    [Fact]
    public async Task HiveTower_WithoutWorldMapData_DoesNothing()
    {
        var fixture = new QuestJournalFixture { Templates = [Plain(1)] };
        await using WorldTestHost host = Start(fixture, Template(KalimdorWorldMapData.NpcHiveAshiDrone));
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATNOMAP", "Atnomap", seed: false);
        Player player = await host.PlayerAsync("Atnomap");
        await WaitLoadedAsync(host, player);

        bool ran = true;
        int drones = -1;
        await host.OnWorldAsync(() =>
        {
            ran = Run(host, player, AreaTriggerEngineScriptsFeature.TriggerHiveTower);
            drones = Count(player, KalimdorWorldMapData.NpcHiveAshiDrone);
        });

        Assert.False(ran);
        Assert.Equal(0, drones);
    }

    [Fact]
    public async Task Murkdeep_CoastrunnersThenWarriorsThenMurkdeep()
    {
        var fixture = new QuestJournalFixture { Templates = [Plain(KalimdorWorldMapData.QuestWantedMurkdeep)] };
        await using WorldTestHost host = Start(fixture, Template(KalimdorWorldMapData.NpcGreymistCoastrunner), Template(KalimdorWorldMapData.NpcGreymistWarrior),
            Template(KalimdorWorldMapData.NpcGreymistHunter), Template(KalimdorWorldMapData.NpcMurkdeep));
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATMURK", "Atmurk", seed: true);
        Player player = await host.PlayerAsync("Atmurk");
        await WaitLoadedAsync(host, player);

        int runners = 0, warriors = 0, murkdeep = 0, hunters = 0;
        bool stored = false, again = false;
        int runnersAfterAgain = 0;
        await host.OnWorldAsync(() =>
        {
            KalimdorWorldMapData data = InstallKalimdor(player);
            Run(host, player, AreaTriggerEngineScriptsFeature.TriggerMurkdeep);
            runners = Count(player, KalimdorWorldMapData.NpcGreymistCoastrunner);
            foreach (Creature c in Of(player, KalimdorWorldMapData.NpcGreymistCoastrunner))
            {
                data.OnCreatureDeath(c);
            }

            warriors = Count(player, KalimdorWorldMapData.NpcGreymistWarrior);
            foreach (Creature c in Of(player, KalimdorWorldMapData.NpcGreymistWarrior))
            {
                data.OnCreatureDeath(c);
            }

            murkdeep = Count(player, KalimdorWorldMapData.NpcMurkdeep);
            hunters = Count(player, KalimdorWorldMapData.NpcGreymistHunter);
            stored = data.FindStoredCreature(KalimdorWorldMapData.NpcMurkdeep) is not null;
            // With Murkdeep up, the trigger does nothing more (the player stands far from the coast, so only the store stops it).
            again = Run(host, player, AreaTriggerEngineScriptsFeature.TriggerMurkdeep);
            runnersAfterAgain = Count(player, KalimdorWorldMapData.NpcGreymistCoastrunner);
        });

        Assert.Equal(3, runners);
        Assert.Equal(2, warriors);
        Assert.Equal(1, murkdeep);
        Assert.Equal(1, hunters);
        Assert.True(stored);
        Assert.True(again);
        Assert.Equal(3, runnersAfterAgain);
    }

    [Fact]
    public async Task HuldarMiran_CompletesTheQuest_AndSaeanTurnsHostileWithTwoAmbushers()
    {
        var fixture = new QuestJournalFixture { Templates = [Plain(AreaTriggerEngineScriptsFeature.QuestResupplyingTheExcavation)] };
        await using WorldTestHost host = Start(fixture, Template(AreaTriggerEngineScriptsFeature.NpcMiran), Template(AreaTriggerEngineScriptsFeature.NpcHuldar),
            Template(AreaTriggerEngineScriptsFeature.NpcSaean), Template(AreaTriggerEngineScriptsFeature.NpcDarkIronAmbusher));
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATHULD", "Athuld", seed: true);
        Player player = await host.PlayerAsync("Athuld");
        await WaitLoadedAsync(host, player);

        bool ran = false;
        uint saeanFaction = 0;
        TemporaryFactionFlags flags = default;
        int ambushers = 0;
        await host.OnWorldAsync(() =>
        {
            CreatureMapSystem creatures = player.Map!.FindUpdater<CreatureMapSystem>()!;
            creatures.SummonInstanceCreature(AreaTriggerEngineScriptsFeature.NpcMiran, player.X + 3f, player.Y, player.Z, 0f);
            creatures.SummonInstanceCreature(AreaTriggerEngineScriptsFeature.NpcHuldar, player.X, player.Y + 3f, player.Z, 0f);
            ran = Run(host, player, AreaTriggerEngineScriptsFeature.TriggerHuldarMiran);
            Creature saean = Of(player, AreaTriggerEngineScriptsFeature.NpcSaean).Single();
            saeanFaction = saean.FactionTemplate;
            flags = saean.TemporaryFactionFlags;
            ambushers = Count(player, AreaTriggerEngineScriptsFeature.NpcDarkIronAmbusher);
        });

        Assert.True(ran);
        Assert.Equal(AreaTriggerEngineScriptsFeature.FactionHostile, saeanFaction);
        Assert.Equal(TemporaryFactionFlags.RestoreRespawn, flags);
        Assert.Equal(2, ambushers);
        Assert.Equal(QuestStatus.Complete, await Status(host, "Athuld", AreaTriggerEngineScriptsFeature.QuestResupplyingTheExcavation));
    }

    [Fact]
    public async Task TemporaryFaction_RestoresOnlyAtItsOwnPoints()
    {
        var fixture = new QuestJournalFixture { Templates = [Plain(1)] };
        await using WorldTestHost host = Start(fixture, Template(AreaTriggerEngineScriptsFeature.NpcSaean));
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATFACT", "Atfact", seed: false);
        Player player = await host.PlayerAsync("Atfact");
        await WaitLoadedAsync(host, player);

        uint afterCombatStop = 0, afterHome = 0, afterClear = 0;
        await host.OnWorldAsync(() =>
        {
            Creature saean = player.Map!.FindUpdater<CreatureMapSystem>()!.SummonInstanceCreature(AreaTriggerEngineScriptsFeature.NpcSaean, player.X, player.Y, player.Z, 0f)!;
            saean.SetFactionTemporary(14, TemporaryFactionFlags.RestoreReachHome);
            saean.RestoreTemporaryFactionAt(TemporaryFactionFlags.RestoreCombatStop);
            afterCombatStop = saean.FactionTemplate;
            saean.RestoreTemporaryFactionAt(TemporaryFactionFlags.RestoreReachHome);
            afterHome = saean.FactionTemplate;
            saean.SetFactionTemporary(14, TemporaryFactionFlags.None);
            saean.ClearTemporaryFaction();
            afterClear = saean.FactionTemplate;
        });

        Assert.Equal(14u, afterCombatStop);
        Assert.Equal(35u, afterHome);
        Assert.Equal(35u, afterClear);
    }

    [Fact]
    public async Task TemporaryFaction_WithoutRestoreRespawn_OutlivesTheRespawn()
    {
        var fixture = new QuestJournalFixture { Templates = [Plain(1)] };
        await using WorldTestHost host = Start(fixture, Template(AreaTriggerEngineScriptsFeature.NpcSaean));
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATPERS", "Atpers", seed: false);
        Player player = await host.PlayerAsync("Atpers");
        await WaitLoadedAsync(host, player);

        uint persistent = 0, restored = 0;
        await host.OnWorldAsync(() =>
        {
            Creature a = player.Map!.FindUpdater<CreatureMapSystem>()!.SummonInstanceCreature(AreaTriggerEngineScriptsFeature.NpcSaean, player.X, player.Y, player.Z, 0f)!;
            a.SetFactionTemporary(14, TemporaryFactionFlags.None);
            a.InitializeFields();
            persistent = a.FactionTemplate;
            a.SetFactionTemporary(14, TemporaryFactionFlags.RestoreRespawn);
            a.InitializeFields();
            restored = a.FactionTemplate;
        });

        Assert.Equal(14u, persistent);
        Assert.Equal(35u, restored);
    }

    [Fact]
    public async Task StormwindRecruiter_SendsCustomEventA_ToJustine_ForAnAllianceRecruit()
    {
        var fixture = new QuestJournalFixture { Templates = [Plain(1)] };
        await using WorldTestHost host = Start(fixture, Template(AreaTriggerEngineScriptsFeature.NpcJustineDemalier));
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATRECR", "Atrecr", seed: false);
        Player player = await host.PlayerAsync("Atrecr");
        await WaitLoadedAsync(host, player);

        bool returned = true;
        RecordingAI? ai = null;
        await host.OnWorldAsync(() =>
        {
            Creature justine = player.Map!.FindUpdater<CreatureMapSystem>()!
                .SummonInstanceCreature(AreaTriggerEngineScriptsFeature.NpcJustineDemalier, player.X + 2f, player.Y, player.Z, 0f)!;
            ai = new RecordingAI(justine);
            justine.AI = ai;
            returned = Run(host, player, AreaTriggerEngineScriptsFeature.TriggerStormwindRecruiter);
        });

        Assert.False(returned);
        (uint type, Unit sender, Unit? invoker) = Assert.Single(ai!.Events);
        Assert.Equal(AiEventTypes.CustomEventAiA, type);
        Assert.Equal(AreaTriggerEngineScriptsFeature.NpcJustineDemalier, ((Creature)sender).Entry);
        Assert.Same(player, invoker);
    }

    [Fact]
    public async Task Wondervolt_WithoutATrapNearby_DoesNothing()
    {
        var fixture = new QuestJournalFixture { Templates = [Plain(1)] };
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await EnterAsync(host, fixture, "ATVOLT", "Atvolt", seed: false);
        Player player = await host.PlayerAsync("Atvolt");
        await WaitLoadedAsync(host, player);

        bool ran = true;
        await host.OnWorldAsync(() => ran = Run(host, player, AreaTriggerEngineScriptsFeature.TriggerWondervoltFirst));
        Assert.False(ran);
    }

    private static KalimdorWorldMapData InstallKalimdor(Player player)
    {
        // The test world runs Eastern Kingdoms only; the Kalimdor script takes the place of its Eastern Kingdoms one, the way
        // WorldMapScriptsFeature puts it on map 1.
        if (player.Map!.FindUpdater<InstanceData>() is WorldMapData eastern)
        {
            Assert.True(player.Map.RemoveUpdater(eastern));
        }

        var data = new KalimdorWorldMapData(player.Map!);
        data.Initialize();
        player.Map!.AddUpdater(data);
        return data;
    }

    private static IEnumerable<Creature> Of(Player player, uint entry)
        => [.. player.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.Where(c => c.Entry == entry)];

    private static int Count(Player player, uint entry) => Of(player, entry).Count();

    private static QuestTemplate Plain(uint id) => new()
    {
        Entry = id, Method = 2, QuestLevel = 10, MinLevel = 1, Title = $"Quest {id}", Details = "d", Objectives = "o",
    };

    private static CreatureTemplate Template(uint entry) => new()
    {
        Entry = entry, Name = $"Npc{entry}", MinLevel = 60, MaxLevel = 60, DisplayIds = [903], Faction = 35, MinLevelHealth = 500, MaxLevelHealth = 500,
    };

    private static bool Run(WorldTestHost host, Player player, uint trigger)
        => AreaTriggerEngineScriptsFeature.Run(host.WorldServices.GetRequiredService<QuestNpcFeature>().Services, player, trigger);

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
        CreatureTemplate bystander = Template(302);
        var spawn = new CreatureSpawn { Guid = 4243, Entry = 302, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f };
        var content = new CreatureContent([.. templates, bystander], [spawn], [], [], [],
            new CreatureAiContent([], [], new BroadcastTextCatalog(
                [new BroadcastText(AreaTriggerEngineScriptsFeature.TextMiranAmbush, "Help! We're being attacked! Dark Irons!", "", 1, 0, 0, [0, 0, 0], [0, 0, 0])]),
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
            fixture.Characters.Seed([.. fixture.Templates!.Select(t => fixture.Progress(record.Id, t.Entry, 0, 0) with { Status = (byte)QuestStatus.Incomplete })]);
        }

        await client.LoginAsync((ulong)record.Id);
        return client;
    }
}
