using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>
/// Quest DB scripts and an escort quest over real classic-db z2815 rows (<see cref="ClassicDbScriptedQuestRows"/>): the rows go through the
/// content importer into a world-schema-42 SQLite database, the stores load them, and a world with simulated time runs the quests the way
/// mangos-classic does (Player::AddQuest starts <c>quest_template.StartScript</c>, Player::RewardQuest <c>CompleteScript</c>, ScriptDev2
/// npc_ruul_snowhoof walks <c>script_waypoint</c>). Nothing waits on the wall clock: the world ticks 100 ms at a time.
/// </summary>
public sealed class ClassicDbScriptedQuestTests
{
    private const byte Orc = 2;
    private const byte Rogue = 4;

    /// <summary>The imported content, loaded once per test class run.</summary>
    private static readonly Lazy<Task<(CreatureContent Creatures, QuestContent Quests)>> s_content = new(ImportAsync);

    /// <summary>The imported excerpt, loaded through the stores (shared with <see cref="ClassicDbScriptedQuestWorldTests"/>).</summary>
    internal static Task<(CreatureContent Creatures, QuestContent Quests)> Content => s_content.Value;

    [Fact]
    public async Task ImportedRows_CarryTheScriptsAndTheEscortPath()
    {
        (CreatureContent creatures, QuestContent quests) = await s_content.Value;

        Assert.Equal((2843u, 0u), Scripts(quests, 2843));
        Assert.Equal((0u, 9028u), Scripts(quests, 8984)); // the end script id is not the quest id
        Assert.Equal([7u], creatures.Ai.DbScripts.Get(DbScriptKind.QuestStart, 2843).Select(s => s.Command));
        Assert.Equal([3u, 0u, 7u, 3u], creatures.Ai.DbScripts.Get(DbScriptKind.QuestStart, 2480).Select(s => s.Command));
        Assert.Equal(17, creatures.Ai.DbScripts.Get(DbScriptKind.QuestEnd, 9028).Count);
        Assert.Empty(creatures.Ai.DbScripts.Get(DbScriptKind.QuestStart, 9028)); // each table is its own id namespace
        IReadOnlyList<CreatureWaypoint> path = creatures.GetScriptWaypoints(RuulSnowhoofAI.Entry);
        Assert.Equal(Enumerable.Range(1, 36).Select(i => (uint)i), path.Select(p => p.Point));
        Assert.Equal((3231.15f, -524.41f), (path[31].X, path[31].Y)); // point 32, where the quest is done
    }

    [Fact]
    public async Task Quest2843_StartScript_CompletesTheQuestTenSecondsAfterItIsTaken()
    {
        (CreatureContent creatures, QuestContent quests) = await s_content.Value;
        using var rig = new Rig(creatures, quests, mapId: 0, giverEntry: 7853, journal: [Rewarded(2842)]);

        Assert.True(rig.Accept(2843));
        Assert.Equal(QuestStatus.Incomplete, rig.Status(2843));
        Assert.Equal(1, rig.System.PendingDbScriptSteps);
        rig.Run(9_900);
        Assert.Equal(QuestStatus.Incomplete, rig.Status(2843));
        rig.Run(100); // dbscripts_on_quest_start 2843: QUEST_EXPLORED 2843 at 10,000 ms
        Assert.Equal(QuestStatus.Complete, rig.Status(2843));
        Assert.True(rig.State.Quests.Get(2843)!.Explored);
        Assert.Equal(0, rig.System.PendingDbScriptSteps);
    }

    [Fact]
    public async Task Quest2480_StartScript_MovesTheGiver_ThenCompletesTheQuest_ThenSendsHimBack()
    {
        (CreatureContent creatures, QuestContent quests) = await s_content.Value;
        using var rig = new Rig(creatures, quests, mapId: 0, giverEntry: 2391, journal: [Rewarded(2479)]);

        Assert.True(rig.Accept(2480));
        Assert.False(rig.Giver.IsMoving);
        rig.Run(2_000); // MOVE_TO (-4.33, -900.68) at 2 s
        Assert.True(rig.Giver.IsMoving);
        rig.Run(17_000);
        Assert.Equal(-4.33f, rig.Giver.X, 0.5f);
        Assert.Equal(-900.68f, rig.Giver.Y, 0.5f);
        rig.Run(10_900);
        Assert.Equal(QuestStatus.Incomplete, rig.Status(2480));
        rig.Run(100); // QUEST_EXPLORED 2480 at 30 s
        Assert.Equal(QuestStatus.Complete, rig.Status(2480));
        rig.Run(5_000); // MOVE_TO (-4.66, -903.92) at 31 s
        Assert.Equal(-903.92f, rig.Giver.Y, 0.5f);
    }

