using System.Numerics;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Opportunities;

/// <summary>One chest the test world places near the human start (-8949.95, -132.49, 83.53).</summary>
internal readonly record struct ChestSpawn(uint Entry, uint Spawn, float DeltaX, float DeltaY);

/// <summary>
/// A real world (loopback host, game objects, loot, items, spells) with synthetic chests, a few items and a pick-lock spell, and bots
/// that enter it. The chests: <see cref="FreeChest"/> (a click opens it; a green item, half the time a blue one), <see cref="JunkChest"/> (one grey
/// item), <see cref="KeyChest"/> (needs <see cref="KeyItem"/>), <see cref="PickChest"/> (a lock of Pick Lock at skill 25), <see cref="NodeChest"/>
/// (a herb node: lock of Herbalism).
/// </summary>
internal static class OpportunityWorld
{
    internal const uint FreeChest = 95001, JunkChest = 95002, KeyChest = 95003, PickChest = 95004, NodeChest = 95005;
    internal const uint GreenItem = 95101, BlueItem = 95102, GreyItem = 95103, KeyItem = 95900;
    internal const uint KeyLock = 71, PickLock = 72, HerbLock = 73;
    internal const uint PickLockSpell = 1804;

    /// <summary>The herb gathering spell (an OpenLock on <see cref="HerbLock"/>) and the rare creature with a blue item to drop (its entry is its spawn number).</summary>
    internal const uint HerbSpell = 9201, RareEntry = 96700;

    /// <summary>A Pick Lock whose range is 0: the server turns the cast down at once (out of range) and the cast never stays in flight.</summary>
    internal const uint ShortPickSpell = 9202;
    internal const float StartX = -8949.95f, StartY = -132.49f, StartZ = 83.53f;

    internal static ulong ChestGuid(ChestSpawn spawn) => ObjectGuid.WithEntry(HighGuid.GameObject, spawn.Entry, spawn.Spawn).Value;

    private static GameObjectTemplate Chest(uint entry, uint lockId, uint loot) => new()
    {
        Entry = entry, Type = (uint)GameObjectType.Chest, DisplayId = 311, Name = "Test Chest " + entry,
        Data = Data(lockId, loot),
    };

