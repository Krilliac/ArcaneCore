using System.Buffers.Binary;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests;

/// <summary>Creature objects, grids, visibility, life cycle and movement (docs/areas/creatures.md).</summary>
public sealed class CreatureTests
{
    // --- fields ---------------------------------------------------------------------------

    [Fact]
    public void Creature_FieldsFollowTemplate_ModelAndAddon()
    {
        CreatureTemplate template = Template(GuardEntry, t =>
        {
            t.Name = "Stormwind City Guard";
            t.MinLevel = 55;
            t.MaxLevel = 55;
            t.DisplayIds = [3167];
            t.Faction = 11;
            t.Rank = 1;
            t.MinLevelHealth = 3052;
            t.MaxLevelHealth = 3052;
            t.NpcFlags = 0x1;
            t.UnitFlags = 0x1000;
            t.Scale = 1.2f;
        });
        CreatureSpawn spawn = Spawn(79, GuardEntry, -8900, -110);
        CreatureContent content = Content([template], [spawn],
            models: [new CreatureModelInfo(3167, 0.5f, 2.0f, 0, 0)],
            addons: [new CreatureAddon(79, 2410, 1, 2, 333)]);

        var creature = new Creature(spawn.Guid, template, spawn, content, new Random(1));

        Assert.Equal(ObjectGuid.WithEntry(HighGuid.Unit, GuardEntry, 79), creature.Guid);
        Assert.Equal(TypeId.Unit, creature.TypeId);
        Assert.Equal(TypeMask.Object | TypeMask.Unit, creature.GetUInt32(UpdateFields.ObjectFieldType));
        Assert.Equal(GuardEntry, creature.GetUInt32(UpdateFields.ObjectFieldEntry));
        Assert.Equal(1.2f, creature.GetFloat(UpdateFields.ObjectFieldScaleX));
        Assert.Equal(3167u, creature.DisplayId);
        Assert.Equal(3167u, creature.NativeDisplayId);
        Assert.Equal(55, creature.Level);
        Assert.Equal(3052u, creature.Health);
        Assert.Equal(3052u, creature.MaxHealth);
        Assert.Equal(11u, creature.FactionTemplate);
        Assert.Equal(0x1u, creature.NpcFlags);

        // Template unit flags + UNIT_FLAG_PLUS_MOB for an elite (vmangos IsPlusMob).
        Assert.Equal(0x1000u | (uint)UnitFlags.PlusMob, creature.GetUInt32(UpdateFields.UnitFieldFlags));

        // BYTES_0: race 0, class warrior, gender from model (0 = male), rage.
        Assert.Equal((Race)0, creature.Race);
        Assert.Equal(Class.Warrior, creature.Class);
        Assert.Equal(Gender.Male, creature.Gender);
        Assert.Equal(PowerType.Rage, creature.PowerType);
        Assert.Equal(Creature.CreateRage, creature.GetUInt32(UpdateFields.UnitFieldMaxpower2));
        Assert.Equal(0u, creature.GetUInt32(UpdateFields.UnitFieldPower2));

        // Model sizes × scale.
        Assert.Equal(0.5f * 1.2f, creature.GetFloat(UpdateFields.UnitFieldBoundingradius), 5);
        Assert.Equal(2.0f * 1.2f, creature.GetFloat(UpdateFields.UnitFieldCombatreach), 5);

        // creature_addon: mount, stand state, sheath, emote.
        Assert.Equal(2410u, creature.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        Assert.Equal((StandState)1, creature.StandState);
        Assert.Equal(2, creature.GetByte(UpdateFields.UnitFieldBytes2, 0));
        Assert.Equal(0x10, creature.GetByte(UpdateFields.UnitFieldBytes2, 1));
        Assert.Equal(333u, creature.GetUInt32(UpdateFields.UnitNpcEmotestate));

        Assert.Equal(1.0f, creature.GetFloat(UpdateFields.UnitModCastSpeed));
        Assert.Equal(2000u, creature.GetUInt32(UpdateFields.UnitFieldBaseattacktime));
        Assert.Equal(new CreatureHome(-8900, -110, 83.5f, 1.5f), creature.Home);
        Assert.Equal((-8900f, -110f), (creature.X, creature.Y));
    }

    [Fact]
    public void Creature_ManaUser_GetsMana_RogueGetsEnergy_LevelAndHealthInterpolate()
    {
        CreatureTemplate caster = Template(1, t =>
        {
            t.UnitClass = 8;
            t.MinLevelMana = 100;
            t.MaxLevelMana = 100;
        });
        var mage = new Creature(1, caster, null, CreatureContent.Empty, new Random(1));
        Assert.Equal(PowerType.Mana, mage.PowerType);
        Assert.Equal(100u, mage.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Equal(100u, mage.GetUInt32(UpdateFields.UnitFieldMaxpower1));
        Assert.Equal((Gender)2, mage.Gender); // no model info: GENDER_NONE (2)

        var rogue = new Creature(2, Template(2, t => t.UnitClass = 4), null, CreatureContent.Empty, new Random(1));
        Assert.Equal(PowerType.Energy, rogue.PowerType);
        Assert.Equal(Creature.CreateEnergy, rogue.GetUInt32(UpdateFields.UnitFieldMaxpower4));

        CreatureTemplate ranged = Template(3, t =>
        {
            t.MinLevel = 10;
            t.MaxLevel = 20;
            t.MinLevelHealth = 100;
            t.MaxLevelHealth = 200;
        });
        for (int seed = 0; seed < 20; seed++)
        {
            var c = new Creature(3, ranged, null, CreatureContent.Empty, new Random(seed));
            Assert.InRange(c.Level, (byte)10, (byte)20);
            Assert.Equal(100u + (uint)((c.Level - 10) / 10f * 100), c.MaxHealth);
        }
    }

    [Fact]
    public void ChooseDisplayId_UsesProbabilities_EqualChance_OrTheBox()
    {
        Assert.Equal(Creature.DisplayIdBox, Creature.ChooseDisplayId(Template(1, t => t.DisplayIds = []), new Random(1)));
        Assert.Equal(Creature.DisplayIdBox, Creature.ChooseDisplayId(Template(1, t => t.DisplayIds = [0, 0]), new Random(1)));

        // Only the weighted id can come out.
        CreatureTemplate weighted = Template(1, t =>
        {
            t.DisplayIds = [10, 20, 30];
            t.DisplayProbabilities = [0, 100, 0];
        });
        for (int seed = 0; seed < 20; seed++)
        {
            Assert.Equal(20u, Creature.ChooseDisplayId(weighted, new Random(seed)));
        }

        // Equal chance among the leading non-zero ids: both appear, the id after the gap never.
        CreatureTemplate equal = Template(1, t => t.DisplayIds = [10, 20, 0, 40]);
        var seen = new HashSet<uint>();
        var random = new Random(5);
        for (int i = 0; i < 200; i++)
        {
            seen.Add(Creature.ChooseDisplayId(equal, random));
        }

        Assert.Equal([10u, 20u], seen.Order());
    }

    [Fact]
    public void ComputeGrid_MatchesVMangosGridPair()
    {
        Assert.Equal(new GridCoord(32, 32), CreatureMapSystem.ComputeGrid(0, 0));
        Assert.Equal(new GridCoord(32, 32), CreatureMapSystem.ComputeGrid(533.0f, 533.0f));
        Assert.Equal(new GridCoord(33, 31), CreatureMapSystem.ComputeGrid(533.4f, -0.1f));

        // Northshire Abbey (-8949.95, -132.49): ((-8949.95 - 266.67) / 533.33 + 32.5) = 15.2; y → 31.7.
        Assert.Equal(new GridCoord(15, 31), CreatureMapSystem.ComputeGrid(-8949.95f, -132.49f));
        Assert.Equal(new GridCoord(0, 63), CreatureMapSystem.ComputeGrid(-1e6f, 1e6f));
    }

    // --- grids & visibility ----------------------------------------------------------------

    [Fact]
    public void Grid_LoadsAroundPlayers_AndCreatureIsCreatedWithCreateObject()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0), Spawn(2, WolfEntry, 5000, 5000)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content);

