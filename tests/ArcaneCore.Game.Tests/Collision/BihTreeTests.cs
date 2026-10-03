using System.Numerics;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>The BIH reader/traversal against brute force, hand-built nodes and corrupt trees.</summary>
public sealed class BihTreeTests
{
    private static List<(Vector3 Low, Vector3 High)> RandomBoxes(int count, int seed)
    {
        var random = new Random(seed);
        var boxes = new List<(Vector3, Vector3)>();
        for (int i = 0; i < count; i++)
        {
            var low = new Vector3(random.NextSingle() * 100, random.NextSingle() * 100, random.NextSingle() * 100);
            boxes.Add((low, low + new Vector3(1 + random.NextSingle() * 5, 1 + random.NextSingle() * 5, 1 + random.NextSingle() * 5)));
        }

        return boxes;
    }

    private static float RayBox(Vector3 o, Vector3 d, (Vector3 Low, Vector3 High) box, float max)
    {
        float tMin = 0;
        float tMax = max;
        for (int a = 0; a < 3; a++)
        {
            float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
            float da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            float lo = a == 0 ? box.Low.X : a == 1 ? box.Low.Y : box.Low.Z;
            float hi = a == 0 ? box.High.X : a == 1 ? box.High.Y : box.High.Z;
            if (MathF.Abs(da) < 1e-12f)
            {
                if (oa < lo || oa > hi)
                {
                    return float.PositiveInfinity;
                }

                continue;
            }

            float t1 = (lo - oa) / da;
            float t2 = (hi - oa) / da;
            tMin = MathF.Max(tMin, MathF.Min(t1, t2));
            tMax = MathF.Min(tMax, MathF.Max(t1, t2));
        }

        return tMin <= tMax ? tMin : float.PositiveInfinity;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 2)]
    [InlineData(200, 3)]
    public void Ray_FindsTheSameNearestBoxAsBruteForce(int count, int seed)
    {
        List<(Vector3 Low, Vector3 High)> boxes = RandomBoxes(count, seed);
        BihTree tree = BihTree.Build(boxes);
        var random = new Random(seed + 100);
        for (int q = 0; q < 300; q++)
        {
            var origin = new Vector3(random.NextSingle() * 140 - 20, random.NextSingle() * 140 - 20, random.NextSingle() * 140 - 20);
            Vector3 direction = q % 10 == 0
                ? Vector3.UnitZ * (q % 20 == 0 ? 1 : -1) // axis-parallel rays exercise the parallel branches
                : Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            const float limit = 150;

            float expected = boxes.Select(b => RayBox(origin, direction, b, limit)).DefaultIfEmpty(float.PositiveInfinity).Min();
            float max = limit;
            tree.IntersectRay(origin, direction, ref max, (int i, ref float m) =>
            {
                float t = RayBox(origin, direction, boxes[i], m);
                if (t < m)
                {
                    m = t;
                    return true;
                }

                return false;
            }, stopAtFirstHit: false);

            if (float.IsPositiveInfinity(expected))
            {
                Assert.Equal(limit, max);
            }
            else
            {
                Assert.Equal(expected, max, 3);
            }
        }
    }

    [Fact]
    public void Point_VisitsEveryBoxContainingIt()
    {
        List<(Vector3 Low, Vector3 High)> boxes = RandomBoxes(150, 9);
        BihTree tree = BihTree.Build(boxes);
        var random = new Random(10);
        for (int q = 0; q < 300; q++)
        {
            var p = new Vector3(random.NextSingle() * 105, random.NextSingle() * 105, random.NextSingle() * 105);
            var visited = new HashSet<int>();
            tree.IntersectPoint(p, i => visited.Add(i));
            var containing = Enumerable.Range(0, boxes.Count).Where(i => BihTree.Contains(boxes[i].Low, boxes[i].High, p));
            Assert.Subset(visited, containing.ToHashSet());
        }
    }

    [Fact]
    public void RoundTrip_ThroughTheFileLayout_KeepsTheTree()
    {
        BihTree tree = BihTree.Build(RandomBoxes(20, 4));
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            tree.Write(writer);
        }

        byte[] bytes = stream.ToArray();
        var reader = new Maps.Collision.CollisionDataReader(bytes);
        BihTree copy = BihTree.Read(ref reader);
        Assert.Equal(tree.Low, copy.Low);
        Assert.Equal(tree.High, copy.High);
        Assert.Equal(tree.Nodes, copy.Nodes);
        Assert.Equal(tree.Objects, copy.Objects);
        Assert.Equal(bytes.Length, reader.Position);
    }

    [Fact]
    public void Bvh2Node_ClipsToItsInterval()
    {
        // root: BVH2 on X with interval [10, 20] → leaf with primitive 0.
        uint[] nodes =
        [
            (0u << 30) | (1u << 29) | 3u, BitConverter.SingleToUInt32Bits(10), BitConverter.SingleToUInt32Bits(20),
            (3u << 30) | 0u, 1, 0,
        ];
        BihTree tree = BihTree.FromRaw(new Vector3(0, 0, 0), new Vector3(30, 10, 10), nodes, [0]);

        Assert.True(Hits(tree, new Vector3(15, 5, -5), Vector3.UnitZ));
        Assert.False(Hits(tree, new Vector3(25, 5, -5), Vector3.UnitZ));
        Assert.True(Hits(tree, new Vector3(-5, 5, 5), Vector3.UnitX));

        var visited = new List<int>();
        tree.IntersectPoint(new Vector3(15, 5, 5), visited.Add);
        tree.IntersectPoint(new Vector3(5, 5, 5), visited.Add);
        Assert.Equal([0], visited);
    }

    [Fact]
    public void CorruptTree_YieldsNoHits_AndDoesNotThrowOrLoop()
    {
        // A child offset pointing at itself, one past the end, and a leaf overrunning the objects.
        BihTree selfLoop = BihTree.FromRaw(Vector3.Zero, new Vector3(10), [(0u << 30) | 0u, 0, 0], [0]);
        BihTree pastEnd = BihTree.FromRaw(Vector3.Zero, new Vector3(10), [(1u << 30) | 300u, 0, 0], [0]);
        BihTree overrun = BihTree.FromRaw(Vector3.Zero, new Vector3(10), [(3u << 30) | 5u, 9, 0], [0]);

        foreach (BihTree tree in new[] { selfLoop, pastEnd, overrun })
        {
            Assert.False(Hits(tree, new Vector3(5, 5, -5), Vector3.UnitZ));
            tree.IntersectPoint(new Vector3(5, 5, 5), _ => Assert.Fail("no primitive expected"));
        }

        Assert.Throws<InvalidDataException>(() => BihTree.FromRaw(Vector3.Zero, Vector3.One, [0], []));
    }

    [Fact]
    public void Truncated_TreeBytes_AreRejected()
    {
        BihTree tree = BihTree.Build(RandomBoxes(5, 1));
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            tree.Write(writer);
        }

        byte[] bytes = stream.ToArray()[..^3];
        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new Maps.Collision.CollisionDataReader(bytes);
            BihTree.Read(ref reader);
        });
    }

    private static bool Hits(BihTree tree, Vector3 origin, Vector3 direction)
    {
        bool hit = false;
        float max = 100;
        tree.IntersectRay(origin, direction, ref max, (int _, ref float _) => hit = true, stopAtFirstHit: true);
        return hit;
    }
}
