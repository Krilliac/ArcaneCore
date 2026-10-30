using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.World;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Zone script batch 7: Tooga (tanaris.cpp) and Freedom for All Creatures, Kindal Moonweaver, the Captured Sprite Darters and the cage door
/// with the Kalimdor map script's freed count (feralas.cpp, world_kalimdor.cpp), against a real map with a recording caster and quest service.
/// Random draws come from the map's seeded Random(1).
/// </summary>
public sealed class ZoneScriptBatch7Tests
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

        public bool HasAura(Unit unit, uint spellId) => false;

        public void RemoveAuras(Unit unit, uint spellId)
        {
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
            IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? scriptWaypoints = null, uint faction = 35, float x = 0f, float y = 0f, float z = Z)
        {
            var aiContent = new CreatureAiContent([], [.. (textIds ?? []).Select(id => new CreatureAiText(id, $"text{id}", 0, 0, 0))]);
            CreatureContent content = new([Template(entry, Tpl(name, faction)), .. extraTemplates ?? []],
                [Spawn(1, entry, x, y, z), .. extraSpawns ?? []], [], [], [], aiContent, scriptWaypoints: scriptWaypoints);
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

    /// <summary>Puts <paramref name="other"/> <paramref name="distance"/> yards from the NPC (beyond both bounding radii).</summary>
    private static void PlaceNear(Rig rig, Creature other, float distance)
    {
        float reach = distance + rig.Npc.BoundingRadius + other.BoundingRadius;
        other.Relocate(rig.Npc.X + reach, rig.Npc.Y, rig.Npc.Z);
    }

    // --- registration -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ToogaAI.Entry, typeof(ToogaAI))]
    [InlineData(KindalMoonweaverAI.Entry, typeof(KindalMoonweaverAI))]
    [InlineData(CapturedSpriteDarterAI.Entry, typeof(CapturedSpriteDarterAI))]
    public void TheEntryRunsItsScript(uint entry, Type ai)
    {
        using var rig = new Rig(entry, "npc");
        Assert.IsType(ai, rig.Npc.AI);
    }

    [Fact]
    public void TheEventQuestsAreListedAsScripted()
    {
        IReadOnlyCollection<uint> quests = new CreatureAiFactory().ScriptedEventQuests;
        Assert.Contains(ToogaAI.QuestTooga, quests);
        Assert.Contains(KindalMoonweaverAI.QuestFreedomForAllCreatures, quests);
    }

    // --- Tooga ------------------------------------------------------------------------------------------------

    private static readonly int[] ToogaTexts =
    [
        ToogaAI.SayThirst, ToogaAI.SayWorried, ToogaAI.SayPost1, ToogaAI.SayTortaPost2, ToogaAI.SayPost3, ToogaAI.SayTortaPost4, ToogaAI.SayPost5,
        ToogaAI.SayTortaPost6,
    ];

    private static Rig ToogaRig()
        => new(ToogaAI.Entry, "Tooga", ToogaTexts, extraTemplates: [Template(ToogaAI.NpcTorta, Tpl("Torta"))], extraSpawns: [Spawn(2, ToogaAI.NpcTorta, 60f, 0f)]);

    [Fact]
    public void Tooga_TheQuestMakesHimFollowAsAPassiveEscort_AnotherQuestDoesNothing()
    {
        using Rig rig = ToogaRig();
        var ai = rig.Ai<ToogaAI>();

        ai.OnQuestAccept(rig.Player, 999_999);
        Assert.False(ai.HasFollowState(FollowerAI.FollowState.InProgress));

        ai.OnQuestAccept(rig.Player, ToogaAI.QuestTooga);

        Assert.True(ai.HasFollowState(FollowerAI.FollowState.InProgress));
        Assert.Equal(ToogaAI.FactionEscortNeutralFriendPassive, rig.Npc.FactionTemplate);
        Assert.Equal(MovementGeneratorType.Follow, rig.Npc.Motion.CurrentType);
    }

    [Fact]
    public void Tooga_WhileFollowingHeSometimesSaysOneOfHisTwoWorriedLines_AndNothingElse()
    {
        using Rig rig = ToogaRig();
        var ai = rig.Ai<ToogaAI>();
        ai.OnQuestAccept(rig.Player, ToogaAI.QuestTooga);
        rig.Session.Clear();

        for (int i = 0; i < 6000; i++)
        {
            rig.Player.Relocate(rig.Npc.X + 1f, rig.Npc.Y, rig.Npc.Z, 0, 0);
            rig.World.RunTick(100);
        }

        List<string> heard = rig.Heard();
        Assert.NotEmpty(heard); // 120 rolls of 2 in 51
        Assert.All(heard, line => Assert.Contains(line, new[] { $"text{ToogaAI.SayThirst}", $"text{ToogaAI.SayWorried}" }));
        Assert.True(heard.Count < 30);
    }

    [Fact]
    public void Tooga_ReachingTortaWithTheQuestIncompleteCreditsTheGroup_TheyTradeSixLines_ThenHeWalksIntoTheWaterAndGoes()
    {
        using Rig rig = ToogaRig();
        rig.Quest.Incomplete.Add(ToogaAI.QuestTooga);
        var ai = rig.Ai<ToogaAI>();
        ai.OnQuestAccept(rig.Player, ToogaAI.QuestTooga);
        Creature torta = rig.One(ToogaAI.NpcTorta);
        PlaceNear(rig, torta, 4.5f);
        rig.Session.Clear();

        ai.MoveInLineOfSight(torta);

        Assert.Equal([(rig.Player, ToogaAI.QuestTooga)], rig.Quest.Completed);
        Assert.True(ai.HasFollowState(FollowerAI.FollowState.Complete | FollowerAI.FollowState.PostEvent));
        rig.RunUntil(() => rig.Heard().Count == 6, 40_000);

        int[] scene = [ToogaAI.SayPost1, ToogaAI.SayTortaPost2, ToogaAI.SayPost3, ToogaAI.SayTortaPost4, ToogaAI.SayPost5, ToogaAI.SayTortaPost6];
        Assert.Equal([.. scene.Select(id => $"text{id}")], rig.Heard());
        List<(string Name, string Message)> chat = rig.Chat();
        Assert.Equal(["Tooga", "Torta", "Tooga", "Torta", "Tooga", "Torta"], chat.Select(c => c.Name));
        Assert.Equal(MovementGeneratorType.Point, rig.Npc.Motion.CurrentType);
        Assert.True(rig.Npc.IsAlive);

        ai.OnMovementInform(MovementGeneratorType.Point, ToogaAI.PointToWater);
        rig.RunUntil(() => !rig.Npc.IsAlive, 3_000);
        Assert.Single(rig.Quest.Completed);
    }

    [Fact]
    public void Tooga_TheReunionLinesComeFiveSecondsApartAfterAFirstSecond()
    {
        using Rig rig = ToogaRig();
        var ai = rig.Ai<ToogaAI>();
        ai.OnQuestAccept(rig.Player, ToogaAI.QuestTooga);
        Creature torta = rig.One(ToogaAI.NpcTorta);
        PlaceNear(rig, torta, 1f);
        rig.Session.Clear();
        ai.MoveInLineOfSight(torta);

        int first = 0;
        for (; first < 20_000 && rig.Heard().Count == 0; first += 100)
        {
            rig.World.RunTick(100);
        }

        int second = 0;
        for (; second < 20_000 && rig.Heard().Count == 1; second += 100)
        {
            rig.World.RunTick(100);
        }

        Assert.InRange(first, 6_000, 6_300); // 1 s, then phase 0 says nothing for 5 s
        Assert.InRange(second, 4_900, 5_200);
    }

    [Fact]
    public void Tooga_WithoutTheQuestThereIsNoCreditButTheReunionStillPlays_AndADeadTortaEndsItAtOnce()
    {
        using Rig rig = ToogaRig();
        var ai = rig.Ai<ToogaAI>();
        ai.OnQuestAccept(rig.Player, ToogaAI.QuestTooga);
        Creature torta = rig.One(ToogaAI.NpcTorta);
        PlaceNear(rig, torta, 1f);
        rig.Session.Clear();
        ai.MoveInLineOfSight(torta);
        Assert.Empty(rig.Quest.Completed);
        rig.RunUntil(() => rig.Heard().Count == 1, 10_000);

        rig.Map.Combat.Kill(rig.Player, torta);
        rig.RunUntil(() => !rig.Npc.IsAlive, 8_000);

        Assert.Single(rig.Heard());
    }

    [Fact]
    public void Tooga_TortaOutOfReachOrAnotherCreature_DoNothing()
    {
        using Rig rig = ToogaRig();
        var ai = rig.Ai<ToogaAI>();
        ai.OnQuestAccept(rig.Player, ToogaAI.QuestTooga);
        rig.Quest.Incomplete.Add(ToogaAI.QuestTooga);
        Creature torta = rig.One(ToogaAI.NpcTorta);
        PlaceNear(rig, torta, 5.5f);

        ai.MoveInLineOfSight(torta);
        ai.MoveInLineOfSight(rig.Player);

        Assert.Empty(rig.Quest.Completed);
        Assert.False(ai.HasFollowState(FollowerAI.FollowState.Complete));
    }

    // --- Freedom for All Creatures ----------------------------------------------------------------------------

    private static readonly int[] KindalTexts = [KindalMoonweaverAI.SayQuestStart, KindalMoonweaverAI.SayInitialAggro, KindalMoonweaverAI.SayAggro, KindalMoonweaverAI.SayQuestComplete];

    /// <summary>Four eight-point flight paths for the darter entry, each a straight 16-yard line out from (x, y).</summary>
    private static IEnumerable<(uint, uint, CreatureWaypoint)> DarterPaths(float x, float y)
    {
        for (uint path = 0; path < 4; path++)
        {
            for (uint point = 1; point <= 8; point++)
            {
                float angle = path * MathF.PI / 2;
                yield return (CapturedSpriteDarterAI.Entry, path, new CreatureWaypoint(point, x + (MathF.Cos(angle) * 2f * point), y + (MathF.Sin(angle) * 2f * point), Z, 0f, 0));
            }
        }
    }

    [Fact]
    public void Kindal_TheQuestMakesHerSayHerLineStandLoseNpcImmunityAndFollow_AnotherQuestDoesNothing()
    {
        using var rig = new Rig(KindalMoonweaverAI.Entry, "Kindal Moonweaver", KindalTexts);
        var ai = rig.Ai<KindalMoonweaverAI>();
        rig.Npc.UnitFlags |= UnitFlags.ImmuneToNpc;
        rig.Npc.StandState = StandState.Sit;

        ai.OnQuestAccept(rig.Player, 999_999);
        Assert.False(ai.HasFollowState(FollowerAI.FollowState.InProgress));
        Assert.Empty(rig.Heard());

        ai.OnQuestAccept(rig.Player, KindalMoonweaverAI.QuestFreedomForAllCreatures);

        Assert.True(ai.HasFollowState(FollowerAI.FollowState.InProgress));
        Assert.Equal(KindalMoonweaverAI.FactionEscortAllianceNeutralActive, rig.Npc.FactionTemplate);
        Assert.Equal(StandState.Stand, rig.Npc.StandState);
        Assert.Equal(UnitFlags.None, rig.Npc.UnitFlags & UnitFlags.ImmuneToNpc);
        Assert.Equal([$"text{KindalMoonweaverAI.SayQuestStart}"], rig.Heard());
    }

    [Fact]
    public void TheMapScript_CountsSixFreedDartersToDone_AndAnInProgressRestartsTheCount()
    {
        using var rig = new Rig(KindalMoonweaverAI.Entry, "Kindal Moonweaver");
        KalimdorWorldMapData data = rig.AddKalimdorData();

        for (int i = 0; i < 5; i++)
        {
            data.SetData(KalimdorWorldMapData.TypeFreedomCreatures, EncounterState.Special);
        }

        Assert.Equal(EncounterState.Special, data.GetData(KalimdorWorldMapData.TypeFreedomCreatures));
        data.SetData(KalimdorWorldMapData.TypeFreedomCreatures, EncounterState.InProgress);
        Assert.Equal(0u, data.FreedSpriteDarters);
        for (int i = 0; i < 5; i++)
        {
            data.SetData(KalimdorWorldMapData.TypeFreedomCreatures, EncounterState.Special);
        }

        Assert.NotEqual(EncounterState.Done, data.GetData(KalimdorWorldMapData.TypeFreedomCreatures));
        data.SetData(KalimdorWorldMapData.TypeFreedomCreatures, EncounterState.Special);
        Assert.Equal(EncounterState.Done, data.GetData(KalimdorWorldMapData.TypeFreedomCreatures));
    }

    [Fact]
    public void Kindal_WhenSixDartersAreFreeSheCreditsTheGroupThanksThePlayerResetsTheCount_AndGoesBackFiveSecondsLater()
    {
        using var rig = new Rig(KindalMoonweaverAI.Entry, "Kindal Moonweaver", KindalTexts);
        KalimdorWorldMapData data = rig.AddKalimdorData();
        rig.Quest.Incomplete.Add(KindalMoonweaverAI.QuestFreedomForAllCreatures);
        var ai = rig.Ai<KindalMoonweaverAI>();
        ai.OnQuestAccept(rig.Player, KindalMoonweaverAI.QuestFreedomForAllCreatures);
        Run(rig.World, 2_000);
        Assert.Empty(rig.Quest.Completed);
        rig.Session.Clear();

        for (int i = 0; i < 6; i++)
        {
            data.SetData(KalimdorWorldMapData.TypeFreedomCreatures, EncounterState.Special);
        }

        rig.World.RunTick(100);

        Assert.Equal([(rig.Player, KindalMoonweaverAI.QuestFreedomForAllCreatures)], rig.Quest.Completed);
        Assert.Equal([$"text{KindalMoonweaverAI.SayQuestComplete}"], rig.Heard());
        Assert.Equal(EncounterState.NotStarted, data.GetData(KalimdorWorldMapData.TypeFreedomCreatures));
        Assert.InRange(ai.DespawnMs, KindalMoonweaverAI.DespawnDelayMs - 200, KindalMoonweaverAI.DespawnDelayMs);

        Run(rig.World, 5_200);

        Assert.False(rig.Npc.AI is KindalMoonweaverAI { } still && still.HasFollowState(FollowerAI.FollowState.InProgress));
        Assert.Single(rig.Quest.Completed);
    }

    [Fact]
    public void Kindal_MultiShotIsRearmedAfterACast_SoItFiresOncePerEightToElevenSeconds()
    {
        // mangos-classic feralas.cpp npc_kindal_moonweaver re-arms m_shootTimer where m_multiShotTimer is meant (a typo); retail intends one Multi-Shot per urand(8,11) s.
        using var rig = new Rig(KindalMoonweaverAI.Entry, "Kindal Moonweaver", KindalTexts,
            extraTemplates: [Template(KalimdorWorldMapData.NpcGrimtotemRaider, Tpl("Grimtotem Raider", 14))],
            extraSpawns: [Spawn(2, KalimdorWorldMapData.NpcGrimtotemRaider, 14f, 0f)], faction: 231);
        rig.Npc.MaxHealth = HugeHealth;
        rig.Npc.Health = HugeHealth;
        Creature raider = rig.One(KalimdorWorldMapData.NpcGrimtotemRaider);
        raider.MaxHealth = HugeHealth;
        raider.Health = HugeHealth;
        rig.System.AttackStart(rig.Npc, raider);

        for (int elapsed = 0; elapsed < 30_000; elapsed += 100)
        {
            rig.Npc.Relocate(0f, 0f, Z, 0, 0); // hold her at range: the cast gate is the distance to the victim
            rig.World.RunTick(100);
        }

        int multiShots = rig.Caster.Casts.Count(c => c.Spell == KindalMoonweaverAI.SpellMultiShot);
        Assert.InRange(multiShots, 1, 4); // 30 s of 8-11 s timers; the unarmed timer cast on every tick
    }

    [Fact]
    public void Kindal_DoneWithoutTheQuestIncomplete_ThanksWithoutCredit()
    {
        using var rig = new Rig(KindalMoonweaverAI.Entry, "Kindal Moonweaver", KindalTexts);
        KalimdorWorldMapData data = rig.AddKalimdorData();
        var ai = rig.Ai<KindalMoonweaverAI>();
        ai.OnQuestAccept(rig.Player, KindalMoonweaverAI.QuestFreedomForAllCreatures);
        rig.Session.Clear();
        for (int i = 0; i < 6; i++)
        {
            data.SetData(KalimdorWorldMapData.TypeFreedomCreatures, EncounterState.Special);
        }

        Run(rig.World, 1_200);

        Assert.Empty(rig.Quest.Completed);
        Assert.Equal([$"text{KindalMoonweaverAI.SayQuestComplete}"], rig.Heard());
    }

    [Fact]
    public void Kindal_NotFollowingSheIgnoresTheCount()
    {
        using var rig = new Rig(KindalMoonweaverAI.Entry, "Kindal Moonweaver", KindalTexts);
        KalimdorWorldMapData data = rig.AddKalimdorData();
        rig.Quest.Incomplete.Add(KindalMoonweaverAI.QuestFreedomForAllCreatures);
        for (int i = 0; i < 6; i++)
        {
            data.SetData(KalimdorWorldMapData.TypeFreedomCreatures, EncounterState.Special);
        }

        Run(rig.World, 2_000);

        Assert.Empty(rig.Quest.Completed);
        Assert.Equal(EncounterState.Done, data.GetData(KalimdorWorldMapData.TypeFreedomCreatures));
    }

    [Fact]
    public void SpriteDarter_EachResetPicksOneOfTheFourFlightPaths()
    {
        var seen = new HashSet<uint>();
        using var rig = new Rig(CapturedSpriteDarterAI.Entry, "Captured Sprite Darter", scriptWaypoints: DarterPaths(0f, 0f));
        for (int i = 0; i < 40; i++)
        {
            rig.Npc.AI!.OnRespawn();
            uint path = rig.Ai<CapturedSpriteDarterAI>().FlightPath;
            Assert.InRange(path, 0u, 3u);
            seen.Add(path);
        }

        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void SpriteDarter_FlownToPointEightItCountsAsFreedAndDespawns()
    {
        using var rig = new Rig(CapturedSpriteDarterAI.Entry, "Captured Sprite Darter", scriptWaypoints: DarterPaths(0f, 0f));
        KalimdorWorldMapData data = rig.AddKalimdorData();
        var ai = rig.Ai<CapturedSpriteDarterAI>();

        Assert.True(ai.StartFlight(rig.Player));
        Assert.True(ai.IsRunning);
        Assert.Equal(8, ai.WaypointCount);
        rig.RunUntil(() => !rig.Npc.IsAlive || !rig.Npc.IsInWorld, 60_000);

        Assert.Equal(1u, data.FreedSpriteDarters);
        Assert.Equal(EncounterState.Special, data.GetData(KalimdorWorldMapData.TypeFreedomCreatures));
    }

    [Fact]
    public void SpriteDarter_InAFightItManaBurnsAManaUserAfterThreeToSixSeconds()
    {
        using var rig = new Rig(CapturedSpriteDarterAI.Entry, "Captured Sprite Darter", scriptWaypoints: DarterPaths(0f, 0f));
        var ai = rig.Ai<CapturedSpriteDarterAI>();
        uint first = ai.ManaBurnMs;
        Assert.InRange(first, CapturedSpriteDarterAI.FirstManaBurnMinMs, CapturedSpriteDarterAI.FirstManaBurnMaxMs);
        rig.Player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        rig.System.AttackStart(rig.Npc, rig.Player);
        rig.Npc.Combat.Threat.AddThreat(rig.Player, 10f);

        Run(rig.World, first + 300);

        Assert.Contains(rig.Caster.Casts, c => c.Spell == CapturedSpriteDarterAI.SpellManaBurn && ReferenceEquals(c.Target, rig.Player));
        Assert.InRange(ai.ManaBurnMs, CapturedSpriteDarterAI.ManaBurnMinMs - 300, CapturedSpriteDarterAI.ManaBurnMaxMs);
    }

    [Fact]
    public void CageDoor_UseRestartsTheCount_FreesTheDartersInRange_BringsARaiderAtThePlayer_AndKindalAttacksIt()
    {
        using var rig = new Rig(KindalMoonweaverAI.Entry, "Kindal Moonweaver", KindalTexts,
            extraTemplates: [Template(CapturedSpriteDarterAI.Entry, Tpl("Captured Sprite Darter")), Template(KalimdorWorldMapData.NpcGrimtotemRaider, Tpl("Grimtotem Raider", 14))],
            extraSpawns: [Spawn(2, CapturedSpriteDarterAI.Entry, 3f, 3f), Spawn(3, CapturedSpriteDarterAI.Entry, -3f, 3f), Spawn(4, CapturedSpriteDarterAI.Entry, 200f, 0f)],
            scriptWaypoints: DarterPaths(0f, 3f), faction: 231);
        KalimdorWorldMapData data = rig.AddKalimdorData();
        var objects = new GameObjectMapSystem(rig.Map, new GameObjectContent([GameObjectTestKit.GoTemplate(KalimdorWorldMapData.GoCageDoor, GameObjectType.Door)], [], [], [], []));
        rig.Map.AddUpdater(objects);
        GameObject door = objects.Summon(KalimdorWorldMapData.GoCageDoor, 0f, 3f, Z, 0f)!;
        data.SetData(KalimdorWorldMapData.TypeFreedomCreatures, EncounterState.Special);
        rig.Session.Clear();

        Assert.True(data.OnGameObjectUse(rig.Player, door));

        Assert.Equal(EncounterState.InProgress, data.GetData(KalimdorWorldMapData.TypeFreedomCreatures));
        Assert.Equal(0u, data.FreedSpriteDarters);
        Creature[] darters = [.. rig.System.Creatures.Where(c => c.Entry == CapturedSpriteDarterAI.Entry)];
        Creature[] freed = [.. darters.Where(d => Assert.IsType<CapturedSpriteDarterAI>(d.AI).HasEscortState(EscortAI.EscortState.Escorting))];
        Assert.Equal(2, freed.Length);
        Assert.All(freed, d => Assert.Equal(KalimdorWorldMapData.FactionEscortNeutralFriendActive, d.FactionTemplate));
        Assert.All(freed, d => Assert.Equal(TemporaryFactionFlags.RestoreRespawn, d.TemporaryFactionFlags));
        Creature far = Assert.Single(darters, d => d.X > 100f);
        Assert.False(Assert.IsType<CapturedSpriteDarterAI>(far.AI).HasEscortState(EscortAI.EscortState.Escorting));

        Creature raider = rig.One(KalimdorWorldMapData.NpcGrimtotemRaider);
        Assert.Equal(KalimdorWorldMapData.RaiderSpawn.X, raider.X, 1f);
        Assert.Contains($"text{KindalMoonweaverAI.SayInitialAggro}", rig.Heard());
        Assert.Same(raider, rig.Npc.Combat.Victim);
    }

    [Fact]
    public void CageDoor_AnotherObjectIsNotTaken()
    {
        using var rig = new Rig(KindalMoonweaverAI.Entry, "Kindal Moonweaver");
        KalimdorWorldMapData data = rig.AddKalimdorData();
        var objects = new GameObjectMapSystem(rig.Map, new GameObjectContent([GameObjectTestKit.GoTemplate(1234, GameObjectType.Door)], [], [], [], []));
        rig.Map.AddUpdater(objects);
        GameObject other = objects.Summon(1234, 0f, 3f, Z, 0f)!;

        Assert.False(data.OnGameObjectUse(rig.Player, other));
        Assert.Equal(EncounterState.NotStarted, data.GetData(KalimdorWorldMapData.TypeFreedomCreatures));
    }

    // --- Twiggy Flathead ----------------------------------------------------------------------------------------

    private static readonly int[] TwiggyTexts = [TwiggyFlatheadAI.SayBigWillReady, TwiggyFlatheadAI.SayBegin, TwiggyFlatheadAI.SayFray, TwiggyFlatheadAI.SayDown, TwiggyFlatheadAI.SayOver];

    /// <summary>Twiggy by the Affray ring with the player beside him.</summary>
    private static Rig TwiggyRig()
        => new(TwiggyFlatheadAI.Entry, "Twiggy Flathead", TwiggyTexts,
            extraTemplates: [Template(TwiggyFlatheadAI.NpcAffrayChallenger, Tpl("Affray Challenger")), Template(TwiggyFlatheadAI.NpcBigWill, Tpl("Big Will"))],
            x: -1690f, y: -4330f, z: 2.79f);

    private static bool Started(Rig rig, bool questIncomplete = true)
        => TwiggyFlatheadAI.OnAreaTrigger(rig.System, rig.Player, (_, q) => questIncomplete && q == TwiggyFlatheadAI.QuestAffray);

    private static List<Creature> Challengers(Rig rig) => [.. rig.System.Creatures.Where(c => c.Entry == TwiggyFlatheadAI.NpcAffrayChallenger && c.IsAlive)];

    [Fact]
    public void Twiggy_TheTriggerStartsTheEventOnlyWithTheQuestIncomplete_AndNotTwice()
    {
        using Rig rig = TwiggyRig();
        var ai = rig.Ai<TwiggyFlatheadAI>();

        Assert.False(Started(rig, questIncomplete: false));
        Assert.False(ai.EventInProgress);

        Assert.True(Started(rig));
        Assert.True(ai.EventInProgress);
        Assert.Equal([$"text{TwiggyFlatheadAI.SayBegin}"], rig.Heard());
        Assert.False(Started(rig));
        Assert.Single(rig.Heard());
    }

    [Fact]
    public void Twiggy_ADeadPlayerOrOneTooFarAway_DoesNotStartIt()
    {
        using Rig rig = TwiggyRig();
        rig.Player.Relocate(rig.Npc.X + 40f, rig.Npc.Y, rig.Npc.Z, 0, 0);
        Assert.False(Started(rig));

        rig.Player.Relocate(rig.Npc.X + 1f, rig.Npc.Y, rig.Npc.Z, 0, 0);
        rig.Map.Combat.Kill(null, rig.Player);
        Assert.False(Started(rig));
        Assert.False(rig.Ai<TwiggyFlatheadAI>().EventInProgress);
    }

    [Fact]
    public void Twiggy_ASecondAfterTheStartSixFriendlyChallengersStandInTheRing_AndFiveSecondsLaterTheFirstTurnsOnThePlayer()
    {
        using Rig rig = TwiggyRig();
        var ai = rig.Ai<TwiggyFlatheadAI>();
        Started(rig);
        Run(rig.World, 1_200);

        List<Creature> ring = Challengers(rig);
        Assert.Equal(TwiggyFlatheadAI.MaxChallengers, ring.Count);
        Assert.All(ring, c => Assert.Equal(TwiggyFlatheadAI.FactionFriendly, c.FactionTemplate));
        for (int i = 0; i < ring.Count; i++)
        {
            Assert.Contains(ring, c => MathF.Abs(c.X - TwiggyFlatheadAI.Spots[i].X) < 0.01f && MathF.Abs(c.Y - TwiggyFlatheadAI.Spots[i].Y) < 0.01f);
        }

        rig.Session.Clear();
        Run(rig.World, 5_000);

        Assert.Equal(1, ai.ChallengerCount);
        Creature first = Assert.Single(rig.System.Creatures, c => c.Guid == ai.Challengers[0]);
        Assert.Equal(TwiggyFlatheadAI.FactionHostileChallenger, first.FactionTemplate);
        Assert.Same(rig.Player, first.Combat.Victim);
        Assert.Contains($"text{TwiggyFlatheadAI.SayFray}", rig.Heard());
        Assert.All(ring.Where(c => c.Guid != first.Guid), c => Assert.Equal(TwiggyFlatheadAI.FactionFriendly, c.FactionTemplate));
    }

    [Fact]
    public void Twiggy_AKilledChallengerBringsTheNextFifteenSecondsLater_WithTwiggysDownLine()
    {
        using Rig rig = TwiggyRig();
        var ai = rig.Ai<TwiggyFlatheadAI>();
        Started(rig);
        Run(rig.World, 6_200);
        Assert.Equal(1, ai.ChallengerCount);
        Creature first = Assert.Single(rig.System.Creatures, c => c.Guid == ai.Challengers[0]);
        rig.Session.Clear();

        rig.Map.Combat.Kill(rig.Player, first);
        rig.World.RunTick(100);
        Assert.Contains($"text{TwiggyFlatheadAI.SayDown}", rig.Heard());

        Run(rig.World, 14_500);
        Assert.Equal(1, ai.ChallengerCount);
        Run(rig.World, 700);
        Assert.Equal(2, ai.ChallengerCount);
    }

    [Fact]
    public void Twiggy_AfterTheSixthBigWillRunsInFriendly_TurnsHostileAtHisPointWithHisLine_AndHisDeathEndsIt()
    {
        using Rig rig = TwiggyRig();
        var ai = rig.Ai<TwiggyFlatheadAI>();
        Started(rig);
        Run(rig.World, 6_200);
        for (int i = 1; i < TwiggyFlatheadAI.MaxChallengers; i++)
        {
            Creature current = Assert.Single(rig.System.Creatures, c => c.Guid == ai.Challengers[i - 1]);
            rig.Map.Combat.Kill(rig.Player, current);
            rig.RunUntil(() => ai.ChallengerCount == i + 1, 20_000);
        }

        rig.Map.Combat.Kill(rig.Player, Assert.Single(rig.System.Creatures, c => c.Guid == ai.Challengers[^1]));
        rig.RunUntil(() => !ai.BigWill.IsEmpty, 20_000);
        Creature bigWill = Assert.Single(rig.System.Creatures, c => c.Guid == ai.BigWill);
        Assert.Equal(TwiggyFlatheadAI.FactionFriendly, bigWill.FactionTemplate);
        rig.Session.Clear();

        rig.RunUntil(() => bigWill.FactionTemplate == TwiggyFlatheadAI.FactionHostileWill, 30_000);
        Assert.Contains(("Big Will", $"text{TwiggyFlatheadAI.SayBigWillReady}"), rig.Chat());

        rig.Map.Combat.Kill(rig.Player, bigWill);
        rig.World.RunTick(100);
        Assert.Contains($"text{TwiggyFlatheadAI.SayOver}", rig.Heard());
        Assert.False(ai.EventInProgress);
    }

    [Fact]
    public void Twiggy_ThePlayerDyingFailsTheEvent_TheLivingChallengersGo_AndItCanStartAgain()
    {
        using Rig rig = TwiggyRig();
        var ai = rig.Ai<TwiggyFlatheadAI>();
        Started(rig);
        Run(rig.World, 1_200);
        Assert.Equal(TwiggyFlatheadAI.MaxChallengers, Challengers(rig).Count);

        rig.Map.Combat.Kill(null, rig.Player);
        Run(rig.World, 5_500);

        Assert.False(ai.EventInProgress);
        Assert.Empty(Challengers(rig));
    }

    // --- Wizzlecrank's Shredder ---------------------------------------------------------------------------------

    private static readonly int[] ShredderTexts =
    [
        WizzlecranksShredderAI.SayStart, WizzlecranksShredderAI.SayStartup1, WizzlecranksShredderAI.SayStartup2, WizzlecranksShredderAI.SayMercenary,
        WizzlecranksShredderAI.SayProgress1, WizzlecranksShredderAI.SayProgress2, WizzlecranksShredderAI.SayProgress3, WizzlecranksShredderAI.SayEnd,
    ];

    /// <summary>A 25-point script path, 3 yd apart along x from the spawn.</summary>
    private static IEnumerable<(uint, uint, CreatureWaypoint)> ShredderPath()
    {
        for (uint point = 1; point <= 25; point++)
        {
            uint wait = point switch { 9 => 5000u, 18 => 7000u, 25 => 40_000u, _ => 0u }; // the z2815 waits that matter: the end scene runs in point 25's
            yield return (WizzlecranksShredderAI.Entry, 0u, new CreatureWaypoint(point, 3f * point, 0f, Z, 0f, wait));
        }
    }

    private static Rig ShredderRig()
        => new(WizzlecranksShredderAI.Entry, "Wizzlecrank's Shredder", ShredderTexts,
            extraTemplates: [Template(WizzlecranksShredderAI.NpcMercenary, Tpl("Southsea Mercenary", 14)), Template(WizzlecranksShredderAI.NpcPilotWizz, Tpl("Pilot Wizzlecrank"))],
            scriptWaypoints: ShredderPath());

    [Fact]
    public void Shredder_TheQuestMakesItSayItsLine_TakeTheRatchetFactionUntilRespawn_AndRunItsPath_AnotherQuestDoesNothing()
    {
        using Rig rig = ShredderRig();
        var ai = rig.Ai<WizzlecranksShredderAI>();

        ai.OnQuestAccept(rig.Player, 999_999);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Empty(rig.Heard());

        ai.OnQuestAccept(rig.Player, WizzlecranksShredderAI.QuestEscape);

        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.True(ai.IsRunning);
        Assert.Equal(WizzlecranksShredderAI.FactionRatchet, rig.Npc.FactionTemplate);
        Assert.Equal(TemporaryFactionFlags.RestoreRespawn, rig.Npc.TemporaryFactionFlags);
        Assert.Equal([$"text{WizzlecranksShredderAI.SayStart}"], rig.Heard());
    }

    [Fact]
    public void Shredder_TheWholeRun_StartUpLines_TheAmbushAt18_TheEndScene_ThenTheGroupCreditAndThePilot()
    {
        using Rig rig = ShredderRig();
        rig.Quest.Incomplete.Add(WizzlecranksShredderAI.QuestEscape);
        var ai = rig.Ai<WizzlecranksShredderAI>();
        ai.OnQuestAccept(rig.Player, WizzlecranksShredderAI.QuestEscape);
        bool sawWalk = false, sawRunAgain = false;
        for (int elapsed = 0; elapsed < 300_000 && rig.Quest.Completed.Count == 0; elapsed += 100)
        {
            rig.Player.Relocate(rig.Npc.X + 1f, rig.Npc.Y + 1f, rig.Npc.Z, 0, 0);
            foreach (Creature mercenary in rig.System.Creatures.Where(c => c.Entry == WizzlecranksShredderAI.NpcMercenary && c.IsAlive).ToList())
            {
                rig.Map.Combat.Kill(rig.Player, mercenary);
            }

            int index = ai.CurrentWaypointIndex;
            sawWalk |= index is > 10 and < 18 && !ai.IsRunning;
            sawRunAgain |= index > 19 && ai.IsRunning && sawWalk;
            rig.World.RunTick(100);
        }

        Assert.Equal([(rig.Player, WizzlecranksShredderAI.QuestEscape)], rig.Quest.Completed);
        Assert.True(sawWalk);
        Assert.True(sawRunAgain);
        int[] lines =
        [
            WizzlecranksShredderAI.SayStart, WizzlecranksShredderAI.SayStartup1, WizzlecranksShredderAI.SayStartup2,
            WizzlecranksShredderAI.SayProgress1, WizzlecranksShredderAI.SayProgress2, WizzlecranksShredderAI.SayProgress3, WizzlecranksShredderAI.SayEnd,
        ];
        Assert.Equal([.. lines.Select(id => $"text{id}")], rig.Heard()); // the mercenary calls out at its real spot, out of this player's hearing
        Assert.Equal(2, rig.System.Creatures.Count(c => c.Entry == WizzlecranksShredderAI.NpcMercenary));
        Creature pilot = rig.One(WizzlecranksShredderAI.NpcPilotWizz);
        Assert.True(pilot.IsAlive);
        Assert.Equal(StandState.Dead, rig.Npc.StandState);
    }

    [Fact]
    public void Shredder_TheMercenariesAttackTheShredder()
    {
        using Rig rig = ShredderRig();
        var ai = rig.Ai<WizzlecranksShredderAI>();
        ai.OnQuestAccept(rig.Player, WizzlecranksShredderAI.QuestEscape);
        for (int elapsed = 0; elapsed < 120_000 && !rig.System.Creatures.Any(c => c.Entry == WizzlecranksShredderAI.NpcMercenary); elapsed += 100)
        {
            rig.Player.Relocate(rig.Npc.X + 1f, rig.Npc.Y + 1f, rig.Npc.Z, 0, 0);
            rig.World.RunTick(100);
        }

        Creature[] mercenaries = [.. rig.System.Creatures.Where(c => c.Entry == WizzlecranksShredderAI.NpcMercenary)];
        Assert.Equal(2, mercenaries.Length);
        Assert.All(mercenaries, m => Assert.Same(rig.Npc, m.Combat.Victim));
        Assert.Equal(WizzlecranksShredderAI.MercenarySpots[0].X, mercenaries.Min(m => m.X), 1f);
    }
}
