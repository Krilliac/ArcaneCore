using System.Runtime.CompilerServices;
using ArcaneCore.Data.Npc;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Quests.Adapters;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
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
public sealed partial class QuestNpcFeature : IWorldFeature, ICharacterHooks, IAreaTriggerListener, IAreaTriggerGate, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<QuestNpcFeature> _logger;
    private readonly TimeProvider _clock;
    private readonly ConditionalWeakTable<Player, CharacterQuestData> _staged = new();
    private readonly HashSet<Map> _maps = [];
    private readonly Dictionary<Player, Action<uint, int>> _itemListeners = new(ReferenceEqualityComparer.Instance);
    private QuestObjectiveAdapter? _objectives;
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

    /// <summary>The effective immutable catalog used by live NPC reaction checks.</summary>
    public FactionTemplateCatalog FactionTemplates { get; private set; } = FactionTemplateCatalog.Empty;

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
            var taxiOptions = new NpcServiceOptions();
            _services.GetService<IConfiguration>()?.GetSection(NpcServiceOptions.SectionName).Bind(taxiOptions);
            if (!string.IsNullOrWhiteSpace(taxiOptions.TaxiNodesDbcPath))
            {
                npcs = npcs with { TaxiNodes = NpcServiceDbcReaders.LoadTaxiNodes(taxiOptions.TaxiNodesDbcPath) };
            }

            if (!string.IsNullOrWhiteSpace(taxiOptions.TaxiPathDbcPath))
            {
                npcs = npcs with { TaxiPaths = NpcServiceDbcReaders.LoadTaxiPaths(taxiOptions.TaxiPathDbcPath) };
            }
            FactionTemplateCatalog factions = _services.GetService<FactionTemplateCatalog>()
                ?? (string.IsNullOrWhiteSpace(Options.FactionTemplateDbcPath)
                    ? FactionTemplateCatalog.Empty
                    : FactionTemplateDbcReader.Load(Options.FactionTemplateDbcPath));
            FactionTemplates = factions;
            Services = BuildServices(new QuestStore(quests), new NpcStore(npcs), factions,
                _services.GetService<Reputation.ReputationFeature>()?.Service, progression: true);
        }

        _world = world;
        _objectives = new QuestObjectiveAdapter(Services, RewardGroups.Resolver(_services),
            _services.GetService<ProgressionFeature>()?.Progression.Options.GroupXpDistance ?? new ProgressionOptions().GroupXpDistance);
        if (_services.GetService<SpellFeature>() is { } spells)
        {
            _objectives.Attach(spells.System);
            // SPELL_EFFECT_QUEST_COMPLETE credits the quest of its misc value (vmangos SpellEffects.cpp:5324-5331).
            QuestSpellEvents.Install(spells.System, (player, questId) => Services.AreaExploredOrEventHappens(player, questId));
        }

        Persistence.Start();
        world.MapCreated += OnMapCreated;
        world.MapUnloading += OnMapUnloading;
        foreach (Map map in world.Maps)
        {
            OnMapCreated(map);
        }

        world.PlayerLoggedIn += OnPlayerLoggedIn;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
        _logger.LogInformation("Loaded {Quests} quest templates for persisted journals and queries", Services.Quests.Count);
        // Duplicate adapter providers fail here, at startup; the report itself waits for the first tick because features that
        // attach later (the spell system) supply part of what the quests need.
        _ = Services.ProvidedAdapters;
        world.Post(LogSupportSummary);
    }

    /// <summary>
    /// Player::_LoadQuestStatus. Database access and the save barrier stay on the session task;
    /// this private journal fills fields without touching QuestNpcServices' world-owned registry.
    /// An already-expired timer fails after the login sequence, as in vmangos Player::Update.
    /// </summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        await WaitForSettlementAsync(character.Id).ConfigureAwait(false);
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
        _objectives?.Dispose();
        foreach ((Player player, Action<uint, int> listener) in _itemListeners)
        {
            player.Inventory.ItemCountChanged -= listener;
        }

        _itemListeners.Clear();
        if (_world is { } world)
        {
            world.MapCreated -= OnMapCreated;
            world.MapUnloading -= OnMapUnloading;
            world.PlayerLoggedIn -= OnPlayerLoggedIn;
            world.PlayerLoggingOut -= OnPlayerLoggingOut;
        }

        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>The world has stopped; drain quest/taxi saves before host shutdown succeeds.</summary>
    public async Task StopAsync()
    {
        try
        {
            await StopSettlementsAsync().ConfigureAwait(false);
        }
        finally
        {
            await Persistence.DisposeAsync().ConfigureAwait(false);
        }
    }

    private QuestNpcServices BuildServices(QuestStore quests, NpcStore npcs, FactionTemplateCatalog? factions = null,
        Game.Reputation.ReputationService? reputation = null, bool progression = false) => new(quests, npcs,
        ExtendDependencies(new QuestNpcDependencies(
            Creatures: new CreatureQuestLookup(factions ?? FactionTemplateCatalog.Empty, reputation),
            Reputation: reputation is { Factions.Count: > 0 } ? reputation : null,
            Experience: progression ? _services.GetService<ProgressionFeature>()?.Progression : null,
            RewardEffects: progression && _services.GetService<SpellFeature>() is { } spells
                ? new QuestRewardEffects(spells.System, _logger)
                : null,
            ReputationRewards: reputation is { Factions.Count: > 0 } ? reputation : null,
            SpellCaster: progression && _services.GetService<SpellFeature>() is { } caster ? new SpellSystemQuestCaster(caster.System) : null,
            Party: progression ? new WorldQuestParty(_services, () => _world) : null), npcs),
        Options, new PersistenceSink(this), () => _clock.GetUtcNow().ToUnixTimeSeconds(), _logger);

    private QuestNpcDependencies ExtendDependencies(QuestNpcDependencies dependencies, NpcStore npcs)
        => _services.GetService<NpcServicesFeature>() is { } npcServices ? npcServices.Extend(dependencies, npcs) : dependencies;

    public void OnAreaTrigger(Player player, uint triggerId) => Services.AreaTriggerReached(player, triggerId);

    /// <summary>
    /// <c>areatrigger_teleport.required_quest_done</c>: the quest must be turned in (<see cref="QuestNpcServices.IsRewarded"/>, so a
    /// repeatable quest never counts, as in the reference). A player whose journal is not loaded yet is refused (fail closed).
    /// The refusal carries no text of its own: the reference sends none for an unfinished quest.
    /// </summary>
    public AreaTriggerVerdict Check(Player player, AreaTriggerTeleport teleport)
        => teleport.RequiredQuestDone == 0 || Services.IsRewarded(player, teleport.RequiredQuestDone) == true
            ? AreaTriggerVerdict.Allow
            : AreaTriggerVerdict.Refuse(null);

    private void OnMapCreated(Map map)
    {
        if (_maps.Add(map))
        {
            _objectives?.Attach(map);
            map.AddUpdater(new QuestNpcMapUpdater(Services));
        }
    }

    // An unloaded dungeon instance (docs/integration/instances.md) is forgotten.
    private void OnMapUnloading(Map map)
    {
        if (_maps.Remove(map))
        {
            _objectives?.Detach(map);
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
        // Item-collect objectives follow the live inventory (vmangos ItemAddedQuestCheck /
        // ItemRemovedQuestCheck); counters are reconciled with the loaded bags first.
        RemoveItemListener(player);
        Action<uint, int> listener = (entry, delta) =>
        {
            if (delta > 0)
            {
                Services.ItemAdded(player, entry, (uint)delta);
            }
            else if (delta < 0)
            {
                Services.ItemRemoved(player, entry, (uint)-(long)delta);
            }
        };
        _itemListeners[player] = listener;
        player.Inventory.ItemCountChanged += listener;
        Services.ReconcileItemCounts(player);
        if (ReferenceEquals(_world?.FindOnlinePlayer(player.Guid), player))
        {
            // A disconnected settlement may have changed money without publishing a journal delta.
            Services.MoneyChanged(player);
        }
    }

    private void OnPlayerLoggingOut(Player player)
    {
        RemoveItemListener(player);
        Services.Untrack(player);
        _staged.Remove(player);
    }

    private void RemoveItemListener(Player player)
    {
        if (_itemListeners.Remove(player, out Action<uint, int>? listener))
        {
            player.Inventory.ItemCountChanged -= listener;
        }
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
