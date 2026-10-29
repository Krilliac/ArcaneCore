using System.Numerics;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// The data of the hearthstone and mage travel tests, as the 1.12 rows have it but with short casts: the Hearthstone (item 6948, on-use spell
/// 8690: TELEPORT_UNITS to the caster's bind point, an hour's cooldown), Teleport: Stormwind (3561, rune 17031, spell_target_position),
/// Portal: Stormwind (10059, rune 17032, TRANS_DOOR summoning the party-only GAMEOBJECT_TYPE_SPELLCASTER 176296 whose spell 17334 teleports to
/// Stormwind), and, when asked for, a vendor that stocks both runes beside the start (there is no reagent vendor flag: the stock says it).
/// Everything stands at the human start on map 0; "Stormwind" is 200 yards east of it.
/// </summary>
internal sealed class MageTravelFixture : ICreatureDataStore, INpcContentStore
{
    public const float X = -8949.95f, Y = -132.49f, Z = 83.53f;
    public static readonly Vector3 Stormwind = new(X + 200f, Y + 40f, Z);
    public static readonly Vector3 Bind = new(X + 150f, Y - 30f, Z);

    public const uint Hearthstone = 6948;
    public const uint HearthSpell = 8690;
    public const uint TeleportSpell = 3561;
    public const uint PortalSpell = 10059;
    public const uint PortalObject = 176296;
    public const uint PortalEffect = 17334;
    public const uint RuneOfTeleportation = 17031;
    public const uint RuneOfPortals = 17032;
    public const uint VendorEntry = 9551;

    /// <summary>A plain tradeable good a bot can sell through the trade desk (reference price 1000 copper: its sell price).</summary>
    public const uint DealGoods = 996_101;
    public const int CastMs = 300;

    /// <summary>A vendor stocking both runes stands two yards from the start.</summary>
    public bool Vendor { get; init; }

    /// <summary>Portal: Stormwind costs mana a warrior does not have: the server refuses the cast (NoPower).</summary>
    public bool PortalNeedsMana { get; init; }

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty with
    {
        VendorItems = Vendor
            ? [new VendorItem { Entry = VendorEntry, Item = RuneOfTeleportation, Slot = 0 }, new VendorItem { Entry = VendorEntry, Item = RuneOfPortals, Slot = 1 }]
            : [],
    });

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Vendor
        ? new CreatureContent(
            [new CreatureTemplate { Entry = VendorEntry, Name = "Test reagent vendor", Faction = 900011, NpcFlags = (uint)NpcFlags.Vendor, DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 95501, Entry = VendorEntry, MapId = 0, X = X + 2f, Y = Y + 1f, Z = Z }], [], [], [])
        : new CreatureContent([], [], [], [], []));

    public static ItemTestContent Items()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = Hearthstone, Class = 15, Name = "Hearthstone", Stackable = 1, Bonding = 1, Spells = [new ItemSpell(HearthSpell, 0, 0, 0, -1, 0, -1)] },
            new ItemTemplate { Entry = RuneOfTeleportation, Class = 5, Name = "Rune of Teleportation", Stackable = 20, BuyPrice = 10 },
            new ItemTemplate { Entry = RuneOfPortals, Class = 5, Name = "Rune of Portals", Stackable = 20, BuyPrice = 20 },
            new ItemTemplate { Entry = DealGoods, Class = 15, Name = "Feedeal Goods", DisplayId = 1, Quality = 1, SellPrice = 1000, BuyPrice = 4000, Stackable = 20 },
        ]);
        return items;
    }

    public static GameObjectTestContext Objects()
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[GameObjectMapSystem.SpellCasterSpellData] = PortalEffect;
        data[GameObjectMapSystem.SpellCasterPartyOnlyData] = 1;
        var portal = new GameObjectTemplate { Entry = PortalObject, Type = (uint)GameObjectType.SpellCaster, DisplayId = 4396, Name = "Portal to Stormwind", Data = data };
        return new GameObjectTestContext(new GameObjectContent([portal], [], [], [], []), new LootContent([], []));
    }

    /// <summary>The spells, installed on the running world (the effect handlers stay those the world registered).</summary>
    public void InstallSpells(SpellSystem spells)
    {
        SpellEffectInfo none = new();
        SpellCastTime cast = new(CastMs, 0, CastMs);
        spells.Store = new SpellStore(
        [
            .. spells.Store.All.Where(s => s.Id is not (HearthSpell or TeleportSpell or PortalSpell or PortalEffect)),
            new SpellInfo
            {
                Id = HearthSpell, CastTime = cast, RecoveryTime = 3_600_000,
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.TeleportUnits, TargetA = SpellImplicitTarget.UnitCaster, TargetB = SpellImplicitTarget.LocationCasterHomeBind }, none, none],
            },
            new SpellInfo
            {
                Id = TeleportSpell, CastTime = cast, Reagents = [new SpellReagent((int)RuneOfTeleportation, 1)],
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.TeleportUnits, TargetA = SpellImplicitTarget.UnitCaster, TargetB = SpellImplicitTarget.LocationDatabase }, none, none],
            },
            new SpellInfo
            {
                Id = PortalSpell, CastTime = cast, Reagents = [new SpellReagent((int)RuneOfPortals, 1)], PowerType = 0, ManaCost = PortalNeedsMana ? 100u : 0u,
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.TransDoor, TargetA = SpellImplicitTarget.UnitCaster, MiscValue = (int)PortalObject }, none, none],
            },
            new SpellInfo
            {
                Id = PortalEffect,
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.TeleportUnits, TargetA = SpellImplicitTarget.UnitCaster, TargetB = SpellImplicitTarget.LocationDatabase }, none, none],
            },
        ], [],
        [
            (TeleportSpell, new SpellTargetPosition(0, Stormwind.X, Stormwind.Y, Stormwind.Z, 0f)),
            (PortalEffect, new SpellTargetPosition(0, Stormwind.X, Stormwind.Y, Stormwind.Z, 0f)),
        ]);
    }
}

