using ArcaneCore.Game.WorldState.Events;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>The load-time rules of both game_event dialects (mangos-classic GameEventMgr.cpp:107-226, vmangos :183-259).</summary>
public sealed class GameEventValidationTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = Utc(2026, 10, 3, 12);

    private static GameEventSource Source(
        ushort id = 10,
        GameEventScheduleType type = GameEventScheduleType.Date,
        uint occurence = 1440,
        uint length = 60,
        ushort linkedTo = 0,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        bool hardcoded = false,
        bool disabled = false,
        byte patchMin = 0,
        byte patchMax = 10)
        => new(id, type, occurence, length, 0, linkedTo, "test event", start, end, hardcoded, disabled, patchMin, patchMax);

    private static GameEventDefinition CMangos(GameEventSource source, out List<string> issues)
    {
        issues = [];
        return GameEventValidation.BuildCMangos(source, Now, TimeZoneInfo.Utc, YearlyRebaseMode.SpanNewYear, issues);
    }

    [Fact]
    public void Serverside_Events_GetTheFarFuture_AndNeverActivateOnTheirOwn()
    {
        GameEventDefinition def = CMangos(Source(type: GameEventScheduleType.Serverside, occurence: 0, length: 0), out List<string> issues);

        Assert.Equal(GameEventCalendar.FarFuture, def.Start);
        Assert.Equal(GameEventCalendar.FarFuture, def.End);
        Assert.Equal(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), def.Start);
        Assert.True(def.IsValid);
        Assert.False(GameEventSchedule.IsActive(def, Now));
        Assert.False(GameEventSchedule.IsActive(def, Utc(2099, 12, 31)));
        Assert.DoesNotContain(issues, i => i.Contains("length 0", StringComparison.Ordinal)); // a serverside length of 0 is allowed
    }

    [Fact]
    public void OccurenceZero_DisablesTheEvent_AndTakesTheLengthAsOccurence()
    {
        GameEventDefinition def = CMangos(Source(occurence: 0, length: 90, start: Utc(2020, 1, 1), end: Utc(2030, 1, 1)), out List<string> issues);

        Assert.Equal(GameEventCalendar.FarFuture, def.Start);
        Assert.Equal(90u, def.OccurenceMinutes);
        Assert.Contains(issues, i => i.Contains("occurence is 0", StringComparison.Ordinal));
        Assert.False(GameEventSchedule.IsActive(def, Now));
    }

    [Fact]
    public void LengthZero_AndOccurenceShorterThanLength_AreUnusable()
    {
        GameEventDefinition zero = CMangos(Source(length: 0, start: Utc(2020, 1, 1), end: Utc(2030, 1, 1)), out List<string> zeroIssues);
        Assert.Equal(GameEventCalendar.FarFuture, zero.Start);
        Assert.False(zero.IsValid);
        Assert.Contains(zeroIssues, i => i.Contains("length 0", StringComparison.Ordinal));

        GameEventDefinition shorter = CMangos(Source(occurence: 30, length: 60, start: Utc(2020, 1, 1), end: Utc(2030, 1, 1)), out List<string> shorterIssues);
        Assert.Equal(GameEventCalendar.FarFuture, shorter.Start);
        Assert.True(shorter.IsValid);
        Assert.Contains(shorterIssues, i => i.Contains("occurence 30 < length 60", StringComparison.Ordinal));
    }

    [Fact]
    public void DateEvent_TakesItsTimesFromTheTimeRow_AndWithoutOneNeverRuns()
    {
        GameEventDefinition withTime = CMangos(Source(start: Utc(2026, 10, 3), end: Utc(2026, 10, 5), occurence: 4320, length: 2880), out _);
        Assert.Equal((Utc(2026, 10, 3), Utc(2026, 10, 5)), (withTime.Start, withTime.End));
        Assert.True(GameEventSchedule.IsActive(withTime, Now, boundary: GameEventStartBoundary.Exclusive));

        // event 85 in classic-db: schedule type 1 and no game_event_time row: start 1, end 0
        GameEventDefinition without = CMangos(Source(), out _);
        Assert.Equal((DateTimeOffset.FromUnixTimeSeconds(1), DateTimeOffset.FromUnixTimeSeconds(0)), (without.Start, without.End));
        Assert.False(GameEventSchedule.IsActive(without, Now));
    }

    [Fact]
    public void TimeRowOnANonDateSchedule_IsIgnored()
    {
        GameEventDefinition def = CMangos(Source(type: GameEventScheduleType.Serverside, occurence: 0, length: 0, start: Utc(2026, 1, 1), end: Utc(2026, 2, 1)), out List<string> issues);
        Assert.Equal(GameEventCalendar.FarFuture, def.Start);
        Assert.Contains(issues, i => i.Contains("not date scheduled", StringComparison.Ordinal));
    }

    [Fact]
    public void YearlyRow_IsRebasedToTheCurrentYear()
    {
        GameEventDefinition def = CMangos(
            Source(type: GameEventScheduleType.Yearly, occurence: 525600, length: 27360, start: Utc(2020, 12, 16, 23), end: Utc(2030, 12, 31, 22, 59, 59)),
            out _);
        Assert.Equal(Utc(2026, 12, 16, 23), def.Start);
        Assert.False(GameEventSchedule.IsActive(def, Now));
        Assert.True(GameEventSchedule.IsActive(def, Utc(2026, 12, 20), boundary: GameEventStartBoundary.Exclusive));
    }

    [Fact]
    public void ComputedSchedules_AreComputedAtLoad()
    {
        GameEventDefinition easter = CMangos(Source(type: GameEventScheduleType.Easter, occurence: 524160, length: 7200), out _);
        Assert.Equal(Utc(2026, 4, 5), easter.Start);
        GameEventDefinition lunar = CMangos(Source(type: GameEventScheduleType.LunarNewYear, occurence: 525600, length: 28800), out _);
        Assert.Equal(Utc(2026, 2, 17), lunar.Start);
    }

    [Fact]
    public void DarkmoonSchedule_IsDisabledWithAnIssue_NotStubbed()
    {
        GameEventDefinition def = CMangos(Source(type: GameEventScheduleType.DarkmoonFaire1, occurence: 86400, length: 10080, start: Utc(2020, 1, 1), end: Utc(2030, 1, 1)), out List<string> issues);
        Assert.Equal(GameEventCalendar.FarFuture, def.Start);
        Assert.Contains(issues, i => i.Contains("not implemented", StringComparison.Ordinal));
        Assert.False(GameEventSchedule.IsActive(def, Now));
    }

    [Fact]
    public void ResolveLinks_ClearsALinkToAnInvalidOrMissingEvent()
    {
        var issues = new List<string>();
        var parent = new GameEventDefinition(12, Utc(2020, 1, 1), Utc(2030, 1, 1), 525600, 100, 0, "parent");
        var child = new GameEventDefinition(24, Utc(2020, 1, 1), Utc(2030, 1, 1), 525600, 100, 0, "child") { LinkedTo = 12 };
        var orphan = new GameEventDefinition(25, Utc(2020, 1, 1), Utc(2030, 1, 1), 525600, 100, 0, "orphan") { LinkedTo = 99 };
        var invalidParent = new GameEventDefinition(30, Utc(2020, 1, 1), Utc(2030, 1, 1), 525600, 0, 0, "zero length");
        var toInvalid = new GameEventDefinition(31, Utc(2020, 1, 1), Utc(2030, 1, 1), 525600, 100, 0, "to invalid") { LinkedTo = 30 };

        IReadOnlyList<GameEventDefinition> resolved = GameEventValidation.ResolveLinks([parent, child, orphan, invalidParent, toInvalid], issues);

        Assert.Equal((ushort)12, resolved.Single(d => d.Id == 24).LinkedTo);
        Assert.Equal((ushort)0, resolved.Single(d => d.Id == 25).LinkedTo);
        Assert.Equal((ushort)0, resolved.Single(d => d.Id == 31).LinkedTo);
        Assert.Equal(2, issues.Count);
        Assert.Equal((ushort)99, orphan.LinkedTo); // the input is not changed
    }

    [Fact]
    public void VmangosRows_LengthZeroIsInvalid_AndThePatchRangeDisables()
    {
        var issues = new List<string>();
        GameEventDefinition zero = GameEventValidation.BuildVMangos(Source(length: 0, start: Utc(2020, 1, 1), end: Utc(2030, 1, 1)), issues);
        Assert.False(zero.IsValid);

        GameEventDefinition ok = GameEventValidation.BuildVMangos(Source(start: Utc(2020, 1, 1), end: Utc(2030, 1, 1), hardcoded: true), issues);
        Assert.True(ok.IsValid);
        Assert.True(ok.Hardcoded);
        Assert.False(ok.Disabled);

        // patch_min > patch_max: reset to 0..10 (the event stays enabled); patch_min 11: outside 1.12 (patch 10)
        GameEventDefinition reset = GameEventValidation.BuildVMangos(Source(start: Utc(2020, 1, 1), end: Utc(2030, 1, 1), patchMin: 9, patchMax: 3), issues);
        Assert.False(reset.Disabled);
        Assert.Contains(issues, i => i.Contains("patch_min=9, patch_max=3", StringComparison.Ordinal));
        GameEventDefinition outside = GameEventValidation.BuildVMangos(Source(start: Utc(2020, 1, 1), end: Utc(2030, 1, 1), patchMin: 0, patchMax: 9), issues);
        Assert.True(outside.Disabled);

        // a stored disabled flag survives
        Assert.True(GameEventValidation.BuildVMangos(Source(start: Utc(2020, 1, 1), end: Utc(2030, 1, 1), disabled: true), issues).Disabled);

        // no times at all: invalid
        Assert.False(GameEventValidation.BuildVMangos(Source(), []).IsValid);
    }

    [Fact]
    public void StartBoundary_DecidesTheExactStartSecond()
    {
        var def = new GameEventDefinition(1, Utc(2026, 10, 3, 12), Utc(2026, 10, 10), 10080, 1440, 0, "boundary");
        Assert.True(GameEventSchedule.IsActive(def, Utc(2026, 10, 3, 12)));                                                 // vmangos start <= current
        Assert.True(GameEventSchedule.IsActive(def, Utc(2026, 10, 3, 12), boundary: GameEventStartBoundary.Inclusive));
        Assert.False(GameEventSchedule.IsActive(def, Utc(2026, 10, 3, 12), boundary: GameEventStartBoundary.Exclusive));    // mangos-classic start < current
        Assert.True(GameEventSchedule.IsActive(def, Utc(2026, 10, 3, 12, 0, 1), boundary: GameEventStartBoundary.Exclusive));
    }

    [Fact]
    public void DefinitionDefaults_AreTheVmangosShape()
    {
        var def = new GameEventDefinition(1, Utc(2020, 1, 1), Utc(2030, 1, 1), 100, 10, 0, "d");
        Assert.Equal(GameEventScheduleType.Date, def.ScheduleType);
        Assert.Equal((ushort)0, def.LinkedTo);
        Assert.True(def.IsValid);
        Assert.False((def with { LengthMinutes = 0 }).IsValid);
        Assert.True((def with { LengthMinutes = 0, ScheduleType = GameEventScheduleType.Serverside }).IsValid);
    }
}
