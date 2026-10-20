using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

public sealed class PlayerbotSocialAndGroupPerfTests
{
    [Theory]
    [InlineData("  WTB   [Linen Cloth] x20!! ", "wtb linen cloth x20")]
    [InlineData("LF2M Deadmines - need tank & healer!", "lf2m deadmines need tank healer")]
    [InlineData("Buying Traveler's Backpack...", "buying traveler's backpack")]
    [InlineData("   ---   ", "")]
    public void WorldChatterNormalize_MatchesLegacyRegexSemantics(string input, string expected)
    {
        Assert.Equal(expected, PlayerbotWorldChatter.Normalize(input));
    }

    [Fact]
    public void WorldChatterWanted_MatchesWholeItemWordsOnly()
    {
        BotChatItem[] goods =
        [
            new(2589, "Linen Cloth", 20, 20, 13),
            new(2592, "Wool Cloth", 10, 20, 33),
            new(4500, "Traveler's Backpack", 1, 1, 5000),
        ];

        Assert.Equal("Linen Cloth", PlayerbotWorldChatter.Wanted("WTB Linen Cloth 2s/stack", goods)?.Name);
        Assert.Equal("Traveler's Backpack", PlayerbotWorldChatter.Wanted("buying TRAVELER'S BACKPACK pst", goods)?.Name);
        Assert.Null(PlayerbotWorldChatter.Wanted("WTB Linen", goods));
        Assert.Null(PlayerbotWorldChatter.Wanted("WTB Silk Cloth", goods));
    }

    [Fact]
    public void GroupCoordinator_NeedsBotSnapshot_FalseBetweenScansAfterInitialSnapshot()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayerbotGroupCoordinator>.Instance;
        var coordinator = new PlayerbotGroupCoordinator(services, logger);

        // Before the first ScanMs elapses (initial _scanElapsed == ScanMs), a snapshot is due.
        Assert.True(coordinator.NeedsBotSnapshot(50));
    }
}
