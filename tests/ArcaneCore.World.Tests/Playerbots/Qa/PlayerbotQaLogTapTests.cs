using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>The logger provider that feeds QA without any change to the bot subsystems.</summary>
public sealed class PlayerbotQaLogTapTests
{
    private const string StallText = "stalled 120s: goal=Quest target=6747 quest=1656 at -5608.8,-439.4,401.2 map 0";

    private static void LogStall(ILogger logger, Guid bot, string name)
        => logger.LogWarning("Playerbot {BotId} ({Name}) {Stall}; it gives up that goal", bot, name, StallText);

    [Fact]
    public void OnlyWhitelistedCategoriesAndWarningLevel_AreEnabled()
    {
        var bus = new PlayerbotQaSignalBus(16);
        using var tap = new PlayerbotQaLogTap(bus, true, null, null);

        ILogger bots = tap.CreateLogger(PlayerbotQaLogTap.BotCategory);
        Assert.True(bots.IsEnabled(LogLevel.Warning));
        Assert.True(bots.IsEnabled(LogLevel.Error));
        Assert.True(bots.IsEnabled(LogLevel.Critical));
        Assert.False(bots.IsEnabled(LogLevel.Information));
        Assert.False(bots.IsEnabled(LogLevel.Debug));
        Assert.False(bots.IsEnabled(LogLevel.Trace));
        Assert.False(bots.IsEnabled(LogLevel.None));
        Assert.True(tap.CreateLogger(PlayerbotQaLogTap.CreatureCategory).IsEnabled(LogLevel.Warning));
        Assert.Equal("ArcaneCore.World.Playerbots.ManagedPlayerbotFeature", PlayerbotQaLogTap.BotCategory);

        ILogger other = tap.CreateLogger("ArcaneCore.World.Chat.ChatFeature");
        Assert.False(other.IsEnabled(LogLevel.Warning));
        LogStall(other, QaData.Bot1, "Ironwander"); // another category: ignored even though the text matches
        bots.LogInformation("Playerbot {BotId} ({Name}) {Stall}; it gives up that goal", QaData.Bot1, "Ironwander", StallText); // below Warning: ignored
        Assert.Equal(0, bus.Received);

        LogStall(bots, QaData.Bot1, "Ironwander");
        Assert.Equal(1, bus.Received);

        using var off = new PlayerbotQaLogTap(bus, false, null, null);
        Assert.False(off.CreateLogger(PlayerbotQaLogTap.BotCategory).IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public void BotActionFault_LoggedAtError_WithItsException_IsStillClassifiedAsAnActionFault()
    {
        var bus = new PlayerbotQaSignalBus(16);
        using var tap = new PlayerbotQaLogTap(bus, true, null, null);
        ILogger bots = tap.CreateLogger(PlayerbotQaLogTap.BotCategory);
        var error = new InvalidOperationException("playerbot-recovery-spirit-healer-failed");

        // exactly the call ManagedPlayerbotFeature.Fault makes
        bots.LogError(error, "Playerbot {BotId} {What} failed ({Type}: {Message})", QaData.Bot1, "action", error.GetType().Name, error.Message);

        var drained = new List<QaSignal>();
        Assert.Equal(1, bus.Drain(drained, 10));
        QaSignal signal = Assert.Single(drained);
        Assert.Equal(QaSignalKind.ActionFault, signal.Kind);
        Assert.Equal(QaData.Bot1, signal.BotKey);
        Assert.Equal("InvalidOperationException", signal.ExceptionType);
        Assert.Equal("action", signal.Goal);
    }

    [Fact]
    public void CreatureCategory_IsTheLoggerTheCreatureFeatureReallyUses()
    {
        // The map systems log through the CreatureWorldFeature logger; if that category moves, relay lines would silently stop reaching QA.
        Assert.Equal(PlayerbotQaLogTap.CreatureCategory, typeof(ArcaneCore.World.Creatures.CreatureWorldFeature).FullName);
    }

    [Fact]
    public void StructuredState_BecomesSignal_WithoutBotName()
    {
        var bus = new PlayerbotQaSignalBus(16);
        var time = new ManualTime(QaData.T0);
        using var tap = new PlayerbotQaLogTap(bus, true, null, time);

        LogStall(tap.CreateLogger(PlayerbotQaLogTap.BotCategory), QaData.Bot1, "Ironwander");

        var drained = new List<QaSignal>();
        Assert.Equal(1, bus.Drain(drained, 10));
        QaSignal s = Assert.Single(drained);
        Assert.Equal(QaSignalKind.Stall, s.Kind);
        Assert.Equal(QaData.Bot1, s.BotKey);
        Assert.Equal(QaData.T0, s.AtUtc);
        Assert.Equal(1656u, s.Quest);
        Assert.DoesNotContain("Ironwander", s.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(QaData.Bot1.ToString(), s.Message, StringComparison.Ordinal);
        Assert.Equal(1, tap.Signals);
        Assert.Equal(0, tap.Failures);
    }

    [Fact]
    public void FullBus_DropsOldest_CountsDrops_NeverThrows()
    {
        var bus = new PlayerbotQaSignalBus(2);
        for (uint quest = 1; quest <= 5; quest++) Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot1, 0, 0, quest)));

        Assert.Equal(5, bus.Received);
        Assert.Equal(3, bus.Dropped);
        var drained = new List<QaSignal>();
        Assert.Equal(2, bus.Drain(drained, 10));
        Assert.Equal(new uint[] { 4, 5 }, drained.Select(s => s.Quest).ToArray()); // the newest survive

        // Through the logger: a flood of warnings against a tiny queue neither throws nor blocks.
        var small = new PlayerbotQaSignalBus(1);
        using var tap = new PlayerbotQaLogTap(small, true, null, null);
        ILogger logger = tap.CreateLogger(PlayerbotQaLogTap.BotCategory);
        for (int i = 0; i < 100; i++) LogStall(logger, Guid.NewGuid(), "Bot" + i);
        Assert.Equal(100, small.Received);
        Assert.Equal(99, small.Dropped);
        Assert.Equal(0, tap.Failures);

        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerbotQaSignalBus(0));
    }

    [Fact]
    public void FullBus_ConcurrentWriters_LoseNothingUncounted()
    {
        var bus = new PlayerbotQaSignalBus(64);
        Parallel.For(0, 8, _ =>
        {
            for (int i = 0; i < 500; i++) bus.TryWrite(QaData.Stall(QaData.Bot1, 0, 0));
        });

        var drained = new List<QaSignal>();
        int kept = bus.Drain(drained, int.MaxValue);
        Assert.Equal(4000, bus.Received);
        Assert.Equal(bus.Received, bus.Dropped + kept);
    }

    [Fact]
    public void ParserException_IsSwallowed_AndCounted()
    {
        var bus = new PlayerbotQaSignalBus(16);
        int calls = 0;
        using var tap = new PlayerbotQaLogTap(bus, true, (message, at, key) =>
        {
            if (++calls == 1) throw new InvalidOperationException("parser blew up");
            return (PlayerbotQaSignalParser.TryParse(message, at, key, out QaSignal? signal), signal);
        }, null);
        ILogger logger = tap.CreateLogger(PlayerbotQaLogTap.BotCategory);

        LogStall(logger, QaData.Bot1, "Ironwander"); // must not throw into the caller
        Assert.Equal(1, tap.Failures);
        Assert.Equal(0, tap.Signals);
        Assert.Equal(0, bus.Received);

        LogStall(logger, QaData.Bot1, "Ironwander"); // and the tap keeps working afterwards
        Assert.Equal(1, tap.Failures);
        Assert.Equal(1, tap.Signals);
        Assert.Equal(1, bus.Received);
    }

    [Fact]
    public void ThrowingFormatter_IsSwallowed_AndCounted()
    {
        var bus = new PlayerbotQaSignalBus(16);
        using var tap = new PlayerbotQaLogTap(bus, true, null, null);
        ILogger logger = tap.CreateLogger(PlayerbotQaLogTap.BotCategory);

        logger.Log(LogLevel.Warning, new EventId(1), "state", null, (Func<string, Exception?, string>)((_, _) => throw new FormatException("bad format")));

        Assert.Equal(1, tap.Failures);
        Assert.Equal(0, bus.Received);
    }
}