    [Fact]
    public async Task Quest8984_CompleteScript9028_RunsWhenTheQuestIsRewarded()
    {
        (CreatureContent creatures, QuestContent quests) = await s_content.Value;
        using var rig = new Rig(creatures, quests, mapId: 0, giverEntry: 16107,
            journal: [Rewarded(8983), new CharacterQuestStatus(1, 8984, (byte)QuestStatus.Complete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)]);
        Assert.Equal((uint)NpcFlags.QuestGiver, rig.Giver.NpcFlags);

        Assert.True(rig.Quests.TryPrepareReward(rig.Player, rig.Giver.Guid, 8984, 0, out QuestRewardPlan? plan), "8984 is rewardable");
        rig.Quests.ApplyReward(plan!);
        Assert.True(rig.Status(8984) is QuestStatus.None or QuestStatus.Complete);
        Assert.True(rig.State.Quests.Get(8984)?.Rewarded ?? true);
        Assert.Equal((uint)NpcFlags.QuestGiver, rig.Giver.NpcFlags);
        rig.Run(1_000); // MODIFY_NPC_FLAGS: questgiver off at 1 s
        Assert.Equal(0u, rig.Giver.NpcFlags);
        Assert.DoesNotContain(rig.System.Creatures, c => c.Entry == 16110);
        rig.Run(1_000); // TEMP_SPAWN_CREATURE 16110 (Annalise Lerent) at 2 s, where the script puts her
        Creature annalise = Assert.Single(rig.System.Creatures, c => c.Entry == 16110);
        Assert.Equal((95.6559f, -1713.36f), (annalise.X, annalise.Y));
        rig.Run(52_000); // questgiver back at 54 s
        Assert.Equal((uint)NpcFlags.QuestGiver, rig.Giver.NpcFlags);
    }

