using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// One player next to one quest-giving creature with a configurable quest set, journal and
/// reward allowlist; the real quest service over the real inventory.
/// </summary>
internal sealed class QuestFlowKit : IDisposable
{
    public const uint CreatureEntry = 910010;

    public QuestFlowKit(IReadOnlyList<QuestTemplate> templates, IReadOnlyList<CharacterQuestStatus>? rows = null,
        IReadOnlyList<uint>? starters = null, IReadOnlyList<uint>? enders = null, IReadOnlyList<uint>? rewardable = null,
        Race race = Race.Human, byte level = 5, Class cls = Class.Warrior)
    {
        Player = TestWorld.CreatePlayer(1, 0, 0, Session, race: race);
        Player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)cls);
        Player.Level = level;
        ItemTestData.Wire(Player.Inventory);
        Player.Inventory.Load([]);
        Player.Money = 50;
        World.AddPlayer(Player);
        var template = new CreatureTemplate { Entry = CreatureEntry, Name = "Flow questgiver", Faction = 2, NpcFlags = 2 };
        Creature = new Creature(910020, template,
            new CreatureSpawn { Guid = 910020, Entry = CreatureEntry, MapId = 0, X = 0, Y = 0, Z = Player.Z }, CreatureContent.Empty, new Random(1));
        Player.Map!.AddObject(Creature);
        World.RunTick(5);
        var content = new QuestContent(templates,
            (starters ?? []).Select(id => new CreatureQuestRelation { Id = CreatureEntry, Quest = id }).ToArray(),
            (enders ?? []).Select(id => new CreatureQuestRelation { Id = CreatureEntry, Quest = id }).ToArray());
        var factions = new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(2, 0, 0, 8, 0, 0), new(3, 0, 0, 8, 0, 1)]);
        Services = new QuestNpcServices(new QuestStore(content), NpcStore.Empty,
            new QuestNpcDependencies(Creatures: new CreatureQuestLookup(factions)),
            new QuestNpcOptions { OrdinaryRewardQuestIds = (rewardable ?? []).ToArray() }, Sink, () => 100, NullLogger.Instance);
        State = Services.Track(Player);
        Services.CompleteLoad(State, new CharacterQuestData(rows ?? [], []));
        Session.Clear();
    }

    public WorldRuntime World { get; } = TestWorld.CreateRuntime();

    public FakeSession Session { get; } = new();

    public RecordingSink Sink { get; } = new();

    public Player Player { get; }

    public Creature Creature { get; }

    public QuestNpcServices Services { get; }

    public PlayerNpcState State { get; }

    public void Dispose() => World.Dispose();

    public bool Accept(uint questId) => Services.AcceptQuest(Player, Creature.Guid, questId);

    public List<(WorldOpcode Opcode, byte[] Payload)> Drain()
    {
        var list = new List<(WorldOpcode, byte[])>();
        while (Session.Sent.TryDequeue(out var p))
        {
            list.Add(p);
        }

        return list;
    }

    public static QuestTemplate Task(uint id, byte minLevel = 1, uint races = 0, uint classes = 0, int previous = 0, uint limitTime = 0,
        byte special = 0, int exclusiveGroup = 0, uint nextInChain = 0, uint srcItem = 0, byte srcCount = 0, uint reqItem = 0,
        uint reqItemCount = 0, byte maxLevel = 0, byte method = 2) => new()
    {
        Entry = id, Method = method, MinLevel = minLevel, MaxLevel = maxLevel, QuestLevel = 1, Title = $"Quest {id}",
        RequiredRaces = races, RequiredClasses = classes, PrevQuestId = previous, LimitTime = limitTime, SpecialFlags = special,
        ExclusiveGroup = exclusiveGroup, NextQuestInChain = nextInChain, SrcItemId = srcItem, SrcItemCount = srcCount,
        ReqItemId1 = reqItem, ReqItemCount1 = reqItemCount,
        ReqCreatureOrGOId1 = reqItem == 0 ? 90 : 0, ReqCreatureOrGOCount1 = reqItem == 0 ? 2u : 0,
        RequestItemsText = "Bring it.", Details = "Do it.", Objectives = "Done?",
    };

    public static CharacterQuestStatus Row(uint id, QuestStatus status, bool rewarded = false, long timer = 0, uint kills = 0)
        => new(1, id, (byte)status, rewarded, false, timer, kills, 0, 0, 0, 0, 0, 0, 0, 0);

    public sealed class RecordingSink : IQuestNpcSink
    {
        public List<CharacterQuestStatus> Rows { get; } = [];

        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows) => Rows.AddRange(rows);

        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask)
        {
        }

        public void CharacterChanged(Player player)
        {
        }
    }
}
