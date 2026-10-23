using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Ops.Lifecycle;
using Xunit;

namespace ArcaneCore.World.Tests.Chat;

/// <summary>
/// Pins the vmangos secsToTimeString output (shared/Util.cpp:197-250) across every copy that exists today, so that
/// folding them into one Kernel implementation cannot change a byte: the trailing spaces, the final full stop, the
/// "secs &lt;= 1" / "hours &lt;= 1" singular rules, the ulong range above long.MaxValue and the invariant minus sign.
/// Expected strings are derived by hand from the reference's algorithm.
/// </summary>
public sealed class SecsToTimeStringCharacterisationTests
{
    public static TheoryData<long, string> LongForm => new()
    {
        { 2, "2 Seconds." },
        { 3599, "59 Minutes 59 Seconds." },
        { 3601, "1 Hour 1 Second." },
        { 7201, "2 Hours 1 Second." },
        { 86399, "23 Hours 59 Minutes 59 Seconds." },
        { 172801, "2 Days 1 Second." }, // the zero hours and minutes between are skipped
        { 3 * 86400 + 5 * 3600 + 7 * 60 + 9, "3 Days 5 Hours 7 Minutes 9 Seconds." },
        { long.MaxValue, "106751991167300 Days 15 Hours 30 Minutes 7 Seconds." },
    };

    [Theory]
    [MemberData(nameof(LongForm))]
    public void EveryLongFormCopy_ProducesTheSameBytes(long seconds, string expected)
    {
        Assert.Equal(expected, ChatText.SecsToTimeString(seconds));
        Assert.Equal(expected, GmDuration.SecsToTimeString(seconds));
        Assert.Equal(expected, ChatRestrictionService.FormatDuration(seconds));
        Assert.Equal(expected, ServerTimeText.Format((ulong)seconds));
    }

    [Fact]
    public void TheUlongCopy_PrintsValuesAboveLongMaxValueWithoutWrapping()
    {
        Assert.Equal("106751991167300 Days 15 Hours 30 Minutes 8 Seconds.", ServerTimeText.Format((ulong)long.MaxValue + 1));
        Assert.Equal("213503982334601 Days 7 Hours 15 Seconds.", ServerTimeText.Format(ulong.MaxValue));
    }

    [Fact]
    public void TheShortUlongCopy_PrintsValuesAboveLongMaxValueWithoutWrapping()
    {
        Assert.Equal("106751991167300d15h30m8s", BanTime.SecsToTimeString((ulong)long.MaxValue + 1));
        Assert.Equal("213503982334601d7h15s", BanTime.SecsToTimeString(ulong.MaxValue));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(59L)]
    [InlineData(60L)]
    [InlineData(3600L)]
    [InlineData(3661L)]
    [InlineData(86400L)]
    [InlineData(90061L)]
    [InlineData(172800L)]
    public void TheShortForm_IsTheSameInGmDurationAndBanTime(long seconds)
        => Assert.Equal(BanTime.SecsToTimeString((ulong)seconds), GmDuration.SecsToTimeString(seconds, shortText: true));

    [Theory]
    [InlineData(0L, false, true, "0 Hour ")]
    [InlineData(90061L, false, true, "1 Day 1 Hour ")]
    [InlineData(3600L * 50, false, true, "2 Days 2 Hours ")]
    [InlineData(90061L, true, true, "1d1h")]
    [InlineData(86400L, true, true, "1d0h")]
    [InlineData(0L, true, false, "0s")]
    [InlineData(61L, true, false, "1m1s")]
    public void GmDuration_HoursOnlyAndShortTextCombinations(long seconds, bool shortText, bool hoursOnly, string expected)
        => Assert.Equal(expected, GmDuration.SecsToTimeString(seconds, shortText, hoursOnly));

    [Theory] // the invariant minus sign and the "<= 1" rule both apply to negatives; callers clamp, but the helper is not asked to
    [InlineData(-1L, "-1 Second.")]
    [InlineData(-61L, "-1 Minutes -1 Second.")]
    public void GmDuration_NegativeInputsAreNotClamped(long seconds, string expected)
        => Assert.Equal(expected, GmDuration.SecsToTimeString(seconds));

    [Fact]
    public void CallersThatTrim_SeeTheDocumentedShape()
    {
        // GmAuditStrings.Span: TrimEnd(' ', '.'); ServerFaultCommands / PlayerbotAnomalyHistory: Trim() on the short form.
        Assert.Equal("1 Day 2 Hours 3 Minutes 4 Seconds", GmDuration.SecsToTimeString(93784).TrimEnd(' ', '.'));
        Assert.Equal("1 Day", GmDuration.SecsToTimeString(86400).TrimEnd(' ', '.'));
        Assert.Equal("1d2h3m4s", GmDuration.SecsToTimeString(93784, shortText: true).Trim());
        Assert.Equal("in 1 Minute", $"in {ServerTimeText.Format(60)}".TrimEnd('.').TrimEnd());
    }

    [Theory] // the mute notice, mangos_string 705
    [InlineData(1L, "You must wait 1 Second. before speaking again.")]
    [InlineData(60L, "You must wait 1 Minute  before speaking again.")] // two spaces: the word's own and the format's
    [InlineData(3661L, "You must wait 1 Hour 1 Minute 1 Second. before speaking again.")]
    public void TheMuteNotice_IsByteIdentical(long seconds, string expected)
        => Assert.Equal(expected, ChatRestrictionService.WaitMessage(seconds));
}