        world.RunTick(50);
        Assert.Equal(0, system.LoadedGridCount); // nobody there: nothing loaded

        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        session.Clear();
        world.RunTick(50);

        Assert.True(system.IsGridLoaded(30, 0));
        Assert.False(system.IsGridLoaded(5000, 5000));
        Creature wolf = Assert.Single(system.Creatures);
        Assert.Same(map, wolf.Map);

        ParsedBlock create = Assert.Single(DrainBlocks(session));
        Assert.Equal(ObjectUpdateType.CreateObject, create.Type);
        Assert.Equal(wolf.Guid.Value, create.Guids[0]);
        Assert.Equal(TypeId.Unit, create.TypeId);
        Assert.Equal(WolfEntry, create.Values[UpdateFields.ObjectFieldEntry]);
        Assert.Equal(55u, create.Values[UpdateFields.UnitFieldHealth]);
        Assert.Equal(30f, create.Movement!.Value.X);
        Assert.Contains(wolf.Guid, player.VisibleObjects);

        // Nothing more while nothing changes.
        world.RunTick(50);
        Assert.Empty(DrainBlocks(session));
    }

    [Fact]
    public void LeavingRange_SendsOutOfRange_ComingBackCreatesAgain()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);
        session.Clear();

        player.Relocate(250, 0, 83.5f, 0, 0);
        world.RunTick(50);
        ParsedBlock gone = Assert.Single(DrainBlocks(session));
        Assert.Equal(ObjectUpdateType.OutOfRangeObjects, gone.Type);
        Assert.Equal([wolf.Guid.Value], gone.Guids);
        Assert.DoesNotContain(wolf.Guid, player.VisibleObjects);

        player.Relocate(0, 0, 83.5f, 0, 0);
        world.RunTick(50);
        Assert.Equal(ObjectUpdateType.CreateObject, Assert.Single(DrainBlocks(session)).Type);
    }

    [Fact]
    public void VisibilityEdge_UsesGreyDistance()
    {
        // Distance 100 + radii: a creature at 100.5 yd is created (radii 0.389 + 0.389)…
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 100.5f, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);
        Assert.Contains(wolf.Guid, player.VisibleObjects);

        // …and stays visible 0.5 yd further out thanks to the grey distance.
        player.Relocate(-0.9f, 0, 83.5f, 0, 0);
        world.RunTick(50);
        Assert.Contains(wolf.Guid, player.VisibleObjects);
        player.Relocate(-2.0f, 0, 83.5f, 0, 0);
        world.RunTick(50);
        Assert.DoesNotContain(wolf.Guid, player.VisibleObjects);
    }

    [Fact]
    public void PlayerRemoval_DropsTracking_AndReentryCreatesAgain()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0)]);
        (WorldRuntime world, _, _) = CreateSystem(content);
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        world.RemovePlayer(player);
        world.RunTick(50);

        world.AddPlayer(player);
        session.Clear();
        world.RunTick(50);
        Assert.Equal(ObjectUpdateType.CreateObject, Assert.Single(DrainBlocks(session)).Type);
    }

    [Fact]
    public void Grid_UnloadsAfterDelay_KeepingRespawnTimes()
    {
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 1 };
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0, respawnSeconds: 600)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content, options);
        map.Grids.Options.GridCleanUpDelayMs = 60_000;
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);
        system.KillCreature(wolf);
        long respawnAt = wolf.RespawnAtMs;
        Assert.Equal(system.ClockMs + 600_000, respawnAt);

        // Walk far away: the grid unloads after the delay and remembers the respawn time.
        player.Relocate(5000, 5000, 83.5f, 0, 0);
        world.RunTick(6000); // Active -> Idle, after the map's expiry/10 activity check
        world.RunTick(1);    // Idle -> Removal with the canonical map expiry
        world.RunTick(60_000);

        Assert.False(system.IsGridLoaded(30, 0));
        Assert.DoesNotContain(system.Creatures, c => c.Spawn?.Guid == 1);
        Assert.Null(wolf.Map);
        Assert.Equal(respawnAt, system.PendingRespawnAt(1));

        // Coming back reloads it with the creature still dead (invisible).
        player.Relocate(0, 0, 83.5f, 0, 0);
        session.Clear();
        world.RunTick(50);
        Creature reloaded = Assert.Single(system.Creatures, c => c.Spawn?.Guid == 1);
        Assert.NotSame(wolf, reloaded);
        Assert.Equal(CreatureDeathState.Dead, reloaded.DeathState);
        Assert.DoesNotContain(reloaded.Guid, player.VisibleObjects);
        Assert.Null(system.PendingRespawnAt(1));
    }

    // --- death & respawn -------------------------------------------------------------------

    [Fact]
    public void Kill_Corpse_Decay_Respawn_FollowTheTimers()
    {
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 2 };
        CreatureContent content = Content([Template(configure: t => t.NpcFlags = 0x2)], [Spawn(1, WolfEntry, 30, 0, respawnSeconds: 5)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content, options);
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);
        session.Clear();

        system.KillCreature(wolf);
        Assert.Equal(CreatureDeathState.Corpse, wolf.DeathState);
        world.RunTick(50);

        // The viewer gets the death as a values update (health 0, npc flags cleared).
        ParsedBlock values = Assert.Single(DrainBlocks(session));
        Assert.Equal(ObjectUpdateType.Values, values.Type);
        Assert.Equal(0u, values.Values[UpdateFields.UnitFieldHealth]);
        Assert.Equal(0u, values.Values[UpdateFields.UnitNpcFlags]);

        // Corpse decays after 2 s: map removal, back home, invisible.
        for (int i = 0; i < 40; i++)
        {
            world.RunTick(50);
        }

        Assert.Equal(CreatureDeathState.Dead, wolf.DeathState);
        var removed = new List<(WorldOpcode Opcode, byte[] Payload)>();
        Assert.Empty(DrainBlocks(session, removed));
        byte[] destroyed = Assert.Single(removed, p => p.Opcode == WorldOpcode.SmsgDestroyObject).Payload;
        Assert.Equal(wolf.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(destroyed));
        Assert.DoesNotContain(wolf.Guid, player.VisibleObjects);

        // Respawn 5 s after death: alive, full health, flags back, created again.
        for (int i = 0; i < 60; i++)
        {
            world.RunTick(50);
        }

        Assert.Equal(CreatureDeathState.Alive, wolf.DeathState);
        Assert.Equal(55u, wolf.Health);
        Assert.Equal(0x2u, wolf.NpcFlags);
        ParsedBlock back = Assert.Single(DrainBlocks(session));
        Assert.Equal(ObjectUpdateType.CreateObject, back.Type);
        Assert.Equal(55u, back.Values[UpdateFields.UnitFieldHealth]);
    }

    [Fact]
    public void RespawnTimeShorterThanCorpse_RemovesCorpseEarly()
    {
        // vmangos Creature::Update CORPSE: a DB spawn whose respawn time passed loses its corpse.
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 300 };
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0, respawnSeconds: 1)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content, options);
        var player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);
        system.KillCreature(wolf);
        for (int i = 0; i < 21; i++)
        {
            world.RunTick(50);
        }

        Assert.NotEqual(CreatureDeathState.Corpse, wolf.DeathState);
        world.RunTick(50);
        Assert.Equal(CreatureDeathState.Alive, wolf.DeathState);
    }

    [Fact]
    public void CorpseDecay_DefaultsByRank_TemplateOverrideOnlyWhenSwitchedOn()
    {
        var options = new CreatureOptions();
        Creature Make(uint rank, uint decay = 0) => new(1, Template(1, t =>
        {
            t.Rank = rank;
            t.CorpseDecaySeconds = decay;
        }), null, CreatureContent.Empty, new Random(1));

        Assert.Equal(300u, Make(0).CorpseDecaySeconds(options));
        Assert.Equal(600u, Make(1).CorpseDecaySeconds(options));
        Assert.Equal(1200u, Make(2).CorpseDecaySeconds(options));
        Assert.Equal(3600u, Make(3).CorpseDecaySeconds(options));
        Assert.Equal(900u, Make(4).CorpseDecaySeconds(options));
        Assert.Equal(600u, Make(1, 42).CorpseDecaySeconds(options)); // vmangos ignores the cmangos CorpseDecay column (Creature.cpp:1326-1343)
        options.Respawn.HonorTemplateCorpseDecay = true;
        Assert.Equal(42u, Make(1, 42).CorpseDecaySeconds(options));
    }

    [Fact]
    public void SpawnTemporary_UsesCreateObject2_ThenCreateObjectForLaterViewers()
    {
        CreatureContent content = Content([Template()], []);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        var a = new FakeSession(1);
        Player alice = TestWorld.CreatePlayer(1, 0, 0, a);
        world.AddPlayer(alice);
        world.RunTick(50);
        a.Clear();

        Creature temp = system.SpawnTemporary(Template(), 10, 10, 83.5f, 0);
        world.RunTick(50);
        ParsedBlock created = Assert.Single(DrainBlocks(a));
        Assert.Equal(ObjectUpdateType.CreateObject2, created.Type);
        Assert.Equal(temp.Guid.Value, created.Guids[0]);
        Assert.Null(temp.Spawn);

        var b = new FakeSession(2);
        Player bob = TestWorld.CreatePlayer(2, 0, 0, b);
        world.AddPlayer(bob);
        b.Clear();
        world.RunTick(50);
        Assert.Contains(DrainBlocks(b), blk => blk.Type == ObjectUpdateType.CreateObject && blk.Guids[0] == temp.Guid.Value);

        // A temporary creature does not respawn: after its corpse it is gone.
        system.KillCreature(temp);
        system.ForceRespawn(temp);
        Assert.Null(system.FindCreature(temp.Guid));
    }

    // --- movement ---------------------------------------------------------------------------

    [Fact]
    public void RandomMovement_FirstMoveAfterOneSecond_WithinWanderDistance()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0, movementType: 1, wander: 5)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);
        session.Clear();

        var others = new List<(WorldOpcode Opcode, byte[] Payload)>();
        for (int i = 0; i < 18; i++)
        {
            world.RunTick(50);
        }

        DrainBlocks(session, others);
        Assert.DoesNotContain(others, p => p.Opcode == WorldOpcode.SmsgMonsterMove);

        world.RunTick(50);
        DrainBlocks(session, others);
        MonsterMove move = ParseMonsterMove(Assert.Single(others, p => p.Opcode == WorldOpcode.SmsgMonsterMove).Payload);
        Assert.Equal(wolf.Guid.Value, move.Guid);
        Assert.Equal((30f, 0f, 83.5f), (move.StartX, move.StartY, move.StartZ));
        Assert.Equal(MonsterMoveType.Normal, move.Type);
        Assert.Equal(0u, move.Flags); // walking
        Assert.Equal(1u, move.PointCount);
        float dist = MathF.Sqrt(((move.DestX - 30) * (move.DestX - 30)) + (move.DestY * move.DestY));
        Assert.InRange(dist, 0, 5.0001f);
        Assert.Equal((uint)Math.Max(1, MathF.Round(dist / 2.5f * 1000)), move.DurationMs);
        Assert.True(wolf.IsMoving);

        // It arrives after the duration and stays inside the wander circle.
        for (int i = 0; i <= (move.DurationMs / 50) + 1; i++)
        {
            world.RunTick(50);
        }

        Assert.Equal((move.DestX, move.DestY), (wolf.X, wolf.Y));
    }

    [Fact]
    public void WaypointMovement_WalksNodes_WaitsAndFaces_ThenLoops()
    {
        CreatureWaypoint[] path =
        [
            new(1, 40, 0, 83.5f, 100, 0),
            new(2, 40, 10, 83.5f, 2.0f, 500),
        ];
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0, movementType: 2)],
            waypoints: path.Select(p => (1u, p)));
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);
        Assert.True(wolf.IsMoving); // starts toward node 1 at once
        Assert.Equal((40f, 0f), (wolf.Spline!.EndX, wolf.Spline.EndY));

        // 10 yd at 2.5 yd/s = 4 s; then node 2 (with a wait → faces 2.0).
        var others = new List<(WorldOpcode Opcode, byte[] Payload)>();
        for (int i = 0; i < 82; i++)
        {
            world.RunTick(50);
        }

        DrainBlocks(session, others);
        List<MonsterMove> moves = [.. others.Where(p => p.Opcode == WorldOpcode.SmsgMonsterMove).Select(p => ParseMonsterMove(p.Payload))];

        // The client got the create mid-move, so first the catch-up toward node 1, then node 2.
        Assert.Equal(2, moves.Count);
        Assert.Equal((40f, 0f), (moves[0].DestX, moves[0].DestY));
        MonsterMove toSecond = moves[1];
        Assert.Equal((40f, 10f), (toSecond.DestX, toSecond.DestY));
        Assert.Equal(MonsterMoveType.FacingAngle, toSecond.Type);
        Assert.Equal(2.0f, toSecond.FacingAngle);
        Assert.Equal((uint)SplineFlags.None, toSecond.Flags); // facing flag is masked out
        Assert.Equal(4000u, toSecond.DurationMs);

        // Arrive (4 s), wait 0.5 s, then loop back to node 1.
        others.Clear();
        for (int i = 0; i < 81; i++)
        {
            world.RunTick(50);
        }

        DrainBlocks(session, others);
        Assert.DoesNotContain(others, p => p.Opcode == WorldOpcode.SmsgMonsterMove);
        Assert.Equal(2.0f, wolf.Orientation);
        for (int i = 0; i < 11; i++)
        {
            world.RunTick(50);
        }

        DrainBlocks(session, others);
        MonsterMove back = ParseMonsterMove(Assert.Single(others, p => p.Opcode == WorldOpcode.SmsgMonsterMove).Payload);
        Assert.Equal((40f, 0f), (back.DestX, back.DestY));
        Assert.Equal(MonsterMoveType.Normal, back.Type);
    }

    [Fact]
    public void ViewerArrivingMidMove_GetsCreateThenCatchUpMove()
    {
        CreatureWaypoint[] path = [new(1, 80, 0, 83.5f, 100, 0), new(2, 30, 0, 83.5f, 100, 0)];
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0, movementType: 2)],
            waypoints: path.Select(p => (1u, p)));
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        var a = new FakeSession(1);
        world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, a));
        world.RunTick(50);
        world.RunTick(1000);
        Creature wolf = Assert.Single(system.Creatures);
        Assert.True(wolf.IsMoving);

        var b = new FakeSession(2);
        Player bob = TestWorld.CreatePlayer(2, 0, 0, b);
        float createX = wolf.X; // AddPlayer queues the shared map's current object snapshot
        world.AddPlayer(bob);
        b.Clear();
        world.RunTick(50);
        var others = new List<(WorldOpcode Opcode, byte[] Payload)>();
        ParsedBlock create = Assert.Single(DrainBlocks(b, others), blk => blk.Guids[0] == wolf.Guid.Value);
        Assert.Equal(createX, create.Movement!.Value.X); // position when the create was queued
        Assert.DoesNotContain(others, p => p.Opcode == WorldOpcode.SmsgMonsterMove);

        world.RunTick(50);
        DrainBlocks(b, others);
        MonsterMove catchUp = ParseMonsterMove(Assert.Single(others, p => p.Opcode == WorldOpcode.SmsgMonsterMove).Payload);
        Assert.Equal(wolf.Spline!.Id, catchUp.SplineId);
        Assert.Equal(80f, catchUp.DestX);
        Assert.Equal(wolf.Spline.DurationMs - wolf.Spline.ElapsedMs(system.ClockMs), catchUp.DurationMs);
    }

    [Fact]
    public void Kill_WhileMoving_SendsStop()
    {
        CreatureWaypoint[] path = [new(1, 80, 0, 83.5f, 100, 0)];
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0, movementType: 2)],
            waypoints: path.Select(p => (1u, p)));
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        var session = new FakeSession();
        world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, session));
        world.RunTick(50);
        world.RunTick(500);
        Creature wolf = Assert.Single(system.Creatures);
        session.Clear();

        system.KillCreature(wolf);
        Assert.False(wolf.IsMoving);
        (WorldOpcode opcode, byte[] payload) = session.Next();
        Assert.Equal(WorldOpcode.SmsgMonsterMove, opcode);
        MonsterMove stop = ParseMonsterMove(payload);
        Assert.Equal(MonsterMoveType.Stop, stop.Type);
        Assert.Equal((wolf.X, wolf.Y), (stop.StartX, stop.StartY));
    }

    [Fact]
    public void MonsterMovePackets_MatchTheVMangosLayout()
    {
        ObjectGuid guid = ObjectGuid.WithEntry(HighGuid.Unit, 69, 1);
        byte[] move = CreatureMovePackets.BuildMove(guid, 1, 2, 3, 7, finalOrientation: null, run: true, 1500, 4, 5, 6);
        var w = new PacketWriter();
        w.WritePackedGuid(guid.Value);
        w.WriteSingle(1);
        w.WriteSingle(2);
        w.WriteSingle(3);
        w.WriteUInt32(7);
        w.WriteByte(0);              // MonsterMoveNormal
        w.WriteUInt32(0x100);        // Runmode
        w.WriteUInt32(1500);
        w.WriteUInt32(1);
        w.WriteSingle(4);
        w.WriteSingle(5);
        w.WriteSingle(6);
        Assert.Equal(w.ToArray(), move);

        byte[] stop = CreatureMovePackets.BuildStop(guid, 1, 2, 3, 8);
        var s = new PacketWriter();
        s.WritePackedGuid(guid.Value);
        s.WriteSingle(1);
        s.WriteSingle(2);
        s.WriteSingle(3);
        s.WriteUInt32(8);
        s.WriteByte(1);              // MonsterMoveStop, nothing after it (vmangos)
        Assert.Equal(s.ToArray(), stop);
    }
}
