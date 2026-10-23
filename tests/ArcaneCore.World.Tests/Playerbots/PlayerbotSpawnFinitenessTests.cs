using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Characterisation of two helpers the Playerbots de-duplication keeps: the finiteness of a <see cref="CreatureSpawn"/> position
/// (X, Y and Z only; Orientation is not part of the check) and the CMSG_JOIN_CHANNEL payload (CString name, CString empty password).
/// </summary>
public sealed class PlayerbotSpawnFinitenessTests
{
    private static CreatureSpawn Spawn(float x, float y, float z, float orientation = 0f)
        => new() { Guid = 1, Entry = 1, MapId = 0, X = x, Y = y, Z = z, Orientation = orientation };

    [Fact]
    public void SpawnIsFinite_AcceptsFinitePosition()
    {
        Assert.True(Spawn(0f, 0f, 0f).IsFinite());
        Assert.True(Spawn(-1e30f, float.MaxValue, float.MinValue).IsFinite());
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.NaN, 0f)]
    [InlineData(0f, 0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f, 0f)]
    [InlineData(0f, float.NegativeInfinity, 0f)]
    [InlineData(0f, 0f, float.PositiveInfinity)]
    public void SpawnIsFinite_RejectsAnyNonFiniteCoordinate(float x, float y, float z)
        => Assert.False(Spawn(x, y, z).IsFinite());

    [Fact]
    public void SpawnIsFinite_IgnoresOrientation()
        => Assert.True(Spawn(1f, 2f, 3f, float.NaN).IsFinite());

    [Fact]
    public void JoinPacket_IsNameThenEmptyPasswordCStrings()
    {
        Assert.Equal("World\0\0"u8.ToArray(), PlayerbotChannelMembership.JoinPacket("World"));
        Assert.Equal(new byte[] { 0, 0 }, PlayerbotChannelMembership.JoinPacket(string.Empty));
    }
}
