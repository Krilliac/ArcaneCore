using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The scenario test world (real handlers, the manual clock, a SQLite character database, managed playerbots) with group content:
/// a quest giver at the human start offering only the quests a test names, the creatures they ask for, flat open ground with open
/// paths, and test spells for the group roles (a taunt and a resurrection; the priest's Lesser Heal comes with the class content).
/// Bots are created, levelled and taught on the world thread and then left to their brains; time moves only when a test says so.
/// </summary>
internal sealed class GroupTestWorld : IAsyncDisposable
{
    public const uint GiverEntry = 990710;
    public const uint GiverSpawn = 990710;

    /// <summary>An elite ogre (rank 1) 70 yards east of the start.</summary>
    public const uint OgreEntry = 990720;
    public const uint OgreSpawn = 990720;

    /// <summary>A second elite for the raid-size quest, 70 yards west.</summary>
    public const uint WarlordEntry = 990721;
    public const uint WarlordSpawn = 990721;

    /// <summary>A normal creature too strong for one bot (the risk estimate's group signal), 70 yards north.</summary>
    public const uint BruteEntry = 990730;
    public const uint BruteSpawn = 990730;

    /// <summary>The boss inside The Deadmines (map 36), 40 yards in from the entrance.</summary>
    public const uint BossEntry = 990740;
    public const uint BossSpawn = 990740;

    /// <summary>An elite quest (quest_template Type 1) for three.</summary>
    public const uint EliteQuest = 990701;

    /// <summary>An ordinary quest whose objective is too strong alone.</summary>
    public const uint BruteQuest = 990702;

    /// <summary>A dungeon quest (Type 81) for three whose objective spawns only inside The Deadmines.</summary>
    public const uint DungeonQuest = 990703;

    /// <summary>A raid quest (Type 62) for six.</summary>
    public const uint RaidQuest = 990704;

    /// <summary>An elite quest for two (wipe tests).</summary>
    public const uint DuoQuest = 990705;

    /// <summary>
    /// A dungeon quest (Type 81) for three that asks only for an item: <see cref="BossTrophy"/>, a quest drop of the Deadmines boss reached
    /// through a reference loot table (creature_loot_template row with a negative mincountOrRef).
    /// </summary>
    public const uint DungeonItemQuest = 990706;

    /// <summary>An elite quest for two (Type 1, level 13) whose ogre, <see cref="EntranceOgreEntry"/>, stands 60 yards from the Deadmines entrance (dungeon worlds only).</summary>
    public const uint EntranceEliteQuest = 990707;

    public const uint EntranceOgreEntry = 990722;
    public const uint EntranceOgreSpawn = 990722;

    /// <summary>An ordinary quest (Type 0, no suggested group) for one harmless rat, <see cref="RatEntry"/>, 70 yards south: a quest a bot soloes.</summary>
    public const uint SoloQuest = 990708;

    public const uint RatEntry = 990723;
    public const uint RatSpawn = 990723;

    public const uint BossTrophy = 990750;
    public const uint BossLootReference = 990760;

    public const uint Taunt = 990790;
    public const uint Resurrection = 990791;

    /// <summary>Where the dungeon tests' giver and bots stand: Westfall, 30 yards east of the Deadmines entrance.</summary>
    public static readonly Vector3 EntranceArea = new(-11178f, 1679.6f, DeadminesTestContent.OutsideFloor);

    /// <summary>Inside Orgrimmar, beside the real RFC area trigger 2230 (test movement uses open paths).</summary>
    public static readonly Vector3 RfcEntranceArea = new(1835f, -4427.26f, -10.4478f);

    private GroupTestWorld(ScenarioTestWorld world, PlayerbotOptions options)
    {
        World = world;
        Options = options;
        Coordinator = world.Services.GetRequiredService<PlayerbotGroupCoordinator>();
    }

    public ScenarioTestWorld World { get; }

    public PlayerbotOptions Options { get; }

    public PlayerbotGroupCoordinator Coordinator { get; }

    public ManagedPlayerbotFeature Bots => World.Bots;

