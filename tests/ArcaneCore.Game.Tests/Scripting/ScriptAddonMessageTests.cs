using ArcaneCore.Game.Scripting;
using Xunit;

namespace ArcaneCore.Game.Tests.Scripting;

/// <summary>
/// The addon line split. The client builds the message with the format "%s\t%s" (prefix, text) in SendAddonMessage:
/// Wow.exe 5875 0x49F9A2, format string 0x844B5C; HermesProxy@841a26f5 ChatHandler.cs:212.
/// </summary>
public sealed class ScriptAddonMessageTests
{
    [Fact]
    public void Parse_SplitsAtTheFirstTab_AsTheClientJoinsPrefixAndMessage()
    {
        ScriptAddonMessage message = ScriptAddonMessage.Parse(0x5C, "CTRA\tMS 1", null);
        Assert.Equal(0x5Cu, message.ChatType);
        Assert.Equal("CTRA", message.Prefix);
        Assert.Equal("MS 1", message.Text);
        Assert.Null(message.Target);

        // The client does not stop a prefix from containing a TAB (Wow.exe 0x49F97F-0x49F9A0), so only the first one splits.
        ScriptAddonMessage second = ScriptAddonMessage.Parse(1, "P\ta\tb", "Name");
        Assert.Equal("P", second.Prefix);
        Assert.Equal("a\tb", second.Text);
        Assert.Equal("Name", second.Target);
    }

    [Fact]
    public void Parse_WithoutATab_HasNoPrefix_AndKeepsTheWholeLine()
    {
        ScriptAddonMessage message = ScriptAddonMessage.Parse(1, "no tab here", null);
        Assert.Null(message.Prefix);
        Assert.Equal("no tab here", message.Text);
    }

    [Fact]
    public void Parse_EmptyPrefixOrBody_AreKept()
    {
        ScriptAddonMessage noPrefix = ScriptAddonMessage.Parse(1, "\tx", null);
        Assert.Equal("", noPrefix.Prefix);
        Assert.Equal("x", noPrefix.Text);

        ScriptAddonMessage noBody = ScriptAddonMessage.Parse(1, "P\t", null);
        Assert.Equal("P", noBody.Prefix);
        Assert.Equal("", noBody.Text);
    }
}
