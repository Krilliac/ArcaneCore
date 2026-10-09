using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using GameObjectTestKit = ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>
/// The relay commands TEMP_SPAWN_CREATURE (10), ACTIVATE_OBJECT (13), MOVEMENT (20) and SEND_AI_EVENT (35) (cmangos ScriptAction::
/// ExecuteDbscriptCommand, DBScripts/ScriptMgr.cpp:2048-2073, 2115-2128, 2277-2385, 2759-2775), the buddy-by-guid, buddy-by-object and
/// all-eligible-buddies data flags they are used with in classic-db (ScriptMgr.cpp:1360-1645), and the EventAI RECEIVE_AI_EVENT event (30)
/// that answers SEND_AI_EVENT (cmangos CreatureEventAI::ReceiveAIEvent, AI/EventAI/CreatureEventAI.cpp:1563-1575). As in
/// <see cref="RelayScriptTests"/>, a wave at Elly starts the relay with the player as the source and Elly as the target; rows and ids are
/// synthetic.
/// </summary>
public sealed class RelayScriptCommandTests
{
    private const uint TextEmoteWave = 101;
    private const uint Relay = 929000;
    private const uint BuddyEntry = 7201;
    private const uint FarEntry = 7202;
    private const uint DoorEntry = 180391;
    private const uint DoorGuid = 170712;

    private const uint FlagBuddyAsTarget = 0x001;
    private const uint FlagReverse = 0x002;
    private const uint FlagAdditional = 0x008;
    private const uint FlagBuddyByGuid = 0x010;
    private const uint FlagAllEligible = 0x200;
    private const uint FlagBuddyByGo = 0x400;

    [Fact]
    public void Movement_WaypointPathBit_UsesTheSharedPath()
    {
        (uint Entry, uint PathId, CreatureWaypoint Point)[] path =
            [(CreatureContent.WaypointPathEntry, CreatureContent.WaypointPathBit | 9997u,
                new CreatureWaypoint(1, 18, 0, 83.5f, 100, 0))];
        using Town t = Start([Step(0, 20, dataLong: 2, dataLong2: 9997, dataLong3: 2, flags: FlagReverse)], entryPaths: path);
        t.Wave();
        Assert.Equal(MovementGeneratorType.Waypoint, t.Elly.Motion.DefaultType);
        Assert.Equal(18f, t.Elly.Spline!.EndX);
    }

    [Fact]
    public void ZulFarrakCageRelay_OpensTheSpawnGuidDoor()
    {
        using Town t = Start([Step(0, 11, dataLong: DoorGuid, dataLong2: 9_000_000)],
            objects: [GameObjectTestKit.GoSpawn(DoorGuid, DoorEntry, 0, 10)]);
        GameObject door = Assert.Single(t.Objects.GameObjects);
        Assert.Equal(GameObjectState.Ready, door.State);
        t.Wave();
        Assert.Equal(GameObjectState.Active, door.State);
    }

    [Fact]
    public void DoorCommands_OpenAndCloseWithTheMapClock()
    {
        using Town t = Start([Step(0, 11, dataLong: DoorGuid), Step(100, 12, dataLong: DoorGuid)],
            objects: [GameObjectTestKit.GoSpawn(DoorGuid, DoorEntry, 0, 10)]);
        GameObject door = Assert.Single(t.Objects.GameObjects);
        t.Wave();
        Assert.Equal(GameObjectState.Active, door.State);
        Run(t.World, 100);
        Assert.Equal(GameObjectState.Ready, door.State);
    }

    [Fact]
    public void ObjectLockAndDelayedDespawn_UseTheSelectedGameObject()
    {
        using Town t = Start([
            Step(0, 27, dataLong: 4, flags: FlagBuddyByGuid | FlagBuddyByGo, buddy: DoorEntry, radius: DoorGuid),
            Step(0, 40, dataLong: 100, flags: FlagBuddyAsTarget | FlagBuddyByGuid | FlagBuddyByGo,
                buddy: DoorEntry, radius: DoorGuid)],
            objects: [GameObjectTestKit.GoSpawn(DoorGuid, DoorEntry, 0, 10)], objectType: GameObjectType.Chest);
        GameObject door = Assert.Single(t.Objects.GameObjects);
        t.Wave();
        Assert.True((door.Flags & GameObjectFlags.NoInteract) != 0);
        Assert.True(door.IsSpawned);
        Run(t.World, 99);
        Assert.True(door.IsSpawned);
        Run(t.World, 1);
        Assert.False(door.IsSpawned);
    }