    [Fact]
    public async Task Quest6482_RuulSnowhoofEscort_WalksTheScriptPath_AndCompletesTheQuestAtPoint32()
    {
        (CreatureContent creatures, QuestContent quests) = await s_content.Value;
        using var rig = new Rig(creatures, quests, mapId: 1, giverEntry: RuulSnowhoofAI.Entry, journal: []);
        RuulSnowhoofAI ruul = Assert.IsType<RuulSnowhoofAI>(rig.Giver.AI); // the built-in script of the entry, no test registration

        Assert.True(rig.Accept(6482));
        Assert.True(ruul.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(RuulSnowhoofAI.FactionEscortHordeNeutralPassive, rig.Giver.FactionTemplate);
        Assert.Equal(0u, rig.Giver.NpcFlags);
        Assert.Equal(QuestStatus.Incomplete, rig.Status(6482));

        // The player walks with Ruul (the escort fails beyond 100 yd); the first ambush comes at point 14.
        Assert.True(rig.RunUntil(() => ruul.Summoned.Count == 3, follow: true, maxMs: 120_000), "the first ambush at point 14");
        Assert.Equal([3924u, 3925u, 3926u], ruul.Summoned.Select(c => c.Entry).Order());
        Assert.Equal(QuestStatus.Incomplete, rig.Status(6482));
        Assert.True(rig.RunUntil(() => rig.Status(6482) == QuestStatus.Complete, follow: true, maxMs: 300_000), "the quest at point 32");
        Assert.Equal(6, ruul.Summoned.Count); // the second ambush at point 31 came first
        Assert.Equal(3231.15f, rig.Giver.X, 1f);
        Assert.Equal(-524.41f, rig.Giver.Y, 1f);
        Assert.True(rig.RunUntil(() => !rig.Giver.IsAlive, follow: true, maxMs: 120_000), "Ruul leaves at point 36");
    }

    [Fact]
    public async Task Quest6482_FailsWhenThePlayerLeavesTheEscort()
    {
        (CreatureContent creatures, QuestContent quests) = await s_content.Value;
        using var rig = new Rig(creatures, quests, mapId: 1, giverEntry: RuulSnowhoofAI.Entry, journal: []);

        Assert.True(rig.Accept(6482));
        rig.Teleport(rig.Player.X + 300, rig.Player.Y); // beyond DEFAULT_MAX_PLAYER_DISTANCE (100 yd)
        rig.Run(1_100);
        Assert.Equal(QuestStatus.Failed, rig.Status(6482));
    }

    private static (uint Start, uint Complete) Scripts(QuestContent quests, uint entry)
    {
        QuestTemplate quest = Assert.Single(quests.Templates, q => q.Entry == entry);
        return (quest.StartScript, quest.CompleteScript);
    }

    private static CharacterQuestStatus Rewarded(uint quest) => new(1, quest, (byte)QuestStatus.Complete, true, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>The fixture rows through the creature and quest importers into a fresh world database, then through the stores.</summary>
    private static async Task<(CreatureContent, QuestContent)> ImportAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcane-dbscripts-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var db = new WorldDbContext(options))
            {
                await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
                var creatures = new CreatureDumpImporter();
                creatures.Read(new StringReader(ClassicDbScriptedQuestRows.Dump));
                CreatureImportReport report = await creatures.WriteAsync(db, replace: false);
                Assert.Equal(1 + 4 + 1 + 17, report.DbScriptSteps); // quest_start 2843, 2480, 6482; quest_end 9028
                Assert.Equal(36, report.ScriptWaypoints);
                var quests = new ItemQuestDumpImporter();
                quests.Read(new StringReader(ClassicDbScriptedQuestRows.Dump));
                await quests.WriteAsync(db, replace: false);
            }

            await using var read = new WorldDbContext(options);
            return (await new EfCreatureDataStore(read).LoadAsync(), await new EfQuestContentStore(read).LoadAsync());
        }
        finally
        {
            File.Delete(path); // Pooling=False: no pooled connection holds the file
        }
    }

    /// <summary>
    /// One level 30 orc rogue next to a giver of the imported content on its map, a creature system per map with the quest seams bound to the
    /// quest service (as the world binds them: ScriptQuestEvents and EventAiQuestEvents), and the DB script hooks of the world
    /// (<see cref="CreatureQuestScripts"/>).
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _hooks;

        public Rig(CreatureContent creatures, QuestContent quests, uint mapId, uint giverEntry, IReadOnlyList<CharacterQuestStatus> journal)
        {
            World = new WorldRuntime(new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0 }, new NullSaves(),
                NullLogger<WorldRuntime>.Instance);
            Map map = World.GetMap(mapId, 0);
            var seams = new QuestSeams(() => Quests!);
            var ai = new CreatureAiServices { ScriptQuests = seams, QuestEvents = seams };
            System = new CreatureMapSystem(map, creatures, random: new Random(1), aiServices: ai);
            map.AddUpdater(System);

            CreatureSpawn spawn = creatures.GetSpawns(mapId).Single(s => s.Entry == giverEntry);
            Player = CreatePlayer(mapId, spawn.X + 2, spawn.Y, spawn.Z);
            World.AddPlayer(Player);
            World.RunTick(0);
            Giver = Assert.Single(System.Creatures, c => c.Entry == giverEntry);

            // Every creature faction of the excerpt is friendly enough to talk to (FactionTemplateRecord: id, faction, flags, our, friend, enemy).
            var factions = new FactionTemplateCatalog([
                new(1, 1, 0, 1, 0, 0), .. creatures.Templates.Select(t => t.Faction).Distinct().Select(f => new FactionTemplateRecord(f, 0, 0, 8, 0, 0))]);
            Quests = new QuestNpcServices(new QuestStore(quests), NpcStore.Empty, new QuestNpcDependencies(Creatures: new CreatureQuestLookup(factions), Party: new Solo(World)),
                new QuestNpcOptions(), new NullSink(), () => 100, NullLogger.Instance);
            State = Quests.Track(Player);
            Quests.CompleteLoad(State, new CharacterQuestData(journal, []));
            _hooks = CreatureQuestScripts.Attach(Quests, m => ReferenceEquals(m, map) ? System : null, () => creatures, () => ai.Factory);
        }

        public WorldRuntime World { get; }

        public CreatureMapSystem System { get; }

        public Player Player { get; }

        public Creature Giver { get; }

        public QuestNpcServices Quests { get; }

        public PlayerNpcState State { get; }

        public bool Accept(uint quest)
        {
            bool accepted = Quests.AcceptQuest(Player, Giver.Guid, quest);
            Assert.True(accepted, $"accept {quest}: missing {Quests.MissingAdapters(Quests.Quests.Get(quest)!)}, visible {Player.VisibleObjects.Contains(Giver.Guid)}");
            return accepted;
        }

        public QuestStatus Status(uint quest) => State.Quests.GetStatus(quest);

        public void Run(uint ms)
        {
            for (uint done = 0; done < ms; done += 100)
            {
                Tick();
            }
        }

        /// <summary>Tick until <paramref name="condition"/> holds (false after <paramref name="maxMs"/>); with <paramref name="follow"/> the player keeps 3 yd behind the giver.</summary>
        public bool RunUntil(Func<bool> condition, bool follow, uint maxMs)
        {
            for (uint done = 0; done < maxMs; done += 100)
            {
                if (condition())
                {
                    return true;
                }

                if (follow)
                {
                    Teleport(Giver.X - 3, Giver.Y, Giver.Z);
                }

                Tick();
            }

            return condition();
        }

        public void Teleport(float x, float y, float? z = null)
        {
            // A relocation is enough here: the escort's range check reads the coordinates (no visibility or grid work is involved).
            Player.Relocate(x, y, z ?? Player.Z, Player.Orientation, World.NowMs);
        }

        public void Dispose()
        {
            _hooks.Dispose();
            World.Dispose();
        }

        private void Tick()
        {
            World.RunTick(100);
        }

        private static Player CreatePlayer(uint mapId, float x, float y, float z)
        {
            var character = new CharacterRecord
            {
                Id = 1, AccountId = 1, Name = "Scripted", Race = Orc, Class = Rogue, Gender = 0, Level = 30,
                MapId = mapId, ZoneId = 0, X = x, Y = y, Z = z,
            };
            var appearance = new PlayerAppearance(
                DisplayId: 51, FactionTemplate: 1, PowerType.Energy, BaseHealth: 500, BaseMana: 0,
                MaxHealth: 500, MaxPower: 100, StartPower: 100, NextLevelXp: 40000);
            var player = new Player(character, appearance, new NullSession());
            player.Inventory.Templates = new ItemTemplateStore([]);
            player.Inventory.GuidAllocator = new ItemGuidAllocator();
            player.Inventory.Load([]);
            return player;
        }
    }

    /// <summary>The world's quest seams of the creature AI (ScriptQuestEvents and EventAiQuestEvents without groups), on the rig's quest service.</summary>
    private sealed class QuestSeams(Func<QuestNpcServices> quests) : IScriptQuestEvents, IEventAiQuestEvents
    {
        public void AreaExploredOrEventHappens(Player player, uint questId) => quests().AreaExploredOrEventHappens(player, questId);

        public void FailQuest(Player player, uint questId) => quests().FailQuest(player, questId);

        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) => quests().KilledMonsterCredit(player, creatureEntry, source);

        public void GroupEventFailHappens(Player player, uint questId) => quests().GroupEventFailHappens(player, questId);

        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];

        public void EventHappened(Player player, uint questId, Creature source, bool rewardGroup) => quests().AreaExploredOrEventHappens(player, questId);

        public void KillCredit(Player player, uint creatureEntry, Creature source) => quests().KilledMonsterCredit(player, creatureEntry, source.Guid);
    }

    /// <summary>The player plays alone (the party seam the world binds; the quests' QUEST_FLAGS_PARTY_ACCEPT needs one).</summary>
    private sealed class Solo(WorldRuntime world) : IQuestParty
    {
        public IReadOnlyList<Player> MembersOf(Player player) => [];

        public bool IsInSameGroup(Player first, Player second) => false;

        public bool IsInSameRaid(Player first, Player second) => false;

        public Player? FindPlayer(ObjectGuid guid) => world.FindOnlinePlayer(guid);
    }

    private sealed class NullSink : IQuestNpcSink
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

    private sealed class NullSaves : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }

    private sealed class NullSession : IPlayerSession
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
}
