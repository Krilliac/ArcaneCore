using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Benchmarks;

/// <summary>
/// The checks a managed bot's quest-candidate scan (PlayerbotWorldDestinations.HasQuestCandidate / Eligible) runs per quest giver and
/// quest every think: whether a giver spawn listed in <c>game_event_creature</c> is present (the spawn gate), whether ordinary settlement
/// would reward the quest (<see cref="QuestNpcServices.IsRewardable"/>) and whether the bot may take it
/// (<see cref="QuestNpcServices.CanTakeQuest(Player, uint)"/>). The quest is a follow-up of a rewarded quest in an exclusive group, so
/// every check of vmangos Player::CanTakeQuest runs. Synthetic quests and events, not world content.
/// </summary>
[MemoryDiagnoser]
public class QuestCandidateBenchmarks
{
    private const uint Previous = 7000;
    private const uint FollowUp = 7001;
    private const uint Sibling = 7002;
    private const uint GatedGuid = 100;
    private const uint NegativeGuid = 101;

    private WorldRuntime _world = null!;
    private Player _player = null!;
    private QuestNpcServices _quests = null!;
    private GameEventSpawns _spawns = null!;

    [GlobalSetup]
    public void Setup()
    {
        _world = new WorldRuntime(new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 },
            new NoSaves(), NullLogger<WorldRuntime>.Instance);
        var character = new CharacterRecord
        {
            Id = 1, AccountId = 1, Name = "Bench", Race = (byte)Race.Human, Class = (byte)Class.Warrior, Gender = (byte)Gender.Male,
            Level = 5, MapId = 0, ZoneId = 12, X = 0, Y = 0, Z = 83.5f,
        };
        var appearance = new PlayerAppearance(
            DisplayId: 49, FactionTemplate: 1, PowerType.Rage, BaseHealth: 60, BaseMana: 0,
            MaxHealth: 60, MaxPower: 1000, StartPower: 0, NextLevelXp: 400);
        _player = new Player(character, appearance, new DiscardSession());
        _world.AddPlayer(_player);

        QuestTemplate[] templates = [Task(Previous, 0, 0), Task(FollowUp, (int)Previous, 7001), Task(Sibling, 0, 7001)];
        var content = new QuestContent(templates,
            templates.Select(t => new CreatureQuestRelation { Id = 1, Quest = t.Entry }).ToArray(),
            templates.Select(t => new CreatureQuestRelation { Id = 1, Quest = t.Entry }).ToArray());
        var factions = new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(2, 0, 0, 8, 0, 0)]);
        _quests = new QuestNpcServices(new QuestStore(content), NpcStore.Empty,
            new QuestNpcDependencies(Creatures: new CreatureQuestLookup(factions)), new QuestNpcOptions(), new NoSink(), () => 100,
            NullLogger.Instance);
        PlayerNpcState state = _quests.Track(_player);
        _quests.CompleteLoad(state, new CharacterQuestData([new CharacterQuestStatus(1, Previous, (byte)QuestStatus.Complete, true, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)], []));
        if (_quests.CanTakeQuest(_player, FollowUp) != true || !_quests.IsRewardable(FollowUp))
        {
            throw new InvalidOperationException("the follow-up quest must pass every check: the benchmark measures the full path");
        }

        var events = new GameEventContent(
            [new GameEventRecord(1, 1, 1440, 120, 0, 0, "Spring"), new GameEventRecord(27, 1, 1440, 120, 0, 0, "Nights")],
            [new GameEventTimeRecord(1, "2026-10-03 12:00:00", "2030-12-31 22:59:59"), new GameEventTimeRecord(27, "2026-10-03 20:00:00", "2030-12-31 22:59:59")],
            [new GameEventSpawnRecord(GatedGuid, 1), new GameEventSpawnRecord(NegativeGuid, -27)],
            [], [], [], []);
        var options = new GameEventOptions();
        DateTimeOffset now = new(2026, 10, 3, 13, 0, 0, TimeSpan.Zero); // event 1 running, event 27 not yet
        GameEventLoadResult load = GameEventLoader.Load(events, options, now, TimeZoneInfo.Utc);
        var service = new GameEventService(load, options, () => now, TimeZoneInfo.Utc, NullLogger.Instance);
        service.Initialize(new HashSet<ushort>());
        service.Update();
        _spawns = new GameEventSpawns(service, service.Rows, static () => []);
        if (!_spawns.AllowsCreature(GatedGuid) || !_spawns.AllowsCreature(NegativeGuid))
        {
            throw new InvalidOperationException("both gated spawns must be present at the benchmark's clock");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    /// <summary>The spawn gate for two listed givers: one under a running event, one under an event that is not running.</summary>
    [Benchmark]
    public bool SpawnGate() => _spawns.AllowsCreature(GatedGuid) & _spawns.AllowsCreature(NegativeGuid);

    /// <summary>Whether ordinary settlement would reward the follow-up quest.</summary>
    [Benchmark]
    public bool IsRewardable() => _quests.IsRewardable(FollowUp);

    /// <summary>Whether the player may take the follow-up quest (every relation check runs and passes).</summary>
    [Benchmark]
    public bool? CanTakeQuest() => _quests.CanTakeQuest(_player, FollowUp);

    private static QuestTemplate Task(uint id, int previous, int exclusiveGroup) => new()
    {
        Entry = id, Method = 2, MinLevel = 1, QuestLevel = 1, Title = $"Quest {id}", PrevQuestId = previous, ExclusiveGroup = exclusiveGroup,
        ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2, RequestItemsText = "Bring it.", Details = "Do it.", Objectives = "Done?",
    };

    private sealed class DiscardSession : IPlayerSession
    {
        public int AccountId => 1;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
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

    private sealed class NoSink : IQuestNpcSink
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
