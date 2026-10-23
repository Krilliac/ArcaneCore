using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>
/// Characterisation of the bearing (vmangos WorldObject::GetAngle(target), atan2 of the offset normalised to [0, 2 pi)) that
/// <see cref="ForcedMovement.BearingFromTo"/> and the knock-back direction both compute.
/// </summary>
public sealed class BearingFromToTests : IDisposable
{
    private readonly SpellTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private (Player From, Player To) Pair(float toX, float toY)
    {
        (Player from, _) = _kit.AddPlayer(1, 0, 0);
        (Player to, _) = _kit.AddPlayer(2, toX, toY);
        return (from, to);
    }

    [Theory]
    [InlineData(5f, 0f, 0f)]                       // east
    [InlineData(0f, 4f, MathF.PI / 2)]             // north
    [InlineData(-3f, 0f, MathF.PI)]                // west
    [InlineData(0f, -4f, 3 * MathF.PI / 2)]        // south: atan2 is -pi/2, wrapped by 2 pi
    [InlineData(3f, -3f, 7 * MathF.PI / 4)]        // south-east: wrapped
    [InlineData(-2f, 2f, 3 * MathF.PI / 4)]        // north-west: not wrapped
    public void BearingFromTo_IsTheOffsetsAtan2_InZeroToTwoPi(float x, float y, float expected)
    {
        (Player from, Player to) = Pair(x, y);
        float bearing = ForcedMovement.BearingFromTo(from, to);
        Assert.Equal(expected, bearing, 4);
        Assert.InRange(bearing, 0f, 2 * MathF.PI);
    }

    [Fact]
    public void BearingFromTo_IsMeasuredFromTheFirstUnit_SoSwappingTheArgumentsTurnsItByPi()
    {
        (Player from, Player to) = Pair(0f, -4f);
        Assert.Equal(3 * MathF.PI / 2, ForcedMovement.BearingFromTo(from, to), 4);
        Assert.Equal(MathF.PI / 2, ForcedMovement.BearingFromTo(to, from), 4);
    }

    [Fact]
    public void BearingFromTo_OfCoincidentUnits_IsZero()
    {
        (Player from, Player to) = Pair(0f, 0f);
        Assert.Equal(0f, ForcedMovement.BearingFromTo(from, to));
    }

    [Fact]
    public void BearingFromTo_NullArguments_ThrowArgumentNullException()
    {
        (Player from, Player to) = Pair(1f, 1f);
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.BearingFromTo(null!, to));
        Assert.Throws<ArgumentNullException>(() => ForcedMovement.BearingFromTo(from, null!));
    }

    [Theory]
    [InlineData(0f, -4f, 0f, -1f)]   // target south of the source: flung south
    [InlineData(-5f, 0f, -1f, 0f)]   // target west of the source: flung west
    public void KnockBackFrom_AnotherSource_FlingsTheUnitAwayFromIt_ThroughTheWrappedBearing(float x, float y, float cos, float sin)
    {
        using var kit = new SpellTestKit();
        (Player source, _) = kit.AddPlayer(1, 0, 0);
        (Player target, FakeSession session) = kit.AddPlayer(2, x, y);
        kit.World.RunTick(50);
        session.Clear();

        Assert.True(KnockbackService.KnockBackFrom(kit.System, target, source, 10f, 3f));

        byte[] order = Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgMoveKnockBack).Payload;
        var reader = new PacketReader(order);
        reader.ReadPackedGuid();
        reader.ReadUInt32();
        Assert.Equal(cos, reader.ReadSingle(), 4);
        Assert.Equal(sin, reader.ReadSingle(), 4);
    }

    [Fact]
    public void KnockBackFrom_NullArguments_ThrowArgumentNullException()
    {
        using var kit = new SpellTestKit();
        (Player a, _) = kit.AddPlayer(1, 0, 0);
        Assert.Throws<ArgumentNullException>(() => KnockbackService.KnockBackFrom(null!, a, a, 1f, 1f));
        Assert.Throws<ArgumentNullException>(() => KnockbackService.KnockBackFrom(kit.System, null!, a, 1f, 1f));
        Assert.Throws<ArgumentNullException>(() => KnockbackService.KnockBackFrom(kit.System, a, null!, 1f, 1f));
    }
}
