using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// GO1a: retail (vmangos) fidelity primitives of the game object map system: the per-type data helpers,
/// auto-close in seconds * 0x10000 on a whole-second clock, GO_FLAG_NODESPAWN, random respawn delays and
/// SMSG_GAMEOBJECT_DESPAWN_ANIM. References: D:\refs\vmangos\src\game\Objects\GameObjectDefines.h:536-668,
/// GameObject.cpp:572-590, 656, 698-711, 985-1022, 1370-1383, 2467-2471.
/// </summary>
public sealed class GameObjectFidelityPrimitivesTests
{
    private const uint DoorEntry = 200;
    private const uint ButtonEntry = 201;
    private const uint GooberEntry = 202;
    private const uint ChestEntry = 203;
    private const uint ConsumableGooberEntry = 204;
    private const uint ImmuneGooberEntry = 205;

    private sealed class Rig(WorldRuntime world, GameObjectMapSystem system)
    {
        public WorldRuntime World { get; } = world;

        public GameObjectMapSystem System { get; } = system;

        public (Player Player, FakeSession Session) Join(uint guid = 1)
        {
            (Player player, FakeSession session) = Player(guid, 0, 0);
            World.AddPlayer(player);
            World.RunTick(50);
            return (player, session);
        }

        public GameObject Spawned(uint spawnGuid) => System.GameObjects.Single(g => g.Spawn!.Guid == spawnGuid);
    }

    private static Rig CreateRig(GameObjectTemplate[] templates, GameObjectSpawn[] spawns, int seed = 5, Action<GameObjectMapSystem>? configure = null)
    {
        var content = new GameObjectContent(templates, spawns, [], [], []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, content) { Random = new Random(seed) };
        configure?.Invoke(system);
        map.AddUpdater(system);
        return new Rig(world, system);
    }

    private static GameObjectTemplate[] DefaultTemplates() =>
    [
        GoTemplate(DoorEntry, GameObjectType.Door, (2, 3 * 65536)),
        GoTemplate(ButtonEntry, GameObjectType.Button, (2, 2 * 65536)),
        GoTemplate(GooberEntry, GameObjectType.Goober, (3, 2 * 65536)),
        GoTemplate(ChestEntry, GameObjectType.Chest, (1, 500)),
        GoTemplate(ConsumableGooberEntry, GameObjectType.Goober, (5, 1)),
        GoTemplate(ImmuneGooberEntry, GameObjectType.Goober, (11, 1)),
    ];

    // --- GameObjectInfoView ----------------------------------------------------------------------

    [Fact]
    public void AutoCloseSeconds_DividesTheRawColumnBy0x10000_PerType()
    {
        Assert.Equal(3u, GoTemplate(1, GameObjectType.Door, (2, 196608)).AutoCloseSeconds());
        Assert.Equal(3u, GoTemplate(1, GameObjectType.Button, (2, 196608)).AutoCloseSeconds());
        Assert.Equal(3u, GoTemplate(1, GameObjectType.Goober, (3, 196608)).AutoCloseSeconds());
        Assert.Equal(3u, GoTemplate(1, GameObjectType.Trap, (6, 196608)).AutoCloseSeconds());
        Assert.Equal(3u, GoTemplate(1, GameObjectType.Transport, (2, 196608)).AutoCloseSeconds());
        Assert.Equal(3u, GoTemplate(1, GameObjectType.AreaDamage, (5, 196608)).AutoCloseSeconds());
        Assert.Equal(0u, GoTemplate(1, GameObjectType.Door, (2, 3000)).AutoCloseSeconds()); // the old millisecond reading
        Assert.Equal(0u, GoTemplate(1, GameObjectType.Chest, (2, 196608)).AutoCloseSeconds());
        Assert.Equal(0u, GoTemplate(1, GameObjectType.Door, (3, 196608)).AutoCloseSeconds()); // door data3 is noDamageImmune
        Assert.Equal(0xFFFF0000u / 0x10000, GoTemplate(1, GameObjectType.Trap, (6, 0xFFFF0000)).AutoCloseSeconds()); // unsigned division as vmangos
    }

