using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>A sink that keeps what it is given (the interaction tests' own, so it never clashes with another test file's).</summary>
internal sealed class QaInteractionRecordingSink : IPlayerbotQaSink
{
    public List<QaSignal> Signals { get; } = [];

    public bool Enabled { get; set; } = true;

    public void Report(QaSignal signal)
    {
        if (Enabled) Signals.Add(signal);
    }

    public bool ReportFor(Unit unit, QaSignal signal)
    {
        Report(signal);
        return Enabled;
    }
}

/// <summary>A transport that records the issues it is asked to create and finds none.</summary>
internal sealed class QaInteractionTransport : IQaIssueTransport
{
    private int _next = 700;

    public List<(string Title, string Body, IReadOnlyList<string> Labels)> Created { get; } = [];

    public bool SupportsSearch => false;

    public Task<QaIssueIndex> ListIndexAsync(CancellationToken cancellationToken) => Task.FromResult(new QaIssueIndex(complete: true));

    public Task<(QaIssueRef Issue, string Body)?> GetIssueAsync(int number, CancellationToken cancellationToken) => Task.FromResult<(QaIssueRef, string)?>(null);

    public Task<QaIssueRef?> SearchByMarkerAsync(string id, CancellationToken cancellationToken) => Task.FromResult<QaIssueRef?>(null);

    public Task<QaIssueRef> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        lock (Created) Created.Add((title, body, labels));
        int number = Interlocked.Increment(ref _next);
        return Task.FromResult(new QaIssueRef(number, true, $"https://github.com/{QaData.Repo}/issues/{number}"));
    }

    public Task CommentAsync(int number, string body, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ReopenAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseAsDuplicateAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseAsCompletedAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EditBodyAsync(int number, string body, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }
}

/// <summary>
/// The bot QA feature over a signal bus and a sink of its own, publishing through <see cref="QaInteractionTransport"/> (the feature harness of the
/// interaction tests: what the monitor reports reaches a finding and an issue exactly as on a server).
/// </summary>
internal sealed class QaInteractionHarness : IDisposable
{
    public QaInteractionHarness(string directory, Action<PlayerbotQaGitHubOptions>? configure = null)
    {
        var options = new PlayerbotQaOptions { Directory = directory, GitHub = { Enabled = true, Publish = true, Repository = QaData.Repo } };
        configure?.Invoke(options.GitHub);
        Services = new ServiceCollection()
            .AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(options))
            .AddSingleton(new PlayerbotQaSignalBus(1024))
            .AddSingleton<IPlayerbotQaSink>(sp => new PlayerbotQaSink(sp.GetRequiredService<PlayerbotQaSignalBus>(), sp.GetRequiredService<IOptions<PlayerbotQaOptions>>()))
            .BuildServiceProvider();
        Feature = new PlayerbotQaFeature(Services) { EnvironmentReader = _ => QaData.FakeToken, TransportFactory = _ => Transport };
        Feature.Attach(null!);
    }

    public ServiceProvider Services { get; }

    public PlayerbotQaFeature Feature { get; }

    public QaInteractionTransport Transport { get; } = new();

    public IPlayerbotQaSink Sink => Services.GetRequiredService<IPlayerbotQaSink>();

    public PlayerbotQaSignalBus Bus => Services.GetRequiredService<PlayerbotQaSignalBus>();

    /// <summary>One pass: drain, save, export and publish, waiting for the publish task.</summary>
    public async Task PassAsync()
    {
        Feature.RunPass(CancellationToken.None);
        await Feature.Publishing.WaitAsync(TimeSpan.FromSeconds(20));
    }

    public IReadOnlyList<QaFindingView> Findings => Feature.Aggregator!.Snapshot();

    public void Dispose()
    {
        Feature.StopAsync().GetAwaiter().GetResult();
        Services.Dispose();
    }
}

