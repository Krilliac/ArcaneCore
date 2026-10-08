using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>
/// The EventAI event and action types the live world's classic-db z2815 rows use and the server reported as unsupported (world log of
/// 2026-10-08: actions 4, 5, 43, 56, 58, 64, events 14, 30, ...), the cmangos-classic way (CreatureEventAI.cpp ProcessAction :665-1380,
/// CheckEvent :255-557, the AI hooks :1540-1691). Rows and ids are synthetic; most rows fire on spawn (EVENT_T_SPAWNED) or cast a marker
/// spell through a recording caster so the effect can be read back.
/// </summary>
public sealed class ScriptLinkEventAiTests
{
    private const uint Other = 7202;
    private const uint Minion = 7203;

    private sealed class FakeUnitSpells : IUnitSpellQueries
    {
        public Dictionary<(Unit Unit, uint Spell), int> Stacks { get; } = [];

        public List<(Unit Unit, uint Spell)> Removed { get; } = [];

        public int GetAuraStacks(Unit unit, uint spellId) => Stacks.GetValueOrDefault((unit, spellId));

        public bool IsCasting(Unit unit) => false;

        public bool RemoveAuras(Unit unit, uint spellId)
        {
            Removed.Add((unit, spellId));
            return Stacks.Remove((unit, spellId));
        }

        /// <summary>The spells that carry SPELL_ATTR_EX_EXCLUDE_CASTER.</summary>
        public HashSet<uint> CasterExcluded { get; } = [];

        public bool ExcludesCaster(uint spellId) => CasterExcluded.Contains(spellId);
    }

    private sealed class FakeQuests : IEventAiQuestEvents
    {
        public List<(Player Player, uint Quest, bool Group)> Events { get; } = [];

        public List<(Player Player, uint Entry)> Kills { get; } = [];

        public void EventHappened(Player player, uint questId, Creature source, bool rewardGroup) => Events.Add((player, questId, rewardGroup));

        public void KillCredit(Player player, uint creatureEntry, Creature source) => Kills.Add((player, creatureEntry));
    }

    private sealed record Scene(WorldRuntime World, Map Map, CreatureMapSystem System, FakeCaster Spells, FakeUnitSpells UnitSpells, FakeQuests Quests,
        Player Player, FakeSession Session, Creature Me) : IDisposable
    {
        public void Dispose() => World.Dispose();

        public bool Cast(uint spell) => Spells.Casts.Any(c => c.Spell == spell);

        public int Casts(uint spell) => Spells.Casts.Count(c => c.Spell == spell);

        public Creature Find(uint entry) => System.Creatures.Single(c => c.Entry == entry);

        public CreatureEventAI Ai => (CreatureEventAI)Me.AI!;
    }

    private static CreatureAiEvent Row(uint id, byte type, CreatureAiAction action, int p1 = 0, int p2 = 0, int p3 = 0, int p4 = 0, int p5 = 0,
        uint flags = 0, uint creature = WolfEntry, CreatureAiAction action2 = default)
        => new()
        {
            Id = id,
            CreatureId = creature,
            EventType = type,
            Flags = flags,
            Param1 = p1,
            Param2 = p2,
            Param3 = p3,
            Param4 = p4,
            Param5 = p5,
            Action1 = action,
            Action2 = action2,
        };

    private static CreatureAiEvent OnSpawn(uint id, CreatureAiAction action, uint creature = WolfEntry, CreatureAiAction action2 = default)
        => Row(id, (byte)EventAiEventType.Spawned, action, creature: creature, action2: action2);

    private static CreatureAiAction Act(byte type, int p1 = 0, int p2 = 0, int p3 = 0) => new(type, p1, p2, p3);

    private static CreatureAiAction Marker(int spell, int target = (int)EventAiTarget.Self) => Act((byte)EventAiActionType.Cast, spell, target);

