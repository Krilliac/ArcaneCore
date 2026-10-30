using System.Diagnostics;
using ArcaneCore.Game.ClientErrors;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.Observability;

/// <summary>The client-rejection log and the duplicate outbound packet detector (docs/ops/observability.md).</summary>
public sealed class ObservabilityTests
{
    private sealed class Capture(LogLevel minimum) : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                lock (Lines)
                {
                    Lines.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }

    private static readonly SessionLogContext Player = new(false, 7, 1234, 1);
    private static readonly SessionLogContext Bot = new(true, 8, 99, 0);

    private static byte[] ItemPush(uint entry, uint count)
    {
        var w = new PacketWriter(45);
        w.WriteUInt64(0x1234);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(1);
        w.WriteByte(255);
        w.WriteUInt32(0xFFFFFFFF);
        w.WriteUInt32(entry);
        w.WriteUInt32(0);
        w.WriteInt32(0);
        w.WriteUInt32(count);
        return w.ToArray();
    }

    // ---- B: duplicates ----------------------------------------------------------------------------------------------

    private sealed class Clock
    {
        public long Ticks = 1000;

        public void AdvanceMs(long ms) => Ticks += ms * Stopwatch.Frequency / 1000;
    }

    private static (DuplicatePacketDetector Detector, DuplicatePacketDetector.SessionState State, Capture Log, Clock Clock) NewDetector(Action<DuplicatePacketOptions>? tune = null)
    {
        var options = new DuplicatePacketOptions();
        tune?.Invoke(options);
        var log = new Capture(LogLevel.Trace);
        var clock = new Clock();
        var detector = new DuplicatePacketDetector(options, log, () => clock.Ticks);
        return (detector, detector.NewSession(), log, clock);
    }

    [Fact]
    public void DuplicateItemPushResult_WithinTheWindow_IsReportedWithTheOpcode()
    {
        var (detector, state, log, clock) = NewDetector();
        byte[] payload = ItemPush(5000, 1);

        Assert.False(detector.Observe(state, WorldOpcode.SmsgItemPushResult, payload, out _));
        clock.AdvanceMs(10);
        Assert.True(detector.Observe(state, WorldOpcode.SmsgItemPushResult, payload, out DuplicatePacketHit hit));
        detector.Report(state, Player, hit);

        (LogLevel level, string message) = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("SMSG_ITEM_PUSH_RESULT", message);
        Assert.Contains("character 1234", message);
        Assert.Contains("second send:", message);
        Assert.Contains("first send:", message);
        Assert.True(detector.IsOpcodeArmed(WorldOpcode.SmsgItemPushResult));
    }