    private static uint[] Data(uint lockId, uint loot)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[0] = lockId;
        data[1] = loot;
        return data;
    }

    private static LockEntry Lock(uint id, uint type, uint index, uint skill)
    {
        uint[] types = new uint[LockEntry.Cases], indexes = new uint[LockEntry.Cases], skills = new uint[LockEntry.Cases];
        (types[0], indexes[0], skills[0]) = (type, index, skill);
        return new LockEntry(id, types, indexes, skills);
    }

    internal static WorldTestHost Start(IEnumerable<ChestSpawn> chests, out GameObjectTestContext context)
    {
        var templates = new[]
        {
            Chest(FreeChest, 0, FreeChest),
            Chest(JunkChest, 0, JunkChest),
            Chest(KeyChest, KeyLock, FreeChest),
            Chest(PickChest, PickLock, FreeChest),
            Chest(NodeChest, HerbLock, JunkChest),
        };
        GameObjectSpawn[] spawns =
        [
            .. chests.Select(c => new GameObjectSpawn
            {
                Guid = c.Spawn, Entry = c.Entry, MapId = 0, X = StartX + c.DeltaX, Y = StartY + c.DeltaY, Z = StartZ, SpawnTimeSeconds = 300,
            }),
        ];
        var content = new GameObjectContent(templates, spawns,
            [Lock(KeyLock, 1, KeyItem, 0), Lock(PickLock, 2, (uint)LockType.PickLock, 25), Lock(HerbLock, 2, (uint)LockType.Herbalism, 1)], [], []);
        var loot = new LootContent(
        [
            (LootTableKind.GameObject, new LootStoreRow(FreeChest, GreenItem, 100f, 0, 1, 1)),
            (LootTableKind.GameObject, new LootStoreRow(FreeChest, BlueItem, 50f, 0, 1, 1)),
            (LootTableKind.GameObject, new LootStoreRow(JunkChest, GreyItem, 100f, 0, 1, 1)),
            (LootTableKind.Creature, new LootStoreRow(RareEntry, BlueItem, 100f, 0, 1, 1)),
        ], [new CreatureLootInfo(RareEntry, RareEntry, 0, 0, 0)]);
        context = new GameObjectTestContext(content, loot);

        var items = new ItemTestContent();
        items.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = GreenItem, Class = 7, Name = "Green Thing", DisplayId = 4, Quality = 2, Stackable = 20, SellPrice = 60 },
            new ItemTemplate { Entry = BlueItem, Class = 7, Name = "Blue Thing", DisplayId = 5, Quality = 3, Stackable = 20, SellPrice = 200 },
            new ItemTemplate { Entry = GreyItem, Class = 7, Name = "Grey Thing", DisplayId = 6, Quality = 0, Stackable = 20, SellPrice = 1 },
            new ItemTemplate { Entry = KeyItem, Class = 13, Name = "Test Key", DisplayId = 7, Quality = 1, Stackable = 1 },
        ]);
        GameObjectTestStore.Current.Value = context;
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start(configureServices: Configure);
            }
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }

    private static void Configure(IServiceCollection services)
    {
        GatheringWorldTests.Configure(services);
        services.AddSingleton(new SkillCatalog(
            [
                new SkillLineRecord(SkillIds.Mining, SkillCategories.Profession, "Mining", 0),
                new SkillLineRecord(SkillIds.Herbalism, SkillCategories.Profession, "Herbalism", 0),
                new SkillLineRecord(SkillIds.Lockpicking, SkillCategories.Profession, "Lockpicking", 0),
            ],
            [
                new SkillRaceClassInfoRecord(SkillIds.Mining, 0, 0, 0, 0, 21),
                new SkillRaceClassInfoRecord(SkillIds.Herbalism, 0, 0, 0, 0, 21),
                new SkillRaceClassInfoRecord(SkillIds.Lockpicking, 0, 0, 0, 0, 21),
            ],
            [new SkillTierRecord(21, Enumerable.Repeat(0u, 16).ToArray(), Enumerable.Repeat(150u, 16).ToArray())],
            []));
        services.AddSingleton<ISpellContentStore>(new InMemorySkillSpellContentStore(new SpellContent(
            [
                new SpellTemplateRow
                {
                    Id = PickLockSpell, SpellName = "Pick Lock", RangeIndex = 12, Targets = 0x4000, CastingTimeIndex = 2,
                    Effect1 = 33, EffectImplicitTargetA1 = 23, EffectMiscValue1 = (int)LockType.PickLock, EffectBasePoints1 = -1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
                },
                new SpellTemplateRow
                {
                    Id = ShortPickSpell, SpellName = "Short Pick", RangeIndex = 1, Targets = 0x4000, CastingTimeIndex = 2,
                    Effect1 = 33, EffectImplicitTargetA1 = 23, EffectMiscValue1 = (int)LockType.PickLock, EffectBasePoints1 = -1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
                },
                new SpellTemplateRow
                {
                    Id = HerbSpell, SpellName = "Herb Gathering", RangeIndex = 12, Targets = 0x4000, CastingTimeIndex = 2,
                    Effect1 = 33, EffectImplicitTargetA1 = 23, EffectMiscValue1 = (int)LockType.Herbalism, EffectBasePoints1 = -1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
                },
            ],
            [new SpellCastTimeRow { Id = 2, CastTime = 500, MinCastTime = 500 }], [], [new SpellRangeRow { Id = 1 }, new SpellRangeRow { Id = 12, MaxRange = 5 }], [],
            [new PlayerCreateSpellRow { Race = 1, Class = 4, Spell = PickLockSpell }],
            [])));
    }

    /// <summary>A bot, its brain (for the risk model), its opportunity controller on a clock the test sets, and the loot actions the controller queued.</summary>
    internal sealed class Bot
    {
        public required WorldSession Session { get; init; }

        public required PlayerbotBrain Brain { get; init; }

        public required PlayerbotOptions Options { get; init; }

        public PlayerbotOpportunities Controller { get; set; } = null!;

        public List<(WorldOpcode Opcode, byte[] Payload)> Sink { get; } = [];

        public Player Player => Session.Player!;

        /// <summary>One think of the controller with a fresh action budget; world thread only.</summary>
        public OpportunityStep Step(bool interrupting = false)
        {
            Session.ManagedBudget = new ManagedActionBudget(5);
            return Controller.Update(Player, 500, interrupting);
        }
    }

    internal static async Task<Bot> EnterAsync(WorldTestHost host, string account, string name, byte playerClass, PlayerbotOptions options, PlayerbotClaims claims,
        Func<uint> clock)
    {
        Account owner = await host.Accounts.CreateAsync(new Account { Username = account, Salt = new byte[32], Verifier = new byte[32] });
        WorldSession session = await WorldSession.CreateManagedAsync(owner, null, host.WorldServices, host.Opcodes, host.World, host.Registry,
            new WorldSessionOptions(), NullLogger.Instance);
        var create = new PacketWriter();
        create.WriteCString(name);
        create.WriteByte(1);
        create.WriteByte(playerClass);
        for (int i = 0; i < 8; i++) create.WriteByte(0);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, create.ToArray());
        var login = new PacketWriter();
        login.WriteUInt64((ulong)(await session.Services.GetRequiredService<ArcaneCore.Kernel.Characters.ICharacterStore>().GetByAccountAsync(owner.Id)).Single().Id);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await host.WaitForWorldAsync(() => session.Player is not null, "opportunity bot login");
        var brain = new PlayerbotBrain(session, options, claims: claims);
        var bot = new Bot { Session = session, Brain = brain, Options = options };
        bot.Controller = new PlayerbotOpportunities(session, options, () => brain.Risk, claims, (opcode, payload) => bot.Sink.Add((opcode, payload))) { Clock = clock };
        return bot;
    }

    /// <summary>Open flat ground (one height, straight paths) so a bot can plan the walk to a chest; call on the world thread.</summary>
    internal static void InstallOpenGround(WorldTestHost host)
        => WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());

    private sealed class FlatFloor : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => StartZ;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    private sealed class OpenPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);
    }

    internal static Creature AddCreature(WorldTestHost host, Player player, uint low, float x, float y, byte level = 1, uint rank = 0, uint health = 20)
    {
        var creature = new Creature(low, new CreatureTemplate
        {
            Entry = low, Name = "Guard " + low, CreatureType = 7, Rank = rank,
            MinLevel = level, MaxLevel = level, MinLevelHealth = health, MaxLevelHealth = health,
            MinMeleeDamage = 2f, MaxMeleeDamage = 2f, MeleeBaseAttackTime = 2000,
        }, null, CreatureContent.Empty, new Random((int)low));
        creature.Relocate(StartX + x, StartY + y, StartZ, 0, host.World.NowMs);
        player.Map!.AddObject(creature);
        return creature;
    }
}