    [Fact]
    public void RespawnGameObject_ShowsAHiddenSpawnForItsDespawnDelay()
    {
        // cmangos SCRIPT_COMMAND_RESPAWN_GAMEOBJECT (ScriptMgr.cpp:2001-2046): a spawn that is not spawned by default
        // (negative spawntimesecs) appears for datalong2 seconds and then leaves again.
        using Town t = Start([Step(0, 9, dataLong: DoorGuid, dataLong2: 2)],
            objects: [GameObjectTestKit.GoSpawn(DoorGuid, DoorEntry, 0, 10, spawnTimeSeconds: -60)], objectType: GameObjectType.Chest);
        GameObject chest = Assert.Single(t.Objects.GameObjects);
        Assert.False(chest.IsSpawned);
        t.Wave();
        Assert.True(chest.IsSpawned);
        Run(t.World, 1_999);
        Assert.True(chest.IsSpawned);
        Run(t.World, 1);
        Assert.False(chest.IsSpawned);
    }

    [Fact]
    public void RespawnGameObject_WithoutADespawnDelay_LeavesTheShownSpawnInTheWorld()
    {
        // ClassicDB dbscripts_on_event 466-468 (the Water Well Cleansing Aura, 2904, spawntimesecs -180) have datalong2 0. cmangos
        // SetRespawnTime(0) leaves m_respawnDelay 0, so IsSpawned() stays true and the object stays (GameObject.h:757-768); it must not be
        // removed again on the next tick.
        using Town t = Start([Step(0, 9, dataLong: DoorGuid)],
            objects: [GameObjectTestKit.GoSpawn(DoorGuid, DoorEntry, 0, 10, spawnTimeSeconds: -180)], objectType: GameObjectType.Chest);
        GameObject aura = Assert.Single(t.Objects.GameObjects);
        Assert.False(aura.IsSpawned);
        t.Wave();
        Assert.True(aura.IsSpawned);
        Run(t.World, 60_000);
        Assert.True(aura.IsSpawned);
    }

    [Fact]
    public void ModifyUnitFlags_Toggle_RemovesTheWholeMaskWhenAnyBitIsSet_AndSetsItOtherwise()
    {
        // cmangos SCRIPT_COMMAND_MODIFY_UNIT_FLAGS (ScriptMgr.cpp:2993-3016) toggles with HasFlag, which is true for any bit of the mask
        // (Object.h:476-480): not a XOR.
        const UnitFlags mask = UnitFlags.PetRename | UnitFlags.PetAbandon; // two bits nothing else in the test reads
        using Town t = Start([Step(0, 48, dataLong: (uint)mask, dataLong2: 2, flags: FlagReverse)]);
        t.Elly.UnitFlags = (t.Elly.UnitFlags & ~mask) | UnitFlags.PetRename;

        t.Wave();
        Assert.Equal((UnitFlags)0, t.Elly.UnitFlags & mask);

        Run(t.World, 100);
        t.Wave();
        Assert.Equal(mask, t.Elly.UnitFlags & mask);
    }

    [Fact]
    public void CreateItem_WithTheAdditionalFlag_DestroysTheCount()
    {
        // cmangos SCRIPT_COMMAND_CREATE_ITEM (ScriptMgr.cpp:2241-2255): SCRIPT_FLAG_COMMAND_ADDITIONAL destroys instead of creating; ClassicDB
        // has one such row (dbscripts_on_gossip 7166, the Cultist Engineer's 8 shards).
        using Town t = Start([Step(0, 17, dataLong: ItemTestData.QuestPelt, dataLong2: 8, flags: FlagAdditional)]);
        ItemTestData.Wire(t.Player.Inventory);
        ItemTestData.Give(t.Player.Inventory, ItemTestData.QuestPelt, 10);

        t.Wave();

        Assert.Equal(2u, t.Player.Inventory.GetItemCount(ItemTestData.QuestPelt));
    }

