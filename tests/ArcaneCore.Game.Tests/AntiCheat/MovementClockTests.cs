using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.AntiCheat;

/// <summary>
/// The gateway-to-worker clock translation and what it buys the movement checks: with the gateway's receive times a legal run that
/// crosses a delayed link scores nothing, with the worker's own arrival times the same run is scored for speed.
/// </summary>
public sealed class MovementClockTests
{
    private const float Run = 7.0f;
    private const uint HeartbeatMs = 500;
    private const int Beats = 40;
    private const int DelayedBeat = 20;
    private const uint LinkDelayMs = 200;

    [Fact]
    public void Estimate_AddsTheOffset_AndWrapsLikeTheClocks()
    {
        MovementClock clock = MovementClock.Estimate(gatewayMs: 1_000, workerMs: 51_000);
        Assert.Equal(50_000u, clock.OffsetMs);
        Assert.Equal(51_500u, clock.ToWorld(1_500, workerNowMs: 60_000));

        // Both clocks wrap at 2^32: a gateway reading just below the wrap maps to a worker time just past it.
        MovementClock wrapped = MovementClock.Estimate(gatewayMs: 0xFFFFFF00u, workerMs: 100u);
        Assert.Equal(300u, wrapped.ToWorld(0xFFFFFF00u + 200u, workerNowMs: 1_000));
    }

    [Fact]
    public void ToWorld_NeverReturnsAFutureTime()
    {
        MovementClock clock = MovementClock.Estimate(1_000, 51_000);
        Assert.Equal(52_000u, clock.ToWorld(5_000, workerNowMs: 52_000)); // would be 55_000: clamped to now
        Assert.Equal(52_000u, clock.ToWorld(2_000, workerNowMs: 52_000)); // exactly now stays
    }

    [Fact]
    public void LegalRun_WithA200MsLinkDelay_ReportsNoSpeedViolation_OnTheGatewayReceiveTime()
    {
        List<AntiCheatFinding> findings = RunWithOneDelayedPacket(useGatewayStamp: true);
        Assert.Empty(findings);
    }

    [Fact]
    public void SameRun_OnTheWorkerArrivalTime_IsScoredForSpeed()
    {
        // The control: the link delay compresses one receive interval to 300 ms, so the strict budget below no longer covers 500 ms of running.
        List<AntiCheatFinding> findings = RunWithOneDelayedPacket(useGatewayStamp: false);
        Assert.Contains(findings, f => f.Type == AntiCheatViolation.Speed);
    }

    /// <summary>
    /// A client runs at the allowed speed with a heartbeat every 500 ms; the gateway stamps each packet when it arrives and the
    /// link delivers every packet on time except one, which it holds for 200 ms. The checks are strict (no tolerance, quarter-yard slack,
    /// no latency slack) so the jitter shows. The worker clock starts 50 s ahead of the gateway's, as the handshake estimate would find.
    /// </summary>
    private static List<AntiCheatFinding> RunWithOneDelayedPacket(bool useGatewayStamp)
    {
        var options = new AntiCheatOptions { SpeedTolerancePercent = 0, SpeedSlackYards = 0.25f, LatencySlackMs = 0, MaxLatencySlackMs = 0 };
        var checks = new MovementAntiCheat(options);
        var all = new List<AntiCheatFinding>();

        uint gatewayMs = 10_000;
        uint workerMs = 60_000;
        MovementClock clock = MovementClock.Estimate(gatewayMs, workerMs);
        uint clientTime = 1_000_000;
        float x = 100;
        float storedX = x;

        for (int beat = 0; beat <= Beats; beat++)
        {
            if (beat > 0)
            {
                gatewayMs += HeartbeatMs;
                clientTime += HeartbeatMs;
                x += Run * 0.5f;
            }

            uint idealWorldMs = unchecked(gatewayMs + clock.OffsetMs);
            uint arrivalMs = beat == DelayedBeat ? unchecked(idealWorldMs + LinkDelayMs) : idealWorldMs;
            uint receivedMs = useGatewayStamp ? clock.ToWorld(gatewayMs, arrivalMs) : arrivalMs;

            var findings = new List<AntiCheatFinding>();
            var movement = new MovementInfo { Flags = MovementFlags.Forward, Time = clientTime, X = x, Y = 100, Z = 10 };
            checks.CheckServerPosition(storedX, 100, 10);
            checks.Observe(new MovementSample
            {
                Opcode = beat == 0 ? WorldOpcode.MsgMoveStartForward : WorldOpcode.MsgMoveHeartbeat,
                Movement = movement,
                ReceivedMs = receivedMs,
                LatencyMs = 0,
                AllowedSpeed = Run,
                Alive = true,
                Transport = TransportClaim.None,
            }, findings);
            checks.NotifyStored(x, 100, 10);
            storedX = x;
            all.AddRange(findings);
        }

        return all;
    }
}
