using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts.World;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Zone script batch 15: the Theramore sparring matches (dustwallow_marsh.cpp npc_theramore_spar_controller), against a real map with a
/// recording caster and quest service. Random draws come from the map's seeded Random(1).
/// </summary>
public sealed class ZoneScriptBatch15Tests
{
    private const float Z = 83.5f;
    private const uint HugeHealth = 1_000_000;

    private sealed class RecordingCaster : ICreatureSpellCaster
    {
        public List<(Creature Caster, uint Spell, Unit? Target)> Casts { get; } = [];

        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add { }
            remove { }
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Casts.Add((caster, spellId, target));
            return CreatureCastResult.Ok;
        }

        public bool IsCasting(Creature caster) => false;

        public HashSet<(Unit Unit, uint Spell)> Auras { get; } = [];

        public List<(Unit Unit, uint Spell)> Removed { get; } = [];

        public bool HasAura(Unit unit, uint spellId) => Auras.Contains((unit, spellId));

        public void RemoveAuras(Unit unit, uint spellId)
        {
            Removed.Add((unit, spellId));
            Auras.Remove((unit, spellId));
        }

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    private sealed class Quests : IScriptQuestEvents, IEventAiQuestEvents
    {
        public HashSet<uint> Incomplete { get; } = [];

        public List<(Player Player, uint QuestId)> Completed { get; } = [];

        public bool IsQuestIncomplete(Player player, uint questId) => Incomplete.Contains(questId);

        public void AreaExploredOrEventHappens(Player player, uint questId) => Completed.Add((player, questId));

        public void FailQuest(Player player, uint questId)
        {
        }

        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source)
        {
        }