/// <summary>
/// A world with one bot and one real player next to a vendor and a trainer (the real quest/NPC services over the real inventory), a quest table with a
/// kill objective and an item objective, a game object system and the QA interaction monitor wired into all of them as the game's
/// <see cref="Game.Diagnostics.IGameQaObserver"/>. Each player's session hands every packet it receives to the monitor's packet tap, as a managed bot
/// session does. The monitor's bot test is a seam: <see cref="Bot"/> is a bot, <see cref="Human"/> is not.
/// </summary>
internal sealed class QaInteractionKit : IDisposable
{
    public const uint VendorEntry = 990500, TrainerEntry = 990501, GiverEntry = 990502;
    public const uint Bread = 991001, Sword = 991002, Unlisted = 991003, QuestItem = 991004, Missing = 991999;
    public const uint TeachGood = 992001, TeachBroken = 992002, TeachHigh = 992003, TeachUnknown = 992004;
    public const uint KillQuest = 993001, ItemQuest = 993002, UnsupportedQuest = 993003, HighQuest = 993004, ObjectiveCreature = 993090;
    public const uint FlagDropObject = 994001, QuestGiverObject = 994002;

    public static readonly Guid BotKey = new("22222222-3333-4444-5555-666666666666");

    public static ItemTemplateStore Store { get; } = new(
    [
        new ItemTemplate { Entry = Bread, Class = 0, Name = "Bread", DisplayId = 11, Quality = 1, Stackable = 20, BuyPrice = 25, SellPrice = 6, BuyCount = 1 },
        new ItemTemplate { Entry = Sword, Class = 2, SubClass = 7, Name = "Sword", DisplayId = 12, Quality = 2, ItemLevel = 10, InventoryType = 13, Delay = 2000,
            MaxDurability = 50, BuyPrice = 1000, SellPrice = 250, Damages = [new ItemDamage(5, 9, 0)] },
        new ItemTemplate { Entry = Unlisted, Class = 15, Name = "Unlisted", DisplayId = 13, Quality = 1, BuyPrice = 10, SellPrice = 2 },
        new ItemTemplate { Entry = QuestItem, Class = 12, Name = "Boar Tusk", DisplayId = 14, Quality = 1, Stackable = 20 },
    ], []);