/// <summary>Flat open ground at the start's height, seen through everywhere (the walking tests).</summary>
internal sealed class MageTravelFlatFloor : ArcaneCore.Game.Maps.Collision.ILineOfSight
{
    public bool Enabled => true;
    public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
    public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
    {
        hit = to;
        return false;
    }

    public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => MageTravelFixture.Z;
    public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ArcaneCore.Game.Maps.Collision.ModelAreaInfo info)
    {
        info = default;
        return false;
    }
}

/// <summary>Straight-line paths everywhere (the walking tests).</summary>
internal sealed class MageTravelOpenPathfinder : ArcaneCore.Game.Maps.Collision.IPathfinder
{
    public bool Enabled => true;
    public ArcaneCore.Game.Maps.Collision.PathResult FindPath(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        => ArcaneCore.Game.Maps.Collision.PathResult.StraightLine(start, end, ArcaneCore.Game.Maps.Collision.PathType.Normal);
}

internal sealed class MageTravelServices : IWorldTestServices
{
    public static readonly AsyncLocal<MageTravelFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<ICharacterQuestStore>(new MemoryQuestStore());
        services.AddSingleton(new FactionTemplateCatalog([
            new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
            new FactionTemplateRecord(900011, 0, 0, 8, 0, 0),
        ]));
        // A store that commits everything, so the economy feature (and with it player trade) is on.
        services.AddScoped<IEconomyStore>(_ => new CommittingEconomyStore());
    }
}

/// <summary>An economy store that accepts every commit and holds nothing; enough for trades between bots.</summary>
internal sealed class CommittingEconomyStore : IEconomyStore
{
    public Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(EconomyCommitResult.Committed);

    public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MailRecord>>([]);

    public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MailRecord>>([]);

    public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MailRecord>>([]);

    public Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AuctionRecord>>([]);

    public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> itemGuids, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<uint, ItemInstanceData>>(new Dictionary<uint, ItemInstanceData>());

    public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default)
        => Task.FromResult(new AuctionSnapshot([], new Dictionary<uint, ItemInstanceData>()));

    public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new EconomyIdSeed(0, 0, 0));
}

/// <summary>A world with <see cref="MageTravelFixture"/> and managed bots entered into it (all human warriors at the start).</summary>
internal sealed class MageTravelRig : IAsyncDisposable
{
    private readonly List<WorldSession> _sessions = [];

    private MageTravelRig(WorldTestHost host, MageTravelFixture fixture)
    {
        Host = host;
        Fixture = fixture;
    }

