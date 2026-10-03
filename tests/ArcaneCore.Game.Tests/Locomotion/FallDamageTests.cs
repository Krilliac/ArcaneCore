using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>Player::UpdateFallInformationIfNeed and HandleFall (vmangos Player.cpp:20799-20870).</summary>
public sealed class FallDamageTests
{
    // --- the formula ---------------------------------------------------------------------

    [Theory]
    [InlineData(14.56f, 0, 1000u, 0u)]   // below 14.57: no damage (the formula itself is 13.48 yards at zero)
    [InlineData(14.57f, 0, 1000u, 19u)]  // 0.018 * 14.57 - 0.2426 = 0.01966
    [InlineData(20f, 0, 1000u, 117u)]
    [InlineData(30f, 0, 2000u, 594u)]
    [InlineData(30f, 17, 2000u, 0u)]     // Safe Fall 17 yards: 13 yards counted, the percentage is negative
    [InlineData(40f, 17, 4000u, 685u)]
    [InlineData(40f, 0, 3000u, 1432u)]
    [InlineData(60f, 0, 3000u, 2512u)]
    [InlineData(75f, 0, 3000u, 3000u)]   // 3322 capped at the maximum health
    [InlineData(14.57f, 0, 60u, 1u)]
    public void Damage_FollowsTheVmangosFloatFormula(float zDiff, int safeFall, uint maxHealth, uint expected)
        => Assert.Equal(expected, FallDamageCalculator.Damage(zDiff, safeFall, maxHealth));

    [Fact]
    public void TheWorldRateAndTheTakenModifier_ScaleTheDamage()
    {
        Assert.Equal(716u, FallDamageCalculator.Damage(40f, 0, 3000, rate: 0.5f));
        Assert.Equal(716u, FallDamageCalculator.Damage(40f, 0, 3000, rate: 1f, takenMod: 0.5f));
        Assert.Equal(0u, FallDamageCalculator.Damage(40f, 0, 3000, rate: 0f));
    }

    // --- the observer --------------------------------------------------------------------

    private sealed class Scene
    {
        public Scene(AccountSecurity security = AccountSecurity.Player, float rate = 1.0f)
        {
            (World, _, _, _) = CombatTestKit.CreateWorld();
            if (rate != 1.0f)
            {
                LocomotionEnvironment.Register(World, new LocomotionEnvironment(new LocomotionOptions { RateDamageFall = rate }));
            }

            Session = new FakeSession(1, security);
            Player = CombatTestKit.AddPlayer(World, 1, 100, 100, Session);
            Player.MaxHealth = 3000;
            Player.Health = 3000;
            Session.Clear();
        }

        public WorldRuntime World { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        /// <summary>The handler sequence of MovementHandlers.ApplyObserved, without the network.</summary>
        public void Send(WorldOpcode opcode, MovementFlags flags, float z, uint fallTime = 0)
        {
            MovementInfo previous = Player.Movement;
            var incoming = new MovementInfo { Flags = flags, Time = 1, X = 100, Y = 100, Z = z, FallTime = fallTime };
            var context = new MovementObserverContext(Player, World, opcode);
            MovementObservers.Before(context, in previous, ref incoming);
            Player.ApplyClientMovement(incoming, 1);
            MovementObservers.After(context, in previous);
        }

        public void Fall(float startZ, float endZ, uint fallTime = 3000)
        {
            Send(WorldOpcode.MsgMoveJump, MovementFlags.Jumping, startZ);
            Send(WorldOpcode.MsgMoveHeartbeat, MovementFlags.Jumping | MovementFlags.FallingFar, (startZ + endZ) / 2);
            Send(WorldOpcode.MsgMoveFallLand, MovementFlags.None, endZ, fallTime);
        }

        public byte[][] Logs => [.. Session.Sent.Where(p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog).Select(p => p.Payload)];
    }

    private static uint DamageOf(byte[] log) => BitConverter.ToUInt32(log, 9);

