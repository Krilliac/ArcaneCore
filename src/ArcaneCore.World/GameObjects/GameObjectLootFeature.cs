using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Loot;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Items;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// Game objects and loot in the world daemon (an <see cref="IWorldFeature"/>, discovered): loads
/// game object and loot content when the world starts (fail closed, like creatures), attaches a
/// <see cref="GameObjectMapSystem"/> to every map, and turns creature deaths into corpse loot.
/// Quest checks go through the quest feature, groups through the social feature and item
/// templates through the items feature; each is optional (absent → no quest drops, solo loot,
/// no items).
/// <para>Chests of dungeon instances keep their consumed and remaining loot with the logical instance
/// save through <see cref="ILootStateStore"/> and <see cref="LootSettlements"/> (docs/integration/gameobjects-loot.md);
/// without a store they stay refused.</para>
/// <para>Options come from the <c>Loot</c> configuration section (<see cref="LootOptions"/>).</para>
/// </summary>
public sealed class GameObjectLootFeature(IServiceProvider services, ILogger<GameObjectLootFeature> logger) : IWorldFeature, ICharacterSettlementBarrier
{
    public const string SectionName = "Loot";

    private readonly Dictionary<Map, GameObjectMapSystem> _systems = new(ReferenceEqualityComparer.Instance);
    private GameObjectContent _content = GameObjectContent.Empty;
    private LootContent _lootContent = LootContent.Empty;
    private WorldRuntime? _world;
    private LootSettlements? _settlements;

    /// <summary>Durable chest loot of dungeon instances, or null when no <see cref="ILootStateStore"/> is registered.</summary>
    public LootSettlements? Settlements => _settlements;

    /// <summary>The loaded game object content (immutable; safe to read from any thread).</summary>
    public GameObjectContent Content => Volatile.Read(ref _content);

    /// <summary>The loaded loot tables (immutable; replaced as a whole by the live reload, world thread).</summary>
    public LootContent LootContent => Volatile.Read(ref _lootContent);

    /// <summary>
    /// Replace the loot tables (live reload, world thread; vmangos <c>LootStore::LoadLootTable</c> clears and refills its
    /// stores in place, LootMgr.cpp:94-189): every map's loot service and every map created later use the new tables for
    /// the loot they generate from now on. Loot that was already generated keeps what it rolled. Returns the content it replaced.
    /// </summary>
    public LootContent ReplaceLootContent(LootContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        LootContent previous = Interlocked.Exchange(ref _lootContent, content);
        foreach (GameObjectMapSystem system in _systems.Values)
        {
            system.Loot?.ReplaceContent(content);
        }

        return previous;
    }

    public LootOptions Options { get; } = new();

    /// <summary>Behaviour switches of the game objects (configuration section <see cref="GameObjectOptions.SectionName"/>); defaults are retail.</summary>
    public GameObjectOptions ObjectOptions { get; } = new();

    /// <summary>Quest checks used by loot and game objects (adapts the quest feature).</summary>
    public ILootQuestJournal Quests { get; private set; } = NullQuestJournal.Instance;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        services.GetService<IConfiguration>()?.GetSection(SectionName).Bind(Options);
        services.GetService<IConfiguration>()?.GetSection(GameObjectOptions.SectionName).Bind(ObjectOptions);

        GameObjectContent content = GameObjectContent.Empty;
        LootContent loot = LootContent.Empty;
        using (IServiceScope scope = services.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IGameObjectDataStore>() is { } goStore)
            {
                content = goStore.LoadAsync().GetAwaiter().GetResult();
            }

            if (scope.ServiceProvider.GetService<ILootDataStore>() is { } lootStore)
            {
                loot = lootStore.LoadAsync().GetAwaiter().GetResult();
            }