    /// <param name="quests">The quests the giver offers.</param>
    /// <param name="dungeon">The Deadmines content (map 36, its triggers), the giver at <see cref="EntranceArea"/>.</param>
    /// <param name="rfc">RFC map, trigger, encounter, creature and graveyard content for an end-to-end run.</param>
    /// <param name="openWorldTrophyDrop">The open-world ogre drops <see cref="BossTrophy"/> too: the item then has a dropper outside the instance.</param>
    /// <param name="extraCreatures">More creature templates and their spawns (the bosses of the dungeon and raid run tests).</param>
    /// <param name="extraTemplates">More creature templates with no database spawn (an event boss a test places with <c>SpawnTemporary</c>).</param>
    public static async Task<GroupTestWorld> StartAsync(uint[] quests, Action<PlayerbotOptions>? configure = null, bool dungeon = false,
        Action<IServiceCollection>? services = null, (CreatureTemplate Template, CreatureSpawn Spawn)[]? extraCreatures = null, bool rfc = false,
        bool openWorldTrophyDrop = false, CreatureTemplate[]? extraTemplates = null)
    {
        var options = new PlayerbotOptions
        {
            Enabled = true, MaxBots = 10, AllowedMaps = [0, 1], ThinkIntervalMs = 200, MaxActionsPerTick = 32,
            Groups = { FormationTimeoutSeconds = 600 },
        };
        configure?.Invoke(options);
        var content = new GroupContent(quests, dungeon, openWorldTrophyDrop, [.. extraCreatures ?? [], .. (rfc ? GroupRfcContent.Creatures() : [])], extraTemplates ?? []);
        ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(collection =>
        {
            if (dungeon) DeadminesTestContent.Register(collection);
            if (rfc) GroupRfcContent.Register(collection);
            collection.AddSingleton<IOptions<PlayerbotOptions>>(Microsoft.Extensions.Options.Options.Create(options));
            collection.AddSingleton<ICreatureDataStore>(content);
            collection.AddSingleton<IQuestContentStore>(content);
            collection.AddScoped<ILootDataStore>(_ => content);
            if (collection.LastOrDefault(d => d.ServiceType == typeof(IItemTemplateSource))?.ImplementationInstance is ArcaneCore.World.Tests.Items.InMemoryItemTemplateSource items)
                items.Templates.Add(new ItemTemplate { Entry = BossTrophy, Name = "Deadmines Boss's Trophy", Class = 12, Quality = 1, Stackable = 1, MaxCount = 1 });
            services?.Invoke(collection);
        });
        await world.Host.OnWorldAsync(() =>
        {
            world.Bots.ThinkBudgetMs = double.PositiveInfinity; // manual clock: no wall-time think budget
            WorldCollision.Of(world.Host.World).Install(lineOfSight: new Floor(rfc), pathfinder: rfc ? new GroupRfcContent.Pathfinder() : new OpenPathfinder());
            SpellFeature spells = world.Services.GetRequiredService<SpellFeature>();
            spells.System.Store = new SpellStore([.. spells.System.Store.All, TauntSpell(), ResurrectionSpell()], [], []);
            return true;
        });
        return new GroupTestWorld(world, options);
    }

    /// <summary>
    /// Create and start an autonomous bot, then (world thread) raise it to <paramref name="level"/>, teach it <paramref name="spells"/>
    /// and, when given, put it at <paramref name="at"/> (map 0).
    /// </summary>
    public async Task<(Guid Id, string Name)> AddBotAsync(string name, byte race, byte cls, byte level, uint[] spells, Vector3? at = null, uint mapId = 0)
    {
        PlayerbotOperationResult created = await Bots.CreateAsync(name, race, cls);
        Assert.True(created.Success, created.Code);
        PlayerbotOperationResult started = await Bots.StartAsync(name);
        Assert.True(started.Success, started.Code);
        Guid id = started.BotId!.Value;
        await World.Host.OnWorldAsync(() =>
        {
            Player player = Player(id);
            if (player.Level < level) World.Services.GetRequiredService<ProgressionFeature>().Progression.GiveLevel(player, level);
            SpellFeature feature = World.Services.GetRequiredService<SpellFeature>();
            foreach (uint spell in spells) feature.Spellbook.LearnSpell(player, spell);
            player.Health = player.MaxHealth;
            if (at is { } spot)
            {
                if (mapId == player.MapId) player.Relocate(spot.X, spot.Y, spot.Z, 0, World.Host.World.NowMs);
                else World.Services.GetRequiredService<ArcaneCore.World.Teleport.TeleportFeature>().Teleports.TeleportTo(player, mapId, spot.X, spot.Y, spot.Z, 0f);
            }
            return true;
        });
        return (id, name);
    }

    public Player Player(Guid id) => Bots.FindSession(id)?.Player ?? throw new InvalidOperationException("bot not running");

    public WorldSession Session(Guid id) => Bots.FindSession(id) ?? throw new InvalidOperationException("bot not running");

