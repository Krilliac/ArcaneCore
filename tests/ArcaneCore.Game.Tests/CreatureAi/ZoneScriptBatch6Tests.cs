using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Zone script batch 6: the Scourge Cauldron (western_plaguelands.cpp), Ringo (ungoro_crater.cpp), Regthar Deathgate (the_barrens.cpp), the
/// Aged, Dying and Ancient Kodos with the Kodo Kombobulator (desolace.cpp) and Lady Katrana Prestor (stormwind_city.cpp), against a real map
/// with a recording spell caster and quest service. The random draws come from the map's seeded random (Random(1)); the creatures the scripts
/// summon and the players they may fight have a million health.
/// </summary>
public sealed class ZoneScriptBatch6Tests
{
    private const float Z = 83.5f;
    private const uint HugeHealth = 1_000_000;

    private sealed class RecordingCaster : ICreatureSpellCaster
    {
        public List<(Creature Caster, uint Spell, Unit? Target, bool Triggered)> Casts { get; } = [];

        /// <summary>The (unit, spell) auras the scripts see.</summary>
        public HashSet<(ObjectGuid Unit, uint Spell)> Auras { get; } = [];

        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add { }
            remove { }
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Casts.Add((caster, spellId, target, triggered));
            return CreatureCastResult.Ok;
        }

        public bool IsCasting(Creature caster) => false;

        public bool HasAura(Unit unit, uint spellId) => Auras.Contains((unit.Guid, spellId));

        public void RemoveAuras(Unit unit, uint spellId) => Auras.Remove((unit.Guid, spellId));

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

        public List<(Player Player, uint Entry, ObjectGuid Source)> Credits { get; } = [];

        public bool IsQuestIncomplete(Player player, uint questId) => Incomplete.Contains(questId);

        public void AreaExploredOrEventHappens(Player player, uint questId) => Completed.Add((player, questId));

        public void FailQuest(Player player, uint questId)
        {
        }