            // Fail closed like the content stores: stored chests must be known (and orphans purged)
            // before the instance system loads, or a reused instance id could inherit old state.
            if (scope.ServiceProvider.GetService<ILootStateStore>() is { } stateStore)
            {
                _settlements = new LootSettlements(services.GetRequiredService<IServiceScopeFactory>(), logger);
                _settlements.Load(stateStore.LoadInstanceStatesAsync().GetAwaiter().GetResult());
            }
        }

        Volatile.Write(ref _content, content);
        Quests = new QuestJournalAdapter(services, world);
        Volatile.Write(ref _lootContent, loot);

        logger.LogInformation(
            "Loaded {Templates} game object templates, {Spawns} spawns, {Locks} locks, {LootRows} loot rows, {CreatureLoot} creature loot entries",
            content.TemplateCount, content.SpawnCount, content.LockCount, loot.RowCount, loot.CreatureInfoCount);
        world.Post(Install);
    }

    /// <summary>Attach to every map, now and when created (world thread).</summary>
    private void Install()
    {
        WorldRuntime world = _world!;
        world.MapCreated += OnMapCreated;
        world.MapUnloading += OnMapUnloading;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
        InstallDurableLoot(world);
        foreach (Map map in world.Maps.ToArray())
        {
            OnMapCreated(map);
        }

        foreach (uint mapId in Content.MapsWithSpawns)
        {
            GetOrCreateSystem(mapId);
        }
    }

    /// <summary>
    /// Wire the durable chest loot to the instance system. This runs in the install post, which
    /// precedes the post that loads the instance saves (features attach in type-name order, and
    /// this one first), so the startup drops of unbound saves reach <c>InstanceDeleted</c>.
    /// </summary>
    private void InstallDurableLoot(WorldRuntime world)
    {
        if (_settlements is not { } settlements)
        {
            return;
        }

        InstanceFeature? instances = services.GetService<InstanceFeature>();
        Game.Instances.InstanceManager? manager = instances?.Instances;
        settlements.Attach(world, services.GetService<CharacterSaveQueue>(), services.GetService<TeleportFeature>(),
            id => manager?.FindSave(id) is { IsDeleted: false },
            () => instances?.WriteWatermark ?? 0,
            (watermark, token) => instances?.WaitForWritesAsync(watermark, token) ?? Task.CompletedTask);
        if (manager is not null)
        {
            manager.InstanceDeleted += settlements.OnInstanceDeleted;
        }
    }

    /// <summary>Login and character deletion wait for an in-flight chest operation of the character.</summary>
    public Task WaitForSettlementAsync(int characterId, CancellationToken cancellationToken = default)
        => _settlements?.WaitForCharacterAsync(characterId, cancellationToken) ?? Task.CompletedTask;

    /// <summary>Cancel in-flight chest operations and finalize them without world publication.</summary>
    public Task StopAsync() => _settlements?.StopAsync() ?? Task.CompletedTask;

    private void OnMapCreated(Map map)
    {
        if (_systems.ContainsKey(map))
        {
            return;
        }

        var loot = new LootService(LootContent, Options, new Random(), logger)
        {
            Items = new DeferredItemTemplates(services),
            Quests = Quests,
            Groups = new SocialGroups(services),
            CreatureOptions = services.GetService<CreatureWorldFeature>()?.Options ?? new CreatureOptions(),
            Durable = _settlements,
        };
        var system = new GameObjectMapSystem(map, Content, loot, Quests, logger) { Options = ObjectOptions, Random = new Random() };
        map.AddUpdater(system);
        _systems.Add(map, system);
        map.Combat.UnitKilled += OnUnitKilled;
    }

    private void OnMapUnloading(Map map)
    {
        if (_systems.Remove(map))
        {
            map.Combat.UnitKilled -= OnUnitKilled;
        }
    }

    private void OnPlayerLoggingOut(Player player)
    {
        if (player.Map is { } map)
        {
            FindSystem(map)?.Loot?.OnPlayerLeft(player);
        }
    }

    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        try
        {
            if (victim.Map is { } map)
            {
                FindSystem(map)?.Loot?.OnCreatureKilled(killer, victim);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "corpse loot for {Victim} failed", victim.Guid);
        }
    }

    /// <summary>The game object system of this exact map instance (world thread).</summary>
    public GameObjectMapSystem? FindSystem(Map map) => _systems.GetValueOrDefault(map);

    /// <summary>The game object system of the shared copy, if it exists (world thread).</summary>
    public GameObjectMapSystem? FindSystem(uint mapId)
        => _world?.FindMap(mapId) is { } map ? FindSystem(map) : null;

    /// <summary>Attach an independent loot service and object system to a current exact map.</summary>
    public GameObjectMapSystem GetOrCreateSystem(Map map)
    {
        WorldRuntime world = _world ?? throw new InvalidOperationException("the game object feature is not attached");
        if (map.IsUnloaded || !ReferenceEquals(world.FindMap(map.MapId, map.InstanceId), map))
        {
            throw new InvalidOperationException("cannot attach game objects to an obsolete map");
        }

        OnMapCreated(map);
        return _systems[map];
    }

    /// <summary>The game object system of a map, attaching one if needed (world thread).</summary>
    public GameObjectMapSystem GetOrCreateSystem(uint mapId)
    {
        WorldRuntime world = _world ?? throw new InvalidOperationException("the game object feature is not attached");
        return GetOrCreateSystem(world.GetMap(mapId));
    }

    /// <summary>Item templates from the items feature, read at use time (it loads them lazily at first login).</summary>
    private sealed class DeferredItemTemplates(IServiceProvider services) : IItemTemplateStore
    {
        private IItemTemplateStore Inner => services.GetService<ItemsFeature>()?.Templates ?? ItemTemplateStore.Empty;

        public int Count => Inner.Count;

        public ItemTemplate? Find(uint entry) => Inner.Find(entry);

        public IReadOnlyList<StartingItem> StartingItems(byte race, byte cls) => Inner.StartingItems(race, cls);
    }

    private sealed class SocialGroups(IServiceProvider services) : ILootGroups
    {
        public Group? GroupOf(Player player)
        {
            if (services.GetService<SocialFeature>() is not { } social)
            {
                return null;
            }

            try
            {
                return social.Context.Groups.GetGroup(player.Guid);
            }
            catch (InvalidOperationException)
            {
                return null; // social feature not attached
            }
        }

        public void LooterChanged(Group group)
        {
            if (services.GetService<SocialFeature>() is { } social)
            {
                try
                {
                    social.Context.Groups.SendUpdate(group);
                }
                catch (InvalidOperationException)
                {
                    // social feature not attached: nobody to tell
                }
            }
        }

        public bool IsMemberOnline(ObjectGuid guid)
        {
            if (services.GetService<SocialFeature>() is not { } social)
            {
                return true;
            }

            try
            {
                return social.Context.World.FindOnlinePlayer(guid) is not null;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    private sealed class NullQuestJournal : ILootQuestJournal
    {
        public static NullQuestJournal Instance { get; } = new();

        public bool NeedsQuestItem(Player player, uint itemId) => false;

        public bool IsQuestIncomplete(Player player, uint questId) => false;

        public void ItemLooted(Player player, uint itemId, uint count)
        {
        }

        public void MoneyLooted(Player player)
        {
        }

        public void GameObjectUsed(Player player, uint entry, ObjectGuid guid)
        {
        }
    }
}
