using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A stall reported from outside the watch (<see cref="PlayerbotStallWatch.ReportExternal"/>) stands until the bot makes progress, like
/// one the watch finds itself. Before, the watch's restart at the next look cleared it without any progress, and a population pass that
/// came after that think saw no stall.
/// </summary>
public sealed class PlayerbotStallWatchExternalReportTests
{
    private const uint StallMs = 60_000;

    private static readonly Func<Player, PlayerbotStallWatch.Fingerprint> Unchanged = static _ => default;

    [Fact]
    public void AnExternalReport_OutlivesTheNextLooks_UntilTheBotMovesProgressYards()
    {
        var watch = new PlayerbotStallWatch();
        Player bot = CreatePlayer();
        Observe(watch, bot, 1_000); // a running watch, anchored here

        watch.ReportExternal("tram trip failed");
        Assert.Equal("tram trip failed", watch.Report);

        Observe(watch, bot, 2_000); // the restart: re-anchors, keeps the report
        Assert.Equal("tram trip failed", watch.Report);
        bot.Relocate(bot.X + PlayerbotStallWatch.ProgressYards - 1f, bot.Y, bot.Z);
        Observe(watch, bot, 3_000); // short of progress
        Assert.Equal("tram trip failed", watch.Report);

        bot.Relocate(bot.X + 1.5f, bot.Y, bot.Z); // now ProgressYards and more from the anchor
        Observe(watch, bot, 4_000);
        Assert.Null(watch.Report);
    }

    [Fact]
    public void Reset_DropsTheHeldExternalReport()
    {
        var watch = new PlayerbotStallWatch();
        Player bot = CreatePlayer();
        watch.ReportExternal("tram trip failed");
        Assert.True(ExternalHeld(watch));

        watch.Reset();
        Assert.False(ExternalHeld(watch));
        Assert.Null(watch.Report);
        Observe(watch, bot, 1_000);
        Assert.Null(watch.Report);
    }

    [Fact]
    public void AStallFoundAfterAnUnresolvedExternalReport_IsARepeatGiveUp_NotASecondStall()
    {
        var watch = new PlayerbotStallWatch();
        Player bot = CreatePlayer();
        watch.ReportExternal("tram trip failed");
        Assert.Equal(1, watch.Count);
        Assert.Equal(1, watch.GiveUps);

        Assert.False(Observe(watch, bot, 1_000)); // re-anchors, the report stands
        Assert.False(Observe(watch, bot, 1_000 + StallMs - 1)); // under one stall bound without progress
        Assert.True(Observe(watch, bot, 1_000 + StallMs)); // a whole bound: the caller gives up again

        Assert.True(watch.LastGiveUpRepeated);
        Assert.Equal(2, watch.GiveUps);
        Assert.Equal(1, watch.Count); // the same stall, counted once
        Assert.Equal("tram trip failed", watch.LastReport);
        Assert.StartsWith("stalled 60s", watch.Report);
    }

    private static bool Observe(PlayerbotStallWatch watch, Player bot, uint nowMs)
        => watch.Observe(bot, PlayerbotGoalKind.Explore, 0, 0, nowMs, StallMs, Unchanged);

    private static bool ExternalHeld(PlayerbotStallWatch watch)
        => (bool)typeof(PlayerbotStallWatch).GetField("_externalHeld", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watch)!;

    private static Player CreatePlayer()
    {
        var character = new CharacterRecord
        {
            Id = 1, AccountId = 1, Name = "Stallwatch", Race = 1, Class = 1, Gender = 0, Level = 10,
            MapId = 0, ZoneId = 12, X = -8949.95f, Y = -132.493f, Z = 83.5312f,
        };
        var appearance = new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 1000, 0);
        return new Player(character, appearance, new NullPlayerSession());
    }
}