        public void GroupEventFailHappens(Player player, uint questId)
        {
        }

        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];

        public void EventHappened(Player player, uint questId, Creature source, bool rewardGroup)
        {
            Assert.True(rewardGroup);
            Completed.Add((player, questId));
        }

        public void KillCredit(Player player, uint creatureEntry, Creature source)
        {
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(uint entry, string name, int[]? textIds = null, CreatureTemplate[]? extraTemplates = null, CreatureSpawn[]? extraSpawns = null,
            IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? scriptWaypoints = null, uint faction = 35, float x = 0f, float y = 0f, float z = Z,
            IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? entryWaypoints = null)
        {
            var aiContent = new CreatureAiContent([], [.. (textIds ?? []).Where(id => id < 0).Select(id => new CreatureAiText(id, $"text{id}", 0, 0, 0))],
                new BroadcastTextCatalog((textIds ?? []).Where(id => id > 0).Select(id =>
                    new BroadcastText((uint)id, $"text{id}", string.Empty, 0, 0, 0, [0, 0, 0], [0, 0, 0]))));
            CreatureContent content = new([Template(entry, Tpl(name, faction)), .. extraTemplates ?? []],
                [Spawn(1, entry, x, y, z), .. extraSpawns ?? []], [], [], [], aiContent, entryWaypoints: entryWaypoints, scriptWaypoints: scriptWaypoints);
            var services = new CreatureAiServices { ScriptQuests = Quest, QuestEvents = Quest, Spells = Caster };
            (World, Map, System) = CreateAiSystem(content, services);
            (Player, Session) = AddPlayer(World, 1, x + 1f, y);
            Player.Relocate(x + 1f, y, z, 0, 0);
            Player.MaxHealth = HugeHealth;
            Player.Health = HugeHealth;
            Npc = Assert.Single(System.Creatures, c => c.Template.Entry == entry);
        }

        public RecordingCaster Caster { get; } = new();

        public Quests Quest { get; } = new();

        public WorldRuntime World { get; }

        public Map Map { get; }

        public CreatureMapSystem System { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        public Creature Npc { get; }

        public T Ai<T>() where T : CreatureAI => Assert.IsType<T>(Npc.AI);

        public List<(string Name, string Message)> Chat() => [.. Packets(Session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p)).Select(c => (c.Name, c.Message))];

        public List<string> Heard() => [.. Chat().Select(c => c.Message)];

        public Creature One(uint entry) => Assert.Single(System.Creatures, c => c.Template.Entry == entry && !ReferenceEquals(c, Npc));

        public KalimdorWorldMapData AddKalimdorData()
        {
            var data = new KalimdorWorldMapData(Map);
            data.Initialize();
            Map.AddUpdater(data);
            return data;
        }

        public GameObjectMapSystem AddObjects(params (uint Entry, GameObjectType Type)[] templates)
            => AddObjects(templates, []);

        public GameObjectMapSystem AddObjects((uint Entry, GameObjectType Type)[] templates, GameObjectSpawn[] spawns)
        {
            var objects = new GameObjectMapSystem(Map, new GameObjectContent([.. templates.Select(t => GameObjectTestKit.GoTemplate(t.Entry, t.Type))], spawns, [], [], []));
            Map.AddUpdater(objects);
            return objects;
        }

        public void RunUntil(Func<bool> done, int limitMs = 60_000)
        {
            for (int elapsed = 0; elapsed < limitMs && !done(); elapsed += 100)
            {
                World.RunTick(100);
            }

            Assert.True(done(), $"not done within {limitMs} ms");
        }

        public void Dispose() => World.Dispose();
    }

    private static Action<CreatureTemplateBuilder> Tpl(string name, uint faction = 35, uint health = HugeHealth) => b =>
    {
        b.Name = name;
        b.Faction = faction;
        b.MinLevelHealth = health;
        b.MaxLevelHealth = health;
    };


    private static List<int> Ids(Rig rig) => [.. rig.Heard().Select(m => int.Parse(m.AsSpan(4), System.Globalization.CultureInfo.InvariantCulture))];

    private static NpcInfo InfoOf(Creature c)
        => new(c.Guid, c.Entry, c.Spawn?.Guid ?? 0, NpcFlags.Gossip, 0, c.X, c.Y, c.Z, c.BoundingRadius, c.IsAlive, false, c.Combat.IsInCombat, false, 0);

    private static void SetClass(Player player, Class cls) => player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)cls);

    private static void Tick(Rig rig, int ms)
    {
        for (int elapsed = 0; elapsed < ms; elapsed += 100)
        {
            rig.World.RunTick(100);
        }
    }

    private const uint Red = TheramoreSparControllerAI.RedTeam, Blue = TheramoreSparControllerAI.BlueTeam;
    private const float ArenaX = -3648f, ArenaY = -4520f, ArenaZ = 9.462863f;

    private static readonly int[] SparTexts =
    [
        TheramoreSparControllerAI.SayRedWinner, TheramoreSparControllerAI.SayBlueWinner, TheramoreSparControllerAI.SayRedWinnerOverall,
        TheramoreSparControllerAI.SayBlueWinnerOverall, TheramoreSparControllerAI.SayBackToCorners, TheramoreSparControllerAI.SayForTheRed,
        TheramoreSparControllerAI.SayForTheBlue, TheramoreSparControllerAI.SayFirstCombatants, TheramoreSparControllerAI.SayFightIntro,
        TheramoreSparControllerAI.SayFightStart, TheramoreSparControllerAI.SayEdwardLoss, TheramoreSparControllerAI.SayKahilLoss,
        TheramoreSparControllerAI.SayNarrishaLoss, TheramoreSparControllerAI.SayJaradLoss, TheramoreSparControllerAI.SayLanaLoss,
        TheramoreSparControllerAI.SayTarkLoss, TheramoreSparControllerAI.SayRedMotivationWin, TheramoreSparControllerAI.SayRedMotivationLoss,
        TheramoreSparControllerAI.SayBlueMotivationLoss, TheramoreSparControllerAI.SayBlueMotivationWin, TheramoreSparControllerAI.SayRedFirst,
        TheramoreSparControllerAI.SayBlueFirst,
    ];

    private static Rig Arena(bool withRed = true)
    {
        uint[] cast =
        [
            .. withRed ? TheramoreSparControllerAI.RedTeamEntries : [], .. TheramoreSparControllerAI.BlueTeamEntries,
            TheramoreSparControllerAI.NpcMasterCriton, TheramoreSparControllerAI.NpcRedCaptain, TheramoreSparControllerAI.NpcBlueCaptain,
            TheramoreSparControllerAI.NpcMedicHelaina, TheramoreSparControllerAI.NpcMedicTamberlyn,
        ];
        uint guid = 10;
        float offset = 0f;
        // Szigeti stands about 7 yd from both corners (startingPositions), the others in a row within 15 yd of him.
        return new Rig(TheramoreSparControllerAI.NpcMasterSzigeti, "Master Szigeti", SparTexts,
            extraTemplates: [.. cast.Select(e => Template(e, Tpl($"npc{e}")))],
            extraSpawns: [.. cast.Select(e => Spawn(guid++, e, ArenaX + 3f + (offset += 1.5f), ArenaY + 4f, ArenaZ))],
            x: ArenaX, y: ArenaY, z: ArenaZ);
    }

    private static TheramoreSparControllerAI Controller(Rig rig) => rig.Ai<TheramoreSparControllerAI>();

    [Fact]
    public void Spar_TheControllerRunsOnSzigeti_AndGathersBothTeamsOnTheramoreFactionAfterTwoSeconds()
    {
        using Rig rig = Arena();
        TheramoreSparControllerAI ai = Controller(rig);
        Tick(rig, 2_100);

        Assert.Equal(3, ai.Waiting(Red).Count);
        Assert.Equal(3, ai.Waiting(Blue).Count);
        Assert.All(ai.Waiting(Red).Concat(ai.Waiting(Blue)), c => Assert.Equal(TheramoreSparControllerAI.FactionTheramoreFriendly, c.FactionTemplate));
        Assert.Empty(ai.Active);
    }

    [Fact]
    public void Spar_TheFirstPairIsAnnouncedSalutesAndFightsOnTeamFactions()
    {
        using Rig rig = Arena();
        TheramoreSparControllerAI ai = Controller(rig);
        Tick(rig, 10_100);

        Assert.Equal(TheramoreSparControllerAI.NpcGuardJarad, ai.Active[Red].Template.Entry);
        Assert.Equal(TheramoreSparControllerAI.NpcGuardEdward, ai.Active[Blue].Template.Entry);
        Assert.Equal(2, ai.Waiting(Red).Count);
        List<int> said = Ids(rig);
        Assert.Contains(TheramoreSparControllerAI.SayFirstCombatants, said);
        Assert.Contains(TheramoreSparControllerAI.SayRedFirst, said);
        Assert.Contains(TheramoreSparControllerAI.SayBlueFirst, said);

        Tick(rig, 5_000);
        Assert.Contains(TheramoreSparControllerAI.SayForTheRed, Ids(rig));
        Assert.Contains(TheramoreSparControllerAI.SayForTheBlue, Ids(rig));
        Tick(rig, 3_000);
        Assert.Contains(TheramoreSparControllerAI.SayFightIntro, Ids(rig));
        Tick(rig, 3_000);

        Assert.Contains(TheramoreSparControllerAI.SayFightStart, Ids(rig));
        Creature red = ai.Active[Red], blue = ai.Active[Blue];
        Assert.Equal(Red, red.FactionTemplate);
        Assert.Equal(Blue, blue.FactionTemplate);
        Assert.Same(blue, red.Combat.Victim);
        Assert.Same(red, blue.Combat.Victim);
        Assert.True(ai.IsScheduled(TheramoreSparControllerAI.Step.CheckHp));
    }

    private static void FightUntilStarted(Rig rig, TheramoreSparControllerAI ai)
        => rig.RunUntil(() => ai.IsScheduled(TheramoreSparControllerAI.Step.CheckHp), 60_000);

    [Fact]
    public void Spar_AtThirtyPercentTheOtherWins_FightStopsMedicsHealAndTheLoserIsReplaced()
    {
        using Rig rig = Arena();
        TheramoreSparControllerAI ai = Controller(rig);
        FightUntilStarted(rig, ai);
        Creature red = ai.Active[Red], blue = ai.Active[Blue];
        rig.Session.Clear();

        blue.Health = blue.MaxHealth / 4;
        rig.RunUntil(() => ai.WinningTeam == Red, 2_000);

        Assert.False(red.HasTemporaryFaction);
        Assert.False(blue.HasTemporaryFaction);
        Assert.False(red.Combat.IsInCombat);
        Assert.Contains(rig.Caster.Casts, c => c.Spell == TheramoreSparControllerAI.SpellFirstAid && ReferenceEquals(c.Target, blue));
        Assert.Contains(rig.Caster.Casts, c => c.Spell == TheramoreSparControllerAI.SpellFirstAid && ReferenceEquals(c.Target, red));
        List<int> said = Ids(rig);
        Assert.Contains(TheramoreSparControllerAI.SayRedWinner, said);
        Assert.Contains(TheramoreSparControllerAI.SayEdwardLoss, said);
        Assert.Contains(TheramoreSparControllerAI.SayBackToCorners, said);
        Assert.DoesNotContain(TheramoreSparControllerAI.SayRedWinnerOverall, said);

        Tick(rig, 5_300);
        Assert.Equal(TheramoreSparControllerAI.NpcGuardKahil, ai.Active[Blue].Template.Entry);
        Assert.Same(red, ai.Active[Red]);
        Assert.Single(ai.Waiting(Blue));
        Assert.Equal(CreatureReactState.Passive, blue.ReactState);

        FightUntilStarted(rig, ai);
        Assert.True(ReferenceEquals(ai.Active[Blue], red.Combat.Victim),
            $"red {red.Template.Entry} victim {(red.Combat.Victim as Creature)?.Template.Entry} evading {red.IsInEvadeMode} blue {ai.Active[Blue].Template.Entry} evading {ai.Active[Blue].IsInEvadeMode} victim {(ai.Active[Blue].Combat.Victim as Creature)?.Template.Entry}");
    }

    [Fact]
    public void Spar_WhenATeamHasNoOneLeftCritonNamesTheWinner_TheCaptainsSpeak_AndANewRoundStarts()
    {
        using Rig rig = Arena();
        TheramoreSparControllerAI ai = Controller(rig);
        for (int bout = 0; bout < 3; bout++)
        {
            FightUntilStarted(rig, ai);
            Creature blue = ai.Active[Blue];
            blue.Health = blue.MaxHealth / 4;
            int wins = bout + 1;
            rig.RunUntil(() => Ids(rig).Count(id => id == TheramoreSparControllerAI.SayRedWinner) == wins, 2_000);
            if (bout < 2)
            {
                rig.RunUntil(() => ai.Active[Blue] != blue, 7_000);
            }
        }

        Assert.True(Ids(rig).Contains(TheramoreSparControllerAI.SayRedWinnerOverall),
            $"said {string.Join(",", Ids(rig))} waiting {ai.Waiting(Blue).Count} active {string.Join(",", ai.Active.Values.Select(c => c.Template.Entry))}");
        rig.RunUntil(() => Ids(rig).Contains(TheramoreSparControllerAI.SayRedMotivationWin), 9_000);
        Assert.Contains(TheramoreSparControllerAI.SayBlueMotivationLoss, Ids(rig));

        rig.RunUntil(() => ai.Waiting(Blue).Count == 3, 6_000);
        Assert.Empty(ai.Active);
        rig.RunUntil(() => ai.Active.Count == 2, 21_000);
    }

    [Fact]
    public void Spar_WithAWholeTeamMissingThereIsNoBout()
    {
        using Rig rig = Arena(withRed: false);
        TheramoreSparControllerAI ai = Controller(rig);
        Tick(rig, 30_000);

        Assert.Empty(ai.Active);
        Assert.DoesNotContain(TheramoreSparControllerAI.SayFightStart, Ids(rig));
    }
}