    [Fact]
    public void DistanceSound_UsesTheSourceGuidInItsPacket()
    {
        using Town t = Start([Step(0, 16, dataLong: 6209, dataLong2: 2, flags: FlagReverse)]);
        t.Wave();
        byte[] sound = Assert.Single(Packets(t.Session, WorldOpcode.SmsgPlayObjectSound));
        Assert.Equal(6209u, BitConverter.ToUInt32(sound, 0));
        Assert.Equal(t.Elly.Guid.Value, BitConverter.ToUInt64(sound, 4));
    }

    [Fact]
    public void ZulFarrakPrisonerRelay_ChangesTheBuddyFaction()
    {
        using Town t = Start([Step(0, 22, dataLong: 495, buddy: BuddyEntry, radius: 50, flags: 4)],
            more: [Spawn(17001, BuddyEntry, 2, 0)]);
        Creature prisoner = Assert.Single(t.OfEntry(BuddyEntry));
        t.Wave();
        Assert.Equal(495u, prisoner.FactionTemplate);
    }

    private static RelayScriptStep Step(uint delay, uint command, uint dataLong = 0, uint dataLong2 = 0, uint dataLong3 = 0, uint flags = 0,
        uint buddy = 0, uint radius = 0, int dataInt = 0, float x = 0, float y = 0, float z = 0, float o = 0, int dataInt2 = 0)
        => new(Relay, delay, 0, command, dataLong, dataLong2, dataLong3, buddy, radius, flags, dataInt, dataInt2, 0, 0, 0, x, y, z, o, 0, 0);

    private static CreatureAiEvent WaveRow()
        => new()
        {
            Id = 1,
            CreatureId = WolfEntry,
            EventType = 22,
            Flags = 1,
            Param1 = (int)TextEmoteWave,
            Action1 = new CreatureAiAction((byte)EventAiActionType.StartRelayScript, (int)Relay, 7, 0),
        };

    /// <summary>EVENT_T_RECEIVE_AI_EVENT (30): AIEventType, sender entry (0 any); the action casts <paramref name="spell"/> on the creature itself.</summary>
    private static CreatureAiEvent ReceiveRow(uint id, uint creature, int eventType, int senderEntry, uint spell)
        => new()
        {
            Id = id,
            CreatureId = creature,
            EventType = 30,
            Flags = 1,
            Param1 = eventType,
            Param2 = senderEntry,
            Action1 = new CreatureAiAction((byte)EventAiActionType.Cast, (int)spell, 0, 0),
        };

    private sealed record Town(WorldRuntime World, Map Map, CreatureMapSystem System, GameObjectMapSystem Objects, Creature Elly, Player Player,
        FakeSession Session, FakeCaster Spells) : IDisposable
    {
        public void Dispose() => World.Dispose();

        public void Wave() => Elly.ReceiveEmote(Player, TextEmoteWave);

        public IEnumerable<Creature> OfEntry(uint entry) => System.Creatures.Where(c => c.Template.Entry == entry);
    }

