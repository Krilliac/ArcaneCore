using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>
/// <c>World:Playerbots:Social:Events</c> and <c>:Holidays</c> (P7a slice 1, design sections 5 and H1): the defaults, the bounds of every
/// key, and the <c>ExcludedEvents</c> list, a string so that a reload compares it by value (a <c>string[]</c> would compare by reference).
/// </summary>
public sealed class PlayerbotHolidayOptionsTests
{
    [Fact]
    public void TheDefaults_AreTheOnesOfTheDesign_AndOnByDefault()
    {
        var options = new PlayerbotSocialEventsOptions();
        PlayerbotHolidayOptions holidays = options.Holidays;

        Assert.True(options.Enabled);
        Assert.True(holidays.Enabled);
        Assert.Equal((60, 250, 30), (holidays.CelebrateChance, holidays.SpotRadiusYards, holidays.SpotBackoffMinutes));
        Assert.Equal((6, 3, 24), (holidays.MaxPerGossipAnchor, holidays.MaxPerServiceAnchor, holidays.MaxPerTown));
        Assert.Equal((30, 90), (holidays.EmoteMinSeconds, holidays.EmoteMaxSeconds));
        Assert.Equal((2, 3), (holidays.EmotesPerListenerMinute, holidays.EmotesPerAnchorMinute));
        Assert.True(holidays.TalkEnabled);
        Assert.Equal((20, 120), (holidays.TalkBotGapMinutes, holidays.TalkZoneGapSeconds));
        Assert.Equal(string.Empty, holidays.ExcludedEvents);

        options.Validate();
        Assert.Same(options, new PlayerbotSocialOptions { Events = options }.Events);
        new PlayerbotOptions().Validate(); // the defaults of the whole tree are valid with the new section in it
    }

    public static TheoryData<string, Action<PlayerbotHolidayOptions>, bool> Bounds()
    {
        var data = new TheoryData<string, Action<PlayerbotHolidayOptions>, bool>();
        void Range(string name, int low, int high, Action<PlayerbotHolidayOptions, int> set)
        {
            data.Add($"{name}={low}", o => set(o, low), true);
            data.Add($"{name}={high}", o => set(o, high), true);
            data.Add($"{name}={low - 1}", o => set(o, low - 1), false);
            data.Add($"{name}={high + 1}", o => set(o, high + 1), false);
        }

        Range("CelebrateChance", 0, 100, (o, v) => o.CelebrateChance = v);
        Range("SpotRadiusYards", 20, 1000, (o, v) => o.SpotRadiusYards = v);
        Range("SpotBackoffMinutes", 1, 1440, (o, v) => o.SpotBackoffMinutes = v);
        Range("MaxPerGossipAnchor", 1, 20, (o, v) => o.MaxPerGossipAnchor = v);
        Range("MaxPerServiceAnchor", 0, 10, (o, v) => o.MaxPerServiceAnchor = v);
        Range("MaxPerTown", 1, 200, (o, v) => o.MaxPerTown = v);
        Range("EmotesPerListenerMinute", 0, 10, (o, v) => o.EmotesPerListenerMinute = v);
        Range("EmotesPerAnchorMinute", 0, 30, (o, v) => o.EmotesPerAnchorMinute = v);
        Range("TalkBotGapMinutes", 1, 1440, (o, v) => o.TalkBotGapMinutes = v);
        Range("TalkZoneGapSeconds", 1, 86_400, (o, v) => o.TalkZoneGapSeconds = v);

        // The emote gap is 5..3600 seconds at both ends, and the longest must not be shorter than the shortest.
        data.Add("EmoteMinSeconds=5", o => { o.EmoteMinSeconds = 5; o.EmoteMaxSeconds = 5; }, true);
        data.Add("EmoteMinSeconds=4", o => o.EmoteMinSeconds = 4, false);
        data.Add("EmoteMinSeconds=3600", o => { o.EmoteMinSeconds = 3600; o.EmoteMaxSeconds = 3600; }, true);
        data.Add("EmoteMinSeconds=3601", o => { o.EmoteMinSeconds = 3601; o.EmoteMaxSeconds = 3600; }, false);
        data.Add("EmoteMaxSeconds=3600", o => o.EmoteMaxSeconds = 3600, true);
        data.Add("EmoteMaxSeconds=3601", o => o.EmoteMaxSeconds = 3601, false);
        data.Add("EmoteMaxSeconds=4", o => { o.EmoteMinSeconds = 5; o.EmoteMaxSeconds = 4; }, false);
        data.Add("EmoteMaxSeconds<EmoteMinSeconds", o => { o.EmoteMinSeconds = 60; o.EmoteMaxSeconds = 59; }, false);
        data.Add("EmoteMaxSeconds==EmoteMinSeconds", o => { o.EmoteMinSeconds = 60; o.EmoteMaxSeconds = 60; }, true);
        return data;
    }

