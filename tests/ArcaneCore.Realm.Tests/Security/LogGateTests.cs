using ArcaneCore.Kernel.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>One line per interval, the rest counted; the fast path allocates nothing.</summary>
public sealed class LogGateTests
{
    [Fact]
    public void AdmitsOnePerInterval_AndReportsHowManyItSuppressed()
    {
        long now = 1_000;
        var gate = new LogGate(TimeSpan.FromSeconds(10), () => now);

        Assert.True(gate.TryEnter(out int suppressed));
        Assert.Equal(0, suppressed);

        for (int i = 0; i < 5; i++)
        {
            Assert.False(gate.TryEnter(out _));
        }

        Assert.Equal(5, gate.Suppressed);
        now += 9_999;
        Assert.False(gate.TryEnter(out _));

        now += 1;
        Assert.True(gate.TryEnter(out suppressed));
        Assert.Equal(6, suppressed);
        Assert.Equal(0, gate.Suppressed);
    }

    [Fact]
    public void ZeroInterval_AdmitsEveryLine()
    {
        long now = 0;
        var gate = new LogGate(TimeSpan.Zero, () => now);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(gate.TryEnter(out int suppressed));
            Assert.Equal(0, suppressed);
        }
    }

    [Fact]
    public void NegativeInterval_IsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new LogGate(TimeSpan.FromSeconds(-1)));

    [Fact]
    public void ConcurrentCallers_AdmitExactlyOneLinePerInterval_AndLoseNoCount()
    {
        long now = 0;
        var gate = new LogGate(TimeSpan.FromSeconds(10), () => Volatile.Read(ref now));
        int admitted = 0;
        int reportedByAdmitted = 0;
        Parallel.For(0, 10_000, _ =>
        {
            if (gate.TryEnter(out int reported))
            {
                Interlocked.Increment(ref admitted);
                Interlocked.Add(ref reportedByAdmitted, reported);
            }
        });

        Assert.Equal(1, admitted);
        // Callers refused before the admitted one drained the counter are reported on that admitted line, not left in Suppressed
        // (which of the two depends on thread timing), so every refused call is accounted for exactly once across the two.
        Assert.Equal(9_999, reportedByAdmitted + gate.Suppressed);

        int pending = gate.Suppressed;
        Volatile.Write(ref now, 10_000);
        Assert.True(gate.TryEnter(out int suppressed));
        Assert.Equal(pending, suppressed);
    }

    [Fact]
    public void TryEnter_DoesNotAllocate()
    {
        var gate = new LogGate(TimeSpan.FromSeconds(10));
        gate.TryEnter(out _); // warm up: first call admits
        // A bare before/after allocation window occasionally read one allocation quantum (3328 B) with nothing allocated by the code under
        // test (cross-thread GC noise while test classes run in parallel; same class as the PacketReader flake). The probe asserts every
        // GC-free window against the exact limit, so a real per-call allocation still fails.
        Action window = () =>
        {
            for (int i = 0; i < 100_000; i++)
            {
                gate.TryEnter(out _);
            }
        };

        ArcaneCore.TestSupport.AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "LogGate.TryEnter x100000");
        Assert.True(gate.Suppressed > 0); // the windows ran the refused path
    }
}