    private static Town Start(IEnumerable<RelayScriptStep> steps, IEnumerable<CreatureSpawn>? more = null, IEnumerable<CreatureAiEvent>? rows = null,
        bool patrol = false, IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? entryPaths = null, IEnumerable<GameObjectSpawn>? objects = null,
        bool fightingBuddy = false, GameObjectType objectType = GameObjectType.Door)
    {
        var ai = new CreatureAiContent([WaveRow(), .. rows ?? []], [], new BroadcastTextCatalog([]))
        {
            RelayScripts = new RelayScriptCatalog(steps, []),
        };
        CreatureContent content = new(
            [
                Template() with { AIName = CreatureAiFactory.EventAIName, Civilian = true },
                Template(BuddyEntry) with { AIName = CreatureAiFactory.EventAIName, Civilian = !fightingBuddy, ExtraFlags = fightingBuddy ? Creature.ExtraFlagNoAggro : 0 },
                Template(FarEntry) with { AIName = CreatureAiFactory.EventAIName, Civilian = true },
            ],
            [Spawn(1, WolfEntry, 0, 0, movementType: (byte)(patrol ? 2 : 0)), .. more ?? []],
            patrol ? [(1u, new CreatureWaypoint(1, 30, 0, 83.5f, 100, 0)), (1u, new CreatureWaypoint(2, 0, 0, 83.5f, 100, 0))] : [], [], [], ai,
            entryWaypoints: entryPaths);
        var spells = new FakeCaster();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells });
        var goSystem = new GameObjectMapSystem(map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(DoorEntry, objectType)], objects ?? [], [], [], []));
        map.AddUpdater(goSystem);
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 10);
        Creature elly = system.Creatures.Single(c => c.Template.Entry == WolfEntry);
        return new Town(world, map, system, goSystem, elly, player, session, spells);
    }

    // --- 10 TEMP_SPAWN_CREATURE ------------------------------------------------------------

    [Fact]
    public void TempSpawn_SummonsAtThePoint_RunningWhenAsked_AndGoesAfterItsDelayOutOfCombat()
    {
        using Town t = Start([Step(0, 10, dataLong: BuddyEntry, dataLong2: 3000, dataInt: 1, x: 6, y: 7, z: 83.5f, o: 1.25f)]);

        t.Wave();

        Creature summoned = Assert.Single(t.OfEntry(BuddyEntry));
        Assert.Null(summoned.Spawn);
        Assert.Equal((6f, 7f), (summoned.X, summoned.Y));
        Assert.Equal(1.25f, summoned.Orientation, 0.001f);
        Assert.True(summoned.ScriptRun); // dataint 1: SetWalk(false)
        Run(t.World, 2900);
        Assert.Single(t.OfEntry(BuddyEntry));
        Run(t.World, 300);
        Assert.Empty(t.OfEntry(BuddyEntry)); // TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN after datalong2 ms
    }

    [Fact]
    public void TempSpawn_WithoutAPoint_StandsAtTheSource_WalksByDefault_AndStaysWithoutADelay()
    {
        // Reverse: Elly is the source; no coordinates put the summon at contact distance plus both bounding radii from her, at twice her
        // orientation (CreatureCreatePos passes her orientation as the angle and GetClosePoint adds it again).
        using Town t = Start([Step(0, 10, dataLong: BuddyEntry, flags: FlagReverse, o: 2f)]);
        float facing = t.Elly.Orientation;

        t.Wave();

        Creature summoned = Assert.Single(t.OfEntry(BuddyEntry));
        float distance = 0.5f + t.Elly.BoundingRadius + summoned.BoundingRadius;
        Assert.Equal(t.Elly.X + (distance * MathF.Cos(2 * facing)), summoned.X, 0.01f);
        Assert.Equal(t.Elly.Y + (distance * MathF.Sin(2 * facing)), summoned.Y, 0.01f);
        Assert.Equal(2f, summoned.Orientation, 0.001f);
        Assert.False(summoned.ScriptRun);
        Run(t.World, 10_000);
        Assert.Single(t.OfEntry(BuddyEntry)); // TEMPSPAWN_DEAD_DESPAWN: only death removes it
    }

    [Fact]
    public void TempSpawn_ByACreatureSource_IsThatCreaturesSummon_ItsAiHearsOfIt_AfterThePlacement()
    {
        // cmangos WorldObject::SummonCreature (Entities/Object.cpp:2026-2166), which TEMP_SPAWN_CREATURE calls with the source as the spawner:
        // the TemporarySpawn carries the spawner's guid from its construction (GetSpawner, EventAI TARGET_T_SPAWNER), and a creature
        // spawner's AI gets JustSummoned once the summon is placed and summoned (EVENT_T_SUMMONED_UNIT, 17).
        CreatureAiEvent summonedRow = new()
        {
            Id = 2,
            CreatureId = WolfEntry,
            EventType = 17,
            Flags = 1,
            Param1 = (int)BuddyEntry,
            Action1 = new CreatureAiAction((byte)EventAiActionType.Cast, 7051, 0, 0),
        };
        using Town t = Start([Step(0, 10, dataLong: BuddyEntry, flags: FlagReverse)], rows: [summonedRow]);

        t.Wave();

        Creature summoned = Assert.Single(t.OfEntry(BuddyEntry));
        Assert.Same(t.Elly, t.System.SummonerOf(summoned));
        Assert.Equal([7051u], t.Spells.Casts.Select(c => c.Spell));

        // The player as the source (no reverse): nobody's summon, and no creature AI hears of it.
        using Town byPlayer = Start([Step(0, 10, dataLong: BuddyEntry, x: 6, y: 7, z: 83.5f)], rows: [summonedRow]);
        byPlayer.Wave();
        Assert.Null(byPlayer.System.SummonerOf(Assert.Single(byPlayer.OfEntry(BuddyEntry))));
        Assert.Empty(byPlayer.Spells.Casts);
    }

    // --- 13 ACTIVATE_OBJECT ----------------------------------------------------------------

    [Fact]
    public void ActivateObject_UsesTheObjectFoundByGuid_AndWithAdditional_PlaysItsCustomAnimation()
    {
        // classic-db relay 30 (Pat's Hellfire Guy): data_flags 1041 = buddy as target, by guid, by game object; search_radius holds the guid.
        using Town t = Start([Step(0, 13, flags: FlagBuddyAsTarget | FlagBuddyByGuid | FlagBuddyByGo, buddy: DoorEntry, radius: DoorGuid)],
            objects: [GameObjectTestKit.GoSpawn(DoorGuid, DoorEntry, 3, 3)]);
        GameObject door = t.Objects.GameObjects.Single(g => g.Spawn?.Guid == DoorGuid);
        Assert.Equal(GameObjectState.Ready, door.State);

        t.Wave();

        Assert.Equal(GameObjectState.Active, door.State); // the player (a unit) used the door

        using Town anim = Start([Step(0, 13, dataLong: 2, flags: FlagBuddyAsTarget | FlagBuddyByGuid | FlagBuddyByGo | FlagAdditional, buddy: DoorEntry, radius: DoorGuid)],
            objects: [GameObjectTestKit.GoSpawn(DoorGuid, DoorEntry, 3, 3)]);
        Run(anim.World, 100);
        anim.Session.Clear();
        anim.Wave();
        GameObject animated = anim.Objects.GameObjects.Single(g => g.Spawn?.Guid == DoorGuid);
        Assert.Equal(GameObjectState.Ready, animated.State);
        byte[] packet = Assert.Single(Packets(anim.Session, WorldOpcode.SmsgGameobjectCustomAnim));
        Assert.Equal(animated.Guid.Value, BitConverter.ToUInt64(packet, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(packet, 8));
    }

    [Fact]
    public void ActivateObject_FindsTheNearestObjectOfTheBuddyEntry_WithoutAGuid()
    {
        using Town t = Start([Step(0, 13, flags: FlagBuddyAsTarget, buddy: DoorEntry, radius: 20)],
            objects: [GameObjectTestKit.GoSpawn(DoorGuid, DoorEntry, 3, 12), GameObjectTestKit.GoSpawn(DoorGuid + 1, DoorEntry, 40, 40)]);

        t.Wave();

        Assert.Equal(GameObjectState.Active, t.Objects.GameObjects.Single(g => g.Spawn?.Guid == DoorGuid).State);
        Assert.Equal(GameObjectState.Ready, t.Objects.GameObjects.Single(g => g.Spawn?.Guid == DoorGuid + 1).State);
    }

    // --- 20 MOVEMENT -----------------------------------------------------------------------

    [Fact]
    public void Movement_Idle_StopsThePatrol()
    {
        using Town t = Start([Step(0, 20, dataLong: 0, flags: FlagReverse)], patrol: true);
        Run(t.World, 500);
        Assert.True(t.Elly.IsMoving);

        t.Wave();
        Run(t.World, 3000);

        Assert.Equal(MovementGeneratorType.Idle, t.Elly.Motion.DefaultType);
        Assert.False(t.Elly.IsMoving);
    }

    [Fact]
    public void Movement_Random_WandersWithinTheGivenDistanceOfHome()
    {
        using Town t = Start([Step(0, 20, dataLong: 1, dataLong2: 20, flags: FlagReverse)]);

        t.Wave();

        Assert.Equal(MovementGeneratorType.Random, t.Elly.Motion.CurrentType);
        bool moved = false;
        for (int i = 0; i < 200; i++)
        {
            Run(t.World, 200);
            moved |= Distance2D(t.Elly, 0, 0) > 0.5f;
            Assert.True(Distance2D(t.Elly, 0, 0) <= 20.01f);
        }

        Assert.True(moved);
    }

    [Fact]
    public void Movement_Random_IsPushedOverTheDefault_UnlessDataInt2AsksForTheMainGenerator()
    {
        // cmangos ScriptMgr.cpp:2334-2350: RANDOM_MOTION_TYPE clears the motion master (Clear(false, true)) only with dataint2 & 0x1 ("make it main
        // movegen"); otherwise MoveRandomAroundPoint mutates a wander generator on top and the patrol stays beneath it.
        using Town pushed = Start([Step(0, 20, dataLong: 1, dataLong2: 20, flags: FlagReverse)], patrol: true);
        pushed.Wave();
        Assert.Equal(MovementGeneratorType.Random, pushed.Elly.Motion.CurrentType);
        Assert.Equal(MovementGeneratorType.Waypoint, pushed.Elly.Motion.DefaultType);

        using Town main = Start([Step(0, 20, dataLong: 1, dataLong2: 20, flags: FlagReverse, dataInt2: 1)], patrol: true);
        main.Wave();
        Assert.Equal(MovementGeneratorType.Random, main.Elly.Motion.DefaultType);
        Assert.Empty(main.Elly.Motion.ActiveTypes);
    }

    [Fact]
    public void Movement_Waypoint_WithThePassTargetFlag_RunsWithATarget_AndIsSkippedOnlyWithout()
    {
        // cmangos ScriptMgr.cpp:2318-2330: datalong3 & 0x1 hands the target to the path and skips the command only when there is no target.
        (uint, uint, CreatureWaypoint)[] path = [(WolfEntry, 1u, new CreatureWaypoint(1, 15, 0, 83.5f, 100, 0)), (WolfEntry, 1u, new CreatureWaypoint(2, 15, 15, 83.5f, 100, 0))];
        using Town withTarget = Start([Step(0, 20, dataLong: 2, dataLong2: 1, dataLong3: 1, flags: FlagReverse)], entryPaths: path);
        withTarget.Wave();
        Assert.Equal(MovementGeneratorType.Waypoint, withTarget.Elly.Motion.DefaultType);

        using Town noTarget = Start([Step(0, 20, dataLong: 2, dataLong2: 1, dataLong3: 1)], entryPaths: path);
        Assert.True(noTarget.System.StartRelayScript(Relay, noTarget.Elly, target: null));
        Run(noTarget.World, 100);
        Assert.Equal(MovementGeneratorType.Idle, noTarget.Elly.Motion.DefaultType);
    }

    [Fact]
    public void Movement_Waypoint_WalksTheEntryPathOfTheGivenId()
    {
        using Town t = Start([Step(0, 20, dataLong: 2, dataLong2: 1, flags: FlagReverse)],
            entryPaths: [(WolfEntry, 1u, new CreatureWaypoint(1, 15, 0, 83.5f, 100, 0)), (WolfEntry, 1u, new CreatureWaypoint(2, 15, 15, 83.5f, 100, 0))]);

        t.Wave();
        Run(t.World, 8000);

        Assert.Equal(MovementGeneratorType.Waypoint, t.Elly.Motion.DefaultType);
        Assert.True(Distance2D(t.Elly, 0, 0) > 5f);
    }

    // --- 35 SEND_AI_EVENT and EVENT_T_RECEIVE_AI_EVENT --------------------------------------

    [Fact]
    public void SendAiEvent_WithARadius_ReachesEveryLivingCreatureInRange_WithTheSenderFilter()
    {
        // Elly (reverse: the source) sends custom event A (5) around with 30 yd; the buddy at 5 yd hears it, the one at 60 yd does not,
        // and Elly herself does (AnyUnitInObjectRangeCheck has no self exclusion); a row that wants another sender does not fire.
        using Town t = Start([Step(0, 35, dataLong: 5, dataLong2: 30, flags: FlagReverse)],
            more: [Spawn(2, BuddyEntry, 5, 0), Spawn(3, FarEntry, 60, 0)],
            rows: [ReceiveRow(2, BuddyEntry, 5, 0, 7001), ReceiveRow(3, FarEntry, 5, 0, 7002), ReceiveRow(4, WolfEntry, 5, 0, 7003), ReceiveRow(5, WolfEntry, 5, (int)FarEntry, 7004)]);

        t.Wave();

        Assert.Equal([7001u, 7003u], t.Spells.Casts.Select(c => c.Spell).Order());
    }

    [Fact]
    public void SendAiEvent_WithoutARadius_GoesToTheTargetCreature_OrToTheSourceItselfForAPlayerTarget()
    {
        // The buddy takes the source seat; Elly (the target) receives event B (6) from the buddy.
        using Town toTarget = Start([Step(0, 35, dataLong: 6, buddy: BuddyEntry, radius: 12)],
            more: [Spawn(2, BuddyEntry, 4, 0)],
            rows: [ReceiveRow(2, WolfEntry, 6, (int)BuddyEntry, 7011), ReceiveRow(3, BuddyEntry, 6, 0, 7012)]);
        toTarget.Wave();
        Assert.Equal([7011u], toTarget.Spells.Casts.Select(c => c.Spell));

        // A creature source with a player target receives the event itself, the player as the sender.
        using Town self = Start([Step(0, 35, dataLong: 6, flags: FlagReverse)], rows: [ReceiveRow(2, WolfEntry, 6, 0, 7021), ReceiveRow(3, WolfEntry, 8, 0, 7022)]);
        self.Wave();
        Assert.Equal([7021u], self.Spells.Casts.Select(c => c.Spell));
    }

    [Fact]
    public void SendAiEvent_CallAssistance_AlsoMakesTheHelpersAttackTheInvoker()
    {
        // cmangos UnitAI::SendAIEventAround (AI/BaseAI/UnitAI.cpp:643-650): after ReceiveAIEvent, AI_EVENT_CALL_ASSISTANCE runs
        // CreatureAI::HandleAssistanceCall (AI/BaseAI/CreatureAI.cpp:224-233): a helper that may assist the sender attacks the invoker.
        // AI_EVENT_CALL_ASSISTANCE is 13 (AI/BaseAI/AIDefines.h:40); the "type 0" in the UnitAI.cpp:647 comment is stale.
        using Town t = Start([Step(0, 35, dataLong: 13, dataLong2: 30, flags: FlagReverse)], more: [Spawn(2, BuddyEntry, 5, 0)], fightingBuddy: true);
        Creature buddy = Assert.Single(t.OfEntry(BuddyEntry));
        Run(t.World, 200);
        Assert.Null(buddy.Combat.Victim);

        t.Wave();
        Run(t.World, 100);

        Assert.Same(t.Player, buddy.Combat.Victim);
    }

    [Fact]
    public void SendAiEvent_JustDied_IsOnlyReceived_TheHelpersDoNotAnswerItAsACallForAssistance()
    {
        // AI_EVENT_JUST_DIED (0) goes to the same assisting creatures (AnyAssistCreatureInRangeCheck), but only CALL_ASSISTANCE (13) runs
        // HandleAssistanceCall (UnitAI.cpp:648); the buddy's RECEIVE_AI_EVENT row for 0 proves the event arrived.
        using Town t = Start([Step(0, 35, dataLong: 0, dataLong2: 30, flags: FlagReverse)], more: [Spawn(2, BuddyEntry, 5, 0)],
            rows: [ReceiveRow(2, BuddyEntry, 0, 0, 7041)], fightingBuddy: true);
        Creature buddy = Assert.Single(t.OfEntry(BuddyEntry));
        Run(t.World, 200);

        t.Wave();
        Run(t.World, 100);

        Assert.Equal([7041u], t.Spells.Casts.Select(c => c.Spell));
        Assert.Null(buddy.Combat.Victim);
    }

    [Fact]
    public void AllEligibleBuddies_RunTheStepOnceForEachBuddy()
    {
        // classic-db relay 589501: data_flags 512, buddy 6748 within 50 yd: every buddy takes the source seat and sends event A to the
        // target (no radius, a creature target). Two buddies are within 30 yd of the searcher (the player at (0, 10)), one is not.
        using Town t = Start([Step(0, 35, dataLong: 5, flags: FlagAllEligible, buddy: BuddyEntry, radius: 30)],
            more: [Spawn(2, BuddyEntry, 4, 0), Spawn(3, BuddyEntry, -4, 0), Spawn(4, BuddyEntry, 80, 0)],
            rows: [ReceiveRow(2, WolfEntry, 5, (int)BuddyEntry, 7031)]);

        t.Wave();

        Assert.Equal(2, t.Spells.Casts.Count(c => c.Spell == 7031));
    }
}