    public Task<T> OnWorldAsync<T>(Func<T> read) => World.Host.OnWorldAsync(read);

    /// <summary>Run the world (manual clock) until <paramref name="condition"/> holds after a tick, at most <paramref name="maxMs"/> of game time.</summary>
    public Task<bool> RunUntilAsync(uint maxMs, Func<bool> condition) => World.Host.World.AdvanceClockUntilAsync(maxMs, condition);

    /// <summary>
    /// Run the world (manual clock) one 50 ms tick at a time until <paramref name="condition"/> holds after a tick, pacing game time against real
    /// time: game time never runs ahead of the wall clock, as in production. For a condition that waits on work outside the world thread (a chat
    /// worker, a packet still on its way over the socket), which a free-running manual clock would outrun on a loaded machine. Returns false once
    /// <paramref name="gameBudgetMs"/> of game time is used (keep the site's own budget, so game-time timeouts such as an invitation's still mean
    /// what they did) or <paramref name="realTimeout"/> (default 60 s) of real time has passed.
    /// </summary>
    public async Task<bool> RunUntilPacedAsync(Func<bool> condition, int gameBudgetMs, TimeSpan? realTimeout = null)
    {
        const int StepMs = 50;
        System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan limit = realTimeout ?? TimeSpan.FromSeconds(60);
        for (int advanced = 0; advanced < gameBudgetMs; advanced += StepMs)
        {
            if (await RunUntilAsync(StepMs, condition)) return true;
            if (wall.Elapsed >= limit) return false;
            long ahead = advanced + StepMs - wall.ElapsedMilliseconds;
            if (ahead > 0) await Task.Delay((int)ahead);
        }

        return false;
    }

    public Task RunAsync(uint ms) => World.Host.World.AdvanceClockAsync(ms);

    /// <summary>Whether the bot's quest log has <paramref name="quest"/> (any state).</summary>
    public bool HasQuest(Guid id, uint quest)
        => World.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(Player(id)) is { Loaded: true } state && state.Quests.Get(quest) is not null;

    public Game.Groups.GroupManager GroupManager => World.Services.GetRequiredService<ArcaneCore.World.Social.SocialFeature>().Context.Groups;

    /// <summary>Everything the coordinator noted, for failure messages, built on the world thread (the test thread must not enumerate state the world keeps changing).</summary>
    public Task<string> TraceAsync() => OnWorldAsync(Trace);

    /// <summary>The trace itself: <b>world thread only</b> (inside <see cref="OnWorldAsync{T}"/> or a run condition); everything else uses <see cref="TraceAsync"/>.</summary>
    public string Trace()
    {
        if (!World.Host.World.IsWorldThread) throw new InvalidOperationException("Trace() called off the world thread; use TraceAsync()");
        return string.Join(Environment.NewLine, Coordinator.Events) + Environment.NewLine
        + string.Join(Environment.NewLine, Coordinator.Report()) + Environment.NewLine
        + string.Join(Environment.NewLine, Bots.Snapshot().Select(s => $"{s.Name} goal={s.Goal} target={s.TargetEntry} health={s.Health} map={s.MapId} {s.Risk} {s.Group} {Facts(s.BotId)}"));
    }

    private string Facts(Guid id)
    {
        Player? player = Bots.FindSession(id)?.Player;
        return player is null ? "offline" : FormattableString.Invariant(
            $"L{player.Level} {player.Health}/{player.MaxHealth} at ({player.X:F1}, {player.Y:F1}, {player.Z:F1}) map {player.MapId} alive={player.IsAlive} combat={player.Combat.IsInCombat} victim={(player.Combat.Victim as Game.Creatures.Creature)?.Entry} attackers={string.Join(',', player.Combat.Attackers.OfType<Game.Creatures.Creature>().Select(c => $"{c.Entry}:{c.Health}/{c.MaxHealth}"))} death={player.Combat.DeathState} ghost={(player.Flags & PlayerFlags.Ghost) != 0} resRequested={Game.Death.Resurrection.ResurrectionRequests.IsRequested(player)} rez={Coordinator.FindAI(id)?.LastResurrection} step={Coordinator.FindAI(id)?.Recovery.LastStep}");
    }

    public ValueTask DisposeAsync() => World.DisposeAsync();