    [Fact]
    public void AFortyYardFall_HurtsByTheFormula_AndLogsOneFallDamage()
    {
        var scene = new Scene();

        scene.Fall(100, 60);

        byte[] log = Assert.Single(scene.Logs);
        Assert.Equal(2, log[8]);                // DAMAGE_FALL
        Assert.Equal(1432u, DamageOf(log));     // 0.018 * 40 - 0.2426 = 0.4774 of 3000
        Assert.Equal(3000u - 1432u, scene.Player.Health);
        Assert.False(scene.Player.Locomotion.IsFalling);
    }

    [Fact]
    public void AFallThatTookLessThan1229Ms_DoesNotHurt()
    {
        var scene = new Scene();
        scene.Fall(100, 60, fallTime: 1228);
        Assert.Empty(scene.Logs);

        scene.Fall(100, 60, fallTime: 1229);
        Assert.Single(scene.Logs);
    }

    [Fact]
    public void ALandingAtTheStartHeightOrAbove_DoesNotHurt()
    {
        var scene = new Scene();
        scene.Fall(100, 101); // up a ledge: the player is above where the fall began
        Assert.Empty(scene.Logs);
    }

    [Fact]
    public void FallsBelowFourteenAndAHalfYards_DoNotHurt()
    {
        var scene = new Scene();
        scene.Fall(100, 85.44f); // 14.56 yards
        Assert.Empty(scene.Logs);
    }

    [Fact]
    public void TheStartHeight_FollowsThePlayerUpward_AndStartsAgainAfterALanding()
    {
        var scene = new Scene();
        scene.Send(WorldOpcode.MsgMoveJump, MovementFlags.Jumping, 100);
        scene.Send(WorldOpcode.MsgMoveHeartbeat, MovementFlags.Jumping | MovementFlags.FallingFar, 110); // carried up by a lift
        Assert.Equal(110f, scene.Player.Locomotion.FallStartZ);
        scene.Send(WorldOpcode.MsgMoveHeartbeat, MovementFlags.Jumping | MovementFlags.FallingFar, 105);
        Assert.Equal(110f, scene.Player.Locomotion.FallStartZ);
        scene.Send(WorldOpcode.MsgMoveFallLand, MovementFlags.None, 105, 100);
        Assert.False(scene.Player.Locomotion.IsFalling);
    }

    [Fact]
    public void StartingToSwimBeforeLanding_EndsTheFall()
    {
        var scene = new Scene();
        scene.Send(WorldOpcode.MsgMoveJump, MovementFlags.Jumping, 100);
        scene.Send(WorldOpcode.MsgMoveHeartbeat, MovementFlags.Jumping | MovementFlags.FallingFar, 80);
        scene.Send(WorldOpcode.MsgMoveStartSwim, MovementFlags.Swimming | MovementFlags.FallingFar, 60);
        scene.Send(WorldOpcode.MsgMoveFallLand, MovementFlags.None, 55, 3000);

        Assert.Empty(scene.Logs);
    }

    [Fact]
    public void AHoverOrSafeFallFlagInTheBlock_EndsTheFall()
    {
        var scene = new Scene();
        scene.Send(WorldOpcode.MsgMoveJump, MovementFlags.Jumping, 100);
        scene.Send(WorldOpcode.MsgMoveHeartbeat, MovementFlags.Jumping | MovementFlags.FallingFar | MovementFlags.SafeFall, 80);
        Assert.False(scene.Player.Locomotion.IsFalling);
    }

    [Fact]
    public void ATeleport_ForgetsTheFall()
    {
        var scene = new Scene();
        scene.Send(WorldOpcode.MsgMoveJump, MovementFlags.Jumping, 100);
        scene.Send(WorldOpcode.MsgMoveHeartbeat, MovementFlags.Jumping | MovementFlags.FallingFar, 80);

        scene.Player.Relocate(5, 5, 5, 0, 10); // what every teleport, spell relocation and login ends with

        Assert.False(scene.Player.Locomotion.IsFalling);
    }

