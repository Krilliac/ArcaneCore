using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>Death, release (repop), corpses, reclaim, auto release and logout cleanup.</summary>
public sealed class CombatDeathTests
{
    private sealed record Setup(WorldRuntime World, Map Map, TestCombatHooks Hooks, Player A, FakeSession SA, Player V, FakeSession SV);

    private static Setup Create(uint mapId = 0)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(mapId);
        var hooks = new TestCombatHooks();
        map.Combat.Random = new ScriptedRandom();
        map.Combat.Hooks = hooks;
        var sa = new FakeSession(1);
        var sv = new FakeSession(2);
        Player a = CombatTestKit.AddPlayer(world, 1, 0, 0, sa, mapId: mapId);
        Player v = CombatTestKit.AddPlayer(world, 2, 2, 0, sv, Race.Orc, mapId: mapId);
        a.SetFloat(UpdateFields.UnitFieldMindamage, 50);
        a.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
        world.RunTick(1);
        sa.Clear();
        sv.Clear();
        return new Setup(world, map, hooks, a, sa, v, sv);
    }

    /// <summary>A swings once at a 40-health victim: the victim dies, and (updated after A) becomes a corpse in the same tick.</summary>
    private static void KillVictim(Setup s)
    {
        s.V.Health = 40;
        s.Map.Combat.Attack(s.A, s.V);
        s.World.RunTick(50);
    }

    private static List<(WorldOpcode Opcode, byte[] Payload)> Drain(FakeSession session) => [.. CombatTestKit.Drain(session)];

    private static void Advance(WorldRuntime world, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            world.RunTick(1000);
        }
    }

    [Fact]
    public void Kill_SendsPartyKillLog_StopsCombat_RootsAndShowsTheReleaseTimer()
    {
        Setup s = Create();
        using WorldRuntime world = s.World;

        KillVictim(s);

        Assert.Equal(DeathState.Corpse, s.V.Combat.DeathState);
        Assert.False(s.V.IsAlive);
        Assert.Equal(0u, s.V.Health);
        Assert.Null(s.A.Combat.Victim);
        Assert.False(s.A.Combat.IsMeleeAttacking);
        Assert.Equal([(s.A, (Unit)s.V)], s.Hooks.Kills.Select(k => (k.Killer!, k.Victim)).ToList());
        Assert.NotEqual(0, s.V.GetByte(UpdateFields.PlayerFieldBytes, 0) & MapCombat.FieldByteReleaseTimer);
        Assert.Equal(CombatConstants.CorpseRepopTimeMs - 50, s.V.Combat.DeathTimer); // KillPlayer, then the timer, in one Player::Update
        Assert.True(s.V.Combat.PvpDeath);

        List<(WorldOpcode Opcode, byte[] Payload)> toKiller = Drain(s.SA);
        byte[] log = Assert.Single(toKiller, p => p.Opcode == WorldOpcode.SmsgPartykilllog).Payload;
        Assert.Equal([.. BitConverter.GetBytes(s.A.Guid.Value), .. BitConverter.GetBytes(s.V.Guid.Value)], log);
        Assert.Contains(toKiller, p => p.Opcode == WorldOpcode.SmsgAttackstop);

        List<(WorldOpcode Opcode, byte[] Payload)> toVictim = Drain(s.SV);
        Assert.Contains(toVictim, p => p.Opcode == WorldOpcode.SmsgForceMoveRoot);
        Assert.Contains(toVictim, p => p.Opcode == WorldOpcode.SmsgCancelCombat);

        // a dead victim cannot be attacked again
        Assert.False(s.Map.Combat.Attack(s.A, s.V));
    }

    [Fact]
    public void InstancedMap_HasNoReleaseTimerAndNoAutoRelease()
    {
        Setup s = Create(mapId: 33);
        using WorldRuntime world = s.World;

        KillVictim(s);

        Assert.Equal(DeathState.Corpse, s.V.Combat.DeathState);
        Assert.Equal(0, s.V.GetByte(UpdateFields.PlayerFieldBytes, 0) & MapCombat.FieldByteReleaseTimer);
        world.RunTick(CombatConstants.CorpseRepopTimeMs + 1000);
        Assert.Equal(DeathState.Corpse, s.V.Combat.DeathState);
    }

    [Fact]
    public void Repop_MakesAGhostWithAVisibleCorpse()
    {
        Setup s = Create();
        using WorldRuntime world = s.World;
        KillVictim(s);
        Drain(s.SV);
        Advance(world, 5);
        Drain(s.SV);

        Assert.True(s.Map.Combat.RepopPlayer(s.V));

        Assert.Equal(DeathState.Dead, s.V.Combat.DeathState);
        Assert.NotEqual(0u, (uint)(s.V.Flags & PlayerFlags.Ghost));
        Assert.Equal(1u, s.V.Health);
        Assert.False(s.V.IsAlive);
        Assert.Equal(1, s.Hooks.GraveyardRepops);
        Corpse corpse = Assert.Single(s.Map.Combat.Corpses);
        Assert.Same(corpse, s.V.Combat.Corpse);
        Assert.Equal(CorpseType.ResurrectablePvp, corpse.Type);
        Assert.Equal(s.V.Guid.Value, corpse.GetUInt64(UpdateFields.CorpseFieldOwner));
        Assert.Equal(s.V.X, corpse.X);

        List<(WorldOpcode Opcode, byte[] Payload)> toGhost = Drain(s.SV);
        Assert.Contains(toGhost, p => p.Opcode == WorldOpcode.SmsgForceMoveUnroot);
        Assert.Contains(toGhost, p => p.Opcode == WorldOpcode.SmsgMoveWaterWalk);
        byte[] delay = Assert.Single(toGhost, p => p.Opcode == WorldOpcode.SmsgCorpseReclaimDelay).Payload;
        Assert.Equal(30000u, BinaryPrimitives.ReadUInt32LittleEndian(delay));

        // the corpse reaches observers on the next update
        world.RunTick(1);
        Assert.Contains(corpse.Guid, s.A.VisibleObjects);

        // MSG_CORPSE_QUERY: found, map, position, corpse map
        byte[] query = MapCombat.BuildCorpseQuery(s.V);
        Assert.Equal(1, query[0]);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(query.AsSpan(1)));
        Assert.Equal(21, query.Length);

        Assert.False(s.Map.Combat.RepopPlayer(s.V)); // already a ghost
        Assert.False(s.Map.Combat.RepopPlayer(s.A)); // alive
    }

    [Fact]
    public void Reclaim_WaitsForTheDelay_NeedsRange_ThenRevivesAtHalfHealth()
    {
        Setup s = Create();
        using WorldRuntime world = s.World;
        KillVictim(s);
        Advance(world, 5);
        s.Map.Combat.RepopPlayer(s.V);
        world.RunTick(1);
        ObjectGuid corpseGuid = s.V.Combat.Corpse!.Guid;

        Assert.False(s.Map.Combat.TryReclaimCorpse(s.V));
        Advance(world, 29);
        Assert.False(s.Map.Combat.TryReclaimCorpse(s.V));
        Advance(world, 1);

        s.V.Relocate(100, 0, 83.5f, 0, 0);
        Assert.False(s.Map.Combat.TryReclaimCorpse(s.V)); // 39 yd + radii
        s.V.Relocate(30, 0, 83.5f, 0, 0);
        Drain(s.SV);

        Assert.True(s.Map.Combat.TryReclaimCorpse(s.V));

        Assert.True(s.V.IsAlive);
        Assert.Equal(500u, s.V.Health);
        Assert.Equal(0u, (uint)(s.V.Flags & PlayerFlags.Ghost));
        Assert.Null(s.V.Combat.Corpse);
        Assert.Empty(s.Map.Combat.Corpses);
        Assert.Contains(Drain(s.SV), p => p.Opcode == WorldOpcode.SmsgMoveLandWalk);
        world.RunTick(1);
        Assert.DoesNotContain(corpseGuid, s.A.VisibleObjects);
    }

    [Fact]
    public void AutoRelease_AfterSixMinutes()
    {
        Setup s = Create();
        using WorldRuntime world = s.World;
        KillVictim(s);

        world.RunTick(CombatConstants.CorpseRepopTimeMs - 50 - 1);
        Assert.Equal(DeathState.Corpse, s.V.Combat.DeathState);
        world.RunTick(1);
        Assert.Equal(DeathState.Dead, s.V.Combat.DeathState);
        Assert.NotNull(s.V.Combat.Corpse);
    }

    [Fact]
    public void SecondDeathWithinFiveMinutes_DoublesTheReclaimDelay()
    {
        Setup s = Create();
        using WorldRuntime world = s.World;
        KillVictim(s);                         // t = 0: window until 300
        Advance(world, 5);
        s.Map.Combat.RepopPlayer(s.V);         // (300 − 5) / 300 = 0 → 30 s
        Advance(world, 30);
        Assert.True(s.Map.Combat.TryReclaimCorpse(s.V));
        Advance(world, 5);
        Drain(s.SV);

        s.V.Health = 40;
        s.Map.Combat.Attack(s.A, s.V);         // t ≈ 40: window until 40 + 600
        world.RunTick(2000);
        Assert.Equal(DeathState.Corpse, s.V.Combat.DeathState);
        Advance(world, 5);
        Drain(s.SV);
        s.Map.Combat.RepopPlayer(s.V);         // (640 − 47) / 300 = 1 → 60 s

        byte[] delay = Assert.Single(Drain(s.SV), p => p.Opcode == WorldOpcode.SmsgCorpseReclaimDelay).Payload;
        Assert.Equal(60000u, BinaryPrimitives.ReadUInt32LittleEndian(delay));
    }

    [Fact]
    public void Logout_RemovesTheCorpse_AndDetachesAttackers()
    {
        Setup s = Create();
        using WorldRuntime world = s.World;
        KillVictim(s);
        s.Map.Combat.RepopPlayer(s.V);
        Assert.Single(s.Map.Combat.Corpses);

        world.RemovePlayer(s.V);

        Assert.Empty(s.Map.Combat.Corpses);
        Assert.DoesNotContain(s.V, s.A.Combat.Attackers);
    }

    [Fact]
    public void Logout_StopsAttackersOnThePlayer()
    {
        Setup s = Create();
        using WorldRuntime world = s.World;
        s.Map.Combat.Attack(s.A, s.V);

        world.RemovePlayer(s.V);

        Assert.Null(s.A.Combat.Victim);
        Assert.Empty(s.V.Combat.Attackers);
    }

    [Fact]
    public void KillingAMob_LeavesACorpseUnit_AndClearsItsThreat()
    {
        (WorldRuntime world, Map map, _, TestCombatHooks hooks) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetFloat(UpdateFields.UnitFieldMindamage, 50);
        player.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
        var mob = new CombatTestUnit(health: 40);
        mob.Spawn(map, 2, 0, orientation: MathF.PI);

        map.Combat.Attack(player, mob);
        world.RunTick(50);

        Assert.Equal(DeathState.Corpse, mob.Combat.DeathState);
        Assert.Equal(1, mob.Deaths);
        Assert.False(mob.Combat.HasThreatList);
        Assert.Empty(player.Combat.ThreatenedBy);
        Assert.Single(hooks.Kills);
        Assert.Null(player.Combat.Victim);
    }
}
