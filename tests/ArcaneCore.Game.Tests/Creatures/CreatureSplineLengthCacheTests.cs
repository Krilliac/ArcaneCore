using System.Collections;
using System.Numerics;
using ArcaneCore.Game.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Audit lane 4 (spline lengths): <c>CreatureSpline</c> computed every leg length twice per locate. The lengths are now cached
/// on first use and the leg is found by binary search; positions, leg index and blended heading must match the old walk.
/// </summary>
public sealed class CreatureSplineLengthCacheTests
{
    /// <summary>A path that counts element reads, to show a second locate no longer walks every leg.</summary>
    private sealed class CountingPath(Vector3[] points) : IReadOnlyList<Vector3>
    {
        public int Reads { get; private set; }

        public int Count => points.Length;

        public Vector3 this[int index]
        {
            get
            {
                Reads++;
                return points[index];
            }
        }

        public IEnumerator<Vector3> GetEnumerator() => ((IEnumerable<Vector3>)points).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static CreatureSpline Build(Vector3 start, IReadOnlyList<Vector3> path, uint duration)
    {
        Vector3 end = path[^1];
        return new CreatureSpline(1, start.X, start.Y, start.Z, end.X, end.Y, end.Z, false, null, 0L, duration)
        {
            Path = path,
        };
    }

    [Fact]
    public void RepeatedLocates_DoNotRewalkEveryLeg()
    {
        var points = new Vector3[32];
        var rng = new Random(11);
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new Vector3(i * 5f, (float)rng.NextDouble() * 10f, 0f);
        }

        var counting = new CountingPath(points);
        CreatureSpline spline = Build(Vector3.Zero, counting, 10_000);

        _ = spline.PositionAndOrientationAt(5_000, 0f);
        counting.Reads = 0;
        for (int i = 0; i < 10; i++)
        {
            _ = spline.PositionAndOrientationAt(5_000, 0f);
        }

        // Steady state touches only the located leg and its neighbours for the corner blend (about a dozen reads), not all 32 legs twice per call.
        Assert.True(counting.Reads <= 10 * 16, $"steady-state locates read {counting.Reads} path points");
    }

    [Fact]
    public void RandomMultiPointSplines_MatchTheUncachedAlgorithm()
    {
        var rng = new Random(20261031);
        for (int trial = 0; trial < 200; trial++)
        {
            int count = rng.Next(2, 40);
            var start = new Vector3(Next(rng), Next(rng), Next(rng));
            var points = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                // Some repeated points give zero-length legs, which the old code treated specially.
                points[i] = rng.Next(8) == 0 && i > 0 ? points[i - 1] : new Vector3(Next(rng), Next(rng), Next(rng));
            }

            CreatureSpline spline = Build(start, points, 20_000);
            for (long clock = -500; clock <= 21_000; clock += 137)
            {
                float t = Math.Clamp(clock, 0L, 20_000L) / 20_000f;
                (Vector3 expectedPosition, int expectedSegment, float expectedHeading) = OldLocate(start, points, t, 0.25f);

                (float x, float y, float z, float heading) = spline.PositionAndOrientationAt(clock, 0.25f);
                Assert.True(Vector3.Distance(expectedPosition, new Vector3(x, y, z)) < 1e-4f, $"trial {trial} clock {clock}: position");
                Assert.True(AngleDiff(expectedHeading, heading) < 1e-4f, $"trial {trial} clock {clock}: heading");

                int segment = spline.Points.Count - spline.RemainingPoints(clock).Count;
                Assert.Equal(expectedSegment, segment);
            }
        }
    }

    [Fact]
    public void RemainingPoints_ReturnsTheTailFromTheCurrentLeg()
    {
        var points = new[] { new Vector3(10, 0, 0), new Vector3(20, 0, 0), new Vector3(30, 0, 0) };
        CreatureSpline spline = Build(Vector3.Zero, points, 3_000);

        Assert.Equal(points, spline.RemainingPoints(0));
        Assert.Equal(points[1..], spline.RemainingPoints(1_500));
        Assert.Equal(points[2..], spline.RemainingPoints(3_000));
    }

    private static float Next(Random rng) => (float)((rng.NextDouble() * 200.0) - 100.0);

    private static float AngleDiff(float a, float b)
    {
        float d = Math.Abs(a - b) % (MathF.PI * 2f);
        return d > MathF.PI ? (MathF.PI * 2f) - d : d;
    }

    // The pre-cache algorithm (two distance passes per call), kept as the reference.
    private static (Vector3 Position, int Segment, float Orientation) OldLocate(Vector3 start, Vector3[] points, float t, float fallback)
    {
        if (points.Length == 1)
        {
            return (Vector3.Lerp(start, points[0], t), 0, Heading(start, points[0], fallback));
        }

        float total = 0;
        Vector3 previous = start;
        for (int i = 0; i < points.Length; i++)
        {
            total += Vector3.Distance(previous, points[i]);
            previous = points[i];
        }

        float target = total * t;
        previous = start;
        for (int i = 0; i < points.Length; i++)
        {
            float segment = Vector3.Distance(previous, points[i]);
            if (target <= segment || i == points.Length - 1)
            {
                float f = segment <= 0 ? 1f : Math.Clamp(target / segment, 0f, 1f);
                float segHeading = Heading(previous, points[i], fallback);
                return (Vector3.Lerp(previous, points[i], f), i, Blend(start, points, i, segment, f, segHeading));
            }

            target -= segment;
            previous = points[i];
        }

        throw new InvalidOperationException("unreachable");
    }

    private static float Blend(Vector3 start, Vector3[] points, int index, float length, float f, float segHeading)
    {
        if (length <= 0.1f)
        {
            return segHeading;
        }

        float window = MathF.Min(0.25f, 1.25f / length);
        if (window <= 0.01f)
        {
            return segHeading;
        }

        if (index > 0 && f < window)
        {
            Vector3 prevStart = index == 1 ? start : points[index - 2];
            float prevHeading = Heading(prevStart, points[index - 1], segHeading);
            float mid = CreatureSpline.LerpAngle(prevHeading, segHeading, 0.5f);
            return CreatureSpline.LerpAngle(mid, segHeading, f / window);
        }

        if (index < points.Length - 1 && f > 1f - window)
        {
            float nextHeading = Heading(points[index], points[index + 1], segHeading);
            float mid = CreatureSpline.LerpAngle(segHeading, nextHeading, 0.5f);
            return CreatureSpline.LerpAngle(segHeading, mid, (f - (1f - window)) / window);
        }

        return segHeading;
    }

    private static float Heading(Vector3 from, Vector3 to, float fallback)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        if ((dx * dx) + (dy * dy) <= 0.0001f)
        {
            return fallback;
        }

        float h = MathF.Atan2(dy, dx);
        return h < 0 ? h + (MathF.PI * 2f) : h;
    }
}
