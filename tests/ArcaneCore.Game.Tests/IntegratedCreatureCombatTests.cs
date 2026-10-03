using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests;

/// <summary>Cross-feature ownership: one map registry, observer set and creature death pipeline.</summary>
public sealed class IntegratedCreatureCombatTests
{
    [Fact]
    public void DirectSpellDamageTracksTheVictim_WithoutStartingWhiteMeleeOrOutgoingRage()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Player caster = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        Player victim = TestWorld.CreatePlayer(2, 3, 0, new FakeSession(2));
        world.AddPlayer(caster);
        world.AddPlayer(victim);
        Map map = caster.Map!;
        caster.SetFloat(UpdateFields.UnitFieldMindamage, 5);
        caster.SetFloat(UpdateFields.UnitFieldMaxdamage, 5);
        uint rage = caster.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage);

        Assert.Equal(7u, map.Combat.DealDamage(caster, victim, 7, meleeDamage: false));
        Assert.Same(victim, caster.Combat.Victim);
        Assert.False(caster.Combat.IsMeleeAttacking);
        Assert.Equal(rage, caster.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage));
        world.RunTick(50);
        Assert.Equal(53u, victim.Health);
    }

    [Fact]
    public void CreatureUsesMapRegistry_AndDamageValuesReachItsSingleObserver()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 3, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateSystem(content);
        using WorldRuntime world = runtime;
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        session.Clear();
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);
        Assert.Same(wolf, map.FindObject(wolf.Guid));
        Assert.Same(wolf, map.Combat.FindUnit(wolf.Guid));
        Assert.Single(DrainBlocks(session), b => b.Guids.Contains(wolf.Guid.Value)
            && b.Type is ObjectUpdateType.CreateObject or ObjectUpdateType.CreateObject2);

        Assert.Equal(7u, map.Combat.DealDamage(player, wolf, 7, direct: false));
        Assert.Same(player, wolf.Combat.Victim);
        Assert.True(wolf.Combat.IsInCombat);
        Assert.Equal(7f, Assert.Single(wolf.Combat.Threat.Entries).Threat);
        wolf.Combat.ResetAttackTimer();
        world.RunTick(1);

        ParsedBlock values = Assert.Single(DrainBlocks(session), b => b.Type == ObjectUpdateType.Values
            && b.Guids.Contains(wolf.Guid.Value));
        Assert.Equal(48u, values.Values[UpdateFields.UnitFieldHealth]);
    }

    [Fact]
    public void CombatKillStartsCorpseTimers_AndRespawnReturnsAnAliveAttackableObject()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 3, 0, respawnSeconds: 3)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateSystem(content,
            new CreatureOptions { CorpseDecayNormalSeconds = 1 });
        using WorldRuntime world = runtime;
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);
        session.Clear();

        map.Combat.DealDamage(player, wolf, wolf.Health, direct: false);
        map.Combat.Kill(player, wolf); // a repeat must not reward or restart death
        Assert.Equal(CreatureDeathState.Corpse, wolf.DeathState);
        Assert.Equal(DeathState.Corpse, wolf.Combat.DeathState);
        Assert.Equal(system.ClockMs + 3000, wolf.RespawnAtMs);
        Assert.Same(wolf, map.FindObject(wolf.Guid));
        var packets = new List<(WorldOpcode Opcode, byte[] Payload)>();
        DrainBlocks(session, packets);
        Assert.Single(packets, p => p.Opcode == WorldOpcode.SmsgPartykilllog);

        world.RunTick(1000);
        Assert.Equal(CreatureDeathState.Dead, wolf.DeathState);
        Assert.Null(wolf.Map);
        Assert.Null(map.FindObject(wolf.Guid));
        Assert.DoesNotContain(wolf.Guid, player.VisibleObjects);
        session.Clear();

        world.RunTick(2000);
        Assert.Equal(CreatureDeathState.Alive, wolf.DeathState);
        Assert.Equal(DeathState.Alive, wolf.Combat.DeathState);
        Assert.True(wolf.IsAlive);
        Assert.Equal(wolf.MaxHealth, wolf.Health);
        Assert.False(wolf.Combat.IsInCombat);
        Assert.Empty(wolf.Combat.Threat.Entries);
        Assert.Same(wolf, map.FindObject(wolf.Guid));
        Assert.Single(DrainBlocks(session), b => b.Type == ObjectUpdateType.CreateObject
            && b.Guids.Contains(wolf.Guid.Value));
        Assert.True(map.Combat.Hooks.CanAttack(player, wolf));
    }

    [Fact]
    public void DespawnDetachesAttacksAndThreat_BeforeRemovingTheMapObject()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 3, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateSystem(content);
        using WorldRuntime world = runtime;
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);
        map.Combat.DealDamage(player, wolf, 1);
        Assert.Same(wolf, player.Combat.Victim);
        Assert.Same(player, wolf.Combat.Victim);

        system.Despawn(wolf);
        Assert.Null(player.Combat.Victim);
        Assert.Null(wolf.Combat.Victim);
        Assert.Empty(player.Combat.Attackers);
        Assert.Empty(player.Combat.ThreatenedBy);
        Assert.Empty(wolf.Combat.Threat.Entries);
        Assert.DoesNotContain(wolf, map.Combat.TrackedUnits);
        Assert.Null(map.FindObject(wolf.Guid));
        Assert.Null(system.FindCreature(wolf.Guid));
        Assert.DoesNotContain(wolf.Guid, player.VisibleObjects);
    }

    [Fact]
    public void SharedGridUnloadDropsTheSpawnOwner_AndReturningLoadsExactlyOneCreature()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 3, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateSystem(content);
        using WorldRuntime world = runtime;
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        Creature previous = Assert.Single(system.Creatures);
        ArcaneCore.Game.Maps.Grid.GridCoord coord = map.Grids.CellOf(previous)!.Value.Grid;
        player.Relocate(5000, 5000, 83.5f, 0, 0);

        Assert.True(map.Grids.UnloadGrid(coord, force: false));
        Assert.Null(map.FindObject(previous.Guid));
        Assert.Null(system.FindCreature(previous.Guid));
        Assert.Null(previous.Map);

        player.Relocate(0, 0, 83.5f, 0, 0);
        session.Clear();
        world.RunTick(50);
        Creature reloaded = Assert.Single(system.Creatures);
        Assert.NotSame(previous, reloaded);
        Assert.Equal(previous.Guid, reloaded.Guid);
        Assert.Same(reloaded, map.FindObject(reloaded.Guid));
        Assert.Single(DrainBlocks(session), b => b.Guids.Contains(reloaded.Guid.Value)
            && b.Type is ObjectUpdateType.CreateObject or ObjectUpdateType.CreateObject2);
    }

    [Fact]
    public void MovingCreatureSurvivesHomeGridUnload_AndReloadCannotDuplicateItsGuid()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateSystem(content);
        using WorldRuntime world = runtime;
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);
        world.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);
        ArcaneCore.Game.Maps.Grid.GridCoord home = map.Grids.CellOf(wolf)!.Value.Grid;
        system.MoveTo(wolf, 800, 0, 83.5f, run: true, finalOrientation: 0);
        uint duration = wolf.Spline!.DurationMs;
        player.Relocate(800, 0, 83.5f, 0, 0);
        world.RunTick(duration);
        Assert.Equal(800f, wolf.X);
        Assert.NotEqual(home, map.Grids.CellOf(wolf)!.Value.Grid);

        Assert.True(map.Grids.UnloadGrid(home, force: true));
        Assert.Same(wolf, map.FindObject(wolf.Guid));
        Assert.Same(wolf, Assert.Single(system.Creatures));
        Assert.Contains(wolf.Guid, player.VisibleObjects);

        map.Grids.LoadGridsAround(wolf.Home.X, wolf.Home.Y, 0);
        world.RunTick(50);
        Assert.Same(wolf, Assert.Single(system.Creatures));
        Assert.Same(wolf, map.FindObject(wolf.Guid));
    }

    [Fact]
    public void InstallingCreatureSystemAfterPlayersJoinsAlreadyLoadedMapGrids()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        Map map = player.Map!;
        session.Clear();
        var system = new CreatureMapSystem(map, Content([Template()], [Spawn(1, WolfEntry, 3, 0)]));
        map.AddUpdater(system);
        world.RunTick(50);

        Creature wolf = Assert.Single(system.Creatures);
        Assert.Same(wolf, map.FindObject(wolf.Guid));
        Assert.Single(DrainBlocks(session), b => b.Guids.Contains(wolf.Guid.Value)
            && b.Type is ObjectUpdateType.CreateObject or ObjectUpdateType.CreateObject2);
    }

    [Fact]
    public void PlayerMapDepartureKeepsTheBodyReclaimable_AndLogoutRemovesItFromTheSourceMap()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map destination = world.GetMap(1); // its logout hook runs before the body's source map
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);
        Map source = player.Map!;
        source.Combat.Kill(null, player);
        world.RunTick(1);
        Assert.True(source.Combat.RepopPlayer(player));
        world.RunTick(1);
        Corpse corpse = Assert.Single(source.Combat.Corpses);
        source.RemovePlayer(player);
        destination.AddPlayer(player);
        world.RunTick(600_000);
        world.RunTick(1);
        world.RunTick(600_000);

        Assert.Same(corpse, player.Combat.Corpse);
        Assert.Same(source, corpse.Map);
        Assert.Same(corpse, source.FindObject(corpse.Guid));
        world.RemovePlayer(player);
        Assert.Null(player.Combat.Corpse);
        Assert.Null(source.FindObject(corpse.Guid));
        Assert.Empty(source.Combat.Corpses);
    }

    [Fact]
    public void CreatureHeightProviderReadsMapTerrain_AndTreatsMissingTilesAsUnknown()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-integrated-creature-" + Guid.NewGuid().ToString("N"));
        string maps = Directory.CreateDirectory(Path.Combine(directory, "maps")).FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, 31, 32)),
                new MapFileBuilder { GridHeight = 42f }.Build());
            using WorldRuntime world = TestWorld.CreateRuntime();
            world.Options.Maps.DataDirectory = directory;
            Map map = world.GetMap(0);
            var provider = new MapCreatureHeightProvider(map);

            Assert.Equal(42f, provider.GetHeight(0, 30, -30, 83.5f));
            Assert.Null(provider.GetHeight(0, 5000, 5000, 83.5f));
            Assert.Null(provider.GetHeight(1, 30, -30, 83.5f));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