    public WorldTestHost Host { get; }
    public MageTravelFixture Fixture { get; }
    public SpellFeature Spells => Host.WorldServices.GetRequiredService<SpellFeature>();

    public static async Task<MageTravelRig> StartAsync(MageTravelFixture? fixture = null)
    {
        fixture ??= new MageTravelFixture();
        WorldTestHost host;
        using (MageTravelFixture.Items().Use())
        {
            MageTravelServices.Current.Value = fixture;
            GameObjectTestStore.Current.Value = MageTravelFixture.Objects();
            try { host = WorldTestHost.Start(); }
            finally
            {
                MageTravelServices.Current.Value = null;
                GameObjectTestStore.Current.Value = null;
            }
        }

        var rig = new MageTravelRig(host, fixture);
        await host.OnWorldAsync(() => fixture.InstallSpells(rig.Spells.System));
        return rig;
    }

    /// <summary>A managed bot named <paramref name="name"/> (its own account), in the world with its NPC state loaded.</summary>
    public async Task<WorldSession> EnterAsync(string name)
    {
        Account owner = await Host.Accounts.CreateAsync(new Account { Username = name.ToUpperInvariant(), Salt = new byte[32], Verifier = new byte[32] });
        WorldSession session = await WorldSession.CreateManagedAsync(owner, null, Host.WorldServices, Host.Opcodes,
            Host.World, Host.Registry, new WorldSessionOptions(), NullLogger.Instance);
        _sessions.Add(session);
        var create = new PacketWriter();
        create.WriteCString(name);
        create.WriteByte(1);
        create.WriteByte(1);
        for (int i = 0; i < 8; i++) create.WriteByte(0);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, create.ToArray());
        CharacterRecord character = (await session.Services.GetRequiredService<ICharacterStore>().GetByAccountAsync(owner.Id)).Single();
        var login = new PacketWriter();
        login.WriteUInt64((ulong)character.Id);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await Host.WaitForWorldAsync(() => session.Player is { IsInWorld: true }, $"{name} logs in");
        await Host.WaitForWorldAsync(() => Host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.StateOf(session.Player!) is { Loaded: true },
            $"{name}'s npc state loads");
        return session;
    }

    public Task<T> OnWorld<T>(Func<T> action) => Host.OnWorldAsync(action);

    public static PlayerbotOptions Options(Action<PlayerbotTravelOptions>? travel = null)
    {
        var options = new PlayerbotOptions { Enabled = true };
        travel?.Invoke(options.Travel);
        return options;
    }

    /// <summary>Give <paramref name="player"/> <paramref name="count"/> of <paramref name="entry"/> (world thread).</summary>
    public static void Give(Player player, uint entry, uint count = 1)
        => Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(entry, count, out _));

    /// <summary>Teach <paramref name="player"/> <paramref name="spells"/> through its spellbook (world thread).</summary>
    public void Teach(Player player, params uint[] spells)
    {
        foreach (uint spell in spells) Spells.Spellbook.LearnSpell(player, spell);
    }

    /// <summary>
    /// Run thinks (each with a fresh action budget of four) until <paramref name="think"/> answers something other than
    /// <see cref="TravelLegStep.Traveling"/>, letting the world tick between them; that answer.
    /// </summary>
    public async Task<TravelLegStep> ThinkUntilAsync(WorldSession session, Func<TravelLegStep> think, int maxMs = 8_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(maxMs);
        while (true)
        {
            TravelLegStep step = await Host.OnWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(4);
                return think();
            });
            if (step != TravelLegStep.Traveling) return step;
            if (DateTime.UtcNow > deadline) return step;
            await Task.Delay(50);
        }
    }

    public static float DistanceTo(Player player, Vector3 at) => Vector3.Distance(new Vector3(player.X, player.Y, player.Z), at);

    public static string[] Codes(AnomalyCapture capture, string area)
        => [.. capture.OfSubsystem(PlayerbotAnomalies.Subsystem(area)).Select(n => n.Code)];

    public async ValueTask DisposeAsync()
    {
        foreach (WorldSession session in _sessions)
        {
            session.Kick();
            await session.ManagedClosed;
        }

        await Host.DisposeAsync();
    }
}
