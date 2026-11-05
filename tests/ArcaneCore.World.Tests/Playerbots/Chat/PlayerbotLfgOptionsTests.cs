using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>The LFG options (<c>World:Playerbots:Chat:Lfg</c>): the defaults and the ranges.</summary>
public sealed class PlayerbotLfgOptionsTests
{
    [Fact]
    public void Options_HaveTheirDefaults_AndAreChecked()
    {
        var options = new PlayerbotLfgOptions();
        Assert.True(options.Enabled);
        Assert.Equal((60, 64), (options.AnswerRepeatSeconds, options.MaxTrackedVolunteers));
        options.Validate();
        new PlayerbotChatOptions().Validate();
        Assert.Same(options.GetType(), new PlayerbotChatOptions().Lfg.GetType());
    }

    [Theory]
    [InlineData(nameof(PlayerbotLfgOptions.AnswerRepeatSeconds), 9)]
    [InlineData(nameof(PlayerbotLfgOptions.AnswerRepeatSeconds), 601)]
    [InlineData(nameof(PlayerbotLfgOptions.MaxTrackedVolunteers), 7)]
    [InlineData(nameof(PlayerbotLfgOptions.MaxTrackedVolunteers), 513)]
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