    [Fact]
    public void TheLandingUsesThePreviouslyStoredFlags_NotTheOnesInTheLandingBlock()
    {
        var scene = new Scene();
        scene.Send(WorldOpcode.MsgMoveJump, MovementFlags.Jumping, 100);
        scene.Send(WorldOpcode.MsgMoveHeartbeat, MovementFlags.Jumping | MovementFlags.FallingFar, 80);
        scene.Send(WorldOpcode.MsgMoveFallLand, MovementFlags.FallingFar | MovementFlags.Jumping, 60, 3000); // still claims falling
        Assert.Single(scene.Logs);

        var other = new Scene();
        other.Send(WorldOpcode.MsgMoveJump, MovementFlags.Jumping, 100);
        other.Send(WorldOpcode.MsgMoveHeartbeat, MovementFlags.Jumping, 80); // stored block never had FallingFar
        other.Send(WorldOpcode.MsgMoveFallLand, MovementFlags.FallingFar, 60, 3000);
        Assert.Empty(other.Logs);
    }

    [Fact]
    public void HoverAndFeatherFallAuras_PreventTheDamage()
    {
        foreach (AuraType type in new[] { AuraType.Hover, AuraType.FeatherFall })
        {
            var scene = new Scene();
            scene.Player.Locomotion.Auras.Add(new SpellAura(0, type, 0, 0, 0));
            scene.Fall(100, 60);
            Assert.Empty(scene.Logs);
        }
    }

    [Fact]
    public void SafeFall_ReducesTheDistance_AndTheAurasAdd()
    {
        var scene = new Scene();
        scene.Player.Locomotion.Auras.Add(new SpellAura(0, AuraType.SafeFall, 17, 0, 0));
        scene.Fall(100, 60);

        // 0.018 * (40 - 17) - 0.2426 = 0.1714 of 3000
        Assert.Equal(514u, DamageOf(Assert.Single(scene.Logs)));

        var both = new Scene();
        both.Player.Locomotion.Auras.Add(new SpellAura(0, AuraType.SafeFall, 17, 0, 0));
        both.Player.Locomotion.Auras.Add(new SpellAura(0, AuraType.SafeFall, 17, 0, 0));
        both.Fall(100, 60);
        Assert.Empty(both.Logs); // 40 - 34 = 6 yards
    }

    [Fact]
    public void AGameMasterADeadPlayerAndATaxiPassenger_TakeNoFallDamage()
    {
        var gm = new Scene(AccountSecurity.GameMaster);
        gm.Player.SetGameMaster(true);
        gm.Fall(100, 60);
        Assert.Empty(gm.Logs);

        var dead = new Scene();
        dead.Player.Map!.Combat.Kill(null, dead.Player);
        dead.Session.Clear();
        dead.Fall(100, 60);
        Assert.Empty(dead.Logs);

        var taxi = new Scene();
        taxi.Player.UnitFlags |= UnitFlags.TaxiFlight;
        taxi.Fall(100, 60);
        Assert.Empty(taxi.Logs);
    }

    [Fact]
    public void TheWorldRate_AppliesToTheFall()
    {
        var scene = new Scene(rate: 0.5f);
        scene.Fall(100, 60);
        Assert.Equal(716u, DamageOf(Assert.Single(scene.Logs)));
    }

    [Fact]
    public void TheTakenModifierSeam_ScalesTheFall()
    {
        var scene = new Scene();
        LocomotionEnvironment.RegisterFallModifiers(scene.World, new Half());
        scene.Fall(100, 60);
        Assert.Equal(716u, DamageOf(Assert.Single(scene.Logs)));
    }

    private sealed class Half : IFallDamageModifiers
    {
        public float TakenDamageMultiplier(Player player) => 0.5f;
    }

    [Fact]
    public void ALethalFall_KillsThePlayer_WithTheDeathConsequences()
    {
        var scene = new Scene();
        scene.Player.MaxHealth = 1000;
        scene.Player.Health = 1000;

        scene.Fall(100, 20); // 80 yards: capped at the maximum health

        Assert.False(scene.Player.IsAlive);
        Assert.Equal(0u, scene.Player.Health);
        Assert.Contains(scene.Session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }
}