    private static Scene Start(IEnumerable<CreatureAiEvent> events, IEnumerable<CreatureSpawn>? more = null, float playerX = 40,
        Action<CreatureTemplateBuilder>? template = null, IEnumerable<CreatureAiSummon>? summons = null, IEnumerable<CreatureTemplate>? templates = null,
        MapType? mapType = null, uint instanceId = 0)
    {
        var ai = new CreatureAiContent(events, [], summons: summons);
        CreatureContent content = new(
            [
                Template(configure: t =>
                {
                    t.AIName = CreatureAiFactory.EventAIName;
                    t.Faction = 7;
                    template?.Invoke(t);
                }),
                Template(Other, t => { t.AIName = CreatureAiFactory.EventAIName; t.Faction = 7; }),
                Template(Minion, t => { t.AIName = CreatureAiFactory.EventAIName; t.Faction = 7; t.DisplayIds = [4242]; }),
                .. templates ?? [],
            ],
            [Spawn(1, WolfEntry, 0, 0, respawnSeconds: 5), .. more ?? []], [], [], [], ai);
        var spells = new FakeCaster();
        var unitSpells = new FakeUnitSpells();
        var quests = new FakeQuests();
        WorldRuntime runtime = TestWorld.CreateRuntime();
        if (mapType is { } type)
        {
            WorldMaps.Of(runtime).Load(new MapContent([new MapTemplate(0, 0, type, 0, 40, 0, -1, 0, 0, "EventAI map", "")], [], [], [], []));
        }

        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { Spells = spells, UnitSpells = unitSpells, QuestEvents = quests }, world: runtime, instanceId: instanceId);
        (Player player, FakeSession session) = instanceId == 0 ? AddPlayer(world, 1, playerX, 0) : AddPlayerTo(world, map, 1, playerX);
        Creature me = system.Creatures.Single(c => c.Spawn?.Guid == 1);
        return new Scene(world, map, system, spells, unitSpells, quests, player, session, me);
    }

    /// <summary>A player put straight on <paramref name="map"/> (an instance copy the world's login resolver does not know).</summary>
    private static (Player Player, FakeSession Session) AddPlayerTo(WorldRuntime world, Map map, uint guid, float x)
    {
        var session = new FakeSession((int)guid);
        Player player = TestWorld.CreatePlayer(guid, x, 0, session);
        map.AddPlayer(player);
        world.RunTick(0);
        session.Clear();
        return (player, session);
    }

    // --- unit state ----------------------------------------------------------------------------------

    [Fact]
    public void SetFaction_TakesTheFaction_AndZeroOrARespawnGivesTheTemplatesBack()
    {
        // Defias Prisoner (1706): "Turn Hostile Inside The Stockade" sets faction 17.
        using Scene s = Start([OnSpawn(1, Act(2, 17))]);
        Assert.Equal(17u, s.Me.FactionTemplate);

        Assert.True(new SetFactionAction().Execute(s.Ai.Engine.Context, Act(2, 0), default));
        Assert.Equal(7u, s.Me.FactionTemplate);

        Assert.True(new SetFactionAction().Execute(s.Ai.Engine.Context, Act(2, 54), default));
        s.System.KillCreature(s.Me);
        s.System.ForceRespawn(s.Me);
        Assert.Equal(17u, s.Me.FactionTemplate); // the spawn row ran again; without it the template's 7 would be back
    }

    [Fact]
    public void MorphAndMount_SetTheModelFields_AndZeroUndoesThem()
    {
        using Scene s = Start([OnSpawn(1, Act(3, 0, 11284), action2: Act(43, 0, 2328))]);
        Assert.Equal(11284u, s.Me.DisplayId);
        Assert.Equal(2328u, s.Me.GetUInt32(UpdateFields.UnitFieldMountdisplayid));

        new MorphAction().Execute(s.Ai.Engine.Context, Act(3, (int)Minion, 0), default);
        Assert.Equal(4242u, s.Me.DisplayId); // the creature id's model
        new MorphAction().Execute(s.Ai.Engine.Context, Act(3), default);
        Assert.Equal(s.Me.NativeDisplayId, s.Me.DisplayId);
        new MountAction().Execute(s.Ai.Engine.Context, Act(43), default);
        Assert.Equal(0u, s.Me.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Fact]
    public void UnitFieldAndFlags_ChangeTheTarget_AndAFieldOutsideTheUnitBlockFails()
    {
        // Huldar (2057): SET_UNIT_FIELD 147 (UNIT_NPC_FLAGS) 3 on evade; the flag actions add and remove 256 + 512.
        using Scene s = Start([OnSpawn(1, Act(17, UpdateFields.UnitNpcFlags, 3, 0), action2: Act(18, 768, 0))]);
        Assert.Equal(3u, s.Me.GetUInt32(UpdateFields.UnitNpcFlags));
        Assert.Equal((UnitFlags)768, s.Me.UnitFlags & (UnitFlags)768);

        Assert.True(new RemoveUnitFlagAction().Execute(s.Ai.Engine.Context, Act(19, 256, 0), default));
        Assert.Equal((UnitFlags)512, s.Me.UnitFlags & (UnitFlags)768);
        Assert.False(new SetUnitFieldAction().Execute(s.Ai.Engine.Context, Act(17, 3, 99, 0), default)); // an object field
        Assert.False(new SetUnitFieldAction().Execute(s.Ai.Engine.Context, Act(17, UpdateFields.UnitEnd, 99, 0), default));
    }

    [Fact]
    public void SheathStandStateAndReactState_AreSet()
    {
        using Scene s = Start([OnSpawn(1, Act(40, 2), action2: Act(47, 8))]);
        Assert.Equal(2, s.Me.GetByte(UpdateFields.UnitFieldBytes2, 0));
        Assert.Equal((StandState)8, s.Me.StandState);

        Assert.True(new SetReactStateAction().Execute(s.Ai.Engine.Context, Act(50, 0), default));
        Assert.Equal(CreatureReactState.Passive, s.Me.ReactState);
        Assert.False(new SetReactStateAction().Execute(s.Ai.Engine.Context, Act(50, 3), default));
    }

    [Fact]
    public void UpdateTemplate_SwapsTheEntryKeepingTheHealthPercent_UntilTheRespawn()
    {
        // Land Walker (5357): "Change Template and Set Phase 1 on Transmogrify Spell Hit".
        using Scene s = Start([], templates: [Template(14604, t => { t.MinLevelHealth = 2000; t.MaxLevelHealth = 2000; t.Faction = 14; })],
            template: t => { t.MinLevelHealth = 1000; t.MaxLevelHealth = 1000; });
        s.Me.Health = 500;

        Assert.True(new UpdateTemplateAction().Execute(s.Ai.Engine.Context, Act(36, 14604), default));
        Assert.Equal((14604u, 2000u, 1000u, 14u), (s.Me.Entry, s.Me.MaxHealth, s.Me.Health, s.Me.FactionTemplate));
        Assert.False(new UpdateTemplateAction().Execute(s.Ai.Engine.Context, Act(36, 14604), default)); // already that entry

        s.System.KillCreature(s.Me);
        s.System.ForceRespawn(s.Me);
        Assert.Equal((WolfEntry, 1000u), (s.Me.Entry, s.Me.MaxHealth));
    }

    [Fact]
    public void DeathPrevention_LeavesTheCreatureAtOneHealth()
    {
        // Guard Edward (4922): "Set Death Prevention on Spawn".
        using Scene s = Start([OnSpawn(1, Act(42, 1))], template: t => { t.MinLevelHealth = 100; t.MaxLevelHealth = 100; });

        s.Map.Combat.DealDamage(s.Player, s.Me, 5000, direct: false);

        Assert.True(s.Me.IsAlive);
        Assert.Equal(1u, s.Me.Health);
        new SetDeathPreventionAction().Execute(s.Ai.Engine.Context, Act(42, 0), default);
        s.Map.Combat.DealDamage(s.Player, s.Me, 5000, direct: false);
        Assert.False(s.Me.IsAlive);
    }

    // --- sound, emotes, phases ----------------------------------------------------------------------------

    [Fact]
    public void SoundAndEmote_ReachThePlayersThatSeeTheCreature()
    {
        using Scene s = Start([], playerX: 10);
        s.Session.Clear();

        new SoundAction().Execute(s.Ai.Engine.Context, Act(4, 5804), default);
        new EmoteAction().Execute(s.Ai.Engine.Context, Act(5, 11), default); // EMOTE_ONESHOT_LAUGH

        byte[] sound = Assert.Single(Packets(s.Session, WorldOpcode.SmsgPlaySound));
        Assert.Equal(5804u, BitConverter.ToUInt32(sound, 0));
        Assert.Single(Packets(s.Session, WorldOpcode.SmsgEmote));
    }

    [Fact]
    public void RandomEmoteAndPhases_PickByTheRandomValue_AndMinusOneDoesNothing()
    {
        using Scene s = Start([], playerX: 10);
        s.Session.Clear();

        new RandomEmoteAction().Execute(s.Ai.Engine.Context, Act(10, 1, -1, 11), new EventAiInvocation(Random: 1, 0, null, null, null));
        Assert.Empty(Packets(s.Session, WorldOpcode.SmsgEmote));
        new RandomPhaseAction().Execute(s.Ai.Engine.Context, Act(30, 1, 2, 3), new EventAiInvocation(Random: 5, 0, null, null, null));
        Assert.Equal(3, s.Ai.Phase); // 5 % 3 = 2: the third
        new RandomPhaseRangeAction().Execute(s.Ai.Engine.Context, Act(31, 2, 5), new EventAiInvocation(Random: 7, 0, null, null, null));
        Assert.Equal(5, s.Ai.Phase); // 7 % 4 + 2
    }

    // --- movement ------------------------------------------------------------------------------------------

    [Fact]
    public void SetWalk_ChangesTheRunMode_AndAnnouncesIt()
    {
        using Scene s = Start([], playerX: 10);
        s.Session.Clear();

        new SetWalkAction().Execute(s.Ai.Engine.Context, Act(58, 1), default);

        Assert.True(s.Me.Movement.HasFlag(MovementFlags.WalkMode));
        Assert.Single(Packets(s.Session, WorldOpcode.SmsgSplineMoveSetWalkMode));
        new SetWalkAction().Execute(s.Ai.Engine.Context, Act(58, 0), default);
        Assert.False(s.Me.Movement.HasFlag(MovementFlags.WalkMode));
    }

    [Fact]
    public void Immobilized_RootsTheCreature_SoItDoesNotChase_AndACombatOnlyRootEndsAtTheReset()
    {
        // Theramore Combat Dummy (4952): "Set Immobilized State ... on Spawn".
        using Scene s = Start([OnSpawn(1, Act(61, 1, 1))], playerX: 15);
        float x = s.Me.X;

        s.Map.Combat.DealDamage(s.Player, s.Me, 1, direct: false);
        Run(s.World, 1500);

        Assert.True(s.Me.Movement.HasFlag(MovementFlags.Root));
        Assert.Equal(x, s.Me.X);
        s.Ai.Reset();
        Assert.False(s.Me.Movement.HasFlag(MovementFlags.Root));
    }

    [Fact]
    public void FollowMovement_ZeroHoldsAFollowingCreatureStill()
    {
        using Scene s = Start([], playerX: 10);
        s.Me.Motion.MoveFollow(s.Player, 2, 0);
        new SetFollowMovementAction().Execute(s.Ai.Engine.Context, Act(64, 0), default);
        (float x, float y) = (s.Me.X, s.Me.Y);

        Run(s.World, 1500);
        Assert.Equal((x, y), (s.Me.X, s.Me.Y));

        new SetFollowMovementAction().Execute(s.Ai.Engine.Context, Act(64, 1), default);
        Run(s.World, 1500);
        Assert.True(Distance2D(s.Me, s.Player) < 9);
    }

    [Fact]
    public void SetFacing_TurnsToTheTarget()
    {
        using Scene s = Start([], playerX: 10);

        Assert.True(new SetFacingAction().Execute(s.Ai.Engine.Context, Act(59, (int)EventAiTarget.Invoker), new EventAiInvocation(0, 0, s.Player, null, null)));

        Assert.Equal(0f, s.Me.Orientation, 0.01f); // the player stands due east
        Assert.False(new SetFacingAction().Execute(s.Ai.Engine.Context, Act(59, (int)EventAiTarget.Invoker), default));
    }

    // --- despawns and guardians -------------------------------------------------------------------------------

    [Fact]
    public void ForcedDespawn_AfterItsDelay_KillsWithoutAKill_AndTheSpawnComesBackOnItsTimer()
    {
        // Captured Felwood Ooze (10290): "Send AI Event A and Delayed Despawn".
        using Scene s = Start([OnSpawn(1, Act(41, 1000))]);
        int kills = 0;
        s.Map.Combat.UnitKilled += (_, _) => kills++;

        Run(s.World, 800);
        Assert.True(s.Me.IsAlive);
        Run(s.World, 400);
        Assert.False(s.Me.IsAlive);
        Assert.Equal(0, kills);

        Run(s.World, 5300); // the respawn timer is 5 s (and the respawned spawn row schedules the next despawn 1 s later)
        Assert.True(s.Me.IsAlive);
    }

    [Fact]
    public void DespawnGuardians_RemovesTheCreaturesGuardians_OfTheEntryOrAll()
    {
        using Scene s = Start([]);
        PetMapSystem pets = s.Map.Pets!;
        Creature Guardian(uint entry) => s.System.SpawnSummoned(s.System.Content.FindTemplate(entry)!, HighGuid.Pet, c =>
        {
            c.Summon = new SummonLinks(SummonKind.Guardian, s.Me.Guid, 0, -1, 0);
            c.SetOwnerGuid(s.Me.Guid);
            return new CreatureHome(2, 2, 83.5f, 0);
        });
        Creature first = Guardian(Minion);
        Creature second = Guardian(Other);
        pets.Register(first, new SummonService());
        pets.Register(second, new SummonService());

        Assert.True(new DespawnGuardiansAction().Execute(s.Ai.Engine.Context, Act(56, (int)Minion), default));
        Assert.Null(s.System.FindCreature(first.Guid));
        Assert.NotNull(s.System.FindCreature(second.Guid));
        new DespawnGuardiansAction().Execute(s.Ai.Engine.Context, Act(56, 0), default);
        Assert.Null(s.System.FindCreature(second.Guid));
    }

    // --- summons and the summoner's events ------------------------------------------------------------------

    [Fact]
    public void SummonId_UsesTheSummonRow_AndTheSummonerHearsOfTheSummonAndItsDeath()
    {
        using Scene s = Start(
            [
                OnSpawn(1, Act(32, (int)Minion, (int)EventAiTarget.Self, 7)),
                Row(2, 17, Marker(9017), p1: (int)Minion),   // SUMMONED_UNIT
                Row(3, 25, Marker(9025), p1: (int)Minion),   // SUMMONED_JUST_DIED
                Row(4, 26, Marker(9026), p1: (int)Minion),   // SUMMONED_JUST_DESPAWN
                Row(5, 25, Marker(9099), p1: (int)Other),    // another entry: never
            ],
            summons: [new CreatureAiSummon(7, 12, 13, 83.5f, 1f, 60000)]); // a lifetime of 0 would despawn the summon at once (below)
        Creature minion = s.Find(Minion);
        Assert.Equal((12f, 13f), (minion.X, minion.Y));
        Assert.True(s.Cast(9017));

        s.System.KillCreature(minion);
        Assert.True(s.Cast(9025));
        Assert.False(s.Cast(9099));
        s.System.Despawn(minion);
        Assert.True(s.Cast(9026));
    }

    [Fact]
    public void ASummon_AttacksItsSpawner_ThroughTargetEleven()
    {
        // Summoned Voidwalker (5676): "Set Faction Monster, Attack Spawner (Player) on Generic Timer" (ATTACK_START, target 11).
        using Scene s = Start(
            [
                OnSpawn(1, Act(12, (int)Minion, (int)EventAiTarget.Self, 0)),
                OnSpawn(2, Act(2, 14), creature: Minion, action2: Act(55, 11)),
            ],
            templates: [], template: t => t.Faction = 35);
        Creature minion = s.Find(Minion);

        Assert.Same(s.Me, minion.Combat.Victim);
        Assert.Same(s.Me, s.System.SummonerOf(minion));
    }

    [Fact]
    public void SummonId_TheRowsLifetimeIsInMilliseconds_AndRunsOutOfCombat()
    {
        // creature_ai_summons.spawntimesecs holds milliseconds despite its name: cmangos passes it to SummonCreature as the despawn time
        // (CreatureEventAI.cpp:1018-1019; TemporarySpawn.cpp uses std::chrono::milliseconds(m_lifetime)). z2815 rows: 10000 (4), 120000 (3),
        // ... 86400000 (2).
        using Scene s = Start([OnSpawn(1, Act(32, (int)Minion, (int)EventAiTarget.Self, 7))],
            summons: [new CreatureAiSummon(7, 12, 13, 83.5f, 1f, 10000)]);
        Creature minion = s.Find(Minion);

        Run(s.World, 9500);
        Assert.NotNull(s.System.FindCreature(minion.Guid));
        Run(s.World, 1000);
        Assert.Null(s.System.FindCreature(minion.Guid));
    }

    [Fact]
    public void SummonId_ALifetimeAboveFourMillionMilliseconds_DoesNotWrapAround()
    {
        // 4,294,968 x 1000 overflows a uint to 704; the summon has to stay for its 71.6 minutes (z2815 has 7200000 and 86400000, which wrap too).
        using Scene s = Start([OnSpawn(1, Act(32, (int)Minion, (int)EventAiTarget.Self, 7))],
            summons: [new CreatureAiSummon(7, 12, 13, 83.5f, 1f, 4_294_968)]);
        Creature minion = s.Find(Minion);

        Run(s.World, 2000);

        Assert.NotNull(s.System.FindCreature(minion.Guid));
    }

    [Theory]
    [InlineData((byte)32)] // ACTION_T_SUMMON_ID with a row lifetime of 0 (CreatureEventAI.cpp:1020-1021)
    [InlineData((byte)12)] // ACTION_T_SPAWN with a duration of 0 (:819-822)
    public void ASummonWithNoLifetime_DespawnsAsSoonAsItIsOutOfCombat(byte actionType)
    {
        // cmangos TEMPSPAWN_TIMED_OOC_DESPAWN with 0 ms (TemporarySpawn.cpp:45-58): alive and out of combat, it has expired.
        CreatureAiAction summon = actionType == 32
            ? Act(32, (int)Minion, (int)EventAiTarget.Self, 7)
            : Act(12, (int)Minion, (int)EventAiTarget.Self, 0);
        using Scene s = Start(
            [
                OnSpawn(1, summon),
                Row(2, 17, Marker(9017), p1: (int)Minion),   // SUMMONED_UNIT
                Row(3, 26, Marker(9026), p1: (int)Minion),   // SUMMONED_JUST_DESPAWN
            ],
            summons: [new CreatureAiSummon(7, 12, 13, 83.5f, 1f, 0)]);

        Run(s.World, 300);

        Assert.True(s.Cast(9017));
        Assert.True(s.Cast(9026));
        Assert.DoesNotContain(s.System.Creatures, c => c.Entry == Minion);
    }

    [Fact]
    public void ASummonWithNoLifetime_StaysWhileItFights()
    {
        using Scene s = Start([], summons: [new CreatureAiSummon(7, 12, 13, 83.5f, 1f, 0)], playerX: 20,
            templates: [Template(7204, t => { t.Faction = 14; t.MinLevelHealth = 5000; t.MaxLevelHealth = 5000; })]);
        var invocation = new EventAiInvocation(0, 0, s.Player, null, null);

        Assert.True(new SummonIdAction().Execute(s.Ai.Engine.Context, Act(32, 7204, (int)EventAiTarget.Invoker, 7), invocation));
        Creature summoned = s.Find(7204);
        Run(s.World, 1500);

        Assert.NotNull(s.System.FindCreature(summoned.Guid));
        Assert.True(summoned.Combat.IsInCombat);
        Assert.Same(s.Player, summoned.Combat.Victim);
    }

    // --- AI events -------------------------------------------------------------------------------------------

    [Fact]
    public void ThrowAiEvent_ACustomEventReachesEveryCreatureInRange_WithItsSenderAndInvoker()
    {
        // Captive Ghoul (5685): "Send Custom AI event B on Spawn" (type 6, radius 10); Dana (804): "Stop following master on Receive AI Event Custom A".
        using Scene s = Start(
            [
                Row(1, 30, Marker(9030, 10), p1: 6, creature: Other),                // from any sender: cast at the sender (TARGET_T_EVENT_SENDER)
                Row(2, 30, Marker(9031), p1: 6, p2: (int)Minion, creature: Other),   // only from a Minion sender: never
                Row(3, 30, Marker(9032), p1: 5, creature: Other),                    // another event type: never
            ],
            more: [Spawn(2, Other, 5, 0), Spawn(3, Other, 30, 0)]);
        Creature near = s.System.Creatures.Single(c => c.Spawn?.Guid == 2);

        int received = s.System.SendAiEventAround(s.Me, 6, s.Player, 10);

        Assert.Equal(2, received); // the sender itself (AnyUnitInObjectRangeCheck includes it) and the near one; not the one 30 yd away
        (uint Spell, Unit? Target, bool _) cast = Assert.Single(s.Spells.Casts);
        Assert.Equal(9030u, cast.Spell);
        Assert.Same(s.Me, cast.Target); // TARGET_T_EVENT_SENDER
        Assert.NotNull(near);
    }

    [Fact]
    public void ThrowAiEventAction_NeedsItsTarget()
    {
        using Scene s = Start([Row(1, 30, Marker(9030), p1: 5, creature: Other)], more: [Spawn(2, Other, 5, 0)]);

        Assert.False(new ThrowAiEventAction().Execute(s.Ai.Engine.Context, Act(45, 5, 10, (int)EventAiTarget.Invoker), default));
        Assert.True(new ThrowAiEventAction().Execute(s.Ai.Engine.Context, Act(45, 5, 10, (int)EventAiTarget.Self), default));
        Assert.True(s.Cast(9030));
    }

    // --- friends, sight, targets --------------------------------------------------------------------------------

    [Fact]
    public void FriendlyHealth_CastsAtTheFriendMissingTheMostHealth_InCombat()
    {
        // Redridge Mystic (430): "Cast Healing Wave on Friendly Missing HP" (deficit 550, radius 30, target 12 = the event's).
        using Scene s = Start([Row(1, 14, Marker(9014, 12), p1: 10, p2: 30, p3: 1000, p4: 1000)],
            more: [Spawn(2, Other, 5, 0), Spawn(3, Other, 50, 0)], playerX: 3,
            template: t => { t.MinLevelHealth = 1000; t.MaxLevelHealth = 1000; });
        Creature near = s.System.Creatures.Single(c => c.Spawn?.Guid == 2);
        Creature far = s.System.Creatures.Single(c => c.Spawn?.Guid == 3);
        s.Map.Combat.DealDamage(s.Player, s.Me, 1, direct: false);
        s.Map.Combat.DealDamage(s.Player, near, 1, direct: false);
        near.Health = near.MaxHealth / 2;  // 55 max health: 27 missing, more than the 10 asked and more than the caster's own 1
        far.Health = 1;                     // misses more, but 50 yd away

        Run(s.World, 2000);

        Assert.Contains(s.Spells.Casts, c => c.Spell == 9014 && ReferenceEquals(c.Target, near));
        Assert.DoesNotContain(s.Spells.Casts, c => ReferenceEquals(c.Target, far));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FriendlyHealth_TakesTheCreatureItself_UnlessTheEventSpecificCastExcludesTheCaster(bool excludesCaster)
    {
        // cmangos CreatureEventAIMgr.cpp:1082-1096: friendlyHp.targetSelf is false when a CAST at TARGET_T_EVENT_SPECIFIC uses a
        // SPELL_ATTR_EX_EXCLUDE_CASTER spell (z2815: 14 rows, e.g. 198303 with spell 3477); DoSelectLowestHpFriendly then skips the caster.
        using Scene s = Start([Row(1, 14, Marker(9014, 12), p1: 10, p2: 30, p3: 1000, p4: 1000)],
            more: [Spawn(2, Other, 5, 0)], playerX: 3,
            template: t => { t.MinLevelHealth = 1000; t.MaxLevelHealth = 1000; });
        if (excludesCaster)
        {
            s.UnitSpells.CasterExcluded.Add(9014);
        }

        Creature near = s.System.Creatures.Single(c => c.Spawn?.Guid == 2);
        s.Map.Combat.DealDamage(s.Player, s.Me, 1, direct: false);
        s.Map.Combat.DealDamage(s.Player, near, 1, direct: false);
        s.Me.Health = 100;                 // misses 900: the most
        near.Health = near.MaxHealth / 2;  // misses 27, more than the 10 asked

        Run(s.World, 2000);

        (uint Spell, Unit? Target, bool _) cast = s.Spells.Casts.First(c => c.Spell == 9014);
        Assert.Same(excludesCaster ? near : s.Me, cast.Target);
    }

    [Fact]
    public void FriendlyMissingBuff_OutOfCombatFlag_BuffsTheFriendWithoutTheAura()
    {
        // Riverpaw Taskmaster (98): Quick Bloodlust on a friend missing it; flag 2 = only out of combat.
        using Scene s = Start([Row(1, 16, Marker(9016, 12), p1: 3229, p2: 30, p3: 1000, p4: 1000, p5: 2)], more: [Spawn(2, Other, 5, 0)]);
        Creature friend = s.System.Creatures.Single(c => c.Spawn?.Guid == 2);
        s.UnitSpells.Stacks[(s.Me, 3229)] = 1; // the caster already has it

        Run(s.World, 700);

        Assert.Contains(s.Spells.Casts, c => c.Spell == 9016 && ReferenceEquals(c.Target, friend));
    }

    [Fact]
    public void FriendlyCrowdControlled_FindsAStunnedFriendInCombat()
    {
        using Scene s = Start([Row(1, 15, Marker(9015, 12), p2: 30, p3: 1000, p4: 1000)], more: [Spawn(2, Other, 5, 0)], playerX: 3);
        Creature friend = s.System.Creatures.Single(c => c.Spawn?.Guid == 2);
        s.Map.Combat.DealDamage(s.Player, s.Me, 1, direct: false);
        s.Map.Combat.DealDamage(s.Player, friend, 1, direct: false);
        Run(s.World, 1300);
        Assert.False(s.Cast(9015));

        friend.UnitFlags |= UnitFlags.Stunned;
        Run(s.World, 1300);

        Assert.Contains(s.Spells.Casts, c => c.Spell == 9015 && ReferenceEquals(c.Target, friend));
    }

    [Fact]
    public void OutOfCombatLineOfSight_AFriendlyRowSeesAPlayerInRange_AndNotOneFarAway()
    {
        // Seer Wiserunner (2984): "SayText on Player in LoS" (NoHostile 1, range 20, player only).
        using Scene s = Start([Row(1, 10, Marker(9010, (int)EventAiTarget.Invoker), p1: 1, p2: 20, p3: 1000, p4: 1000, p5: 1)], playerX: 40);
        s.Me.AI!.MoveInLineOfSight(s.Player);
        Assert.False(s.Cast(9010));

        s.Player.Relocate(10, 0, 83.5f, 0, 0);
        s.Me.AI!.MoveInLineOfSight(s.Player);

        (uint Spell, Unit? Target, bool _) cast = Assert.Single(s.Spells.Casts);
        Assert.Same(s.Player, cast.Target);
    }

    [Fact]
    public void SelectAttackingTarget_PicksAThreatTargetInsideTheRange()
    {
        // High Overlord Saurfang (14720): "Cast Shield Charge" at a target 8-25 yd away.
        using Scene s = Start([Row(1, 32, Marker(9032, 12), p1: 8, p2: 25, p3: 1000, p4: 1000)], playerX: 3);
        s.Me.AI!.CombatMovement = false;
        s.Map.Combat.DealDamage(s.Player, s.Me, 1, direct: false);
        Run(s.World, 1300);
        Assert.False(s.Cast(9032)); // 3 yd: closer than 8

        s.Player.Relocate(15, 0, 83.5f, 0, 0);
        Run(s.World, 1300);

        Assert.Contains(s.Spells.Casts, c => c.Spell == 9032 && ReferenceEquals(c.Target, s.Player));
    }

    [Fact]
    public void SpellHitTarget_FiresWhenTheCreaturesOwnSpellLands()
    {
        using Scene s = Start([Row(1, 34, Marker(9034, (int)EventAiTarget.Invoker), p1: 26339, p2: -1, p3: 1000, p4: 1000)], playerX: 10);
        var rage = new SpellInfo { Id = 26339 };
        var other = new SpellInfo { Id = 133 };

        s.Spells.RaiseHit(s.Player, s.Me, rage);   // someone else's spell on the creature: not this event
        s.Spells.RaiseHit(s.Me, s.Player, other);  // another spell
        Assert.False(s.Cast(9034));
        s.Spells.RaiseHit(s.Me, s.Player, rage);

        (uint Spell, Unit? Target, bool _) cast = Assert.Single(s.Spells.Casts);
        Assert.Same(s.Player, cast.Target);
    }

    // --- auras, attacks, quests, zone ------------------------------------------------------------------------------

    [Fact]
    public void RemoveAuras_TakesTheSpellOffTheTarget()
    {
        using Scene s = Start([], playerX: 10);
        s.UnitSpells.Stacks[(s.Player, 12380)] = 1;

        new RemoveAurasFromSpellAction().Execute(s.Ai.Engine.Context, Act(28, (int)EventAiTarget.Invoker, 12380), new EventAiInvocation(0, 0, s.Player, null, null));

        (Unit unit, uint spell) = Assert.Single(s.UnitSpells.Removed);
        Assert.Same(s.Player, unit);
        Assert.Equal(12380u, spell);
        Assert.Equal(0, s.UnitSpells.GetAuraStacks(s.Player, 12380));
    }

    [Fact]
    public void QuestEventAndKilledMonster_CreditThePlayer()
    {
        // Innkeeper Firebrew (5111): "Emote and Gives Quest Credit on Received Emote" (QUEST_EVENT 8353, target 6);
        // Sickly Gazelle (12296): KILLED_MONSTER 12297 to the invoker.
        using Scene s = Start([], playerX: 10);
        var invocation = new EventAiInvocation(0, 0, s.Player, null, null);

        new QuestEventAction().Execute(s.Ai.Engine.Context, Act(15, 8353, (int)EventAiTarget.Invoker), invocation);
        new KilledMonsterAction().Execute(s.Ai.Engine.Context, Act(33, 12297, (int)EventAiTarget.Invoker), invocation);

        Assert.Equal([(s.Player, 8353u, false)], s.Quests.Events);
        Assert.Equal([(s.Player, 12297u)], s.Quests.Kills);
    }

    [Fact]
    public void AttackStart_AttacksTheTarget_AndFailsWithoutOne()
    {
        using Scene s = Start([], playerX: 10);

        Assert.False(new AttackStartAction().Execute(s.Ai.Engine.Context, Act(55, (int)EventAiTarget.Invoker), default));
        Assert.True(new AttackStartAction().Execute(s.Ai.Engine.Context, Act(55, (int)EventAiTarget.Invoker), new EventAiInvocation(0, 0, s.Player, null, null)));
        Assert.Same(s.Player, s.Me.Combat.Victim);
    }

    [Fact]
    public void ZoneCombatPulse_DoesNothingOutsideADungeon()
    {
        using Scene s = Start([], playerX: 60);

        new ZoneCombatPulseAction().Execute(s.Ai.Engine.Context, Act(38), default);

        Assert.False(s.Me.Combat.IsInCombat);
    }

    [Fact]
    public void ZoneCombatPulse_InADungeon_EngagesEveryLivingPlayer_AndAttacksTheClosest()
    {
        // cmangos ACTION_T_ZONE_COMBAT_PULSE (CreatureEventAI.cpp:1097-1103): SetInCombatWithZone, then AttackClosestEnemy when there is
        // no victim. The dungeon test is Map::IsDungeon (the map entry's type), not the instance id.
        using Scene s = Start([], playerX: 70, mapType: MapType.Instance);
        (Player near, _) = AddPlayer(s.World, 2, 45, 0);
        Assert.Null(s.Me.Combat.Victim);

        new ZoneCombatPulseAction().Execute(s.Ai.Engine.Context, Act(38), default);

        Assert.True(s.Me.Combat.Threat.Contains(s.Player));
        Assert.True(s.Me.Combat.Threat.Contains(near));
        Assert.Same(near, s.Me.Combat.Victim);
    }

    [Fact]
    public void ZoneCombatPulse_OnABattlegroundInstance_DoesNothing()
    {
        // Creature::SetInCombatWithZone: !pMap->IsDungeon() returns; MapEntry::IsDungeon is instance or raid, so a battleground is out.
        using Scene s = Start([], playerX: 45, mapType: MapType.Battleground, instanceId: 3);
        Assert.Equal(3u, s.Map.InstanceId);
        Assert.Contains(s.Player, s.Map.Players);

        new ZoneCombatPulseAction().Execute(s.Ai.Engine.Context, Act(38), default);

        Assert.False(s.Me.Combat.IsInCombat);
    }

    [Fact]
    public void EveryTypeTheLiveWorldReported_HasAHandlerNow()
    {
        // The world log of 2026-10-08 (D:/ArcaneCore-lanes/_deploy/live-w2-r1/world.log) and the classic-db z2815 rows.
        byte[] actions = [2, 3, 4, 5, 9, 10, 15, 17, 18, 19, 28, 29, 30, 31, 32, 33, 36, 38, 40, 41, 42, 43, 45, 47, 50, 51, 55, 56, 58, 59, 61, 64];
        byte[] events = [10, 14, 15, 16, 17, 25, 26, 30, 32, 34];

        Assert.All(actions, a => Assert.NotNull(EventAiRegistry.Default.FindAction(a)));
        Assert.All(events, e => Assert.NotNull(EventAiRegistry.Default.FindEvent(e)));
    }
}