        public List<(Player Player, uint Entry, ObjectGuid Source)> Talks { get; } = [];

        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) => Credits.Add((player, creatureEntry, source));

        public void TalkedToCreature(Player player, uint creatureEntry, ObjectGuid source) => Talks.Add((player, creatureEntry, source));

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

    /// <summary>One script creature (plus extra templates and spawns) next to a player, with its script texts, a recording caster and quest service.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(uint entry, Action<CreatureTemplateBuilder>? configure = null, int[]? textIds = null, bool hostile = false, float x = 0f, float y = 0f,
            float z = Z, CreatureTemplate[]? extraTemplates = null, CreatureSpawn[]? extraSpawns = null, uint respawnSeconds = 120,
            Func<Creature, (uint ZoneId, uint AreaId)>? zoneAndArea = null)
        {
            var aiContent = new CreatureAiContent([], [.. (textIds ?? []).Select(id => new CreatureAiText(id, $"text{id}", 6, 0, 0))]);
            CreatureContent content = new([Template(entry, configure), .. extraTemplates ?? []],
                [Spawn(1, entry, x, y, z, respawnSeconds: respawnSeconds), .. extraSpawns ?? []], [], [], [], aiContent);
            var services = new CreatureAiServices
            {
                ScriptQuests = Quest, QuestEvents = Quest, Spells = Caster, Hostility = hostile ? new AlwaysHostile() : FactionCreatureHostility.Empty,
                ZoneAndAreaOf = zoneAndArea,
            };
            (World, Map, System) = CreateAiSystem(content, services);
            (Player, Session) = AddPlayer(World, 1, x + 1f, y);
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

        public List<Creature> Alive(params uint[] entries) => [.. System.Creatures.Where(c => c.IsAlive && entries.Contains(c.Entry) && !ReferenceEquals(c, Npc))];

        public Creature One(uint entry) => Assert.Single(System.Creatures, c => c.Template.Entry == entry && !ReferenceEquals(c, Npc));

        public void RunUntil(Func<bool> done, int limitMs = 60_000)
        {
            for (int elapsed = 0; elapsed < limitMs && !done(); elapsed += 100)
            {
                World.RunTick(100);
            }

            Assert.True(done(), $"not done within {limitMs} ms");
        }

        /// <summary>Ticks until <paramref name="done"/> and returns the milliseconds that took (to the next 100 ms).</summary>
        public int TimeUntil(Func<bool> done, int limitMs = 60_000)
        {
            int elapsed = 0;
            for (; elapsed < limitMs && !done(); elapsed += 100)
            {
                World.RunTick(100);
            }

            Assert.True(done(), $"not done within {limitMs} ms");
            return elapsed;
        }

        public void Dispose() => World.Dispose();
    }

    private static Action<CreatureTemplateBuilder> Tpl(string name, uint faction = 35, uint health = HugeHealth, uint npcFlags = 0) => b =>
    {
        b.Name = name;
        b.Faction = faction;
        b.MinLevelHealth = health;
        b.MaxLevelHealth = health;
        b.NpcFlags = npcFlags;
    };

    private static NpcInfo InfoOf(Creature c, NpcFlags flags)
        => new(c.Guid, c.Entry, c.Spawn?.Guid ?? 0, flags, 0, c.X, c.Y, c.Z, c.BoundingRadius, c.IsAlive, false, c.Combat.IsInCombat, false, 0);

    private static Player LonePlayer() => TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));

    // --- registration -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ScourgeCauldronAI.Entry, typeof(ScourgeCauldronAI))]
    [InlineData(RingoAI.Entry, typeof(RingoAI))]
    [InlineData(RegtharDeathgateAI.Entry, typeof(RegtharDeathgateAI))]
    [InlineData(AgedDyingAncientKodoAI.NpcAgedKodo, typeof(AgedDyingAncientKodoAI))]
    [InlineData(AgedDyingAncientKodoAI.NpcDyingKodo, typeof(AgedDyingAncientKodoAI))]
    [InlineData(AgedDyingAncientKodoAI.NpcAncientKodo, typeof(AgedDyingAncientKodoAI))]
    [InlineData(AgedDyingAncientKodoAI.NpcTamedKodo, typeof(AgedDyingAncientKodoAI))]
    public void TheEntryRunsItsScript(uint entry, Type ai)
    {
        using var rig = new Rig(entry);
        Assert.IsType(ai, rig.Npc.AI);
    }

    [Fact]
    public void TheEventQuestsAreListedAsScripted()
    {
        IReadOnlyCollection<uint> quests = new CreatureAiFactory().ScriptedEventQuests;
        Assert.Contains(RingoAI.QuestALittleHelp, quests);
        Assert.Contains(KatranaPrestorGossip.QuestTrueMasters, quests);
    }

    [Fact]
    public void TheKombobulatorScriptIsFoundByItsSpellId()
    {
        SpellScriptRegistry registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        Assert.IsType<KodoKombobulatorScript>(registry.Find(AgedDyingAncientKodoAI.SpellKodoKomboItem));
    }

    // --- the Scourge Cauldron ---------------------------------------------------------------------------------

    private static readonly uint[] Lords = [ScourgeCauldronAI.NpcBilemaw, ScourgeCauldronAI.NpcRazarch, ScourgeCauldronAI.NpcMalvinious, ScourgeCauldronAI.NpcSoulwrath];

    private static Rig CauldronRig(uint area, uint respawnSeconds = 120)
        => new(ScourgeCauldronAI.Entry, Tpl("Scourge Cauldron"), respawnSeconds: respawnSeconds, zoneAndArea: _ => (28, area),
            extraTemplates: [.. Lords.Select(l => Template(l, Tpl($"Lord {l}", faction: 21)))]);

    [Theory]
    [InlineData(ScourgeCauldronAI.AreaFelstone, ScourgeCauldronAI.QuestFelstoneA, ScourgeCauldronAI.NpcBilemaw)]
    [InlineData(ScourgeCauldronAI.AreaFelstone, ScourgeCauldronAI.QuestFelstoneH, ScourgeCauldronAI.NpcBilemaw)]
    [InlineData(ScourgeCauldronAI.AreaDalson, ScourgeCauldronAI.QuestDalsonA, ScourgeCauldronAI.NpcMalvinious)]
    [InlineData(ScourgeCauldronAI.AreaDalson, ScourgeCauldronAI.QuestDalsonH, ScourgeCauldronAI.NpcMalvinious)]
    [InlineData(ScourgeCauldronAI.AreaGahrron, ScourgeCauldronAI.QuestGahrronA, ScourgeCauldronAI.NpcSoulwrath)]
    [InlineData(ScourgeCauldronAI.AreaGahrron, ScourgeCauldronAI.QuestGahrronH, ScourgeCauldronAI.NpcSoulwrath)]
    [InlineData(ScourgeCauldronAI.AreaWrithing, ScourgeCauldronAI.QuestWrithingA, ScourgeCauldronAI.NpcRazarch)]
    [InlineData(ScourgeCauldronAI.AreaWrithing, ScourgeCauldronAI.QuestWrithingH, ScourgeCauldronAI.NpcRazarch)]
    public void Cauldron_APlayerWithEitherQuestOfItsAreaMakesItSummonItsLordInFrontAndDie(uint area, uint quest, uint lord)
    {
        using Rig rig = CauldronRig(area);
        rig.Quest.Incomplete.Add(quest);

        rig.Ai<ScourgeCauldronAI>().MoveInLineOfSight(rig.Player);

        Assert.False(rig.Npc.IsAlive);
        Creature summoned = Assert.Single(rig.Alive(Lords));
        Assert.Equal(lord, summoned.Entry);
        float reach = rig.Npc.BoundingRadius + ScourgeCauldronAI.ContactDistance;
        Assert.Equal(rig.Npc.X + (reach * MathF.Cos(rig.Npc.Orientation)), summoned.X, 0.01f);
        Assert.Equal(rig.Npc.Y + (reach * MathF.Sin(rig.Npc.Orientation)), summoned.Y, 0.01f);
    }

    [Fact]
    public void Cauldron_TheSummonAndDeathAlsoHappenFromTheEnginesSightCheck()
    {
        using Rig rig = CauldronRig(ScourgeCauldronAI.AreaFelstone);
        rig.World.RunTick(100);
        Assert.True(rig.Npc.IsAlive); // no quest yet

        rig.Quest.Incomplete.Add(ScourgeCauldronAI.QuestFelstoneA);
        rig.RunUntil(() => !rig.Npc.IsAlive, 5_000);

        Assert.Single(rig.Alive(ScourgeCauldronAI.NpcBilemaw));
    }

    [Fact]
    public void Cauldron_ARespawnUnderTenMinutesIsRaisedToTenForTheLaterDeaths_ThisDeathKeepsItsOwn()
    {
        using Rig rig = CauldronRig(ScourgeCauldronAI.AreaDalson, respawnSeconds: 315); // the DB's spawntimesecs of the cauldrons
        rig.Quest.Incomplete.Add(ScourgeCauldronAI.QuestDalsonH);

        rig.Ai<ScourgeCauldronAI>().MoveInLineOfSight(rig.Player);

        Assert.False(rig.Npc.IsAlive);
        Assert.Equal(ScourgeCauldronAI.MinRespawnDelaySeconds, rig.Npc.RespawnDelayOverrideSeconds);
        Assert.Equal(315_000L, rig.Npc.RespawnAtMs - rig.System.ClockMs); // cmangos draws this death's delay at JUST_DIED, before DoDie's SetRespawnDelay
    }

    [Fact]
    public void Cauldron_TheTenMinuteRespawnSurvivesTheRespawn_AndIsTheNextDeathsDelay()
    {
        using Rig rig = CauldronRig(ScourgeCauldronAI.AreaFelstone, respawnSeconds: 315);
        rig.Quest.Incomplete.Add(ScourgeCauldronAI.QuestFelstoneA);
        var ai = rig.Ai<ScourgeCauldronAI>();
        ai.MoveInLineOfSight(rig.Player);
        Assert.False(rig.Npc.IsAlive);

        rig.System.ForceRespawn(rig.Npc);
        Assert.True(rig.Npc.IsAlive);
        Assert.Equal(ScourgeCauldronAI.MinRespawnDelaySeconds, rig.Npc.RespawnDelayOverrideSeconds);
        rig.Ai<ScourgeCauldronAI>().MoveInLineOfSight(rig.Player);

        Assert.False(rig.Npc.IsAlive);
        Assert.Equal(600_000L, rig.Npc.RespawnAtMs - rig.System.ClockMs);
        Assert.Equal(2, rig.System.Creatures.Count(c => c.Entry == ScourgeCauldronAI.NpcBilemaw));
    }

    [Theory]
    [InlineData(600u)]
    [InlineData(900u)]
    public void Cauldron_ARespawnOfTenMinutesOrMoreIsLeftAlone(uint respawnSeconds)
    {
        using Rig rig = CauldronRig(ScourgeCauldronAI.AreaGahrron, respawnSeconds);
        rig.Quest.Incomplete.Add(ScourgeCauldronAI.QuestGahrronA);

        rig.Ai<ScourgeCauldronAI>().MoveInLineOfSight(rig.Player);

        Assert.False(rig.Npc.IsAlive);
        Assert.Null(rig.Npc.RespawnDelayOverrideSeconds);
        Assert.Equal(respawnSeconds * 1000L, rig.Npc.RespawnAtMs - rig.System.ClockMs);
    }

    [Theory]
    [InlineData(ScourgeCauldronAI.AreaFelstone, 0u)] // no quest
    [InlineData(ScourgeCauldronAI.AreaFelstone, ScourgeCauldronAI.QuestDalsonA)] // another cauldron's quest
    [InlineData(ScourgeCauldronAI.AreaWrithing, ScourgeCauldronAI.QuestGahrronH)]
    [InlineData(0u, ScourgeCauldronAI.QuestFelstoneA)] // not in one of the four areas
    [InlineData(28u, ScourgeCauldronAI.QuestFelstoneA)] // the zone's id as an area
    public void Cauldron_WithoutAQuestOfItsOwnAreaNothingHappens(uint area, uint quest)
    {
        using Rig rig = CauldronRig(area);
        if (quest != 0)
        {
            rig.Quest.Incomplete.Add(quest);
        }

        rig.Ai<ScourgeCauldronAI>().MoveInLineOfSight(rig.Player);
        rig.World.RunTick(100);

        Assert.True(rig.Npc.IsAlive);
        Assert.Empty(rig.Alive(Lords));
    }

    [Fact]
    public void Cauldron_ACreatureInSightOrADeadCauldronDoesNothing()
    {
        using Rig rig = CauldronRig(ScourgeCauldronAI.AreaFelstone);
        rig.Quest.Incomplete.Add(ScourgeCauldronAI.QuestFelstoneA);
        var ai = rig.Ai<ScourgeCauldronAI>();
        Creature other = Assert.IsType<Creature>(rig.System.SummonAt(rig.Npc, ScourgeCauldronAI.NpcRazarch, 50f, 0f, Z, 0f, null, 600_000));

        ai.MoveInLineOfSight(other);
        Assert.True(rig.Npc.IsAlive);
        Assert.Empty(rig.Alive(ScourgeCauldronAI.NpcBilemaw));

        rig.System.KillCreature(rig.Npc);
        ai.MoveInLineOfSight(rig.Player);
        Assert.Empty(rig.Alive(ScourgeCauldronAI.NpcBilemaw));
    }

    [Fact]
    public void ScourgeCauldron_AreaEvent_KnowsOnlyTheFourAreas()
    {
        Assert.Equal((5216u, 5229u, 11075u), ScourgeCauldronAI.AreaEvent(199));
        Assert.Equal((5219u, 5231u, 11077u), ScourgeCauldronAI.AreaEvent(200));
        Assert.Equal((5225u, 5235u, 11078u), ScourgeCauldronAI.AreaEvent(201));
        Assert.Equal((5222u, 5233u, 11076u), ScourgeCauldronAI.AreaEvent(202));
        Assert.Null(ScourgeCauldronAI.AreaEvent(198));
        Assert.Null(ScourgeCauldronAI.AreaEvent(203));
    }

    // --- Ringo ------------------------------------------------------------------------------------------------

    private static readonly int[] RingoTexts =
    [
        .. RingoAI.FaintSays, .. RingoAI.WakeSays, RingoAI.SayEnd1, RingoAI.SaySpraggleEnd2, RingoAI.SayEnd3, RingoAI.EmoteEnd4, RingoAI.EmoteEnd5,
        RingoAI.SayEnd6, RingoAI.SaySpraggleEnd7, RingoAI.EmoteEnd8,
    ];

    /// <summary>Ringo next to the player; Spraggle Frock far off until a test brings him close.</summary>
    private static Rig RingoRig()
        => new(RingoAI.Entry, Tpl("Ringo"), RingoTexts, extraTemplates: [Template(RingoAI.NpcSpraggle, Tpl("Spraggle Frock"))],
            extraSpawns: [Spawn(2, RingoAI.NpcSpraggle, 300f, 0f)]);

    private static Creature Spraggle(Rig rig) => rig.One(RingoAI.NpcSpraggle);

    private static void Revive(Rig rig, uint spell = RingoAI.SpellReviveRingo)
        => rig.Ai<RingoAI>().OnSpellHit(rig.Player, new SpellInfo { Id = spell });

    /// <summary>Puts Spraggle <paramref name="distance"/> yards from Ringo (beyond the bounding radii) and lets Ringo see him.</summary>
    private static void BringSpraggle(Rig rig, float distance)
    {
        Creature spraggle = Spraggle(rig);
        float reach = distance + rig.Npc.BoundingRadius + spraggle.BoundingRadius;
        spraggle.Relocate(rig.Npc.X + reach, rig.Npc.Y, rig.Npc.Z);
        rig.Ai<RingoAI>().MoveInLineOfSight(spraggle);
    }

    [Fact]
    public void Ringo_LiesDeadUntilTheQuestIsTaken_ThenStandsAndFollowsThePlayerAsAnEscort_AnotherQuestDoesNothing()
    {
        using Rig rig = RingoRig();
        var ai = rig.Ai<RingoAI>();
        Assert.Equal(StandState.Dead, rig.Npc.StandState);

        ai.OnQuestAccept(rig.Player, 999_999);
        Assert.Equal(StandState.Dead, rig.Npc.StandState);
        Assert.False(ai.HasFollowState(FollowerAI.FollowState.InProgress));

        ai.OnQuestAccept(rig.Player, RingoAI.QuestALittleHelp);

        Assert.Equal(StandState.Stand, rig.Npc.StandState);
        Assert.True(ai.HasFollowState(FollowerAI.FollowState.InProgress));
        Assert.Equal(RingoAI.FactionEscortNeutralFriendPassive, rig.Npc.FactionTemplate);
        Assert.Equal(MovementGeneratorType.Follow, rig.Npc.Motion.CurrentType);
    }

    [Fact]
    public void Ringo_FaintsFirstAfterHisDrawnThirtyToSixtySeconds_WithAFaintLine_AndThenDrawsSixtyToOneHundredTwenty()
    {
        using Rig rig = RingoRig();
        var ai = rig.Ai<RingoAI>();
        uint drawn = ai.FaintMs;
        Assert.InRange(drawn, RingoAI.FirstFaintMinMs, RingoAI.FirstFaintMaxMs);
        ai.OnQuestAccept(rig.Player, RingoAI.QuestALittleHelp);
        rig.Session.Clear();

        int elapsed = rig.TimeUntil(() => ai.Fainted, 70_000);

        Assert.InRange(elapsed, (int)drawn - 100, (int)drawn + 200);
        Assert.Equal(StandState.Sleep, rig.Npc.StandState);
        Assert.Contains(Assert.Single(rig.Heard()), RingoAI.FaintSays.Select(id => $"text{id}"));
        Assert.InRange(ai.FaintMs, RingoAI.FaintMinMs, RingoAI.FaintMaxMs);
        Assert.NotEqual(MovementGeneratorType.Follow, rig.Npc.Motion.CurrentType);
    }

    [Fact]
    public void Ringo_DoesNotFaintBeforeTheQuest()
    {
        using Rig rig = RingoRig();
        var ai = rig.Ai<RingoAI>();

        Run(rig.World, 65_000);

        Assert.False(ai.Fainted);
        Assert.Equal(StandState.Dead, rig.Npc.StandState);
        Assert.Empty(rig.Heard());
    }

    [Fact]
    public void Ringo_TheWaterRevivesHimWithAWakeLineAndHeFollowsAgain_AnotherSpellDoesNot()
    {
        using Rig rig = RingoRig();
        var ai = rig.Ai<RingoAI>();
        ai.OnQuestAccept(rig.Player, RingoAI.QuestALittleHelp);
        rig.RunUntil(() => ai.Fainted, 70_000);
        rig.Session.Clear();

        Revive(rig, 118);
        Assert.True(ai.Fainted);
        Assert.Empty(rig.Heard());

        Revive(rig);

        Assert.False(ai.Fainted);
        Assert.Equal(StandState.Stand, rig.Npc.StandState);
        Assert.Contains(Assert.Single(rig.Heard()), RingoAI.WakeSays.Select(id => $"text{id}"));
        Assert.Equal(MovementGeneratorType.Follow, rig.Npc.Motion.CurrentType);
    }

    [Fact]
    public void Ringo_TheWaterDoesNothingBeforeTheFollow()
    {
        using Rig rig = RingoRig();

        Revive(rig);

        Assert.Equal(StandState.Dead, rig.Npc.StandState);
        Assert.Empty(rig.Heard());
    }

    [Fact]
    public void Ringo_ReachingSpraggleWithTheQuestIncompleteCompletesItForTheGroup_AndPlaysTheSceneThenGoes()
    {
        using Rig rig = RingoRig();
        rig.Quest.Incomplete.Add(RingoAI.QuestALittleHelp);
        var ai = rig.Ai<RingoAI>();
        ai.OnQuestAccept(rig.Player, RingoAI.QuestALittleHelp);
        rig.Session.Clear();

        BringSpraggle(rig, 4.5f);

        Assert.Equal([(rig.Player, RingoAI.QuestALittleHelp)], rig.Quest.Completed);
        Assert.True(ai.HasFollowState(FollowerAI.FollowState.Complete | FollowerAI.FollowState.PostEvent));
        rig.RunUntil(() => !rig.Npc.IsAlive, 60_000);

        int[] scene = [RingoAI.SayEnd1, RingoAI.SaySpraggleEnd2, RingoAI.SayEnd3, RingoAI.EmoteEnd4, RingoAI.EmoteEnd5, RingoAI.SayEnd6, RingoAI.SaySpraggleEnd7, RingoAI.EmoteEnd8];
        Assert.Equal([.. scene.Select(id => $"text{id}")], rig.Heard()); // no faint or wake line inside the scene
        List<(string Name, string Message)> chat = rig.Chat();
        Assert.Equal("Spraggle Frock", chat[1].Name);
        Assert.Equal("Spraggle Frock", chat[6].Name);
        Assert.Equal("Ringo", chat[0].Name);
        Assert.Single(rig.Quest.Completed);
    }

    [Fact]
    public void Ringo_TheSceneTakesItsTimers()
    {
        using Rig rig = RingoRig();
        var ai = rig.Ai<RingoAI>();
        ai.OnQuestAccept(rig.Player, RingoAI.QuestALittleHelp);
        rig.Session.Clear();
        BringSpraggle(rig, 1f);

        int first = rig.TimeUntil(() => rig.Heard().Count == 1);
        int second = rig.TimeUntil(() => rig.Heard().Count == 2);
        int fourth = rig.TimeUntil(() => rig.Heard().Count == 4);
        int fifth = rig.TimeUntil(() => rig.Heard().Count == 5);
        Assert.Equal(StandState.Stand, rig.Npc.StandState);

        Assert.InRange(first, 1_000, 1_300); // the first second, then step 0 that does nothing
        Assert.InRange(second, 3_000, 3_200);
        Assert.InRange(fourth, 6_000, 6_400); // 5 s, then 1 s
        Assert.InRange(fifth, 9_000, 9_200); // the faint of the scene
    }

    [Fact]
    public void Ringo_ReachingSpraggleWithoutTheQuestGivesNoCreditButStillPlaysTheScene()
    {
        using Rig rig = RingoRig();
        var ai = rig.Ai<RingoAI>();
        ai.OnQuestAccept(rig.Player, RingoAI.QuestALittleHelp);
        rig.Session.Clear();

        BringSpraggle(rig, 1f);
        rig.RunUntil(() => ai.EndEventProgress > 1, 5_000);

        Assert.Empty(rig.Quest.Completed);
        Assert.Equal($"text{RingoAI.SayEnd1}", Assert.Single(rig.Heard()));
    }

    [Fact]
    public void Ringo_SpraggleOutOfReachOrBeforeTheQuestOrAnotherCreatureDoesNothing()
    {
        using Rig rig = RingoRig();
        rig.Quest.Incomplete.Add(RingoAI.QuestALittleHelp);
        var ai = rig.Ai<RingoAI>();
        ai.OnQuestAccept(rig.Player, RingoAI.QuestALittleHelp);

        BringSpraggle(rig, 5.5f);
        Assert.False(ai.HasFollowState(FollowerAI.FollowState.Complete));
        Assert.Empty(rig.Quest.Completed);

        Creature other = Assert.IsType<Creature>(rig.System.SummonAt(rig.Npc, RingoAI.Entry, rig.Npc.X + 1f, rig.Npc.Y, rig.Npc.Z, 0f, null, 60_000));
        ai.MoveInLineOfSight(other);
        Assert.False(ai.HasFollowState(FollowerAI.FollowState.Complete));
        Assert.Empty(rig.Quest.Completed);
    }

    [Fact]
    public void Ringo_ADeadSpraggleEndsTheSceneAtOnceAndHeGoes()
    {
        using Rig rig = RingoRig();
        var ai = rig.Ai<RingoAI>();
        ai.OnQuestAccept(rig.Player, RingoAI.QuestALittleHelp);
        rig.Session.Clear();
        BringSpraggle(rig, 1f);
        rig.System.KillCreature(Spraggle(rig));

        rig.RunUntil(() => !rig.Npc.IsAlive, 5_000);

        Assert.Empty(rig.Heard());
    }

    // --- Regthar Deathgate ------------------------------------------------------------------------------------

    private static readonly uint[] Horde = [RegtharDeathgateAI.NpcHordeDefender, RegtharDeathgateAI.NpcHordeAxeThrower];
    private static readonly uint[] Kolkar = [RegtharDeathgateAI.NpcKolkarInvader, RegtharDeathgateAI.NpcKolkarStormseer];

    private static Rig RegtharRig()
        => new(RegtharDeathgateAI.Entry, Tpl("Regthar Deathgate", npcFlags: (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver)),
            [RegtharDeathgateAI.SayStart, RegtharDeathgateAI.SayDefender, RegtharDeathgateAI.YellRetreat], x: -280f, y: -1890f, z: 92f,
            extraTemplates:
            [
                .. new[] { RegtharDeathgateAI.NpcWarlordKromzar, RegtharDeathgateAI.NpcHordeDefender, RegtharDeathgateAI.NpcHordeAxeThrower,
                    RegtharDeathgateAI.NpcKolkarInvader, RegtharDeathgateAI.NpcKolkarStormseer }.Select(e => Template(e, Tpl($"Npc {e}"))),
            ]);

    private static void Start(Rig rig)
    {
        rig.Ai<RegtharDeathgateAI>().OnQuestAccept(rig.Player, RegtharDeathgateAI.QuestCounterattack);
        Assert.True(rig.Ai<RegtharDeathgateAI>().EventStarted);
    }

    private static void KillAll(Rig rig, uint[] entries, int count = int.MaxValue)
    {
        foreach (Creature c in rig.Alive(entries).Take(count))
        {
            rig.System.KillCreature(c);
        }
    }

    [Fact]
    public void Regthar_TakingTheQuestSaysHisLineAndSummonsSevenHordeAndFifteenKolkar_AnotherQuestDoesNothing()
    {
        using Rig rig = RegtharRig();
        var ai = rig.Ai<RegtharDeathgateAI>();
        rig.Session.Clear();

        ai.OnQuestAccept(rig.Player, 999_999);
        Assert.False(ai.EventStarted);
        Assert.Empty(rig.Alive([.. Horde, .. Kolkar]));
        Assert.Empty(rig.Heard());

        ai.OnQuestAccept(rig.Player, RegtharDeathgateAI.QuestCounterattack);

        Assert.True(ai.EventStarted);
        Assert.Equal([$"text{RegtharDeathgateAI.SayStart}"], rig.Heard());
        List<Creature> horde = rig.Alive(Horde);
        List<Creature> kolkar = rig.Alive(Kolkar);
        Assert.Equal(RegtharDeathgateAI.HordeSpots.Length, horde.Count);
        Assert.Equal(RegtharDeathgateAI.KolkarSpots.Length, kolkar.Count);
        Assert.All(RegtharDeathgateAI.HordeSpots, s => Assert.Contains(horde, c => MathF.Abs(c.X - s.X) < 0.01f && MathF.Abs(c.Y - s.Y) < 0.01f));
        Assert.All(RegtharDeathgateAI.KolkarSpots, s => Assert.Contains(kolkar, c => MathF.Abs(c.X - s.X) < 0.01f && MathF.Abs(c.Y - s.Y) < 0.01f));
        Assert.Empty(rig.Alive(RegtharDeathgateAI.NpcWarlordKromzar));

        ai.OnQuestAccept(rig.Player, RegtharDeathgateAI.QuestCounterattack); // a second accept during the battle summons no more
        Assert.Equal(22, rig.Alive([.. Horde, .. Kolkar]).Count);
    }

    [Fact]
    public void Regthar_ADeadDefenderComesBackOnItsSpotTwelveSecondsLater()
    {
        using Rig rig = RegtharRig();
        Start(rig);
        Creature victim = rig.Alive(Horde)[0];
        (float x, float y) = (victim.X, victim.Y);
        rig.System.KillCreature(victim);
        Assert.Equal(6, rig.Alive(Horde).Count);

        int elapsed = rig.TimeUntil(() => rig.Alive(Horde).Count == 7, 20_000);

        Assert.InRange(elapsed, 11_900, 12_400);
        Assert.Contains(rig.Alive(Horde), c => MathF.Abs(c.X - x) < 0.01f && MathF.Abs(c.Y - y) < 0.01f);
    }

    [Fact]
    public void Regthar_TheFifthDefenderWaitingTakesOneSecond_BehindTheFourBeforeIt()
    {
        using Rig rig = RegtharRig();
        Start(rig);
        KillAll(rig, Horde, 5);
        Assert.Equal(2, rig.Alive(Horde).Count);

        Run(rig.World, 11_800);
        Assert.Equal(2, rig.Alive(Horde).Count);

        int elapsed = rig.TimeUntil(() => rig.Alive(Horde).Count == 7, 5_000);
        Assert.InRange(elapsed, 100, 1_000); // one per update once the first four's twelve seconds are past
    }

    [Fact]
    public void Regthar_ADeadKolkarComesBackTenSecondsLaterAndCounts()
    {
        using Rig rig = RegtharRig();
        Start(rig);
        var ai = rig.Ai<RegtharDeathgateAI>();
        rig.System.KillCreature(rig.Alive(Kolkar)[0]);
        Assert.Equal(1u, ai.KolkarKills);

        int elapsed = rig.TimeUntil(() => rig.Alive(Kolkar).Count == 15, 20_000);

        Assert.InRange(elapsed, 9_900, 10_400);
    }

    [Fact]
    public void Regthar_TheTwentiethKolkarBringsKromzarAndHisTwoGuards_NotTheNineteenth_AndNothingRespawnsAfter()
    {
        using Rig rig = RegtharRig();
        Start(rig);
        var ai = rig.Ai<RegtharDeathgateAI>();
        KillAll(rig, Kolkar);
        rig.RunUntil(() => rig.Alive(Kolkar).Count == 15, 20_000);
        KillAll(rig, Kolkar, 4);
        Assert.Equal(19u, ai.KolkarKills);
        Run(rig.World, 1_000);
        Assert.False(ai.KromzarSpawned);
        Assert.Empty(rig.Alive(RegtharDeathgateAI.NpcWarlordKromzar));

        KillAll(rig, Kolkar, 1);
        rig.World.RunTick(100);

        Assert.True(ai.KromzarSpawned);
        Creature kromzar = Assert.Single(rig.Alive(RegtharDeathgateAI.NpcWarlordKromzar));
        Assert.Equal(RegtharDeathgateAI.KromzarSpots[1].X, kromzar.X, 0.01f);
        Assert.Equal(12, rig.Alive(Kolkar).Count); // the ten slots still alive and the two guards
        KillAll(rig, Horde, 1);
        Run(rig.World, 15_000);
        Assert.Equal(6, rig.Alive(Horde).Count); // no defender comes back once Krom'zar is out
        Assert.Equal(12, rig.Alive(Kolkar).Count); // nor a Kolkar
    }

    [Fact]
    public void Regthar_KromzarsDeathEndsTheBattle_TheYellAndEveryOtherSummonGoes()
    {
        using Rig rig = RegtharRig();
        Start(rig);
        var ai = rig.Ai<RegtharDeathgateAI>();
        KillAll(rig, Kolkar);
        rig.RunUntil(() => rig.Alive(Kolkar).Count == 15, 20_000);
        KillAll(rig, Kolkar, 5);
        rig.RunUntil(() => ai.KromzarSpawned, 1_000);
        Creature[] guards = [.. rig.Alive(Kolkar).Where(c => RegtharDeathgateAI.KromzarSpots.Any(s => MathF.Abs(c.X - s.X) < 0.01f && MathF.Abs(c.Y - s.Y) < 0.01f))];
        Assert.Equal(2, guards.Length);
        rig.Session.Clear();

        rig.System.KillCreature(rig.One(RegtharDeathgateAI.NpcWarlordKromzar));
        rig.World.RunTick(100);

        Assert.False(ai.EventStarted);
        Assert.Contains($"text{RegtharDeathgateAI.YellRetreat}", rig.Heard());
        Assert.Empty(rig.Alive(Horde));
        Assert.Equal(guards.Select(g => g.Guid).OrderBy(g => g.Value), rig.Alive(Kolkar).Select(c => c.Guid).OrderBy(g => g.Value)); // the guards are no slot
    }

    [Fact]
    public void Regthar_ASecondBattleBringsKromzarAgain()
    {
        using Rig rig = RegtharRig();
        var ai = rig.Ai<RegtharDeathgateAI>();
        for (int battle = 1; battle <= 2; battle++)
        {
            KillAll(rig, Kolkar); // the guards of the battle before: they count nothing, the start clears the count
            Start(rig);
            Assert.Equal(0u, ai.KolkarKills);
            Assert.False(ai.KromzarSpawned);
            KillAll(rig, Kolkar);
            rig.RunUntil(() => rig.Alive(Kolkar).Count == 15, 20_000);
            KillAll(rig, Kolkar, 5);
            rig.RunUntil(() => rig.Alive(RegtharDeathgateAI.NpcWarlordKromzar).Count == 1, 1_000);
            rig.System.KillCreature(rig.Alive(RegtharDeathgateAI.NpcWarlordKromzar)[0]);
            rig.World.RunTick(100);
            Assert.False(ai.EventStarted, $"battle {battle}");
        }
    }

    [Fact]
    public void RegtharGossip_OffersTheQuestionOnlyWithTheQuestIncomplete_AndShowsQuestsAsAQuestGiver()
    {
        var incomplete = new HashSet<uint>();
        var gossip = new RegtharDeathgateGossip((_, q) => incomplete.Contains(q));
        Player player = LonePlayer();
        var npc = new NpcInfo(ObjectGuid.WithEntry(HighGuid.Unit, RegtharDeathgateAI.Entry, 1), RegtharDeathgateAI.Entry, 1,
            NpcFlags.Gossip | NpcFlags.QuestGiver, 1, 0, 0, 0, 0.5f, true, false, false, false, 0);

        ScriptedGossipMenu without = Assert.IsType<ScriptedGossipMenu>(gossip.Hello(player, npc));
        Assert.Equal(RegtharDeathgateGossip.TextMenu, without.NpcTextId);
        Assert.True(without.ShowQuests);
        Assert.Empty(without.Items);

        incomplete.Add(RegtharDeathgateAI.QuestCounterattack);
        ScriptedGossipMenu with = Assert.IsType<ScriptedGossipMenu>(gossip.Hello(player, npc with { NpcFlags = NpcFlags.Gossip }));
        Assert.False(with.ShowQuests);
        ScriptedGossipItem item = Assert.Single(with.Items);
        Assert.Equal(RegtharDeathgateGossip.OptionWhereIsKromzar, item.Text);
        Assert.Equal(RegtharDeathgateGossip.ActionWhereIsKromzar, item.Action);
        Assert.Equal(RegtharDeathgateGossip.SenderMain, item.Sender);
    }

    [Fact]
    public void RegtharGossip_TheQuestionStartsTheBattleAndClosesTheMenu_AnotherActionDoesNothing()
    {
        using Rig rig = RegtharRig();
        var gossip = new RegtharDeathgateGossip((_, _) => true);
        NpcInfo npc = InfoOf(rig.Npc, NpcFlags.Gossip | NpcFlags.QuestGiver);
        rig.Session.Clear();

        Assert.Equal(default, gossip.SelectReply(rig.Player, npc, RegtharDeathgateGossip.SenderMain, 1000));
        Assert.False(rig.Ai<RegtharDeathgateAI>().EventStarted);

        ScriptedGossipReply reply = gossip.SelectReply(rig.Player, npc, RegtharDeathgateGossip.SenderMain, RegtharDeathgateGossip.ActionWhereIsKromzar);

        Assert.True(reply.Close);
        Assert.True(rig.Ai<RegtharDeathgateAI>().EventStarted);
        Assert.Equal([$"text{RegtharDeathgateAI.SayStart}"], rig.Heard());
        Assert.Equal(22, rig.Alive([.. Horde, .. Kolkar]).Count);
    }

    // --- the kodos (recording caster) -------------------------------------------------------------------------

    private static Rig KodoRig(uint entry = AgedDyingAncientKodoAI.NpcAgedKodo, bool hostile = false)
        => new(entry, entry == AgedDyingAncientKodoAI.NpcTamedKodo ? TamedKodo : Tpl("Aged Kodo"), [.. AgedDyingAncientKodoAI.SmeedSays], hostile: hostile,
            extraTemplates:
            [
                .. entry == AgedDyingAncientKodoAI.NpcTamedKodo ? Array.Empty<CreatureTemplate>() : [Template(AgedDyingAncientKodoAI.NpcTamedKodo, TamedKodo)],
                Template(AgedDyingAncientKodoAI.NpcSmeed, Tpl("Smeed Scrabblescrew")),
            ],
            extraSpawns: [Spawn(2, AgedDyingAncientKodoAI.NpcSmeed, 300f, 0f)]);

    /// <summary>creature_template 11627 has npcflag 1 (gossip) in ClassicDB z2815.</summary>
    private static readonly Action<CreatureTemplateBuilder> TamedKodo = Tpl("Tamed Kodo", npcFlags: (uint)NpcFlags.Gossip);

    private static Creature Smeed(Rig rig) => rig.One(AgedDyingAncientKodoAI.NpcSmeed);

    private static void SmeedAt(Rig rig, float distance)
    {
        Creature smeed = Smeed(rig);
        smeed.Relocate(rig.Npc.X + distance + rig.Npc.BoundingRadius + smeed.BoundingRadius, rig.Npc.Y, rig.Npc.Z);
        rig.Ai<AgedDyingAncientKodoAI>().MoveInLineOfSight(smeed);
    }

    private static void GossipHit(Rig rig)
        => rig.Ai<AgedDyingAncientKodoAI>().OnSpellHit(rig.Npc, new SpellInfo { Id = AgedDyingAncientKodoAI.SpellKodoKomboGossip });

    [Fact]
    public void Kodo_SmeedWithinTenYardsSaysALineAndTheKodoCastsKodoKomboGossipOnItself()
    {
        using Rig rig = KodoRig();
        rig.Session.Clear();

        SmeedAt(rig, 9.5f);

        (Creature caster, uint spell, Unit? target, bool triggered) = Assert.Single(rig.Caster.Casts);
        Assert.Same(rig.Npc, caster);
        Assert.Equal(AgedDyingAncientKodoAI.SpellKodoKomboGossip, spell);
        Assert.Same(rig.Npc, target);
        Assert.True(triggered);
        (string name, string message) = Assert.Single(rig.Chat());
        Assert.Equal("Smeed Scrabblescrew", name);
        Assert.Contains(message, AgedDyingAncientKodoAI.SmeedSays.Select(id => $"text{id}"));
    }

    [Fact]
    public void Kodo_SmeedBeyondTenYards_OrAKodoWithTheGossipFlag_OrAnotherUnitDoesNothing()
    {
        using Rig rig = KodoRig();
        rig.Session.Clear();

        SmeedAt(rig, 10.5f);
        rig.Ai<AgedDyingAncientKodoAI>().MoveInLineOfSight(rig.Player);
        Assert.Empty(rig.Caster.Casts);

        rig.Npc.NpcFlags |= (uint)NpcFlags.Gossip;
        SmeedAt(rig, 1f);
        Assert.Empty(rig.Caster.Casts);
        Assert.Empty(rig.Heard());
    }

    [Fact]
    public void Kodo_TheTamedKodoTemplateAlreadyHasTheGossipFlag_SoSmeedNeverSpeaksToIt()
    {
        using Rig rig = KodoRig(AgedDyingAncientKodoAI.NpcTamedKodo);

        SmeedAt(rig, 1f);

        Assert.Empty(rig.Caster.Casts);
    }

    [Fact]
    public void Kodo_KodoKomboGossipStopsItAndGivesTheGossipFlag_AnotherSpellDoesNot()
    {
        using Rig rig = KodoRig();
        var ai = rig.Ai<AgedDyingAncientKodoAI>();

        ai.OnSpellHit(rig.Npc, new SpellInfo { Id = AgedDyingAncientKodoAI.SpellKodoKomboDespawnBuff });
        Assert.Equal(0u, rig.Npc.NpcFlags & (uint)NpcFlags.Gossip);
        Assert.Equal(0u, ai.DespawnMs);

        GossipHit(rig);

        Assert.NotEqual(0u, rig.Npc.NpcFlags & (uint)NpcFlags.Gossip);
        Assert.Equal(AgedDyingAncientKodoAI.RespawnAfterGossipMs, ai.DespawnMs);
        Assert.Equal(MovementGeneratorType.Idle, rig.Npc.Motion.CurrentType);
    }

    [Fact]
    public void Kodo_AMinuteAfterTheGossipItDiesAndRespawnsAsTheWildKodo()
    {
        using Rig rig = KodoRig();
        Assert.True(rig.System.UpdateEntry(rig.Npc, AgedDyingAncientKodoAI.NpcTamedKodo));
        GossipHit(rig);

        Run(rig.World, 59_500);
        Assert.Equal(AgedDyingAncientKodoAI.NpcTamedKodo, rig.Npc.Entry);
        Assert.True(rig.Npc.IsAlive);

        Run(rig.World, 700);

        Assert.True(rig.Npc.IsAlive);
        Assert.Equal(AgedDyingAncientKodoAI.NpcAgedKodo, rig.Npc.Entry);
        Assert.Equal(0u, rig.Npc.NpcFlags & (uint)NpcFlags.Gossip);
        Assert.Equal(0u, rig.Ai<AgedDyingAncientKodoAI>().DespawnMs);
    }

    [Fact]
    public void Kodo_WithoutTheGossipNoTimerRuns()
    {
        using Rig rig = KodoRig();
        Assert.True(rig.System.UpdateEntry(rig.Npc, AgedDyingAncientKodoAI.NpcTamedKodo));

        Run(rig.World, 70_000);

        Assert.Equal(AgedDyingAncientKodoAI.NpcTamedKodo, rig.Npc.Entry);
    }

    [Fact]
    public void Kodo_AFightingKodoIsHeldUntilTheFightEnds()
    {
        using Rig rig = KodoRig(hostile: true);
        GossipHit(rig);
        rig.System.AttackStart(rig.Npc, rig.Player);
        rig.World.RunTick(100);
        Assert.True(rig.Npc.Combat.IsInCombat);

        Run(rig.World, 61_000);

        Assert.True(rig.Npc.Combat.IsInCombat);
        Assert.True(rig.Npc.IsAlive);
        Assert.True(rig.Player.IsAlive, "the victim died");
        Assert.NotEqual(0u, rig.Ai<AgedDyingAncientKodoAI>().DespawnMs); // run out, held for the fight
        Assert.NotEqual(0u, rig.Npc.NpcFlags & (uint)NpcFlags.Gossip); // not respawned
    }

    [Fact]
    public void KodoGossip_WithBothBuffsGivesTheCreditTakesThePlayersBuffAndTheKodoGoesTenSecondsLater()
    {
        using Rig rig = KodoRig(AgedDyingAncientKodoAI.NpcTamedKodo);
        rig.Caster.Auras.Add((rig.Player.Guid, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff));
        rig.Caster.Auras.Add((rig.Npc.Guid, AgedDyingAncientKodoAI.SpellKodoKomboDespawnBuff));

        Assert.Null(new AgedDyingAncientKodoGossip().Hello(rig.Player, InfoOf(rig.Npc, NpcFlags.Gossip)));

        Assert.Equal([(rig.Player, AgedDyingAncientKodoAI.NpcTamedKodo, rig.Npc.Guid)], rig.Quest.Talks); // Player::TalkedToCreature
        Assert.Empty(rig.Quest.Credits);
        Assert.DoesNotContain((rig.Player.Guid, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff), rig.Caster.Auras);
        Run(rig.World, 9_500);
        Assert.True(rig.Npc.IsAlive);
        Run(rig.World, 1_000);
        Assert.False(rig.Npc.IsAlive);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void KodoGossip_WithoutBothBuffsGivesNothing(bool playerBuff, bool kodoBuff)
    {
        using Rig rig = KodoRig(AgedDyingAncientKodoAI.NpcTamedKodo);
        if (playerBuff)
        {
            rig.Caster.Auras.Add((rig.Player.Guid, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff));
        }

        if (kodoBuff)
        {
            rig.Caster.Auras.Add((rig.Npc.Guid, AgedDyingAncientKodoAI.SpellKodoKomboDespawnBuff));
        }

        Assert.Null(new AgedDyingAncientKodoGossip().Hello(rig.Player, InfoOf(rig.Npc, NpcFlags.Gossip)));

        Assert.Empty(rig.Quest.Talks);
        Assert.Empty(rig.Quest.Credits);
        Assert.Equal(playerBuff, rig.Caster.Auras.Contains((rig.Player.Guid, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff)));
        Run(rig.World, 11_000);
        Assert.True(rig.Npc.IsAlive);
    }

    // --- the Kodo Kombobulator (real spell system) ------------------------------------------------------------

    /// <summary>
    /// The three kodo spells with the effects of build 5875's Spell.dbc that the scripts use: all three as the DBC has them instant to the
    /// script (18172 and 18377 CastingTimeIndex 1 = 0 ms, no reagents, no power cost, no start-recovery category; 18153's 5 s cast time is the
    /// item use's and is not under test, its cast is triggered here), 18172 and 18377 lasting DurationIndex 5 = 300 s. Simplified for the test: 18153 and 18172 target any
    /// unit (the DBC's TARGET_CHAIN_DAMAGE at a neutral kodo and TARGET_UNIT_FRIEND from a still-neutral kodo, so the engine's own target checks
    /// cannot answer for the script's), and 18377's aura is a dummy (the DBC's periodic trigger of 18269); the scripts read neither.
    /// </summary>
    private static SpellInfo[] KodoSpells()
    {
        static SpellInfo Instant(SpellInfo s) => s with
        {
            StartRecoveryCategory = 0, StartRecoveryTime = 0, RangeIndex = 4, Range = new SpellRange(0, 30), Duration = new SpellDuration(300_000, 0, 300_000),
        };

        return
        [
            Instant(SpellTestKit.Spell(AgedDyingAncientKodoAI.SpellKodoKomboItem, SpellTestKit.Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.Unit))),
            Instant(SpellTestKit.Spell(AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff,
                SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.Unit, AuraType.Dummy))),
            Instant(SpellTestKit.Spell(AgedDyingAncientKodoAI.SpellKodoKomboDespawnBuff,
                SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy))),
        ];
    }

    /// <summary>
    /// With spell batching on (<c>ARCANECORE_TEST_SPELL_BATCH_MS</c>) a cast that goes off is held until the next window boundary: advance
    /// the clock to it so the held go resolves. A no-op with the window at 0.
    /// </summary>
    private static void FlushSpellBatch(SpellTestKit kit)
    {
        uint window = kit.System.SpellBatchWindowMs;
        if (window != 0)
        {
            kit.Advance(window - (kit.Now % window));
        }
    }

    private sealed class KodoSpellRig : IDisposable
    {
        public KodoSpellRig(uint kodoEntry = AgedDyingAncientKodoAI.NpcAgedKodo)
        {
            Kit = new SpellTestKit(KodoSpells());
            Kit.System.Units = new MapObjectResolver();
            SpellScriptDispatcher.Install(Kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
            CreatureContent content = new(
                kodoEntry == AgedDyingAncientKodoAI.NpcTamedKodo
                    ? [Template(kodoEntry, TamedKodo)]
                    : [Template(kodoEntry, Tpl("Kodo")), Template(AgedDyingAncientKodoAI.NpcTamedKodo, TamedKodo)],
                [Spawn(1, kodoEntry, 10f, 0f)], [], [], [], new CreatureAiContent([], []));
            (_, _, Creatures) = CreateAiSystem(content, new CreatureAiServices { Spells = new SpellSystemCreatureCaster(Kit.System), ScriptQuests = Quest },
                world: Kit.World);
            (Player, _) = Kit.AddPlayer(1, 0, 0);
            Kodo = Assert.Single(Creatures.Creatures);
        }

        public SpellTestKit Kit { get; }

        public Quests Quest { get; } = new();

        public CreatureMapSystem Creatures { get; }

        public Player Player { get; }

        public Creature Kodo { get; }

        public SpellCastResult Kombobulate(Unit? target = null)
        {
            SpellCastResult result = Kit.System.CastSpell(Player, AgedDyingAncientKodoAI.SpellKodoKomboItem, SpellCastTargets.ForUnit((target ?? Kodo).Guid), triggered: true);
            FlushSpellBatch(Kit); // the kodo's own casts of the buffs go through the batch
            return result;
        }

        public void Dispose() => Kit.Dispose();
    }

    [Theory]
    [InlineData(AgedDyingAncientKodoAI.NpcAgedKodo)]
    [InlineData(AgedDyingAncientKodoAI.NpcDyingKodo)]
    [InlineData(AgedDyingAncientKodoAI.NpcAncientKodo)]
    public void Kombobulator_TamesAWildKodo_TheBuffs_TheTamedEntry_AndItFollowsTheCaster(uint entry)
    {
        using var rig = new KodoSpellRig(entry);

        Assert.Equal(SpellCastResult.CastOk, rig.Kombobulate());

        Assert.Equal(AgedDyingAncientKodoAI.NpcTamedKodo, rig.Kodo.Entry);
        Assert.True(rig.Kit.System.HasAura(rig.Player, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff));
        Assert.True(rig.Kit.System.HasAura(rig.Kodo, AgedDyingAncientKodoAI.SpellKodoKomboDespawnBuff));
        Assert.Equal(MovementGeneratorType.Follow, rig.Kodo.Motion.CurrentType);
        Assert.IsType<AgedDyingAncientKodoAI>(rig.Kodo.AI);
    }

    [Fact]
    public void Kombobulator_RefusesATamedKodoOrAnotherCreatureOrAPlayer()
    {
        using var tamed = new KodoSpellRig(AgedDyingAncientKodoAI.NpcTamedKodo);
        Assert.Equal(SpellCastResult.BadTargets, tamed.Kombobulate());
        Assert.False(tamed.Kit.System.HasAura(tamed.Player, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff));

        (Player other, _) = tamed.Kit.AddPlayer(2, 5, 0);
        Assert.Equal(SpellCastResult.BadTargets, tamed.Kombobulate(other));
    }

    [Fact]
    public void Kombobulator_RefusesACasterWhoAlreadyLeadsAKodo()
    {
        using var rig = new KodoSpellRig();
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Player, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff,
            SpellCastTargets.ForUnit(rig.Player.Guid), triggered: true));

        Assert.Equal(SpellCastResult.CasterAurastate, rig.Kombobulate());

        Assert.Equal(AgedDyingAncientKodoAI.NpcAgedKodo, rig.Kodo.Entry);
    }

    [Fact]
    public void Kombobulator_RefusesAKodoWithTheDespawnBuff()
    {
        using var rig = new KodoSpellRig();
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Kodo, AgedDyingAncientKodoAI.SpellKodoKomboDespawnBuff,
            SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(SpellCastResult.CasterAurastate, rig.Kombobulate());

        Assert.Equal(AgedDyingAncientKodoAI.NpcAgedKodo, rig.Kodo.Entry);
        Assert.False(rig.Kit.System.HasAura(rig.Player, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff));
    }

    [Fact]
    public void Kombobulator_ThenTheGossipGivesTheCreditOnce()
    {
        using var rig = new KodoSpellRig();
        Assert.Equal(SpellCastResult.CastOk, rig.Kombobulate());
        var gossip = new AgedDyingAncientKodoGossip();

        gossip.Hello(rig.Player, InfoOf(rig.Kodo, NpcFlags.Gossip));
        gossip.Hello(rig.Player, InfoOf(rig.Kodo, NpcFlags.Gossip)); // the player's buff is gone

        Assert.Equal([(rig.Player, AgedDyingAncientKodoAI.NpcTamedKodo, rig.Kodo.Guid)], rig.Quest.Talks);
        Assert.False(rig.Kit.System.HasAura(rig.Player, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff));
    }

    // --- Lady Katrana Prestor ---------------------------------------------------------------------------------

    [Fact]
    public void Katrana_OffersTheFirstLineOnlyWithTheTrueMastersIncomplete_AndHerQuestsAsAQuestGiver()
    {
        var incomplete = new HashSet<uint>();
        var gossip = new KatranaPrestorGossip((_, q) => incomplete.Contains(q), (_, _) => Assert.Fail("no event"));
        Player player = LonePlayer();
        var npc = new NpcInfo(ObjectGuid.WithEntry(HighGuid.Unit, KatranaPrestorGossip.Entry, 1), KatranaPrestorGossip.Entry, 1,
            NpcFlags.Gossip | NpcFlags.QuestGiver, 0, 0, 0, 0, 0.5f, true, false, false, false, 0);

        ScriptedGossipMenu without = Assert.IsType<ScriptedGossipMenu>(gossip.Hello(player, npc));
        Assert.Equal(KatranaPrestorGossip.TextMenu1, without.NpcTextId);
        Assert.True(without.ShowQuests);
        Assert.Empty(without.Items);

        incomplete.Add(1234);
        Assert.Empty(gossip.Hello(player, npc)!.Items);

        incomplete.Add(KatranaPrestorGossip.QuestTrueMasters);
        ScriptedGossipMenu with = Assert.IsType<ScriptedGossipMenu>(gossip.Hello(player, npc with { NpcFlags = NpcFlags.Gossip }));
        Assert.False(with.ShowQuests);
        Assert.Equal(new ScriptedGossipItem(0, KatranaPrestorGossip.OptionKat1, KatranaPrestorGossip.SenderMain, KatranaPrestorGossip.Action1),
            Assert.Single(with.Items));
    }

    [Fact]
    public void Katrana_TheFourLinesLeadThroughHerThreeMenus_AndTheLastCompletesTheEventAndCloses()
    {
        var events = new List<(Player Player, uint Quest)>();
        var gossip = new KatranaPrestorGossip((_, _) => true, (p, q) => events.Add((p, q)));
        Player player = LonePlayer();
        var npc = new NpcInfo(ObjectGuid.WithEntry(HighGuid.Unit, KatranaPrestorGossip.Entry, 1), KatranaPrestorGossip.Entry, 1,
            NpcFlags.Gossip, 0, 0, 0, 0, 0.5f, true, false, false, false, 0);

        (uint Action, uint Text, string Option, uint Next)[] steps =
        [
            (KatranaPrestorGossip.Action1, KatranaPrestorGossip.TextMenu2, KatranaPrestorGossip.OptionKat2, KatranaPrestorGossip.Action2),
            (KatranaPrestorGossip.Action2, KatranaPrestorGossip.TextMenu3, KatranaPrestorGossip.OptionKat3, KatranaPrestorGossip.Action3),
            (KatranaPrestorGossip.Action3, KatranaPrestorGossip.TextMenu4, KatranaPrestorGossip.OptionKat4, KatranaPrestorGossip.Action4),
        ];
        foreach ((uint action, uint text, string option, uint next) in steps)
        {
            ScriptedGossipReply reply = gossip.SelectReply(player, npc, KatranaPrestorGossip.SenderMain, action);
            Assert.False(reply.Close);
            Assert.Equal(text, reply.NpcTextId);
            ScriptedGossipMenu menu = Assert.IsType<ScriptedGossipMenu>(reply.NextMenu);
            Assert.Equal(text, menu.NpcTextId);
            Assert.False(menu.ShowQuests);
            Assert.Equal(new ScriptedGossipItem(0, option, KatranaPrestorGossip.SenderMain, next), Assert.Single(menu.Items));
            Assert.Empty(events);
        }

        ScriptedGossipReply last = gossip.SelectReply(player, npc, KatranaPrestorGossip.SenderMain, KatranaPrestorGossip.Action4);

        Assert.True(last.Close);
        Assert.Null(last.NextMenu);
        Assert.Equal([(player, KatranaPrestorGossip.QuestTrueMasters)], events);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(999u)]
    [InlineData(1004u)]
    public void Katrana_AnotherActionDoesNothing(uint action)
    {
        var gossip = new KatranaPrestorGossip((_, _) => true, (_, _) => Assert.Fail("no event"));
        var npc = new NpcInfo(ObjectGuid.WithEntry(HighGuid.Unit, KatranaPrestorGossip.Entry, 1), KatranaPrestorGossip.Entry, 1,
            NpcFlags.Gossip, 0, 0, 0, 0, 0.5f, true, false, false, false, 0);

        ScriptedGossipReply reply = gossip.SelectReply(LonePlayer(), npc, KatranaPrestorGossip.SenderMain, action);

        Assert.Equal(default, reply);
    }
}
