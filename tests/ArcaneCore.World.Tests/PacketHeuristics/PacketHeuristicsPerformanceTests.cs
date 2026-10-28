using System.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Net;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.PacketHeuristics;

/// <summary>
/// What the checks cost with 1500 sessions: the per-packet price of the network-path hook (shape, cadence, replay) on real <see cref="WorldSession"/>
/// objects, and what one simulated 100 ms tick (every session sends one packet) adds. The numbers go to the test output; the assertions are generous
/// bounds that only catch an order-of-magnitude regression, plus the exact claim that the steady state allocates nothing. The cost bounds are on
/// the measuring thread's CPU time (<see cref="ThreadCpuTime"/>), not wall time: on a runner shared with other gates a pass's wall time includes
/// the time the thread waited for a core (Node1, five concurrent gates: 5.2 ms per tick of wall time against the 5 ms bound). Wall time is still
/// printed, and is the bound only where the platform gives no thread CPU time.
/// </summary>
public sealed class PacketHeuristicsPerformanceTests(ITestOutputHelper output)
{
    private const int Sessions = 1500;
    private const int Rounds = 200;

    [Fact]
    public async Task ThePerPacketCost_With1500Sessions_IsSmall_AndTheSteadyStateAllocatesNothing()
    {
        await using var host = WorldTestHost.Start();
        long fakeNow = 1_000_000;
        (PacketHeuristicsFeature feature, _) = PacketHeuristicsTestKit.Enable(host, o => o.ExemptBots = false, () => fakeNow);

        var sessions = new WorldSession[Sessions];
        for (int i = 0; i < sessions.Length; i++)
        {
            sessions[i] = await PacketHeuristicsTestKit.NewBotAtCharacterScreenAsync(host, "PERF" + i);
        }

        // Four message kinds a player sends all the time, each with a payload pool so that nothing repeats within a replay window.
        WorldOpcode[] opcodes = [WorldOpcode.CmsgSetSelection, WorldOpcode.MsgMoveHeartbeat, WorldOpcode.CmsgCastSpell, WorldOpcode.CmsgNameQuery];
        int[] lengths = [8, 28, 14, 8];
        byte[][][] pool = new byte[opcodes.Length][][];
        for (int k = 0; k < opcodes.Length; k++)
        {
            pool[k] = new byte[64][];
            for (int p = 0; p < 64; p++)
            {
                pool[k][p] = new byte[lengths[k]];
                BitConverter.GetBytes(p + 1).CopyTo(pool[k][p], 0);
            }
        }

        void Round(int round)
        {
            fakeNow += 100;
            for (int s = 0; s < sessions.Length; s++)
            {
                int kind = (round + s) & 3;
                feature.OnClientPacket(sessions[s], opcodes[kind], pool[kind][(round + s) & 63]);
            }
        }

        // Warm-up: creates the per-session state and JITs the path.
        int nextRound = 0;
        for (; nextRound < 16; nextRound++)
        {
            Round(nextRound);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long leastAllocated = long.MaxValue;
        long bestElapsedTicks = long.MaxValue;
        TimeSpan? bestCpu = null;
        long findings = 0;
        for (int pass = 0; pass < 3; pass++)
        {
            long before = Total(feature);
            // The CPU clock reads allocate (Linux reads a /proc file): they stay outside the allocation window.
            TimeSpan? startCpu = ThreadCpuTime.Now();
            long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            long startTick = Stopwatch.GetTimestamp();
            for (int r = 0; r < Rounds; r++)
            {
                Round(nextRound++);
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - startTick;
            long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
            TimeSpan? endCpu = ThreadCpuTime.Now();
            if (startCpu is { } c0 && endCpu is { } c1 && (bestCpu is null || c1 - c0 < bestCpu))
                bestCpu = c1 - c0;
            leastAllocated = Math.Min(leastAllocated, afterAlloc - beforeAlloc);
            bestElapsedTicks = Math.Min(bestElapsedTicks, elapsedTicks);
            findings += Total(feature) - before;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(0, bestElapsedTicks);
        long allocated = leastAllocated;

        double packets = (double)Sessions * Rounds;
        // The bounds read the thread's CPU time when there is one (see the class summary); the wall time is reported beside it.
        TimeSpan cost = bestCpu ?? elapsed;
        string clock = bestCpu is null ? "wall" : "thread-cpu";
        double nsPerPacket = cost.TotalMilliseconds * 1_000_000d / packets;
        double msPerTick = cost.TotalMilliseconds / Rounds;
        output.WriteLine($"PERF sessions={Sessions} rounds={Rounds} packets={packets:N0} elapsed={elapsed.TotalMilliseconds:F1} ms wall, {cost.TotalMilliseconds:F1} ms {clock} ns/packet={nsPerPacket:F1} ms/tick({Sessions} packets)={msPerTick:F4} ({clock}) allocatedBytes={allocated} findings={findings}");

        Assert.Equal(0, findings); // the workload is legitimate traffic: nothing is flagged
        Assert.Equal(0, allocated);
        Assert.True(nsPerPacket < 5_000, $"{nsPerPacket:F1} ns per packet ({clock})");
        Assert.True(msPerTick < 5, $"{msPerTick:F3} ms per tick of {Sessions} packets ({clock}; wall {elapsed.TotalMilliseconds / Rounds:F3})");

        // The same loop with the feature off, for the reference price of the gate.
        feature.SetEnabled(false);
        long offStart = Stopwatch.GetTimestamp();
        for (int r = 0; r < Rounds; r++)
        {
            Round(nextRound++);
        }

        TimeSpan off = Stopwatch.GetElapsedTime(offStart);
        output.WriteLine($"PERF off: ns/packet={off.TotalMilliseconds * 1_000_000d / packets:F1}");
    }

    private static long Total(PacketHeuristicsFeature feature)
    {
        long total = 0;
        for (int c = 0; c < PacketChecks.Count; c++)
        {
            for (int t = 0; t < PacketChecks.TrafficKinds; t++)
            {
                total += feature.Count((PacketCheck)c, (PacketTraffic)t);
            }
        }

        return total;
    }
}
