using System.Numerics;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>AC-PI-001: a crafted WMO liquid header must be rejected with InvalidDataException, never allocate.</summary>
public sealed class WmoLiquidHardeningTests
{
    private static byte[] ModelWithLiquid(uint tilesX, uint tilesY, int heightCount = 0, int flagCount = 0)
    {
        var liquid = new WmoLiquid(tilesX, tilesY, Vector3.Zero, 1, new float[heightCount], new byte[flagCount]);
        WorldModel box = VMapFixture.Box(Vector3.Zero, Vector3.One);
        var group = new GroupModel(Vector3.Zero, Vector3.One, 0, 1, [.. box.Groups[0].Vertices], [.. box.Groups[0].Triangles], liquid: liquid);
        return new WorldModel(1, [group]).ToBytes();
    }

    [Theory]
    [InlineData(0x40000000u, 0x80000000u)] // (X+1)*(Y+1)*4 wraps negative in long arithmetic
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu)]
    [InlineData(0xFFFFFFFFu, 0u)]
    [InlineData(0u, 0xFFFFFFFFu)]
    [InlineData(0x7FFFFFFFu, 0x7FFFFFFFu)]
    [InlineData(1000u, 1000u)]
    public void OversizedLiquidGrid_IsRejected_AsInvalidData_WithoutAllocating(uint tilesX, uint tilesY)
    {
        byte[] bytes = ModelWithLiquid(tilesX, tilesY);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => WorldModel.Parse(bytes));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1_000_000, $"parse allocated {allocated} bytes");
    }

    [Fact]
    public void LiquidLargerThanItsChunk_IsRejected_EvenWhenLaterBytesExist()
    {
        // Grid claims 1x1 (needs 4*4+1 bytes) but the LIQU chunk only holds the 24-byte header.
        byte[] bytes = ModelWithLiquid(1, 1);
        Assert.Throws<InvalidDataException>(() => WorldModel.Parse(bytes));
    }

    [Fact]
    public void ValidLiquid_StillParses()
    {
        WorldModel copy = WorldModel.Parse(ModelWithLiquid(1, 1, heightCount: 4, flagCount: 1));
        Assert.Equal(4, copy.Groups[0].Liquid!.Heights.Length);
    }
}