    /// <summary>The warrior's Taunt (vmangos 355 shape: SPELL_EFFECT_ATTACK_ME on the enemy, melee range; no stance needed here).</summary>
    private static SpellInfo TauntSpell() => new()
    {
        Id = Taunt, Name = "Taunt", RangeIndex = 2, Range = new SpellRange(0, 5), RecoveryTime = 10_000,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.AttackMe, TargetA = SpellImplicitTarget.UnitEnemy }],
    };

    /// <summary>The priest's Resurrection (SPELL_EFFECT_RESURRECT_NEW: the health and mana it offers), castable on a dead friend.</summary>
    private static SpellInfo ResurrectionSpell() => new()
    {
        Id = Resurrection, Name = "Resurrection", AttributesEx2 = SpellAttributesEx2.AllowDeadTarget, RangeIndex = 4, Range = new SpellRange(0, 30),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ResurrectNew, BasePoints = 69, BaseDice = 1, DieSides = 1, MiscValue = 135,
            TargetA = SpellImplicitTarget.UnitFriend,
        }],
    };

    /// <summary>Flat ground everywhere: the dungeon's floor inside map 36, the Westfall floor near its entrance, the start's height elsewhere.</summary>
    private sealed class Floor(bool rfc) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => mapId == 36 ? DeadminesTestContent.InsideFloor : rfc && mapId == GroupRfcContent.Map ? -15.5288f
                : rfc && mapId == 1 && x > 1000f ? RfcEntranceArea.Z : x < -10_000f ? DeadminesTestContent.OutsideFloor : StartZ;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    /// <summary>The giver, the creatures and the quests (a later registration replaces the scenario content's stores).</summary>
    private sealed class GroupContent(uint[] quests, bool dungeon, bool openWorldTrophyDrop, (CreatureTemplate Template, CreatureSpawn Spawn)[] extra, CreatureTemplate[] templatesOnly) : ICreatureDataStore, IQuestContentStore, ILootDataStore
    {
        Task<LootContent> ILootDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new LootContent(
            [
                (LootTableKind.Creature, new LootStoreRow(BossEntry, 0, 100f, 0, -(int)BossLootReference, 1)),
                (LootTableKind.Reference, new LootStoreRow(BossLootReference, BossTrophy, -100f, 0, 1, 1)),
                .. openWorldTrophyDrop ? [(LootTableKind.Creature, new LootStoreRow(OgreEntry, BossTrophy, -100f, 0, 1, 1))] : Array.Empty<(LootTableKind, LootStoreRow)>(),
            ],
            [new CreatureLootInfo(BossEntry, BossEntry, 0, 0, 0), .. openWorldTrophyDrop ? [new CreatureLootInfo(OgreEntry, OgreEntry, 0, 0, 0)] : Array.Empty<CreatureLootInfo>()]));

        private Vector3 Home => dungeon ? EntranceArea : new Vector3(StartX, StartY, StartZ);

        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [
                new CreatureTemplate
                {
                    Entry = GiverEntry, Name = "Group Marshal", Faction = 12, NpcFlags = (uint)NpcFlags.QuestGiver, DisplayIds = [49],
                    MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
                },
                Hostile(OgreEntry, "Group Ogre", level: 10, health: 150, minDamage: 3, maxDamage: 4, rank: 1),
                Hostile(WarlordEntry, "Group Warlord", level: 10, health: 1500, minDamage: 6, maxDamage: 9, rank: 1),
                Hostile(BruteEntry, "Group Brute", level: 1, health: 150, minDamage: 1, maxDamage: 1, rank: 0),
                Hostile(BossEntry, "Deadmines Boss", level: 12, health: 150, minDamage: 3, maxDamage: 4, rank: 1),
                .. quests.Contains(SoloQuest) ? [Hostile(RatEntry, "Group Rat", level: 1, health: 20, minDamage: 1, maxDamage: 1, rank: 0)] : Array.Empty<CreatureTemplate>(),
                Hostile(EntranceOgreEntry, "Entrance Ogre", level: 10, health: 150, minDamage: 3, maxDamage: 4, rank: 1),
                .. extra.Select(e => e.Template).Concat(templatesOnly).DistinctBy(t => t.Entry),
            ],
            [
                new CreatureSpawn { Guid = GiverSpawn, Entry = GiverEntry, MapId = 0, X = Home.X + 3f, Y = Home.Y, Z = Home.Z, Orientation = MathF.PI },
                new CreatureSpawn { Guid = OgreSpawn, Entry = OgreEntry, MapId = 0, X = StartX + 70f, Y = StartY, Z = StartZ, SpawnTimeMinSeconds = 600, SpawnTimeMaxSeconds = 600 },
                new CreatureSpawn { Guid = WarlordSpawn, Entry = WarlordEntry, MapId = 0, X = StartX - 70f, Y = StartY, Z = StartZ, SpawnTimeMinSeconds = 600, SpawnTimeMaxSeconds = 600 },
                new CreatureSpawn { Guid = BruteSpawn, Entry = BruteEntry, MapId = 0, X = StartX, Y = StartY + 70f, Z = StartZ, SpawnTimeMinSeconds = 600, SpawnTimeMaxSeconds = 600 },
                new CreatureSpawn { Guid = BossSpawn, Entry = BossEntry, MapId = 36, X = -16.4f, Y = -343.07f, Z = DeadminesTestContent.InsideFloor, SpawnTimeMinSeconds = 600, SpawnTimeMaxSeconds = 600 },
                .. dungeon && quests.Contains(EntranceEliteQuest) ? [new CreatureSpawn { Guid = EntranceOgreSpawn, Entry = EntranceOgreEntry, MapId = 0, X = EntranceArea.X, Y = EntranceArea.Y + 60f, Z = EntranceArea.Z, SpawnTimeMinSeconds = 600, SpawnTimeMaxSeconds = 600 }] : Array.Empty<CreatureSpawn>(),
                .. quests.Contains(SoloQuest) ? [new CreatureSpawn { Guid = RatSpawn, Entry = RatEntry, MapId = 0, X = StartX, Y = StartY - 70f, Z = StartZ, SpawnTimeMinSeconds = 600, SpawnTimeMaxSeconds = 600 }] : Array.Empty<CreatureSpawn>(),
                .. extra.Select(e => e.Spawn),
            ], [], [], []));

        Task<QuestContent> IQuestContentStore.LoadAsync(CancellationToken cancellationToken)
        {
            QuestTemplate[] all =
            [
                Quest(EliteQuest, "Group: the ogre", type: 1, suggested: 3, OgreEntry),
                Quest(BruteQuest, "The brute", type: 0, suggested: 0, BruteEntry),
                Quest(DungeonQuest, "Dungeon: the boss", type: 81, suggested: 3, BossEntry, minLevel: 10),
                Quest(RaidQuest, "Raid: the warlord", type: 62, suggested: 6, WarlordEntry),
                Quest(DuoQuest, "Group: the ogre for two", type: 1, suggested: 2, OgreEntry),
                Quest(DungeonItemQuest, "Dungeon: the boss's trophy", type: 81, suggested: 3, 0, minLevel: 10, item: BossTrophy),
                Quest(EntranceEliteQuest, "Group: the entrance ogre", type: 1, suggested: 2, EntranceOgreEntry, minLevel: 13),
                Quest(SoloQuest, "Solo: the rat", type: 0, suggested: 0, RatEntry),
            ];
            QuestTemplate[] offered = [.. all.Where(q => quests.Contains(q.Entry))];
            return Task.FromResult(new QuestContent(offered,
                [.. offered.Select(q => new CreatureQuestRelation { Id = GiverEntry, Quest = q.Entry })],
                [.. offered.Select(q => new CreatureQuestRelation { Id = GiverEntry, Quest = q.Entry })]));
        }

        private static QuestTemplate Quest(uint id, string title, uint type, byte suggested, uint objective, byte minLevel = 1, uint item = 0) => new()
        {
            Entry = id, Method = 2, MinLevel = minLevel, QuestLevel = 10, RequiredRaces = 0xFF, Title = title, Type = type,
            SuggestedPlayers = suggested, ReqCreatureOrGOId1 = (int)objective, ReqCreatureOrGOCount1 = objective == 0 ? 0u : 1u, RewXP = 100,
            QuestFlags = 8, // QUEST_FLAGS_SHARABLE, as most real group quests (e.g. classic-db 176 "Wanted: Hogger")
            ReqItemId1 = item, ReqItemCount1 = item == 0 ? 0u : 1u,
        };

        private static CreatureTemplate Hostile(uint entry, string name, byte level, uint health, float minDamage, float maxDamage, uint rank) => new()
        {
            Entry = entry, Name = name, Faction = 14, CreatureType = 7, MinLevel = level, MaxLevel = level, DisplayIds = [903],
            MinLevelHealth = health, MaxLevelHealth = health, MinMeleeDamage = minDamage, MaxMeleeDamage = maxDamage,
            MeleeBaseAttackTime = 2000, Rank = rank,
        };
    }
}