    [Fact]
    public void DistinctPayloads_OutsideTheWindow_AndAllowListedOpcodes_AreNotReported()
    {
        var (detector, state, log, clock) = NewDetector();
        Assert.False(detector.Observe(state, WorldOpcode.SmsgItemPushResult, ItemPush(5000, 1), out _));
        Assert.False(detector.Observe(state, WorldOpcode.SmsgItemPushResult, ItemPush(5001, 1), out _)); // other item
        Assert.False(detector.Observe(state, WorldOpcode.SmsgItemPushResult, ItemPush(5000, 2), out _)); // other count

        byte[] same = ItemPush(7000, 1);
        Assert.False(detector.Observe(state, WorldOpcode.SmsgItemPushResult, same, out _));
        clock.AdvanceMs(500);
        Assert.False(detector.Observe(state, WorldOpcode.SmsgItemPushResult, same, out _)); // window is 100 ms

        foreach (WorldOpcode allowed in new[] { WorldOpcode.SmsgPong, WorldOpcode.SmsgUpdateObject, WorldOpcode.MsgMoveHeartbeat, WorldOpcode.SmsgMonsterMove, WorldOpcode.SmsgCreatureQueryResponse, WorldOpcode.SmsgAttackerstateupdate })
        {
            Assert.True(DuplicatePacketDetector.IsAllowListed(allowed), allowed.ToString());
            Assert.False(detector.Observe(state, allowed, [1, 2, 3], out _));
            Assert.False(detector.Observe(state, allowed, [1, 2, 3], out _));
        }

        Assert.False(DuplicatePacketDetector.IsAllowListed(WorldOpcode.SmsgItemPushResult));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void AnOpcodeThatDuplicatedBefore_CarriesBothStacksNextTime_AndWarningsAreRateLimited()
    {
        var (detector, state, log, clock) = NewDetector(o => o.WarnBurstPerSession = 3);
        byte[] first = ItemPush(1, 1);
        detector.Observe(state, WorldOpcode.SmsgItemPushResult, first, out _);
        Assert.True(detector.Observe(state, WorldOpcode.SmsgItemPushResult, first, out DuplicatePacketHit hit1));
        Assert.Null(hit1.FirstStack); // nothing was captured before the opcode was known to duplicate
        detector.Report(state, Player, hit1);

        clock.AdvanceMs(1000);
        byte[] second = ItemPush(2, 1);
        detector.Observe(state, WorldOpcode.SmsgItemPushResult, second, out _);
        Assert.True(detector.Observe(state, WorldOpcode.SmsgItemPushResult, second, out DuplicatePacketHit hit2));
        Assert.False(string.IsNullOrEmpty(hit2.FirstStack));
        detector.Report(state, Player, hit2);
        Assert.DoesNotContain("not captured", log.Lines[^1].Message);

        for (int i = 0; i < 10; i++)
        {
            clock.AdvanceMs(1000);
            byte[] p = ItemPush((uint)(10 + i), 1);
            detector.Observe(state, WorldOpcode.SmsgItemPushResult, p, out _);
            if (detector.Observe(state, WorldOpcode.SmsgItemPushResult, p, out DuplicatePacketHit h))
            {
                detector.Report(state, Player, h);
            }
        }

        Assert.Equal(3, log.Lines.Count); // burst of 3, the rest suppressed
    }

    [Fact]
    public void BotsAreExcludedByDefault_AndAnAllowListedSendAllocatesNothing()
    {
        Assert.False(new DuplicatePacketOptions().IncludeBots);
        Assert.True(new DuplicatePacketOptions().Enabled);
        var (detector, state, _, _) = NewDetector();
        byte[] payload = [1, 2, 3, 4];
        detector.Observe(state, WorldOpcode.SmsgPong, payload, out _);
        void Window()
        {
            for (int i = 0; i < 1000; i++)
            {
                detector.Observe(state, WorldOpcode.SmsgPong, payload, out _);
            }
        }

        ArcaneCore.TestSupport.AllocationProbe.AssertEveryCleanWindowAtMost(Window, 0, "1000 allow-listed Observe calls");
    }

    [Fact]
    public void ANonRepeatingCheckedSend_AllocatesNothing()
    {
        var (detector, state, _, clock) = NewDetector();
        byte[] payload = ItemPush(3, 1);
        detector.Observe(state, WorldOpcode.SmsgItemPushResult, payload, out _);
        clock.AdvanceMs(1000);
        void Window()
        {
            for (int i = 0; i < 1000; i++)
            {
                clock.AdvanceMs(1000);
                detector.Observe(state, WorldOpcode.SmsgItemPushResult, payload, out _);
            }
        }

        ArcaneCore.TestSupport.AllocationProbe.AssertEveryCleanWindowAtMost(Window, 0, "1000 non-repeating checked Observe calls");
    }

    // ---- A: rejections ----------------------------------------------------------------------------------------------

    private sealed class BucketHolder
    {
        public readonly object Gate = new();
        public TokenBucket Bucket;

        public BucketHolder(ClientRejectionLog log) => Bucket = log.NewBucket();
    }

    [Fact]
    public void CastResultFailure_IsLoggedAtInformationForAPlayerAndDebugForABot_WithIds()
    {
        var logger = new Capture(LogLevel.Trace);
        var log = new ClientRejectionLog(new ClientRejectionOptions(), logger);
        var b = new BucketHolder(log);
        byte[] payload = ArcaneCore.Game.Spells.SpellPackets.BuildCastResult(6478, ArcaneCore.Game.Spells.SpellCastResult.BadTargets);
        Assert.True(ClientRejectionDecoder.TryDecode(WorldOpcode.SmsgCastResult, payload, out ClientRejection r));

        log.LogOutbound(b.Gate, ref b.Bucket, Player, WorldOpcode.SmsgCastResult, r);
        log.LogOutbound(b.Gate, ref b.Bucket, Bot, WorldOpcode.SmsgCastResult, r);

        Assert.Equal(LogLevel.Information, logger.Lines[0].Level);
        Assert.Equal(LogLevel.Debug, logger.Lines[1].Level);
        string line = logger.Lines[0].Message;
        Assert.Contains("CastResult", line);
        Assert.Contains("BadTargets", line);
        Assert.Contains("SMSG_CAST_RESULT", line);
        Assert.Contains("character 1234", line);
        Assert.Contains("map 1", line);
        Assert.Contains("entry 6478", line);
    }

    [Fact]
    public void PerSessionBucketLimitsLines_AndADisabledLevelTakesNoToken()
    {
        var logger = new Capture(LogLevel.Information);
        var log = new ClientRejectionLog(new ClientRejectionOptions { BurstPerSession = 2, RefillPerMinute = 1 }, logger);
        var b = new BucketHolder(log);
        var r = new ClientRejection(ClientRejectionKind.BuyFailed, 2, 0xAB, 118, 0);
        for (int i = 0; i < 10; i++)
        {
            log.LogOutbound(b.Gate, ref b.Bucket, Bot, WorldOpcode.SmsgBuyFailed, r); // Debug is off: no line, no token
        }

        Assert.Empty(logger.Lines);
        for (int i = 0; i < 10; i++)
        {
            log.LogOutbound(b.Gate, ref b.Bucket, Player, WorldOpcode.SmsgBuyFailed, r);
        }

        Assert.Equal(2, logger.Lines.Count);
        Assert.Contains("NotEnoughMoney", logger.Lines[0].Message);
    }

    [Fact]
    public void SilentDrop_IsLoggedWithTheReason_AndCanBeSwitchedOff()
    {
        var logger = new Capture(LogLevel.Trace);
        var on = new ClientRejectionLog(new ClientRejectionOptions(), logger);
        var b = new BucketHolder(on);
        on.LogSilentDrop(b.Gate, ref b.Bucket, Player, WorldOpcode.CmsgCastSpell, "cast refused without a reply: NotKnown", 0, 6478, 3);
        Assert.Contains("NotKnown", Assert.Single(logger.Lines).Message);
        Assert.Contains("CMSG_CAST_SPELL", logger.Lines[0].Message);

        var offLogger = new Capture(LogLevel.Trace);
        var off = new ClientRejectionLog(new ClientRejectionOptions { LogSilentDrops = false }, offLogger);
        off.LogSilentDrop(b.Gate, ref b.Bucket, Player, WorldOpcode.CmsgCastSpell, "x", 0, 0, 0);
        Assert.Empty(offLogger.Lines);
    }
}
