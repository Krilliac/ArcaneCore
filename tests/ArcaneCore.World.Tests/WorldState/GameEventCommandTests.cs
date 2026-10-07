using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// <c>.event</c> and <c>.lookup event</c> (vmangos Chat.cpp:373-382, ServerCommands.cpp:639-883, LookupCommands.cpp:1480-1526) against the
/// running daemon: event 1 runs now, event 2 opens in five hours. Account levels are the vmangos ones (list and info 3, start and
/// stop 4, enable and disable 5): the GM (retail level 3 here) can look, the administrator (6) can change.
/// </summary>
public sealed class GameEventCommandTests
{
    private static string LocalText(DateTimeOffset instant)
        => TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).DateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    // The host runs at this instant, not at the wall clock: mid-June, hours from any daylight-saving change in any zone, so the event windows
    // below (-30 min .. +5 h) cannot straddle a day or a clock change however the test is scheduled.
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    private static async Task<WorldTestHost> StartAsync()
    {
        DateTimeOffset now = Now;
        WorldTestHost.GameTime.Value = new ArcaneCore.Game.WorldState.Time.FixedGameTime(Now, TimeZoneInfo.Local);
        GameEventTestStore.Current.Value = new GameEventContent(
            [new GameEventRecord(1, 1, 1440, 180, 0, 0, "Test Festival"), new GameEventRecord(2, 1, 1440, 60, 0, 0, "Night Market")],
            [
                new GameEventTimeRecord(1, LocalText(now.AddMinutes(-30)), "2090-12-31 22:59:59"),
                new GameEventTimeRecord(2, LocalText(now.AddHours(5)), "2090-12-31 22:59:59"),
            ],
            [], [], [], [], []);
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start();
        }
        finally
        {
            GameEventTestStore.Current.Value = null;
            WorldTestHost.GameTime.Value = null;
        }

        GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();
        await host.WaitForWorldAsync(() => events.IsActiveEvent(1), "event 1 starts on a world tick");
        return host;
    }

    private static async Task<string> SayAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        return (await client.ReadChatAsync()).Text;
    }

    [Fact]
    public async Task List_ShowsTheRunningEvents_AndAllShowsTheRest()
    {
        await using WorldTestHost host = await StartAsync();
        await using WorldTestClient gm = await host.EnterWorldAsync("EVTGML", "Evtgml", AccountSecurity.GameMaster);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".event list");
        Assert.Equal("1 - |cffffffff|Hgameevent:1|h[Test Festival]|h|r [active]", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".event list all");
        Assert.Equal("1 - |cffffffff|Hgameevent:1|h[Test Festival]|h|r [active]", (await gm.ReadChatAsync()).Text);
        Assert.Equal("2 - |cffffffff|Hgameevent:2|h[Night Market]|h|r [inactive]", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task Info_ShowsTheScheduleInTheVmangosFormats_ByNumberOrByLink()
    {
        await using WorldTestHost host = await StartAsync();
        await using WorldTestClient gm = await host.EnterWorldAsync("EVTGMI", "Evtgmi", AccountSecurity.GameMaster);

        string info = await SayAsync(gm, ".event 2");

        Assert.StartsWith("Event 2: Night MarketStart: ", info, StringComparison.Ordinal);  // the active text is empty and "%s%s" joins them
        Assert.Matches(@"Start: \d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2} End: 2090-12-31_22-59-59 Occurence: 1 Day  ?Length: 1 Hour Next state change: \d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$", info);

        string byLink = await SayAsync(gm, ".event |cffffffff|Hgameevent:1|h[Test Festival]|h|r");
        Assert.StartsWith("Event 1: Test Festival [active]Start: ", byLink, StringComparison.Ordinal);

        Assert.Equal(GameEventCommands.EventNotExistText, await SayAsync(gm, ".event 99"));
    }

    [Fact]
    public async Task Start_StartsAnInactiveEnabledEvent_AndRefusesTheRest()
    {
        await using WorldTestHost host = await StartAsync();
        await using WorldTestClient admin = await host.EnterWorldAsync("EVTADM", "Evtadm", AccountSecurity.Administrator);
        GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();

        Assert.Equal("event started 2 \"Night Market\"", await SayAsync(admin, ".event start 2"));
        Assert.True(await host.OnWorldAsync(() => events.IsActiveEvent(2)));
        Assert.Equal("Event 2 already active!", await SayAsync(admin, ".event start 2"));
        Assert.Equal("Event not exist!", await SayAsync(admin, ".event start 99"));

        // stop it again: back to inactive, and a second stop is refused
        Assert.Equal("event stopped 2 \"Night Market\"", await SayAsync(admin, ".event stop 2"));
        Assert.False(await host.OnWorldAsync(() => events.IsActiveEvent(2)));
        Assert.Equal("Event 2 not active!", await SayAsync(admin, ".event stop 2"));
    }

    [Fact]
    public async Task EnableAndDisable_StopARunningEvent_RefuseAStart_AndAreRefusedWhenAlreadyInThatState()
    {
        await using WorldTestHost host = await StartAsync();
        await using WorldTestClient admin = await host.EnterWorldAsync("EVTADE", "Evtade", AccountSecurity.Administrator);
        GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();

        Assert.Equal("Event 1 already enabled!", await SayAsync(admin, ".event enable 1"));
        Assert.Equal("event disabled 1 \"Test Festival\"", await SayAsync(admin, ".event disable 1"));
        Assert.False(await host.OnWorldAsync(() => events.IsActiveEvent(1)));       // a running event stops
        Assert.Equal("Event 1 already disabled!", await SayAsync(admin, ".event disable 1"));
        Assert.Equal("Event 1 is disabled!", await SayAsync(admin, ".event start 1"));

        Assert.Equal("event enabled 1 \"Test Festival\"", await SayAsync(admin, ".event enable 1"));
        Assert.Equal("event started 1 \"Test Festival\"", await SayAsync(admin, ".event start 1"));
        Assert.True(await host.OnWorldAsync(() => events.IsActiveEvent(1)));
    }

    [Fact]
    public async Task Levels_AGameMasterCanLookButNotChange_AndMissingArgumentsShowTheSyntax()
    {
        await using WorldTestHost host = await StartAsync();
        await using WorldTestClient gm = await host.EnterWorldAsync("EVTGMX", "Evtgmx", AccountSecurity.GameMaster);
        GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();

        string refused = await SayAsync(gm, ".event stop 1");
        Assert.DoesNotContain("event stopped", refused, StringComparison.Ordinal);
        Assert.True(await host.OnWorldAsync(() => events.IsActiveEvent(1)));

        await using WorldTestClient admin = await host.EnterWorldAsync("EVTADX", "Evtadx", AccountSecurity.Administrator);
        string syntax = await SayAsync(admin, ".event start");
        Assert.Contains("Syntax: .event start", syntax, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupEvent_MatchesTheDescription_IgnoringCase()
    {
        await using WorldTestHost host = await StartAsync();
        await using WorldTestClient gm = await host.EnterWorldAsync("EVTGMLB", "Evtgmlb", AccountSecurity.GameMaster);

        Assert.Equal("2 - |cffffffff|Hgameevent:2|h[Night Market]|h|r", await SayAsync(gm, ".lookup event NIGHT"));
        Assert.Equal("1 - |cffffffff|Hgameevent:1|h[Test Festival]|h|r [active]", await SayAsync(gm, ".lookup event fest"));
        Assert.Equal("No event found!", await SayAsync(gm, ".lookup event zzz"));
    }

    [Theory]
    [InlineData("12", true, 12u)]
    [InlineData("  7 ", true, 7u)]
    [InlineData("|cffffffff|Hgameevent:141|h[Winter Veil]|h|r", true, 141u)]
    [InlineData("|cffffffff|Hitem:141|h[Thing]|h|r", false, 0u)]
    [InlineData("", false, 0u)]
    [InlineData("abc", false, 0u)]
    [InlineData("-5", false, 0u)]
    public void TheEventId_IsANumberOrAGameEventLink(string args, bool ok, uint expected)
    {
        Assert.Equal(ok, GameEventCommands.TryParseEventId(args, out uint id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(0L, "0 Second.")]   // secs <= 1 is singular, even zero
    [InlineData(1L, "1 Second.")]
    [InlineData(86400L, "1 Day ")]
    [InlineData(172800L + 3600L, "2 Days 1 Hour ")]
    [InlineData(7200L + 61L, "2 Hours 1 Minute 1 Second.")]
    [InlineData(3600L * 13, "13 Hours ")]
    public void TimeStrings_AreTheLongFormOfVmangosSecsToTimeString(long seconds, string expected)
        => Assert.Equal(expected, GameEventCommands.SecsToTimeString(seconds));
}
