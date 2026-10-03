using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ArcaneCore.World.Tests.Creatures;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// <c>.reload creature_template</c> (vmangos HandleReloadCreatureTemplatesCommand,
/// ServerCommands.cpp:1758-1773 → ObjectMgr::LoadCreatureTemplates, ObjectMgr.cpp:1190): the creature
/// definitions are rebuilt off the world thread and swapped into the live content object, so running
/// creatures, map systems and queries all see them. Spawns are not touched.
/// </summary>
public sealed class CreatureReloadTests
{
    private const uint WolfEntry = 299;
    private const uint SpawnGuid = 4242;

    private static CreatureTemplate Wolf(string name = "Young Wolf", uint faction = 32) => new()
    {
        Entry = WolfEntry, Name = name, MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = faction, CreatureType = 1, Family = 1, MinLevelHealth = 55, MaxLevelHealth = 55,
    };

    private static CreatureSpawn Spawn() => new() { Guid = SpawnGuid, Entry = WolfEntry, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f };

    private static CreatureContent ContentOf(params CreatureTemplate[] templates) => new(templates, [Spawn()], [], [], []);

    private static WorldTestHost Start(CreatureContent? content, out CreatureTestContext context)
    {
        context = new CreatureTestContext(content ?? CreatureContent.Empty);
        CreatureTestStore.Current.Value = context;
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }

