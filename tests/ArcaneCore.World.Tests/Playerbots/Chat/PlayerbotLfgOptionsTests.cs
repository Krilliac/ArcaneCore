using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>The LFG options (<c>World:Playerbots:Chat:Lfg</c>): the defaults and the ranges.</summary>
public sealed class PlayerbotLfgOptionsTests
{
    [Fact]
    public void Options_AreOnByDefault_AndChecked()
    {
        var options = new PlayerbotLfgOptions();
        Assert.True(options.Enabled && options.AnswerQueries && options.ChannelAvailability && options.PublicAnswers && options.LapseNotice && options.BotAsks);
        Assert.False(options.WorldBossQueries);
        Assert.Equal((2, 6, 60, 120, 120, 60, 32, 4, 64, 6),
            (options.MaxGroupsPerAnswer, options.QueriesPerMinute, options.PlayerQueryCooldownSeconds, options.ReferralSeconds,
                options.ChannelReplyWindowSeconds, options.AnswerRepeatSeconds, options.MaxQueuedLines, options.LinesPerPass,
                options.MaxTrackedVolunteers, options.BotAsksPerHour));
        options.Validate();
        new PlayerbotChatOptions().Validate();
        Assert.Same(options.GetType(), new PlayerbotChatOptions().Lfg.GetType());
    }

    [Theory]
    [InlineData(nameof(PlayerbotLfgOptions.MaxGroupsPerAnswer), 0)]
    [InlineData(nameof(PlayerbotLfgOptions.MaxGroupsPerAnswer), 6)]
    [InlineData(nameof(PlayerbotLfgOptions.QueriesPerMinute), 61)]
    [InlineData(nameof(PlayerbotLfgOptions.PlayerQueryCooldownSeconds), 3601)]
    [InlineData(nameof(PlayerbotLfgOptions.ReferralSeconds), 29)]
    [InlineData(nameof(PlayerbotLfgOptions.ChannelReplyWindowSeconds), 601)]
    [InlineData(nameof(PlayerbotLfgOptions.AnswerRepeatSeconds), 9)]
    [InlineData(nameof(PlayerbotLfgOptions.MaxQueuedLines), 0)]
    [InlineData(nameof(PlayerbotLfgOptions.LinesPerPass), 33)]
    [InlineData(nameof(PlayerbotLfgOptions.MaxTrackedVolunteers), 7)]
    [InlineData(nameof(PlayerbotLfgOptions.BotAsksPerHour), -1)]
    public void Validate_RefusesAValueOutsideItsRange(string key, int value)
    {
        var options = new PlayerbotLfgOptions();
        typeof(PlayerbotLfgOptions).GetProperty(key)!.SetValue(options, value);
        Assert.Throws<InvalidOperationException>(options.Validate);
        var chat = new PlayerbotChatOptions();
        typeof(PlayerbotLfgOptions).GetProperty(key)!.SetValue(chat.Lfg, value);
        Assert.Throws<InvalidOperationException>(chat.Validate);
    }
}
