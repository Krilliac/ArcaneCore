using ArcaneCore.World.Reload;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// vmangos HandleReloadCreatureTemplatesCommand and HandleReloadGameObjectTemplatesCommand (ServerCommands.cpp:1758-1785) take an optional
/// template entry. The word after the reloadable name is that entry, not part of the reason text of the hot-swap report.
/// </summary>
public sealed class ReloadCommandEntryTests
{
    [Theory]
    [InlineData("1234", 1234u, "")]
    [InlineData("1234 after the db fix", 1234u, "after the db fix")]
    [InlineData("0   spaced", 0u, "spaced")]
    public void ALeadingNumber_IsTheEntry_AndTheRestIsTheReason(string text, uint expectedEntry, string expectedReason)
    {
        string reason = text;

        Assert.True(ReloadCommands.TakeEntry(ref reason, out uint entry));

        Assert.Equal(expectedEntry, entry);
        Assert.Equal(expectedReason, reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("after the db fix")]
    [InlineData("-5")]
    [InlineData("12x")]
    public void AnythingElse_IsLeftAsTheReason(string text)
    {
        string reason = text;

        Assert.False(ReloadCommands.TakeEntry(ref reason, out _));

        Assert.Equal(text, reason);
    }
}