    [Fact]
    public void InfoView_ReadsTheVmangosColumnOfEachType()
    {
        Assert.Equal(7u, GoTemplate(1, GameObjectType.Trap, (5, 7)).CooldownSeconds());
        Assert.Equal(8u, GoTemplate(1, GameObjectType.Goober, (6, 8)).CooldownSeconds());
        Assert.Equal(0u, GoTemplate(1, GameObjectType.Door, (5, 9)).CooldownSeconds());

        Assert.Equal(4u, GoTemplate(1, GameObjectType.Trap, (4, 4)).Charges());
        Assert.Equal(5u, GoTemplate(1, GameObjectType.GuardPost, (1, 5)).Charges());
        Assert.Equal(6u, GoTemplate(1, GameObjectType.SpellCaster, (1, 6)).Charges());
        Assert.Equal(0u, GoTemplate(1, GameObjectType.Chest, (1, 6)).Charges());

        Assert.Equal(11u, GoTemplate(1, GameObjectType.Button, (3, 11)).LinkedTrapEntry());
        Assert.Equal(12u, GoTemplate(1, GameObjectType.Chest, (7, 12)).LinkedTrapEntry());
        Assert.Equal(13u, GoTemplate(1, GameObjectType.SpellFocus, (2, 13)).LinkedTrapEntry());
        Assert.Equal(14u, GoTemplate(1, GameObjectType.Goober, (12, 14)).LinkedTrapEntry());

        Assert.True(GoTemplate(1, GameObjectType.Chest, (3, 1)).IsDespawnAtAction());
        Assert.True(GoTemplate(1, GameObjectType.Goober, (5, 1)).IsDespawnAtAction());
        Assert.False(GoTemplate(1, GameObjectType.Door, (3, 1)).IsDespawnAtAction());

        // GetDespawnPossibility: door data3, button data4, quest giver data5, goober data11, flag stand data5, everything else true.
        Assert.False(GoTemplate(1, GameObjectType.Door).DespawnPossibility());
        Assert.True(GoTemplate(1, GameObjectType.Door, (3, 1)).DespawnPossibility());
        Assert.True(GoTemplate(1, GameObjectType.Button, (4, 1)).DespawnPossibility());
        Assert.True(GoTemplate(1, GameObjectType.QuestGiver, (5, 1)).DespawnPossibility());
        Assert.True(GoTemplate(1, GameObjectType.Goober, (11, 1)).DespawnPossibility());
        Assert.False(GoTemplate(1, GameObjectType.Goober, (4, 1)).DespawnPossibility());
        Assert.True(GoTemplate(1, GameObjectType.Chest).DespawnPossibility());
        Assert.True(GoTemplate(1, GameObjectType.Generic).DespawnPossibility());

        Assert.True(GoTemplate(1, GameObjectType.Chest).CannotBeUsedUnderImmunity());
        Assert.True(GoTemplate(1, GameObjectType.Door, (3, 1)).CannotBeUsedUnderImmunity());
        Assert.False(GoTemplate(1, GameObjectType.Door).CannotBeUsedUnderImmunity());
        Assert.False(GoTemplate(1, GameObjectType.Mailbox).CannotBeUsedUnderImmunity());

        Assert.True(GoTemplate(1, GameObjectType.Mailbox).IsUsableMounted());
        Assert.True(GoTemplate(1, GameObjectType.QuestGiver, (8, 1)).IsUsableMounted());
        Assert.True(GoTemplate(1, GameObjectType.Text, (3, 1)).IsUsableMounted());
        Assert.True(GoTemplate(1, GameObjectType.Goober, (17, 1)).IsUsableMounted());
        Assert.True(GoTemplate(1, GameObjectType.SpellCaster, (3, 1)).IsUsableMounted());
        Assert.False(GoTemplate(1, GameObjectType.Door, (3, 1)).IsUsableMounted());
    }

    // --- auto-close on the whole-second clock ------------------------------------------------------

