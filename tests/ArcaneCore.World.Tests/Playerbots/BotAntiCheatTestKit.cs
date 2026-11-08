using System.Collections.Concurrent;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The control for a test that asserts the anticheat found nothing on a bot's trip on the manual clock: a clean result means nothing
/// unless the checks were judging. Run it after that assertion, on the same bot.
/// </summary>
internal static class BotAntiCheatTestKit
{
    /// <summary>Over the run speed in the long run (11 yd/s against 7.7), under one step's own budget of a 500 ms beat (5.85 yd), so only the distance budget can score it.</summary>
    private const float OverrunYards = 5.5f;

    private const uint BeatMs = 500;

    /// <summary>A full budget is 9.7 yd: the first beat spends 5.5, each later one 1.65 net of the refill, so the overdraft comes on the fourth beat, the fifth after a re-baseline.</summary>
    private const int Beats = 12;

    /// <summary>
    /// The distance budget charged at least <paramref name="atLeastYards"/> on the way here, so the clean result covered real steps. Then the
    /// same bot overruns the budget through the real handler, stamped on the game clock, and is scored for it: the honest walk was clean
    /// because the refill kept up with it, not because the check was off.
    /// </summary>
    public static async Task AssertJudgedThenOverrunIsScoredAsync(WorldTestHost host, WorldSession session, AntiCheatFeature anticheat,
        ConcurrentQueue<AntiCheatFinding> findings, float atLeastYards)
    {
        float judged = await host.OnWorldAsync(() => anticheat.BudgetedYardsOf(session.Player!));
        Assert.True(judged >= atLeastYards, $"the distance budget judged only {judged:F1} yards of the trip");

        for (int beat = 0; beat < Beats; beat++)
        {
            await host.World.AdvanceClockAsync(BeatMs);
            await host.OnWorldAsync(() =>
            {
                Player bot = session.Player!;
                var packet = new PacketWriter(64);
                new MovementInfo
                {
                    Flags = MovementFlags.Forward, Time = host.World.NowMs, X = bot.X + OverrunYards, Y = bot.Y, Z = bot.Z, Orientation = bot.Orientation,
                }.Write(packet);
                Assert.True(session.TryManagedAction(WorldOpcode.MsgMoveHeartbeat, packet.ToArray()), "heartbeat refused");
                Assert.Equal(host.World.NowMs, session.CurrentPacketReceivedMs);
            });
        }

        Assert.Contains(findings, f => f.Type == AntiCheatViolation.Speed && f.Detail == "faster than the allowed speed over several packets");
    }
}