    [Theory]
    [MemberData(nameof(Bounds))]
    public void EveryKey_IsRangeChecked(string what, Action<PlayerbotHolidayOptions> change, bool valid)
    {
        var options = new PlayerbotSocialEventsOptions();
        change(options.Holidays);

        if (valid)
        {
            options.Holidays.Validate();
            options.Validate();
        }
        else
        {
            Assert.Throws<InvalidOperationException>(options.Holidays.Validate);
            Assert.Throws<InvalidOperationException>(options.Validate);
        }

        Assert.NotNull(what);
    }

    [Fact]
    public void ABadValue_FailsTheWholeOptionsTree_NamingTheKey()
    {
        var options = new PlayerbotOptions();
        options.Social.Events.Holidays.CelebrateChance = 101;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("CelebrateChance", error.Message);
        Assert.Contains("Events", error.Message);

        options = new PlayerbotOptions();
        options.Social.Events.Holidays.ExcludedEvents = "12, x";
        error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("ExcludedEvents", error.Message);

        options = new PlayerbotOptions();
        options.Social.Events = null!;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("", new int[0])]
    [InlineData("   ", new int[0])]
    [InlineData(null, new int[0])]
    [InlineData("12", new[] { 12 })]
    [InlineData("12, 24 25", new[] { 12, 24, 25 })]
    [InlineData("12,24,25", new[] { 12, 24, 25 })]
    [InlineData("  7 ,\t8\n9 ", new[] { 7, 8, 9 })]
    [InlineData("12 12, 12", new[] { 12 })]
    [InlineData("1,65535", new[] { 1, 65535 })]
    public void ExcludedEvents_AcceptsCommaAndWhitespaceSeparatedIds_AndIgnoresDuplicates(string? text, int[] expected)
    {
        (IReadOnlySet<ushort> ids, string? error) = PlayerbotHolidayOptions.ParseExcludedEvents(text);

        Assert.Null(error);
        Assert.Equal(expected.Select(id => (ushort)id).OrderBy(id => id), ids.OrderBy(id => id));
        Assert.Null(PlayerbotHolidayOptions.CheckExcludedEvents(text));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("x")]
    [InlineData("12;24")]
    [InlineData("12, ,x")]
    [InlineData("1.5")]
    [InlineData("99999999999999999999")]
    public void ExcludedEvents_RefusesAnIdOutsideOneToSixtyFiveThousandFiveHundredThirtyFive_OrNotANumber(string text)
    {
        (_, string? error) = PlayerbotHolidayOptions.ParseExcludedEvents(text);

        Assert.False(string.IsNullOrEmpty(error));
        Assert.Equal(error, PlayerbotHolidayOptions.CheckExcludedEvents(text));
        var options = new PlayerbotHolidayOptions { ExcludedEvents = text };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ExcludedEvents_AllowsSixtyFourIds_AndRefusesSixtyFive()
    {
        string sixtyFour = string.Join(", ", Enumerable.Range(100, 64));
        string sixtyFive = string.Join(", ", Enumerable.Range(100, 65));

        (IReadOnlySet<ushort> ids, string? error) = PlayerbotHolidayOptions.ParseExcludedEvents(sixtyFour);
        Assert.Null(error);
        Assert.Equal(64, ids.Count);
        new PlayerbotHolidayOptions { ExcludedEvents = sixtyFour }.Validate();

        Assert.False(string.IsNullOrEmpty(PlayerbotHolidayOptions.ParseExcludedEvents(sixtyFive).Error));
        Assert.Throws<InvalidOperationException>(new PlayerbotHolidayOptions { ExcludedEvents = sixtyFive }.Validate);

        // A repeat is not another id: 65 entries of which one repeats are 64 ids and fine.
        string repeated = sixtyFour + ", 100";
        Assert.Null(PlayerbotHolidayOptions.ParseExcludedEvents(repeated).Error);
    }
}