    [Fact]
    public void Door_WithThreeSecondRawColumn_ClosesAfterThreeToFourSeconds_NotThreeMinutes()
    {
        Rig rig = CreateRig(DefaultTemplates(), [GoSpawn(1, DoorEntry, 3, 0)]);
        (Player player, _) = rig.Join();
        GameObject door = rig.Spawned(1);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, door.Guid));
        Assert.Equal(GameObjectState.Active, door.State);
        rig.World.RunTick(2500);
        Assert.Equal(GameObjectState.Active, door.State);
        Assert.Equal(GameObjectLootState.Activated, door.LootState);
        rig.World.RunTick(500); // elapsed 3.0 s: m_cooldownTime < now is strict, still open
        Assert.Equal(GameObjectState.Active, door.State);
        rig.World.RunTick(1000); // elapsed 4.0 s: past the (R, R+1] window
        Assert.Equal(GameObjectState.Ready, door.State);
        Assert.False(door.Flags.HasFlag(GameObjectFlags.InUse));
        Assert.Equal(GameObjectLootState.Ready, door.LootState);
    }

    [Fact]
    public void Button_And_Goober_UseTheSameSecondsColumn()
    {
        Rig rig = CreateRig(DefaultTemplates(), [GoSpawn(1, ButtonEntry, 3, 0), GoSpawn(2, GooberEntry, -3, 0)]);
        (Player player, _) = rig.Join();
        GameObject button = rig.Spawned(1);
        GameObject goober = rig.Spawned(2);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, button.Guid));
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, goober.Guid));
        rig.World.RunTick(1900);
        Assert.Equal(GameObjectState.Active, button.State);
        Assert.Equal(GameObjectState.Active, goober.State);
        rig.World.RunTick(1200); // 3.1 s elapsed: both 2-second windows are over
        Assert.Equal(GameObjectState.Ready, button.State);
        Assert.Equal(GameObjectState.Ready, goober.State);
    }

    [Fact]
    public void Door_WithRawColumnBelow0x10000_NeverAutoCloses()
    {
        Rig rig = CreateRig([GoTemplate(DoorEntry, GameObjectType.Door, (2, 3000))], [GoSpawn(1, DoorEntry, 3, 0)]);
        (Player player, _) = rig.Join();
        GameObject door = rig.Spawned(1);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, door.Guid));
        rig.World.RunTick(10_000);
        Assert.Equal(GameObjectState.Active, door.State);
        Assert.Equal(GameObjectLootState.Activated, door.LootState);
    }

    // --- GO_FLAG_NODESPAWN -------------------------------------------------------------------------

    [Fact]
    public void NeverDespawningSpawns_CarryNoDespawn_InTheirCreateBlock()
    {
        Rig rig = CreateRig(DefaultTemplates(),
        [
            GoSpawn(1, DoorEntry, 3, 0),
            GoSpawn(2, GooberEntry, 4, 0),
            GoSpawn(3, ChestEntry, 5, 0),
            GoSpawn(4, ConsumableGooberEntry, 6, 0),
            GoSpawn(5, ImmuneGooberEntry, 7, 0),
            GoSpawn(6, DoorEntry, 8, 0, spawnTimeSeconds: -5),
        ]);
        (_, FakeSession session) = rig.Join();
        Dictionary<ulong, ParsedBlock> creates = DrainBlocks(session).Where(b => b.Type == ObjectUpdateType.CreateObject || b.Type == ObjectUpdateType.CreateObject2)
            .SelectMany(b => b.Guids.Select(g => (g, b))).ToDictionary(p => p.g, p => p.b);
        const uint noDespawn = (uint)GameObjectFlags.NoDespawn;

        Assert.True(rig.Spawned(1).NeverDespawns);
        Assert.Equal(noDespawn, creates[rig.Spawned(1).Guid.Value].Values[UpdateFields.GameobjectFlags]);
        Assert.Equal(noDespawn, creates[rig.Spawned(2).Guid.Value].Values[UpdateFields.GameobjectFlags]);
        Assert.False(rig.Spawned(3).NeverDespawns); // a chest answers despawn possibility true
        Assert.False(creates[rig.Spawned(3).Guid.Value].Values.ContainsKey(UpdateFields.GameobjectFlags));
        Assert.False(rig.Spawned(4).NeverDespawns); // consumable
        Assert.False(rig.Spawned(5).NeverDespawns); // noDamageImmune set
        Assert.False(rig.Spawned(6).NeverDespawns); // negative spawntimesecs: script spawned
    }

    [Fact]
    public void NeverDespawningSpawn_StaysInTheWorld_WhenDespawned_ButAChestDoesNot()
    {
        Rig rig = CreateRig(DefaultTemplates(), [GoSpawn(1, DoorEntry, 3, 0), GoSpawn(2, ChestEntry, 4, 0)]);
        rig.Join();
        GameObject door = rig.Spawned(1);
        GameObject chest = rig.Spawned(2);

        rig.System.Despawn(door);
        rig.System.Despawn(chest);

        Assert.True(door.IsSpawned);
        Assert.Equal(GameObjectLootState.Ready, door.LootState);
        Assert.Equal(0, door.RespawnAtMs);
        Assert.False(chest.IsSpawned);
        Assert.True(chest.RespawnAtMs > 0);
    }

    // --- respawn delay -----------------------------------------------------------------------------

    private static GameObjectSpawn RangeSpawn(uint guid, int min, int max, uint flags = 0)
        => GoSpawn(guid, ChestEntry, 3, 0, spawnTimeSeconds: min) with { SpawnTimeMaxSeconds = max, SpawnFlags = flags };

    [Fact]
    public void RespawnDelay_IsRolledBetweenMinAndMax_PerSpawn()
    {
        GameObjectSpawn[] spawns = [.. Enumerable.Range(1, 16).Select(i => RangeSpawn((uint)i, 100, 300))];
        Rig rig = CreateRig(DefaultTemplates(), spawns);
        rig.Join();
        var delays = new List<long>();
        foreach (GameObject go in rig.System.GameObjects.OrderBy(g => g.Spawn!.Guid))
        {
            rig.System.Despawn(go);
            delays.Add(go.RespawnAtMs - rig.System.ClockMs);
        }

        Assert.All(delays, d => Assert.InRange(d, 100_000, 300_000));
        Assert.True(delays.Distinct().Count() > 1, "a seeded roll over sixteen spawns must not always land on one value");
        Assert.Contains(delays, d => d > 100_000);
    }

    [Fact]
    public void RespawnDelay_UsesTheMinimum_WhenRandomRespawnIsOff_OrMaxIsAbsent()
    {
        Rig off = CreateRig(DefaultTemplates(), [RangeSpawn(1, 100, 300)], configure: s => s.Options = new GameObjectOptions { RandomRespawn = false });
        off.Join();
        GameObject go = off.Spawned(1);
        off.System.Despawn(go);
        Assert.Equal(100_000, go.RespawnAtMs - off.System.ClockMs);

        Rig absent = CreateRig(DefaultTemplates(), [GoSpawn(1, ChestEntry, 3, 0, spawnTimeSeconds: 120)]);
        absent.Join();
        GameObject fixedDelay = absent.Spawned(1);
        absent.System.Despawn(fixedDelay);
        Assert.Equal(120_000, fixedDelay.RespawnAtMs - absent.System.ClockMs);
    }

    [Fact]
    public void RandomRespawnTimeFlag_ScalesTheRolledDelayByNinetyToOneHundredTenPercent()
    {
        GameObjectSpawn[] spawns = [.. Enumerable.Range(1, 16).Select(i => RangeSpawn((uint)i, 100, 100, flags: 0x04))];
        Rig rig = CreateRig(DefaultTemplates(), spawns);
        rig.Join();
        var delays = new List<long>();
        foreach (GameObject go in rig.System.GameObjects)
        {
            rig.System.Despawn(go);
            delays.Add(go.RespawnAtMs - rig.System.ClockMs);
        }

        Assert.All(delays, d => Assert.InRange(d, 90_000, 110_000));
        Assert.True(delays.Distinct().Count() > 1);
    }

    // --- despawn animation -------------------------------------------------------------------------

    [Fact]
    public void Despawn_SendsDespawnAnim_BeforeTheDestroy()
    {
        Rig rig = CreateRig(DefaultTemplates(), [GoSpawn(1, ChestEntry, 3, 0)]);
        (_, FakeSession session) = rig.Join();
        GameObject chest = rig.Spawned(1);
        session.Clear();

        rig.System.Despawn(chest);

        WorldOpcode[] order = [.. session.Sent.Select(p => p.Opcode)];
        int anim = Array.IndexOf(order, WorldOpcode.SmsgGameobjectDespawnAnim);
        int destroy = Array.IndexOf(order, WorldOpcode.SmsgDestroyObject);
        Assert.True(anim >= 0, "SMSG_GAMEOBJECT_DESPAWN_ANIM was not sent");
        Assert.True(destroy > anim, "the destroy must follow the despawn animation");
        Assert.Equal(BitConverter.GetBytes(chest.Guid.Value), session.Sent.ToArray()[anim].Payload);
    }

    [Fact]
    public void DespawnAnim_IsTheGuidOnly()
        => Assert.Equal(BitConverter.GetBytes(0xF1100000_00000005UL), GameObjectPackets.DespawnAnim(new ObjectGuid(0xF1100000_00000005UL)));
}
