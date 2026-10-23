using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
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
/// Zone script batch 8: Arei and Captured Arko'narin (felwood.cpp) and Ogron (dustwallow_marsh.cpp), against a real map with a recording caster
/// and quest service.
/// Random draws come from the map's seeded Random(1).
/// </summary>
public sealed class ZoneScriptBatch8Tests
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

    private static List<int> Ids(Rig rig) => [.. rig.Heard().Select(m => int.Parse(m.AsSpan(4), System.Globalization.CultureInfo.InvariantCulture))];

    private static Creature AddCreature(Rig rig, uint entry, float x, float y, float z)
        => rig.System.SpawnTemporary(rig.System.Content.FindTemplate(entry)!, x, y, z, 0f);

    private static void KeepPlayerNear(Rig rig) => rig.Player.Relocate(rig.Npc.X + 1f, rig.Npc.Y + 1f, rig.Npc.Z, 0, 0);

    // --- registration -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AreiAI.Entry, typeof(AreiAI))]
    [InlineData(OgronAI.Entry, typeof(OgronAI))]
    [InlineData(CapturedArkonarinAI.Entry, typeof(CapturedArkonarinAI))]
    public void TheEntryRunsItsScript(uint entry, Type ai)
    {
        using var rig = new Rig(entry, "npc");
        Assert.IsType(ai, rig.Npc.AI);
    }

    [Fact]
    public void TheEventQuestsAreListedAsScripted()
    {
        IReadOnlyCollection<uint> quests = new CreatureAiFactory().ScriptedEventQuests;
        Assert.Contains(AreiAI.QuestAncientSpirit, quests);
        Assert.Contains(OgronAI.QuestQuestioning, quests);
        Assert.Contains(CapturedArkonarinAI.QuestRescueJaedenar, quests);
    }

    // --- Arei -------------------------------------------------------------------------------------------------

    private static readonly int[] AreiTexts =
    [
        AreiAI.SayEscortStart, AreiAI.SayAttackIrontree, AreiAI.SayAttackToxicHorror, AreiAI.SayExitWoods, AreiAI.SayClearPath, AreiAI.SayAshenvale,
        AreiAI.SayTransform, AreiAI.SayLiftCurse, AreiAI.SayEscortComplete,
    ];

    /// <summary>A short stand-in for her 37-point path, ending at the real ambush spot so the Irontrees reach her.</summary>
    private static IEnumerable<(uint, uint, CreatureWaypoint)> AreiPath(float x, float y, float z)
    {
        for (uint point = 1; point <= 37; point++)
        {
            yield return (AreiAI.Entry, 0u, new CreatureWaypoint(point, x + (point * 0.5f), y, z, 0f, 0));
        }
    }

    private static Rig AreiRig()
    {
        const float x = 6555f, y = -1205f, z = 442f;
        return new Rig(AreiAI.Entry, "Arei", AreiTexts,
            extraTemplates:
            [
                Template(AreiAI.NpcIrontreeStomper, Tpl("Irontree Stomper", 14)), Template(AreiAI.NpcIrontreeWanderer, Tpl("Irontree Wanderer", 14)),
                Template(AreiAI.NpcToxicHorror, Tpl("Toxic Horror", 14)),
            ],
            scriptWaypoints: AreiPath(x, y, z), x: x, y: y, z: z);
    }

    [Fact]
    public void Arei_TheQuestMakesHerSayHerLineTakeThePassiveEscortFactionAndRun_AnotherQuestDoesNothing()
    {
        using Rig rig = AreiRig();
        var ai = rig.Ai<AreiAI>();

        ai.OnQuestAccept(rig.Player, 999_999);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));

        ai.OnQuestAccept(rig.Player, AreiAI.QuestAncientSpirit);

        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.True(ai.IsRunning);
        Assert.Equal(AreiAI.FactionEscortNeutralPassive, rig.Npc.FactionTemplate);
        Assert.Equal(TemporaryFactionFlags.RestoreRespawn, rig.Npc.TemporaryFactionFlags);
        Assert.Equal([AreiAI.SayEscortStart], Ids(rig));
    }

    private static Creature[] RunToAmbush(Rig rig, AreiAI ai)
    {
        ai.OnQuestAccept(rig.Player, AreiAI.QuestAncientSpirit);
        rig.RunUntil(() =>
        {
            KeepPlayerNear(rig);
            return rig.System.Creatures.Any(c => c.Entry == AreiAI.NpcIrontreeStomper);
        });
        return [.. rig.System.Creatures.Where(c => c.Entry is AreiAI.NpcIrontreeStomper or AreiAI.NpcIrontreeWanderer)];
    }

    [Fact]
    public void Arei_AtHerLastPointSheStops_AStomperAndTwoWanderersComeAtHer_WithHerCallToDestroyIt()
    {
        using Rig rig = AreiRig();
        var ai = rig.Ai<AreiAI>();

        Creature[] ambush = RunToAmbush(rig, ai);

        Assert.True(ai.HasEscortState(EscortAI.EscortState.Paused));
        Assert.Equal(1, ambush.Count(c => c.Entry == AreiAI.NpcIrontreeStomper));
        Assert.Equal(2, ambush.Count(c => c.Entry == AreiAI.NpcIrontreeWanderer));
        Assert.Equal(3, ai.SummonsLeft);
        Assert.All(ambush, c => Assert.Same(rig.Npc, c.Combat.Victim));
        Assert.Equal(AreiAI.Ambush[0].X, ambush.Single(c => c.Entry == AreiAI.NpcIrontreeStomper).X, 1f);
        Assert.Contains(AreiAI.SayExitWoods, Ids(rig));
        for (int i = 0; i < 50; i++)
        {
            KeepPlayerNear(rig);
            rig.World.RunTick(100);
        }

        Assert.True(rig.Npc.IsAlive); // she holds at her last point instead of ending the escort
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
    }

    [Fact]
    public void Arei_TheLastIrontreeDownStartsTheEpilogue_FourFiveThreeSevenSecondsApart_ThenTheGroupCreditAndSheGoesTenSecondsLater()
    {
        using Rig rig = AreiRig();
        rig.Quest.Incomplete.Add(AreiAI.QuestAncientSpirit);
        var ai = rig.Ai<AreiAI>();
        Creature[] ambush = RunToAmbush(rig, ai);
        rig.Map.Combat.Kill(rig.Player, ambush[0]);
        rig.Map.Combat.Kill(rig.Player, ambush[1]);
        Assert.Equal(-1, ai.EpilogIndex);
        rig.Session.Clear();

        rig.Map.Combat.Kill(rig.Player, ambush[2]);

        Assert.Equal(0, ai.EpilogIndex);
        Assert.Equal([AreiAI.SayClearPath], Ids(rig));
        var at = new Dictionary<int, int>();
        int castAt = -1;
        for (int elapsed = 100; elapsed <= 20_000 && rig.Quest.Completed.Count == 0; elapsed += 100)
        {
            KeepPlayerNear(rig);
            rig.World.RunTick(100);
            foreach (int id in Ids(rig))
            {
                at.TryAdd(id, elapsed);
            }

            if (castAt < 0 && rig.Caster.Casts.Any(c => c.Spell == AreiAI.SpellAreiTransform))
            {
                castAt = elapsed;
            }
        }

        Assert.Equal([(rig.Player, AreiAI.QuestAncientSpirit)], rig.Quest.Completed);
        Assert.Equal([AreiAI.SayClearPath, AreiAI.SayAshenvale, AreiAI.SayTransform, AreiAI.SayLiftCurse, AreiAI.SayEscortComplete], Ids(rig));
        Assert.InRange(at[AreiAI.SayAshenvale], 4000, 4100);
        Assert.InRange(at[AreiAI.SayTransform] - at[AreiAI.SayAshenvale], 5000, 5100);
        Assert.InRange(at[AreiAI.SayLiftCurse] - at[AreiAI.SayTransform], 3000, 3100);
        Assert.InRange(at[AreiAI.SayEscortComplete] - at[AreiAI.SayLiftCurse], 7000, 7100);
        Assert.Equal(at[AreiAI.SayLiftCurse], castAt);
        Assert.Contains(rig.Caster.Casts, c => c.Spell == AreiAI.SpellAreiTransform && ReferenceEquals(c.Target, rig.Npc));

        for (int i = 0; i < 95; i++)
        {
            rig.World.RunTick(100);
        }

        Assert.True(rig.Npc.IsAlive);
        rig.RunUntil(() => !rig.Npc.IsAlive || !rig.System.Creatures.Contains(rig.Npc), 1000);
    }

    [Fact]
    public void Arei_TheFirstIrontreeAndTheFirstToxicHorrorEachDrawOneLine_OnlyOnce()
    {
        using Rig rig = AreiRig();
        var ai = rig.Ai<AreiAI>();
        ai.OnQuestAccept(rig.Player, AreiAI.QuestAncientSpirit);
        rig.Session.Clear();
        Creature horror = AddCreature(rig, AreiAI.NpcToxicHorror, rig.Npc.X + 3f, rig.Npc.Y, rig.Npc.Z);
        Creature wanderer = AddCreature(rig, AreiAI.NpcIrontreeWanderer, rig.Npc.X - 3f, rig.Npc.Y, rig.Npc.Z);

        ai.OnAggro(horror);
        ai.OnAggro(horror);
        ai.OnAggro(wanderer);
        ai.OnAggro(wanderer);

        Assert.Equal([AreiAI.SayAttackToxicHorror, AreiAI.SayAttackIrontree], Ids(rig));
    }

    [Fact]
    public void Arei_InAFightSheUsesWitherStrikeAfterOneToFourSeconds()
    {
        using Rig rig = AreiRig();
        var ai = rig.Ai<AreiAI>();
        Creature horror = AddCreature(rig, AreiAI.NpcToxicHorror, rig.Npc.X + 2f, rig.Npc.Y, rig.Npc.Z);
        uint first = ai.WitherStrikeMs;
        Assert.InRange(first, 1000u, 4000u);
        horror.AI!.AttackStart(rig.Npc);
        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == AreiAI.SpellWitherStrike), 6000);

        Assert.Contains(rig.Caster.Casts, c => c.Spell == AreiAI.SpellWitherStrike && ReferenceEquals(c.Target, horror));
    }

    // --- Ogron ------------------------------------------------------------------------------------------------

    private static readonly int[] OgronTexts =
    [
        OgronAI.SayStart, OgronAI.SaySpot, OgronAI.SayRetWhat, OgronAI.SayRetSwear, OgronAI.SayReplyRet, OgronAI.SayRetTaken, OgronAI.SayTellFire,
        OgronAI.SayRetNoCloser, OgronAI.SayRetNoFire, OgronAI.SayRetHear, OgronAI.SayCalFound, OgronAI.SayCalMercy, OgronAI.SayHallGlad,
        OgronAI.EmoteRetArrow, OgronAI.SayRetArrow, OgronAI.SayCalCleanup, OgronAI.SayNoDie, OgronAI.SaySurvive, OgronAI.SayRetLucky, OgronAI.SayThanks,
    ];

    /// <summary>His z2815 script_waypoint path (23 points, waits at 10 and 12).</summary>
    private static readonly (float X, float Y, float Z, uint Wait)[] OgronPoints =
    [
        (-3322.65f, -3124.63f, 33.842f, 0), (-3326.34f, -3126.83f, 34.426f, 0), (-3336.98f, -3129.61f, 30.692f, 0), (-3342.6f, -3132.15f, 30.422f, 0),
        (-3355.83f, -3140.95f, 29.534f, 0), (-3365.83f, -3144.28f, 35.176f, 0), (-3368.9f, -3147.27f, 36.091f, 0), (-3369.36f, -3169.83f, 36.325f, 0),
        (-3371.44f, -3183.91f, 33.454f, 0), (-3373.82f, -3190.86f, 34.717f, 5000), (-3368.53f, -3198.21f, 34.926f, 0), (-3366.27f, -3210.87f, 33.733f, 5000),
        (-3368.53f, -3198.21f, 34.926f, 0), (-3373.82f, -3190.86f, 34.717f, 0), (-3371.44f, -3183.91f, 33.454f, 0), (-3369.36f, -3169.83f, 36.325f, 0),
        (-3368.9f, -3147.27f, 36.091f, 0), (-3365.83f, -3144.28f, 35.176f, 0), (-3355.83f, -3140.95f, 29.534f, 0), (-3342.6f, -3132.15f, 30.422f, 0),
        (-3336.98f, -3129.61f, 30.692f, 0), (-3326.34f, -3126.83f, 34.426f, 0), (-3322.65f, -3124.63f, 33.842f, 0),
    ];

    private static Rig OgronRig()
    {
        IEnumerable<(uint, uint, CreatureWaypoint)> path = OgronPoints.Select((p, i) => (OgronAI.Entry, 0u, new CreatureWaypoint((uint)i + 1, p.X, p.Y, p.Z, 0f, p.Wait)));
        return new Rig(OgronAI.Entry, "Ogron", OgronTexts,
            extraTemplates:
            [
                Template(OgronAI.NpcReethe, Tpl("Paval Reethe")), Template(OgronAI.NpcCaldwell, Tpl("Captain Caldwell")),
                Template(OgronAI.NpcHallan, Tpl("Private Hallan")), Template(OgronAI.NpcSkirmisher, Tpl("Theramore Skirmisher")),
            ],
            extraSpawns: [Spawn(2, OgronAI.NpcReethe, -3365.77f, -3214.06f, 33.5845f)],
            scriptWaypoints: path, x: -3317.64f, y: -3125.36f, z: 33.0284f);
    }

    [Fact]
    public void Ogron_TheQuestStartsHisWalkWithTheFriendlyEscortFactionAndHisLine_AnotherQuestDoesNothing()
    {
        using Rig rig = OgronRig();
        var ai = rig.Ai<OgronAI>();

        ai.OnQuestAccept(rig.Player, 999_999);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));

        ai.OnQuestAccept(rig.Player, OgronAI.QuestQuestioning);

        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.False(ai.IsRunning);
        Assert.Equal(OgronAI.FactionEscortNeutralFriendPassive, rig.Npc.FactionTemplate);
        Assert.Equal(TemporaryFactionFlags.RestoreRespawn | TemporaryFactionFlags.ToggleImmuneToNpc, rig.Npc.TemporaryFactionFlags);
        Assert.Equal([OgronAI.SayStart], Ids(rig));
    }

    private static Creature[] Soldiers(Rig rig)
        => [.. rig.System.Creatures.Where(c => c.Entry is OgronAI.NpcCaldwell or OgronAI.NpcHallan or OgronAI.NpcSkirmisher && c.IsAlive)];

    [Fact]
    public void Ogron_TheWholeQuestioning_TheGuestsTurnOnHim_AndAfterTheFightTheGroupCreditTheThanksAndTheRunBack()
    {
        using Rig rig = OgronRig();
        rig.Quest.Incomplete.Add(OgronAI.QuestQuestioning);
        var ai = rig.Ai<OgronAI>();
        Creature reethe = rig.One(OgronAI.NpcReethe);
        ai.OnQuestAccept(rig.Player, OgronAI.QuestQuestioning);

        rig.RunUntil(() =>
        {
            KeepPlayerNear(rig);
            return ai.CurrentPhase == OgronAI.Phase.Fight;
        }, 200_000);

        Creature[] soldiers = Soldiers(rig);
        Assert.Equal(4, soldiers.Length);
        Assert.Equal(2, soldiers.Count(c => c.Entry == OgronAI.NpcSkirmisher));
        Assert.All(soldiers, c => Assert.Equal(OgronAI.FactionTheramoreHostile, c.FactionTemplate));
        Assert.All(soldiers, c => Assert.Same(rig.Npc, c.Combat.Victim));
        Assert.Contains(rig.Caster.Casts, c => c.Spell == OgronAI.SpellFakeShot && c.Caster.Entry == OgronAI.NpcHallan && ReferenceEquals(c.Target, reethe));
        Assert.Empty(rig.Quest.Completed);

        for (int i = 0; i < 10; i++)
        {
            KeepPlayerNear(rig);
            rig.World.RunTick(100); // he fights back (and takes his combat start point) before they fall
        }

        foreach (Creature soldier in soldiers)
        {
            rig.Map.Combat.Kill(rig.Player, soldier);
        }

        rig.RunUntil(() =>
        {
            KeepPlayerNear(rig);
            return !ai.HasEscortState(EscortAI.EscortState.Paused) && ai.CurrentPhase == OgronAI.Phase.Complete;
        }, 60_000);

        Assert.Equal([(rig.Player, OgronAI.QuestQuestioning)], rig.Quest.Completed);
        Assert.True(ai.IsRunning);
        Assert.True(reethe.IsAlive);
        int[] expected =
        [
            OgronAI.SayStart, OgronAI.SaySpot, OgronAI.SayRetWhat, OgronAI.SayRetSwear, OgronAI.SayReplyRet, OgronAI.SayRetTaken, OgronAI.SayTellFire,
            OgronAI.SayRetNoCloser, OgronAI.SayRetNoFire, OgronAI.SayRetHear, OgronAI.SayCalFound, OgronAI.SayCalMercy, OgronAI.SayHallGlad,
            OgronAI.EmoteRetArrow, OgronAI.SayRetArrow, OgronAI.SayCalCleanup, OgronAI.SayNoDie, OgronAI.SaySurvive, OgronAI.SayRetLucky, OgronAI.SayThanks,
        ];
        Assert.Equal(expected, Ids(rig));
    }

    [Fact]
    public void Ogron_WithoutReethe_ThePausedStepsStillRunButOnlyHisOwnLinesAreSaid()
    {
        using Rig rig = OgronRig();
        var ai = rig.Ai<OgronAI>();
        rig.Map.Combat.Kill(rig.Player, rig.One(OgronAI.NpcReethe));
        ai.OnQuestAccept(rig.Player, OgronAI.QuestQuestioning);

        rig.RunUntil(() =>
        {
            KeepPlayerNear(rig);
            return ai.CurrentPhase == OgronAI.Phase.Guests;
        }, 200_000);

        Assert.Equal([OgronAI.SayStart, OgronAI.SaySpot, OgronAI.SayReplyRet, OgronAI.SayTellFire], Ids(rig));
    }

    // --- Captured Arko'narin ------------------------------------------------------------------------------------

    private static readonly int[] ArkonarinTexts = [CapturedArkonarinAI.SayEscortStart, .. CapturedArkonarinAI.RandomAggro];

    private static IEnumerable<(uint, uint, CreatureWaypoint)> ArkonarinPath()
    {
        for (uint point = 1; point <= 88; point++)
        {
            yield return (CreatureContent.WaypointPathEntry, CreatureContent.WaypointPathBit | CapturedArkonarinAI.PathId,
                new CreatureWaypoint(point, point * 0.4f, 0f, Z, 0f, 0));
        }
    }

    private static Rig ArkonarinRig()
        => new(CapturedArkonarinAI.Entry, "Captured Arko'narin", ArkonarinTexts,
            extraTemplates: [Template(CapturedArkonarinAI.NpcSpiritTrey, Tpl("Spirit of Trey", 14)), Template(9862, Tpl("Jaedenar Legionnaire", 14))],
            entryWaypoints: ArkonarinPath());

    [Fact]
    public void Arkonarin_SheKneels_TheQuestStandsHerUp_TakesTheActiveEscortFaction_OpensHerCage_AndWalksHerPath()
    {
        using Rig rig = ArkonarinRig();
        var ai = rig.Ai<CapturedArkonarinAI>();
        var objects = new GameObjectMapSystem(rig.Map, new GameObjectContent([GameObjectTestKit.GoTemplate(CapturedArkonarinAI.GoArkonarinCage, GameObjectType.Door)], [], [], [], []));
        rig.Map.AddUpdater(objects);
        GameObject cage = objects.Summon(CapturedArkonarinAI.GoArkonarinCage, 1f, 1f, Z, 0f)!;
        GameObjectState closed = cage.State;
        Assert.Equal(StandState.Kneel, rig.Npc.StandState);

        ai.OnQuestAccept(rig.Player, 999_999);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));

        ai.OnQuestAccept(rig.Player, CapturedArkonarinAI.QuestRescueJaedenar);

        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.False(ai.IsRunning);
        Assert.Equal(StandState.Stand, rig.Npc.StandState);
        Assert.Equal(CapturedArkonarinAI.FactionEscortNeutralActive, rig.Npc.FactionTemplate);
        Assert.Equal(TemporaryFactionFlags.RestoreRespawn | TemporaryFactionFlags.ToggleImmuneToNpc, rig.Npc.TemporaryFactionFlags);
        Assert.NotEqual(closed, cage.State);
        Assert.Equal(88, ai.WaypointCount);
    }

    [Fact]
    public void Arkonarin_Point1HerLine_FightsFromPoint37_WaitsAt83ForTrey_AndAt85TheGroupCreditAndSheLetsGoOfTheQuest()
    {
        using Rig rig = ArkonarinRig();
        rig.Quest.Incomplete.Add(CapturedArkonarinAI.QuestRescueJaedenar);
        var ai = rig.Ai<CapturedArkonarinAI>();
        ai.OnQuestAccept(rig.Player, CapturedArkonarinAI.QuestRescueJaedenar);

        rig.RunUntil(() =>
        {
            KeepPlayerNear(rig);
            return ai.CurrentWaypointIndex >= 2;
        });
        Assert.Equal([CapturedArkonarinAI.SayEscortStart], Ids(rig));
        Assert.False(ai.CanAttack);

        rig.RunUntil(() =>
        {
            KeepPlayerNear(rig);
            return ai.HasEscortState(EscortAI.EscortState.Paused);
        }, 120_000);
        Assert.True(ai.CanAttack);
        int held = ai.CurrentWaypointIndex;
        for (int i = 0; i < 50; i++)
        {
            KeepPlayerNear(rig);
            rig.World.RunTick(100);
        }

        Assert.Equal(held, ai.CurrentWaypointIndex);
        Creature trey = rig.System.SummonAt(rig.Npc, CapturedArkonarinAI.NpcSpiritTrey, rig.Npc.X + 5f, rig.Npc.Y, Z, 0f, null, 60_000)!;
        rig.Map.Combat.Kill(rig.Player, trey);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Paused));

        rig.RunUntil(() =>
        {
            KeepPlayerNear(rig);
            return rig.Quest.Completed.Count > 0;
        }, 30_000);
        Assert.Equal([(rig.Player, CapturedArkonarinAI.QuestRescueJaedenar)], rig.Quest.Completed);
        Assert.True((rig.Npc.UnitFlags & UnitFlags.ImmuneToNpc) != 0);
    }

    [Fact]
    public void Arkonarin_BeforePoint37SheOnlyMelees_AfterItMortalStrikeAndCleave()
    {
        using Rig rig = ArkonarinRig();
        var ai = rig.Ai<CapturedArkonarinAI>();
        Creature legionnaire = AddCreature(rig, 9862, rig.Npc.X + 2f, rig.Npc.Y, Z);
        legionnaire.AI!.AttackStart(rig.Npc);
        for (int i = 0; i < 100; i++)
        {
            rig.World.RunTick(100);
        }

        Assert.DoesNotContain(rig.Caster.Casts, c => c.Caster == rig.Npc);
        Assert.False(ai.CanAttack);
    }

    [Fact]
    public void Arkonarin_InAFightAfterPoint37_SheUsesMortalStrikeAndCleave()
    {
        using Rig rig = ArkonarinRig();
        var ai = rig.Ai<CapturedArkonarinAI>();
        ai.OnQuestAccept(rig.Player, CapturedArkonarinAI.QuestRescueJaedenar);
        rig.RunUntil(() =>
        {
            KeepPlayerNear(rig);
            return ai.CanAttack;
        }, 120_000);
        Creature legionnaire = AddCreature(rig, 9862, rig.Npc.X + 2f, rig.Npc.Y, Z);
        legionnaire.AI!.AttackStart(rig.Npc);
        rig.RunUntil(() =>
        {
            KeepPlayerNear(rig);
            return rig.Caster.Casts.Any(c => c.Spell == CapturedArkonarinAI.SpellMortalStrike) && rig.Caster.Casts.Any(c => c.Spell == CapturedArkonarinAI.SpellCleave);
        }, 15_000);

        Assert.All(rig.Caster.Casts.Where(c => c.Caster == rig.Npc), c => Assert.Same(legionnaire, c.Target));
    }
}
