using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// A ghost survives a logout and a relog: the body goes back where it was left, the reclaim
/// timing continues, and a spirit that logs out unreleased is released first (vmangos
/// WorldSession::LogoutPlayer, WorldSession.cpp:694-701; Player::LoadCorpse Player.cpp:15427-15440;
/// Player::SendCorpseReclaimDelay(load) Player.cpp:20228-20260).
/// </summary>
public sealed class GhostRestoreTests
{
    private const long T = 1_700_000_000;

    private sealed record Rig(WorldRuntime World, Map Map, FixedDeathClock Clock, FakeSession Session, Player Player, RecordingSaveQueue Saves);

    private static Rig Create()
    {
        var saves = new RecordingSaveQueue();
        WorldRuntime world = TestWorld.CreateRuntime(saves);
        var clock = new FixedDeathClock(T);
        DeathHooks.Register(world, new DeathHooks(new DeathOptions(), clock));
        Map map = world.GetMap(0);
        map.Combat.Hooks = new TestCombatHooks();
        var session = new FakeSession(2);
        Player player = CombatTestKit.AddPlayer(world, 2, 10, 20, session, Race.Orc);
        world.RunTick(1);
        session.Clear();
        return new Rig(world, map, clock, session, player, saves);
    }

    private static CharacterLife GhostLife(long ghostTime = T - 10, long expire = T + 300, CorpseSnapshot? corpse = null) => new(
        Health: 1, Powers: [0, 0, 0, 0, 0], Xp: 0, DeathExpireUnix: expire, IsGhost: true,
        Corpse: corpse ?? new CorpseSnapshot(0, 50, 60, 83.5f, 2f, ghostTime, (byte)CorpseType.ResurrectablePvp));

    /// <summary>What the login does on the session task and then on the world thread.</summary>
    private static void Login(Rig r, CharacterLife life)
    {
        PlayerLife.ApplyDeathWindow(r.Player, life, r.Clock.UnixSeconds);
        PlayerLife.ApplyVitals(r.Player, life);
        PlayerLife.ApplyGhostState(r.Player);
        r.Map.Combat.RestoreGhost(r.Player, life.Corpse!);
    }

    [Fact]
    public void ApplyGhostState_MakesTheLoadedPlayerAGhostBeforeItIsInAMap()
    {
        Player player = TestWorld.CreatePlayer(9, 0, 0, new FakeSession(9), 0, Race.Orc);
        player.MaxHealth = 1000;
        player.Health = 1;

        PlayerLife.ApplyGhostState(player);

        Assert.NotEqual(0u, (uint)(player.Flags & PlayerFlags.Ghost));
        Assert.Equal(DeathState.Dead, player.Combat.DeathState);
        Assert.False(player.IsAlive);
    }

    [Fact]
    public void RestoreGhost_PutsTheBodyWhereItWasLeft_AndTheGhostCanReclaimItAfterTheRemainingDelay()
    {
        Rig r = Create();
        using WorldRuntime world = r.World;
        Login(r, GhostLife());

        Corpse corpse = Assert.Single(r.Map.Combat.Corpses);
        Assert.Same(corpse, r.Player.Combat.Corpse);
        Assert.Equal((0u, 50f, 60f, 83.5f, 2f), (corpse.MapId, corpse.X, corpse.Y, corpse.Z, corpse.Orientation));
        Assert.Equal(CorpseType.ResurrectablePvp, corpse.Type);
        Assert.Equal(r.Player.Guid.Value, corpse.GetUInt64(UpdateFields.CorpseFieldOwner));
        Assert.Equal(T - 10, r.Player.Combat.GhostTime);
        Assert.Equal(DeathState.Dead, r.Player.Combat.DeathState);
        Assert.False(r.Player.IsAlive);
        Assert.Equal(0u, r.Player.Combat.DeathTimer);

        // GetCorpseReclaimDelay counts the window from the current time (Player.cpp:20190-20193): at T it is\n        // (300 / 300) = 1 -> 60 s from the ghost time T-10; one second later the count drops to 0 -> 30 s,\n        // so the reclaim opens at T+20 (the load-time packet uses the ghost time instead; both are vmangos).
        Assert.False(r.Map.Combat.TryReclaimCorpse(r.Player));
        r.Player.Relocate(40, 55, 83.5f, 0, 0);
        Assert.False(r.Map.Combat.TryReclaimCorpse(r.Player));
        r.Clock.Now += 19;
        Assert.False(r.Map.Combat.TryReclaimCorpse(r.Player));
        r.Clock.Now += 1;
        Assert.True(r.Map.Combat.TryReclaimCorpse(r.Player));
        Assert.True(r.Player.IsAlive);
        Assert.Empty(r.Map.Combat.Corpses);
    }

