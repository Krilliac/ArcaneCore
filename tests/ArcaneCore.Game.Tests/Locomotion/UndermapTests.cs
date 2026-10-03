using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>Antiundermap2 (vmangos HandleMoverRelocation, MovementHandler.cpp:1132-1161).</summary>
public sealed class UndermapTests
{
    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(30, 0, MapType.Battleground, 0, 40, 0, -1, 0, 0, "Alterac Valley", ""),
        ],
        [], [], [], []);

    private static (WorldRuntime World, Player Player, FakeSession Session, TestCombatHooks Hooks) Setup(uint mapId = 0, uint health = 2000, AccountSecurity security = AccountSecurity.Player)
    {
        (WorldRuntime world, _, _, TestCombatHooks hooks) = CombatTestKit.CreateWorld();
        WorldMaps.Of(world).Load(Content);
        world.GetMap(mapId).Combat.Hooks = hooks;
        var session = new FakeSession(1, security);
        Player player = CombatTestKit.AddPlayer(world, 1, 100, 100, session, mapId: mapId);
        player.MaxHealth = 2000;
        player.Health = health;
        session.Clear();
        return (world, player, session, hooks);
    }

    private static void Move(WorldRuntime world, Player player, float z)
    {
        MovementInfo previous = player.Movement;
        var incoming = new MovementInfo { Flags = MovementFlags.Jumping | MovementFlags.FallingFar, Time = 1, X = 100, Y = 100, Z = z };
        var context = new MovementObserverContext(player, world, WorldOpcode.MsgMoveHeartbeat);
        MovementObservers.Before(context, in previous, ref incoming);
        player.ApplyClientMovement(incoming, 1);
        MovementObservers.After(context, in previous);
    }

    private static byte[] Log(FakeSession session, int index = 0)
        => session.Sent.Where(p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog).Select(p => p.Payload).ElementAt(index);

    [Fact]
    public void BelowTheVoidHeight_HalfTheHealthIsTaken_AsFallToVoidLoggedAsFall_AndThePlayerIsSentToTheGraveyard()
    {
        (WorldRuntime world, Player player, FakeSession session, TestCombatHooks hooks) = Setup();

        Move(world, player, -501f);

        byte[] log = Log(session);
        Assert.Equal(2, log[8]);                          // FALL_TO_VOID is logged as DAMAGE_FALL
        Assert.Equal(1000u, BitConverter.ToUInt32(log, 9));
        Assert.Equal(1000u, player.Health);
        Assert.True(player.IsAlive);
        Assert.Equal(1, hooks.GraveyardRepops);
    }

    [Fact]
    public void JustAboveTheVoidHeight_NothingHappens()
    {
        (WorldRuntime world, Player player, FakeSession session, TestCombatHooks hooks) = Setup();

        Move(world, player, -499f);
        Move(world, player, -500f); // the rule is "below": exactly -500 is still safe

        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog);
        Assert.Equal(2000u, player.Health);
        Assert.Equal(0, hooks.GraveyardRepops);
    }

    [Fact]
    public void OnABattlegroundMap_TheWholeHealthIsTaken_TheBodyIsMadeAGhost_AndTheGraveyardIsUsedOnce()
    {
        (WorldRuntime world, Player player, FakeSession session, TestCombatHooks hooks) = Setup(mapId: 30);

        Move(world, player, -600f);

        Assert.Equal(2000u, BitConverter.ToUInt32(Log(session), 9));
        Assert.False(player.IsAlive);
        Assert.True((player.Flags & PlayerFlags.Ghost) != 0);       // KillPlayer + BuildPlayerRepop
        Assert.NotNull(player.Combat.Corpse);
        Assert.Equal(1, hooks.GraveyardRepops);                      // RepopPlayer already sent it there; not a second time
        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }

    [Fact]
    public void AGhostThatKeepsFalling_OnlyTakesTheGraveyardAgain()
    {
        (WorldRuntime world, Player player, FakeSession session, TestCombatHooks hooks) = Setup(mapId: 30);
        Move(world, player, -600f);
        session.Clear();

        Move(world, player, -700f);
        Move(world, player, -800f);

        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog);
        Assert.Equal(3, hooks.GraveyardRepops); // once for the death, then once per packet, as vmangos does
        Assert.True((player.Flags & PlayerFlags.Ghost) != 0);
    }

    [Fact]
    public void AGameMaster_IsLeftAlone()
    {
        (WorldRuntime world, Player player, FakeSession session, TestCombatHooks hooks) = Setup(security: AccountSecurity.GameMaster);
        player.SetGameMaster(true);

        Move(world, player, -900f);

        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog);
        Assert.Equal(2000u, player.Health);
        Assert.Equal(0, hooks.GraveyardRepops);
    }

    [Fact]
    public void AOneHealthPlayer_LosesNothingToHalfAHealth_ButIsStillSentToTheGraveyard()
    {
        (WorldRuntime world, Player player, _, TestCombatHooks hooks) = Setup(health: 1);

        Move(world, player, -501f); // 1 / 2 = 0 damage, as vmangos computes it

        Assert.True(player.IsAlive);
        Assert.Equal(1u, player.Health);
        Assert.Equal(1, hooks.GraveyardRepops);
    }
}