    private static ReloadCoordinator Reloader(WorldTestHost host, CreatureWorldFeature feature, ICreatureDataStore? store)
    {
        IServiceCollection services = new ServiceCollection().AddSingleton(feature);
        if (store is not null)
        {
            services.AddSingleton(store);
        }

        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new CreatureContentReloadable(services.BuildServiceProvider()));
        return coordinator;
    }

    private static async Task<Creature> WolfAsync(WorldTestHost host, CreatureWorldFeature feature)
    {
        var guid = ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, SpawnGuid);
        await host.WaitForWorldAsync(() => feature.FindSystem(0)?.FindCreature(guid) is not null, "the wolf spawns");
        return await host.OnWorldAsync(() => feature.FindSystem(0)!.FindCreature(guid)!);
    }

    [Fact]
    public async Task ARunningCreature_AndTheQuery_SeeTheNewTemplate()
    {
        await using WorldTestHost host = Start(ContentOf(Wolf()), out CreatureTestContext context);
        CreatureWorldFeature feature = context.Feature!;
        await using WorldTestClient client = await host.EnterWorldAsync("RELOADER", "Reloader");
        Creature wolf = await WolfAsync(host, feature);
        ReloadCoordinator coordinator = Reloader(host, feature, new FixedStore(ContentOf(Wolf("Dire Wolf"))));

        ReloadResult result = await coordinator.ReloadAsync("creature_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal("Dire Wolf", feature.Content.FindTemplate(WolfEntry)?.Name);
        Assert.Equal("Dire Wolf", await host.OnWorldAsync(() => wolf.Template.Name));
        var query = new PacketWriter(12);
        query.WriteUInt32(WolfEntry);
        query.WriteUInt64(0);
        await client.CollectAsync();
        await client.SendAsync(WorldOpcode.CmsgCreatureQuery, query.ToArray());
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgCreatureQueryResponse));
        Assert.Equal(WolfEntry, reader.ReadUInt32());
        Assert.Equal("Dire Wolf", reader.ReadCString());
    }

    [Fact]
    public async Task ARespawningCreature_TakesItsFieldsFromTheNewTemplate()
    {
        await using WorldTestHost host = Start(ContentOf(Wolf()), out CreatureTestContext context);
        CreatureWorldFeature feature = context.Feature!;
        await using WorldTestClient client = await host.EnterWorldAsync("RESPAWNER", "Respawner");
        Creature wolf = await WolfAsync(host, feature);
        Assert.Equal(32u, await host.OnWorldAsync(() => wolf.FactionTemplate));
        ReloadCoordinator coordinator = Reloader(host, feature, new FixedStore(ContentOf(Wolf(faction: 33))));
        await coordinator.ReloadAsync("creature_template");

        await host.OnWorldAsync(() =>
        {
            CreatureMapSystem system = feature.FindSystem(0)!;
            system.KillCreature(wolf);
            system.ForceRespawn(wolf);
        });

        Assert.Equal(33u, await host.OnWorldAsync(() => wolf.FactionTemplate));
    }

    [Fact]
    public async Task AnEmptyTable_KeepsTheLoadedDefinitions()
    {
        await using WorldTestHost host = Start(ContentOf(Wolf()), out CreatureTestContext context);
        CreatureWorldFeature feature = context.Feature!;
        ReloadCoordinator coordinator = Reloader(host, feature, new FixedStore(new CreatureContent([], [], [], [], [])));

        ReloadResult result = await coordinator.ReloadAsync("creature_template");

        Assert.Equal(ReloadStatus.KeptCurrent, result.Status);
        Assert.Equal("Young Wolf", feature.Content.FindTemplate(WolfEntry)?.Name);
    }

    [Fact]
    public async Task ARemovedTemplate_IsReported_AndTheLiveCreatureKeepsItsLastOne()
    {
        await using WorldTestHost host = Start(ContentOf(Wolf()), out CreatureTestContext context);
        CreatureWorldFeature feature = context.Feature!;
        await using WorldTestClient client = await host.EnterWorldAsync("REMOVER", "Remover");
        Creature wolf = await WolfAsync(host, feature);
        var other = new CreatureTemplate { Entry = 1, Name = "Other", DisplayIds = [1] };
        ReloadCoordinator coordinator = Reloader(host, feature, new FixedStore(new CreatureContent([other], [], [], [], [])));

        ReloadResult result = await coordinator.ReloadAsync("creature_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Contains(result.Notes, n => n.Contains("1 spawn(s)", StringComparison.Ordinal) && n.Contains($"{WolfEntry}", StringComparison.Ordinal));
        Assert.Null(feature.Content.FindTemplate(WolfEntry));
        Assert.Equal("Young Wolf", await host.OnWorldAsync(() => wolf.Template.Name));
    }

    [Fact]
    public async Task AFailingStore_ChangesNothing()
    {
        await using WorldTestHost host = Start(ContentOf(Wolf()), out CreatureTestContext context);
        CreatureWorldFeature feature = context.Feature!;
        ReloadCoordinator coordinator = Reloader(host, feature, new FixedStore(null!) { Failure = new InvalidOperationException("world database unavailable") });

        ReloadResult result = await coordinator.ReloadAsync("creature_template");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("world database unavailable", result.Message);
        Assert.Equal("Young Wolf", feature.Content.FindTemplate(WolfEntry)?.Name);
    }

    [Fact]
    public async Task WithoutACreatureStore_TheReloadFails()
    {
        await using WorldTestHost host = Start(ContentOf(Wolf()), out CreatureTestContext context);
        ReloadCoordinator coordinator = Reloader(host, context.Feature!, store: null);

        ReloadResult result = await coordinator.ReloadAsync("creature_template");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("creature data store", result.Message);
    }

    [Fact]
    public async Task AWorldThatStartedWithoutCreatures_GetsThemFromTheReload_WithoutTouchingTheSharedEmptyContent()
    {
        await using WorldTestHost host = Start(content: null, out CreatureTestContext context);
        CreatureWorldFeature feature = context.Feature!;
        Assert.Same(CreatureContent.Empty, feature.Content);
        ReloadCoordinator coordinator = Reloader(host, feature, new FixedStore(ContentOf(Wolf())));

        ReloadResult result = await coordinator.ReloadAsync("creature_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(0, CreatureContent.Empty.TemplateCount);
        Assert.Equal("Young Wolf", feature.Content.FindTemplate(WolfEntry)?.Name);
        Assert.NotNull(await host.OnWorldAsync(() => feature.FindSystem(0)));
    }

    [Fact]
    public async Task TheReloadFeature_RegistersCreatureTemplate_ThroughDiscovery()
    {
        await using WorldTestHost host = Start(ContentOf(Wolf()), out _);

        Assert.Contains("creature_template", host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator.Names);
    }

    private sealed class FixedStore(CreatureContent content) : ICreatureDataStore
    {
        public Exception? Failure { get; init; }

        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default)
            => Failure is null ? Task.FromResult(content) : Task.FromException<CreatureContent>(Failure);
    }
}