    [Fact]
    public void RestoreGhost_SendsTheRemainingReclaimDelay()
    {
        Rig r = Create();
        using WorldRuntime world = r.World;
        Login(r, GhostLife());

        var sent = CombatTestKit.Drain(r.Session).ToList();
        Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgMoveWaterWalk);
        byte[] delay = Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgCorpseReclaimDelay).Payload;
        Assert.Equal(50_000u, BinaryPrimitives.ReadUInt32LittleEndian(delay));
    }

    [Theory]
    [InlineData(T - 200, T + 100)]   // 30 s delay long over (count = 300 / 300 = 1 -> 60 s, ended at T - 140)
    [InlineData(T + 400, T + 300)]   // ghost time after the window: vmangos sends nothing
    public void RestoreGhost_SendsNoDelayWhenThereIsNothingToWaitFor(long ghostTime, long expire)
    {
        Rig r = Create();
        using WorldRuntime world = r.World;
        Login(r, GhostLife(ghostTime, expire));
        Assert.DoesNotContain(CombatTestKit.Drain(r.Session), p => p.Opcode == WorldOpcode.SmsgCorpseReclaimDelay);
    }

    [Fact]
    public void RestoreGhost_ShowsTheReleaseTimerFlagOnAContinentLikeVmangosLoadCorpse()
    {
        Rig r = Create();
        using WorldRuntime world = r.World;
        Login(r, GhostLife());
        Assert.NotEqual(0, r.Player.GetByte(UpdateFields.PlayerFieldBytes, 0) & MapCombat.FieldByteReleaseTimer);
    }

    [Fact]
    public void RestoreGhost_TheBodyIsSeenByOthersOnTheNextUpdate()
    {
        Rig r = Create();
        using WorldRuntime world = r.World;
        var otherSession = new FakeSession(3);
        Player other = CombatTestKit.AddPlayer(world, 3, 51, 60, otherSession);
        world.RunTick(1);
        Login(r, GhostLife());
        world.RunTick(1);
        Assert.Contains(r.Player.Combat.Corpse!.Guid, other.VisibleObjects);
    }

    [Fact]
    public void Logout_OfAnUnreleasedCorpse_ReleasesItFirst_AndSavesAGhostWithItsBody()
    {
        Rig r = Create();
        using WorldRuntime world = r.World;
        r.Player.Relocate(15, 25, 83.5f, 1f, 0);
        r.Player.Combat.PvpDeath = false;
        r.Player.Health = 0;
        r.Map.Combat.KillPlayer(r.Player);
        Assert.Equal(DeathState.Corpse, r.Player.Combat.DeathState);
        r.Clock.Now += 4;

        world.RemovePlayer(r.Player);

        CharacterLife life = Assert.IsType<CharacterLife>(r.Saves.Saved.Last().Life);
        Assert.True(life.IsGhost);
        Assert.Equal(1u, life.Health);
        CorpseSnapshot body = Assert.IsType<CorpseSnapshot>(life.Corpse);
        Assert.Equal((0u, 15f, 25f, 83.5f, 1f, T + 4, (byte)CorpseType.ResurrectablePve),
            (body.MapId, body.X, body.Y, body.Z, body.Orientation, body.GhostTimeUnix, body.Type));
        Assert.Empty(r.Map.Combat.Corpses); // the body is not kept in the world while its owner is offline
    }

    [Fact]
    public void Logout_OfAReleasedGhost_SavesItsBody()
    {
        Rig r = Create();
        using WorldRuntime world = r.World;
        r.Player.Health = 0;
        r.Map.Combat.KillPlayer(r.Player);
        Assert.True(r.Map.Combat.RepopPlayer(r.Player));

        world.RemovePlayer(r.Player);

        CharacterLife life = Assert.IsType<CharacterLife>(r.Saves.Saved.Last().Life);
        Assert.True(life.IsGhost);
        Assert.NotNull(life.Corpse);
        Assert.Empty(r.Map.Combat.Corpses);
    }

    [Fact]
    public void Logout_OfALivingPlayer_SavesNoBody()
    {
        Rig r = Create();
        using WorldRuntime world = r.World;
        world.RemovePlayer(r.Player);
        CharacterLife life = Assert.IsType<CharacterLife>(r.Saves.Saved.Last().Life);
        Assert.False(life.IsGhost);
        Assert.Null(life.Corpse);
    }
}
