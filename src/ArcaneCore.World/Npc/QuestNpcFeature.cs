using System.Runtime.CompilerServices;
using ArcaneCore.Data.Npc;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Persisted quest journals and query content in the daemon. Session hooks fill the untracked
/// player's update fields before self-create; only world-thread login events register journals
/// with the quest service. Creature quest interactions use live map/visibility snapshots and
/// optional faction templates; missing reaction data denies interaction.
/// </summary>
public sealed class QuestNpcFeature : IWorldFeature, ICharacterHooks, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<QuestNpcFeature> _logger;
    private readonly TimeProvider _clock;
    private readonly ConditionalWeakTable<Player, CharacterQuestData> _staged = new();
    private readonly HashSet<Map> _maps = [];
    private WorldRuntime? _world;

    public QuestNpcFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<QuestNpcFeature> logger,
        TimeProvider? timeProvider = null)
    {
        _services = services;
        _scopes = scopes;
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;
        Persistence = new QuestNpcPersistence(scopes, logger);
        Services = BuildServices(QuestStore.Empty, NpcStore.Empty);
    }

    public QuestNpcServices Services { get; private set; }

    public QuestNpcPersistence Persistence { get; }

    public QuestNpcOptions Options { get; } = new();

    /// <summary>Startup content failures stop attachment; absent stores provide empty content.</summary>
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the quest feature is already attached");
        }

        _services.GetService<IConfiguration>()?.GetSection(QuestNpcOptions.SectionName).Bind(Options);
        using (IServiceScope scope = _scopes.CreateScope())
        {
            QuestContent quests = scope.ServiceProvider.GetService<IQuestContentStore>() is { } questStore
                ? questStore.LoadAsync().GetAwaiter().GetResult()
                : QuestContent.Empty;
            NpcContent npcs = scope.ServiceProvider.GetService<INpcContentStore>() is { } npcStore
                ? npcStore.LoadAsync().GetAwaiter().GetResult()
                : NpcContent.Empty;
            FactionTemplateCatalog factions = _services.GetService<FactionTemplateCatalog>()
                ?? (string.IsNullOrWhiteSpace(Options.FactionTemplateDbcPath)
                    ? FactionTemplateCatalog.Empty
                    : FactionTemplateDbcReader.Load(Options.FactionTemplateDbcPath));
            Services = BuildServices(new QuestStore(quests), new NpcStore(npcs), factions);
        }

        _world = world;
        Persistence.Start();
        world.MapCreated += OnMapCreated;
        foreach (Map map in world.Maps)
        {
            OnMapCreated(map);
        }

        world.PlayerLoggedIn += OnPlayerLoggedIn;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
        _logger.LogInformation("Loaded {Quests} quest templates for persisted journals and queries", Services.Quests.Count);
    }

    /// <summary>
    /// Player::_LoadQuestStatus. Database access and the save barrier stay on the session task;
    /// this private journal fills fields without touching QuestNpcServices' world-owned registry.
    /// An already-expired timer fails after the login sequence, as in vmangos Player::Update.
    /// </summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        await Persistence.FlushCharacterAsync(character.Id).ConfigureAwait(false);
        long revision = Persistence.CaptureLoadRevision(character.Id);
        CharacterQuestData loaded = session.Services.GetService<ICharacterQuestStore>() is { } store
            ? await store.LoadAsync(character.Id).ConfigureAwait(false)
            : CharacterQuestData.Empty;
        // Own the lists across the session/world handoff even if a custom store reuses buffers.
        var data = new CharacterQuestData(loaded.Quests.ToArray(), loaded.TaxiMask.ToArray());
        Persistence.LoadCharacter(character.Id, data, revision);
        new PlayerQuestLog(player).Load(data.Quests, Services.Quests);
        _staged.Remove(player);
        _staged.Add(player, data);
    }

    public async ValueTask DisposeAsync()
    {
        if (_world is { } world)
        {
            world.MapCreated -= OnMapCreated;
            world.PlayerLoggedIn -= OnPlayerLoggedIn;
            world.PlayerLoggingOut -= OnPlayerLoggingOut;
        }

        await Persistence.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The world has stopped; drain quest/taxi saves before host shutdown succeeds.</summary>
    public Task StopAsync() => Persistence.DisposeAsync().AsTask();

    private QuestNpcServices BuildServices(QuestStore quests, NpcStore npcs, FactionTemplateCatalog? factions = null) => new(quests, npcs,
        new QuestNpcDependencies(Creatures: new CreatureQuestLookup(factions ?? FactionTemplateCatalog.Empty)),
        Options, new PersistenceSink(this), () => _clock.GetUtcNow().ToUnixTimeSeconds(), _logger);

    private void OnMapCreated(Map map)
    {
        if (_maps.Add(map))
        {
            map.AddUpdater(new QuestNpcMapUpdater(Services));
        }
    }

    private void OnPlayerLoggedIn(Player player)
    {
        CharacterQuestData data = _staged.TryGetValue(player, out CharacterQuestData? staged)
            ? staged
            : CharacterQuestData.Empty;
        _staged.Remove(player);
        if (player.Map is { } map)
        {
            OnMapCreated(map);
        }

        Services.CompleteLoad(Services.Track(player), data);
    }

    private void OnPlayerLoggingOut(Player player)
    {
        Services.Untrack(player);
        _staged.Remove(player);
    }

    private sealed class PersistenceSink(QuestNpcFeature feature) : IQuestNpcSink
    {
        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows) =>
            feature.Persistence.SaveQuests((int)player.Guid.Low, rows);

        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask) =>
            feature.Persistence.SaveTaxiMask((int)player.Guid.Low, mask);

        public void CharacterChanged(Player player) => feature._world?.SavePlayer(player);
    }
}