    public QaInteractionKit(IPlayerbotQaSink sink, bool teachingFails = true)
    {
        World = new WorldRuntime(new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 }, new NoSaves(),
            NullLogger<WorldRuntime>.Instance);
        Monitor = new PlayerbotQaInteractionMonitor(sink, unit => ReferenceEquals(unit, Bot) ? new QaBot(BotKey, (byte)Class.Warrior) : null, () => NowMs,
            () => Services);
        Bot = AddPlayer(1, BotSession);
        Human = AddPlayer(2, HumanSession);
        Learner = new FakeLearner { TeachingFails = teachingFails };
        Items = new InventoryItemService(() => Store, RepairCostTable.Empty, BankBagSlotPriceTable.Empty, () => 1_000);
        Items.SessionStarted(Bot);
        Items.SessionStarted(Human);
        Vendor = Npc(VendorEntry, 101, NpcFlags.Vendor);
        Trainer = Npc(TrainerEntry, 102, NpcFlags.Trainer) with { TrainerType = TrainerType.Class, TrainerClass = (byte)Class.Warrior };
        Giver = Npc(GiverEntry, 103, NpcFlags.QuestGiver | NpcFlags.Gossip);
        NpcContent npcs = NpcContent.Empty with
        {
            VendorItems =
            [
                new VendorItem { Entry = VendorEntry, Item = Bread }, new VendorItem { Entry = VendorEntry, Item = Sword },
            ],
            TrainerSpells =
            [
                new TrainerSpell { Entry = TrainerEntry, Spell = TeachGood, SpellCost = 10 },
                new TrainerSpell { Entry = TrainerEntry, Spell = TeachBroken, SpellCost = 10 },
                new TrainerSpell { Entry = TrainerEntry, Spell = TeachHigh, SpellCost = 10, ReqLevel = 40 },
                new TrainerSpell { Entry = TrainerEntry, Spell = TeachUnknown, SpellCost = 10 },
            ],
        };
        var quests = new QuestContent(
        [
            Quest(KillQuest, objectiveCreature: ObjectiveCreature, count: 8),
            Quest(ItemQuest, objectiveItem: QuestItem, count: 10),
            Quest(UnsupportedQuest, objectiveCreature: ObjectiveCreature, srcSpell: 133), // a source spell: no adapter provides it
            Quest(HighQuest, objectiveCreature: ObjectiveCreature, minLevel: 60),
        ], [new CreatureQuestRelation { Id = GiverEntry, Quest = UnsupportedQuest }, new CreatureQuestRelation { Id = GiverEntry, Quest = HighQuest }], []);
        Services = new QuestNpcServices(new QuestStore(quests), new NpcStore(npcs),
            new QuestNpcDependencies(Creatures: new Lookup(this), Items: Items, Spells: Learner, Qa: Monitor), new QuestNpcOptions(), new NullQuestSink(),
            () => 1_000, NullLogger.Instance);
        BotState = Services.Track(Bot);
        Services.CompleteLoad(BotState, new CharacterQuestData([], []));
        HumanState = Services.Track(Human);
        Services.CompleteLoad(HumanState, new CharacterQuestData([], []));
        Objects = new GameObjectMapSystem(World.GetMap(0), new GameObjectContent(
        [
            new GameObjectTemplate { Entry = FlagDropObject, Type = (uint)GameObjectType.FlagDrop, DisplayId = 1, Name = "Dropped Banner", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] },
            new GameObjectTemplate { Entry = QuestGiverObject, Type = (uint)GameObjectType.QuestGiver, DisplayId = 1, Name = "Wanted Poster", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] },
        ],
        [
            new GameObjectSpawn { Guid = 995001, Entry = FlagDropObject, MapId = 0, X = Bot.X + 1, Y = Bot.Y, Z = Bot.Z, SpawnTimeSeconds = 5 },
            new GameObjectSpawn { Guid = 995002, Entry = QuestGiverObject, MapId = 0, X = Bot.X, Y = Bot.Y + 1, Z = Bot.Z, SpawnTimeSeconds = 5 },
        ], [], [], [])) { Qa = Monitor };
        World.GetMap(0).AddUpdater(Objects);
        World.RunTick(100);
        World.RunTick(100);
    }

    public WorldRuntime World { get; }

    public PlayerbotQaInteractionMonitor Monitor { get; }

    public Player Bot { get; }

    public Player Human { get; }

    public TapSession BotSession { get; } = new();

    public TapSession HumanSession { get; } = new();

    public FakeLearner Learner { get; }

    public InventoryItemService Items { get; }

    public NpcInfo Vendor { get; }

    public NpcInfo Trainer { get; }

    public NpcInfo Giver { get; }

    public QuestNpcServices Services { get; }

    public PlayerNpcState BotState { get; }

    public PlayerNpcState HumanState { get; }

    public GameObjectMapSystem Objects { get; }

    public long NowMs { get; set; } = 1_000_000;

    public GameObject Object(uint entry) => Objects.GameObjects.Single(o => o.Entry == entry);

    /// <summary>Put a quest in a player's log (as an accept would), with its counters at zero.</summary>
    public void GiveQuest(Player player, uint questId)
    {
        PlayerNpcState state = Services.StateOf(player)!;
        int slot = state.Quests.FindSlot(0);
        QuestStatusData data = state.Quests.GetOrAdd(questId);
        data.Status = QuestStatus.Incomplete;
        state.Quests.SetSlot(slot, questId);
    }

    public Creature Victim(uint entry)
    {
        var template = new CreatureTemplate { Entry = entry, Name = $"Victim {entry}", Faction = 14, MinLevel = 5, MaxLevel = 5 };
        return new Creature(996000 + entry % 1000, template, new CreatureSpawn { Guid = 996000 + entry % 1000, Entry = entry, MapId = 0, X = Bot.X + 2, Y = Bot.Y, Z = Bot.Z },
            CreatureContent.Empty, new Random(1));
    }

    public void Dispose() => World.Dispose();

    private Player AddPlayer(uint guid, TapSession session)
    {
        var character = new CharacterRecord
        {
            Id = (int)guid, AccountId = (int)guid, Name = $"Q{guid}", Race = (byte)Race.Human, Class = (byte)Class.Warrior, Gender = 0, Level = 10, MapId = 0,
            ZoneId = 12, X = guid * 3, Y = 0, Z = 83.5f,
        };
        var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 100, 0, 100, 1000, 0, 400), session);
        session.Player = player;
        session.Monitor = () => Monitor;
        player.Inventory.Templates = Store;
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        player.Money = 10_000;
        World.AddPlayer(player);
        World.RunTick(0);
        return player;
    }

    private NpcInfo Npc(uint entry, uint counter, NpcFlags flags)
        => new(ObjectGuid.WithEntry(HighGuid.Unit, entry, counter), entry, counter, flags, 0, Bot.X + 1, Bot.Y, Bot.Z, 0.5f, true, false, false, false, 0);

    private static QuestTemplate Quest(uint id, uint objectiveCreature = 0, uint objectiveItem = 0, uint count = 1, uint srcSpell = 0, byte minLevel = 1) => new()
    {
        Entry = id, Method = 2, MinLevel = minLevel, QuestLevel = 5, SrcSpell = srcSpell, Title = $"Quest {id}", Objectives = $"Objectives of {id}", Details = "Do it.", RequestItemsText = "Bring it.",
        ReqCreatureOrGOId1 = (int)objectiveCreature, ReqCreatureOrGOCount1 = objectiveCreature != 0 ? count : 0,
        ReqItemId1 = objectiveItem, ReqItemCount1 = objectiveItem != 0 ? count : 0,
    };

    /// <summary>A session that hands every packet to the monitor's tap, as WorldSession.CaptureManagedPacket does for a bot.</summary>
    internal sealed class TapSession : IPlayerSession
    {
        public Player? Player { get; set; }

        public Func<PlayerbotQaInteractionMonitor>? Monitor { get; set; }

        public List<WorldOpcode> Sent { get; } = [];

        public int AccountId => 1;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            Sent.Add(opcode);
            if (Player is { } player && Monitor?.Invoke() is { } monitor) monitor.OnPlayerPacket(player, opcode, payload.ToArray());
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    /// <summary>A spell owner that knows the kit's teaching spells; <see cref="TeachingFails"/> makes every teaching cast fail.</summary>
    internal sealed class FakeLearner : ISpellLearner
    {
        public bool TeachingFails { get; set; }

        public HashSet<uint> Known { get; } = [];

        public TrainerSpellInfo? DescribeTrainerSpell(uint teachingSpell) => teachingSpell switch
        {
            TeachGood or TeachBroken or TeachHigh => new TrainerSpellInfo(teachingSpell + 100, 1, 0, 0, false, false, false),
            _ => null,
        };

        public bool HasSpell(Player player, uint spellId) => Known.Contains(spellId);

        public bool IsSpellFitByClassAndRace(Player player, uint spellId) => true;

        public uint GetSkillValueBase(Player player, uint skill) => 0;

        public uint GetSkillValue(Player player, uint skill) => 0;

        public uint GetFreePrimaryProfessionPoints(Player player) => 2;

        public bool CastTeachingSpell(Player player, ObjectGuid trainer, uint teachingSpell)
        {
            if (TeachingFails && teachingSpell == TeachBroken) return false;
            Known.Add(teachingSpell + 100);
            return true;
        }

        public void CastQuestRewardSpell(Player player, ObjectGuid questEnder, uint spellId)
        {
        }
    }

    private sealed class Lookup(QaInteractionKit kit) : ICreatureLookup
    {
        public NpcInfo? Find(Player player, ObjectGuid guid)
            => guid == kit.Vendor.Guid ? kit.Vendor : guid == kit.Trainer.Guid ? kit.Trainer : guid == kit.Giver.Guid ? kit.Giver : null;
    }

    private sealed class NullQuestSink : IQuestNpcSink
    {
        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows)
        {
        }

        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask)
        {
        }

        public void CharacterChanged(Player player)
        {
        }
    }

    private sealed class NoSaves : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}
