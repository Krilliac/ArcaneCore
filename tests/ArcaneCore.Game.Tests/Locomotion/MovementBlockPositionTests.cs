using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>
/// The invariant of <see cref="Unit.Movement"/>: its position is always the unit's position, however the unit was moved (vmangos
/// <c>WorldObject::Relocate</c> and <c>SetOrientation</c> write <c>m_movementInfo</c> too, Object.cpp:1567-1598). Only the position
/// follows: the flags and the time stamp stay what the last movement or relocation made them.
/// </summary>
public sealed class MovementBlockPositionTests
{
    [Fact]
    public void EveryPositionWrite_OnAPlayer_MovesTheMovementBlock_AndKeepsItsFlagsAndTime()
    {
        Player player = TestWorld.CreatePlayer(1, 10f, 20f, new FakeSession());
        player.Relocate(1f, 2f, 3f, 0.5f, 100);
        player.AddMovementFlags(MovementFlags.WalkMode);

        player.SetPosition(40f, 50f, 60f, 1f);
        AssertMirrors(player, 40f, 50f, 60f, 1f);
        Assert.True(player.Movement.HasFlag(MovementFlags.WalkMode));
        Assert.Equal(100u, player.Movement.Time);

        player.Relocate(41f, 51f, 61f);
        AssertMirrors(player, 41f, 51f, 61f, 1f);

        player.X = 42f;
        player.Y = 52f;
        player.Z = 62f;
        player.Orientation = 2f;
        AssertMirrors(player, 42f, 52f, 62f, 2f);
        Assert.True(player.Movement.HasFlag(MovementFlags.WalkMode));
        Assert.Equal(100u, player.Movement.Time);
    }

    [Fact]
    public void ReapplyingTheStoredMovementBlock_LeavesTheUnitWhereItWasMoved()
    {
        // TeleportService.ResetMovementForTeleport and SpellSystem feign death re-apply the stored block with some flags cleared.
        Player player = TestWorld.CreatePlayer(1, 10f, 20f, new FakeSession());
        player.Relocate(1f, 2f, 3f, 0.5f, 100);
        player.SetPosition(-11209.6f, 1666.54f, 24.69f, 1.42f);

        MovementInfo movement = player.Movement;
        movement.Flags &= ~MovementFlags.MaskMoving;
        player.ApplyMovement(movement, 200);

        Assert.Equal((-11209.6f, 1666.54f, 24.69f, 1.42f), (player.X, player.Y, player.Z, player.Orientation));
        AssertMirrors(player, -11209.6f, 1666.54f, 24.69f, 1.42f);
    }

    [Fact]
    public void ACreaturePlacedWithSetPosition_AndTurned_HasItsMovementBlockThere()
    {
        // TotemSystem places a totem with SetPosition after spawning it at a provisional point; creature AI turns with Orientation.
        var template = new CreatureTemplate { Entry = 5913, Name = "Tremor Totem", Faction = 35, MinLevel = 1, MaxLevel = 1 };
        var creature = new Creature(1, template, null, CreatureContent.Empty, new Random(1));
        creature.Relocate(1f, 2f, 3f, 0f, 100);

        creature.SetPosition(4f, 5f, 3f, 0f);
        creature.Orientation = 1.5f;
        AssertMirrors(creature, 4f, 5f, 3f, 1.5f);
    }

    [Fact]
    public void ARefusedNonFinitePosition_LeavesTheMovementBlockAtTheUnit()
    {
        Player player = TestWorld.CreatePlayer(1, 10f, 20f, new FakeSession());
        player.Relocate(1f, 2f, 3f, 0.5f, 100);
        using (Kernel.Diagnostics.Invariant.Capture())
        {
            player.SetPosition(float.NaN, 2f, 3f, 0.75f);
        }

        Assert.True(player.ConsumeRejectedNonFiniteRelocation());
        AssertMirrors(player, 1f, 2f, 3f, 0.75f);
    }

    private static void AssertMirrors(Unit unit, float x, float y, float z, float orientation)
    {
        Assert.Equal((x, y, z, orientation), (unit.X, unit.Y, unit.Z, unit.Orientation));
        MovementInfo movement = unit.Movement;
        Assert.Equal((x, y, z, orientation), (movement.X, movement.Y, movement.Z, movement.Orientation));
    }
}
