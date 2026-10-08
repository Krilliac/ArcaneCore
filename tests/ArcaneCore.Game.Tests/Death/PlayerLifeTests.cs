using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// What a character snapshot carries of its life (health, power, experience, death window,
/// ghost and corpse) and how stored vitals are applied on load (vmangos Player::SaveToDB
/// Player.cpp:16470-16476; LoadFromDB Player.cpp:14915-14917, 15062-15070; LoadCorpse 15427-15440).
/// </summary>
public sealed class PlayerLifeTests
{
    private const long ClockStart = 1_700_000_000;

    private static Player NewPlayer(FakeSession? session = null, Race race = Race.Human)
    {
        Player player = TestWorld.CreatePlayer(7, 10, 20, session ?? new FakeSession(), 0, race);
        player.Level = 20;
        player.MaxHealth = 1000;
        player.Health = 1000;
        for (int i = 0; i < 5; i++)
        {
            player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + i, 500u + (uint)i);
        }

        return player;
    }

    [Fact]
    public void Capture_ReadsHealthPowersExperienceAndTheDeathWindow()
    {
        Player player = NewPlayer();
        player.Health = 321;
        for (int i = 0; i < 5; i++)
        {
            player.SetUInt32(UpdateFields.UnitFieldPower1 + i, 10u + (uint)i);
        }

        player.SetUInt32(UpdateFields.PlayerXp, 777);
        player.Combat.DeathExpireTime = ClockStart + 120;

        CharacterLife life = Assert.IsType<CharacterLife>(player.CreateSnapshot(0).Life);

        Assert.Equal(321u, life.Health);
        Assert.Equal([10u, 11u, 12u, 13u, 14u], life.Powers);
        Assert.Equal(777u, life.Xp);
        Assert.Equal(ClockStart + 120, life.DeathExpireUnix);
        Assert.False(life.IsGhost);
        Assert.Null(life.Corpse);
    }

    [Fact]
    public void Capture_OfAGhost_CarriesTheCorpseAndTheGhostTime()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        using (world)
        {
            var clock = new FixedDeathClock(ClockStart);
            DeathHooks.Register(world, new DeathHooks(new DeathOptions(), clock));
            Map map = world.GetMap(0);
            var session = new FakeSession(2);
            Player player = CombatTestKit.AddPlayer(world, 2, 5, 6, session, Race.Orc);
            player.Relocate(5, 6, 83.5f, 1.25f, 0);
            player.Combat.PvpDeath = true;
            map.Combat.KillPlayer(player);
            clock.Now += 9;
            Assert.True(map.Combat.RepopPlayer(player));

            CharacterLife life = Assert.IsType<CharacterLife>(player.CreateSnapshot(0).Life);

            Assert.True(life.IsGhost);
            Assert.Equal(1u, life.Health);
            CorpseSnapshot corpse = Assert.IsType<CorpseSnapshot>(life.Corpse);
            Assert.Equal((0u, 5f, 6f, 83.5f, 1.25f), (corpse.MapId, corpse.X, corpse.Y, corpse.Z, corpse.Orientation));
            Assert.Equal(ClockStart + 9, corpse.GhostTimeUnix);
            Assert.Equal((byte)CorpseType.ResurrectablePvp, corpse.Type);
            Assert.Equal(ClockStart + 300, life.DeathExpireUnix); // KillPlayer opened the 5-minute window
        }
    }

    [Fact]
    public void ApplyVitals_KeepsStoredValuesBelowTheMaximums()
    {
        Player player = NewPlayer();
        var life = new CharacterLife(432, [1, 2, 3, 4, 5], 0, 0, false, null);

        PlayerLife.ApplyVitals(player, life);

        Assert.Equal(432u, player.Health);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(1u + (uint)i, player.GetUInt32(UpdateFields.UnitFieldPower1 + i));
        }
    }

    [Fact]
    public void ApplyVitals_NeverExceedsTheCurrentMaximums()
    {
        // vmangos: "restore remembered power/health values (but not more max values)" (Player.cpp:15062).
        Player player = NewPlayer();
        var life = new CharacterLife(99999, [9000, 9000, 9000, 9000, 9000], 0, 0, false, null);

        PlayerLife.ApplyVitals(player, life);

        Assert.Equal(1000u, player.Health);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(500u + (uint)i, player.GetUInt32(UpdateFields.UnitFieldPower1 + i));
        }
    }

    [Fact]
    public void ApplyVitals_AZeroHealthNonGhost_KeepsItsSavedValuesAndIsAlive()
    {
        // vmangos LoadFromDB: the death state comes from the ghost flag alone (Player.cpp:14973-14975), so a body
        // saved at 0 health before its release loads ALIVE and gets its saved powers back (Player.cpp:15062-15070);
        // LoadCorpse's half restore is only for a dead player (Player.cpp:15427-15439). ArcaneCore's IsAlive also needs
        // health above 0, so the health is loaded as 1 (the one value it cannot represent is ALIVE at 0).
        Player player = NewPlayer();
        var life = new CharacterLife(0, [7, 40, 3, 0, 0], 0, 0, false, null);

        Assert.False(PlayerLife.HasNoBodyToReturnTo(life));
        PlayerLife.ApplyVitals(player, life);

        Assert.Equal(1u, player.Health);
        Assert.Equal(7u, player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Mana));
        Assert.Equal(40u, player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage));
        Assert.Equal(3u, player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Focus));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy));
        Assert.Equal(DeathState.Alive, player.Combat.DeathState);
        Assert.True(player.IsAlive); // alive to every check, as vmangos Unit::IsAlive (m_deathState == ALIVE, Unit.h:503)
    }

    [Fact]
    public void AZeroHealthNonGhostLoad_RegeneratesOnItsFirstTick()
    {
        // vmangos Player::Update regenerates every ALIVE player (Player.cpp:1240-1243), which is what raises such a load.
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
        PlayerLife.ApplyVitals(player, new CharacterLife(0, [0, 0, 0, 0, 0], 0, 0, false, null));
        Assert.Equal(1u, player.Health);
        Assert.True(player.IsAlive);

        world.RunTick(50);

        Assert.Equal(16u, player.Health); // 1 + warrior 1.26 · 30 − 22.6 = 15.2
        Assert.True(player.IsAlive);
    }

    [Fact]
    public void Reapply_RaisesTheSavedPowersOfAZeroHealthNonGhost()
    {
        Player player = NewPlayer();
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 50);
        var life = new CharacterLife(0, [300, 0, 0, 0, 0], 0, 0, false, null);
        LoadedLife loaded = PlayerLife.ApplyVitals(player, life);
        Assert.Equal(50u, player.GetUInt32(UpdateFields.UnitFieldPower1));

        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 500); // an aura (intellect) arrived
        PlayerLife.ReapplyAfterAuras(player, loaded);

        Assert.Equal(300u, player.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Equal(1u, player.Health);
    }

    [Fact]
    public void ApplyVitals_AGhostWithoutABody_IsResurrectedAtHalf()
    {
        // vmangos Player::LoadCorpse: "Prevent Dead Player login without corpse" -> ResurrectPlayer(0.5f).
        Player player = NewPlayer();
        var life = new CharacterLife(1, [0, 40, 0, 0, 0], 0, 0, true, null);

        Assert.True(PlayerLife.HasNoBodyToReturnTo(life));
        PlayerLife.ApplyVitals(player, life);

        Assert.Equal(500u, player.Health);
        Assert.Equal(250u, player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Mana));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage));
        Assert.Equal(251u, player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy)); // 503 / 2
    }

    [Fact]
    public void ApplyVitals_AGhostWithABody_KeepsItsOneHealth()
    {
        Player player = NewPlayer();
        var life = new CharacterLife(1, [0, 0, 0, 0, 0], 0, 0, true, new CorpseSnapshot(0, 1, 2, 3, 0, 5, 1));

        Assert.False(PlayerLife.HasNoBodyToReturnTo(life));
        Assert.True(PlayerLife.IsGhostWithBody(life));
        PlayerLife.ApplyVitals(player, life);

        Assert.Equal(1u, player.Health);
    }

    [Fact]
    public void Reapply_RaisesHealthAndPowerToTheStoredValuesOnceAurasRaisedTheMaximums()
    {
        Player player = NewPlayer();
        player.MaxHealth = 60;
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 50);
        var life = new CharacterLife(400, [300, 0, 0, 0, 0], 0, 0, false, null);
        LoadedLife loaded = PlayerLife.ApplyVitals(player, life);
        Assert.Equal(60u, player.Health);
        Assert.Equal(50u, player.GetUInt32(UpdateFields.UnitFieldPower1));

        player.MaxHealth = 1000;                                    // an aura (stamina, intellect) arrived
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 500);
        PlayerLife.ReapplyAfterAuras(player, loaded);

        Assert.Equal(400u, player.Health);
        Assert.Equal(300u, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Fact]
    public void Reapply_DoesNotOverrideChangesMadeSinceTheLoad()
    {
        Player player = NewPlayer();
        player.MaxHealth = 60;
        var life = new CharacterLife(400, [0, 0, 0, 0, 0], 0, 0, false, null);
        LoadedLife loaded = PlayerLife.ApplyVitals(player, life);

        player.MaxHealth = 1000;
        player.Health = 25;                                         // damage taken in the meantime
        PlayerLife.ReapplyAfterAuras(player, loaded);

        Assert.Equal(25u, player.Health);
    }

    [Fact]
    public void Reapply_LeavesADeadPlayerAlone()
    {
        Player player = NewPlayer();
        var life = new CharacterLife(400, [0, 0, 0, 0, 0], 0, 0, false, null);
        LoadedLife loaded = PlayerLife.ApplyVitals(player, life);
        player.Combat.DeathState = DeathState.Dead;
        player.Health = 1;

        PlayerLife.ReapplyAfterAuras(player, loaded);

        Assert.Equal(1u, player.Health);
    }

    [Theory]
    [InlineData(ClockStart + 10, ClockStart + 10)]                    // inside the window: kept
    [InlineData(ClockStart + 900, ClockStart + 899)]                    // 3 steps would be 900: capped one second short (Player.cpp:14915-14917)
    [InlineData(ClockStart + 100_000, ClockStart + 899)]
    [InlineData(0, 0)]
    public void ApplyDeathWindow_CapsTheStoredExpiry(long stored, long expected)
    {
        Player player = NewPlayer();
        var life = new CharacterLife(1000, [0, 0, 0, 0, 0], 0, stored, false, null);

        PlayerLife.ApplyDeathWindow(player, life, ClockStart);

        Assert.Equal(expected, player.Combat.DeathExpireTime);
    }
}
