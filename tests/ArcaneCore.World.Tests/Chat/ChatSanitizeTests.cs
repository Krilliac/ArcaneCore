using ArcaneCore.Protocol;
using ArcaneCore.World.Chat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Chat;

/// <summary>
/// SanitizeChatMessage (vmangos ChatHandler.cpp:44-64): stripLineInvisibleChars and the link-sequence
/// check of isValidChatMessage (Chat.cpp:2165-2208), severities 1 and 2.
/// </summary>
public sealed class ChatSanitizeTests
{
    private const string Item = "|cffa335ee|Hitem:812:0:0:0:0:0:0:0:70|h[Glowing Brightwood Staff]|h|r"; // vmangos Chat.cpp:2172

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a  b", "a b")]
    [InlineData("a \t\a\n b", "a b")]
    [InlineData("  lead and trail  ", " lead and trail ")]
    [InlineData("tab\tsep", "tab sep")]
    [InlineData("\n", " ")]
    public void StripInvisibleChars_CollapsesRunsToOneSpace(string message, string expected)
        => Assert.Equal(expected, ChatSanitizer.StripInvisibleChars(message));

    [Theory]
    [InlineData("no pipes at all", 2, true)]
    [InlineData(Item, 2, true)]
    [InlineData(Item, 1, true)]
    [InlineData("|cff808080|Hquest:2278:47|h[The Platinum Discs]|h|r and |cff71d5ff|Hspell:21563|h[Command]|h|r", 2, true)]
    [InlineData("an escaped || pipe", 2, true)]
    [InlineData("an unknown |x command", 2, false)]
    [InlineData("an unknown |x command", 1, false)]
    [InlineData("trailing pipe |", 2, false)]
    [InlineData("trailing pipe |", 1, false)]
    [InlineData("|h|c|Hitem:1|h[x]|h|r", 2, false)] // out of order
    [InlineData("|h|c|Hitem:1|h[x]|h|r", 1, true)]  // order is only checked at severity 2
    [InlineData("|cffffffff|Hitem:1|h[x]", 2, true)] // an open sequence at the end is accepted
    [InlineData("|cffffffff|cffffffff", 2, false)]
    [InlineData("|r|r", 2, false)]
    public void IsValidChatMessage_FollowsTheReferenceLoop(string message, int severity, bool valid)
        => Assert.Equal(valid, ChatSanitizer.IsValidChatMessage(message, severity));

    [Fact]
    public void IsValidChatMessage_RejectsMoreThan255Bytes_AndCountsBytesNotCharacters()
    {
        Assert.True(ChatSanitizer.IsValidChatMessage(new string('a', 255), 1));
        Assert.False(ChatSanitizer.IsValidChatMessage(new string('a', 256), 1));
        Assert.True(ChatSanitizer.IsValidChatMessage(new string('é', 127), 1));  // 254 bytes
        Assert.False(ChatSanitizer.IsValidChatMessage(new string('é', 128), 1)); // 256 bytes
    }

    [Fact]
    public void Severity3_NeedsCatalogsThatAreNotAvailable_AndBehavesAsTwo()
    {
        Assert.False(ChatSanitizer.IsValidChatMessage("|h|c|Hitem:1|h[x]|h|r", 3));
        Assert.True(ChatSanitizer.IsValidChatMessage(Item, 3));
    }

    [Theory]
    [InlineData("|cffa335ee|Hitem:812:0:0:0:0:0:0:0:70|h[Glowing Brightwood Staff]|h|r", true)]
    [InlineData("|cffa335ee|Hitem:999999:0:0:0:0:0:0:0:70|h[Missing Staff]|h|r", false)]
    [InlineData("|cffa335ee|Hitem:0:0:0:0:0:0:0:0:70|h[Zero Entry]|h|r", false)]
    [InlineData("|cffa335ee|Hitem:abc:0|h[Bad Entry]|h|r", false)]
    [InlineData("|cff808080|Hquest:2278:47|h[The Platinum Discs]|h|r", true)]
    [InlineData("|cff808080|Hquest:404:47|h[Missing Quest]|h|r", false)]
    [InlineData("|cff71d5ff|Hspell:21563|h[Command]|h|r", true)]
    [InlineData("|cff71d5ff|Hspell:99999|h[Missing Spell]|h|r", false)]
    [InlineData("|cff4e96f7|Htalent:161:1|h[Ice Barrier]|h|r", true)]
    [InlineData("|cff4e96f7|Htalent:999:1|h[Missing Talent]|h|r", false)]
    [InlineData("|cffffd000|Henchant:20008|h[Enchant Bracer - Greater Intellect]|h|r", true)]
    [InlineData("|cffffd000|Henchant:99999|h[Missing Enchant]|h|r", false)]
    [InlineData("|cffffffff|Hunknown:812|h[Unknown Link Type]|h|r", false)]
    [InlineData("|cffffffff|Hitem|h[No Colon]|h|r", false)]
    public void Severity3_WithCatalogValidator_ChecksHyperlinkEntryExistence(string message, bool expected)
    {
        static bool CatalogContains(string linkType, uint id) => (linkType, id) switch
        {
            ("item", 812) => true,
            ("quest", 2278) => true,
            ("spell", 21563) => true,
            ("talent", 161) => true,
            ("enchant", 20008) => true,
            _ => false,
        };

        Assert.Equal(expected, ChatSanitizer.IsValidChatMessage(message, 3, CatalogContains));
    }

    [Fact]
    public void ChatFeature_IsValidChatMessage_UsesSeverity3CatalogDelegate()
    {
        var feature = new ChatFeature
        {
            LinkExists = (type, id) => type == "item" && id == 812,
        };
        feature.Options.StrictLinkSeverity = 3;

        Assert.True(feature.IsValidChatMessage(Item));
        Assert.False(feature.IsValidChatMessage("|cffa335ee|Hitem:813:0:0:0:0:0:0:0:70|h[Other]|h|r"));
        Assert.False(feature.ValidateCatalogLink("item", 0));
    }

    private static async Task<(WorldTestHost Host, WorldTestClient Speaker, WorldTestClient Listener)> StartAsync(Action<ChatOptions>? options = null)
    {
        WorldTestHost host = WorldTestHost.Start();
        ChatOptions chat = host.WorldServices.GetRequiredService<ChatFeature>().Options;
        chat.FakeMessagePreventing = true; // the vmangos opt-in values; the defaults are retail (off)
        chat.StrictLinkSeverity = 2;
        options?.Invoke(chat);
        WorldTestClient speaker = await host.EnterWorldAsync("SPEAKER", "Speaker");
        WorldTestClient listener = await host.EnterWorldAsync("LISTENER", "Listener");
        await speaker.CollectAsync();
        await listener.CollectAsync();
        return (host, speaker, listener);
    }

    [Fact]
    public async Task Defaults_AreRetail_NothingIsSanitised()
    {
        var defaults = new ChatOptions();
        Assert.False(defaults.FakeMessagePreventing); // mangos-classic World.cpp:681
        Assert.Equal(0, defaults.StrictLinkSeverity); // mangos-classic World.cpp:683

        WorldTestHost host = WorldTestHost.Start();
        WorldTestClient speaker = await host.EnterWorldAsync("SPEAKER", "Speaker");
        WorldTestClient listener = await host.EnterWorldAsync("LISTENER", "Listener");
        await using (host) await using (speaker) await using (listener)
        {
            await speaker.CollectAsync();
            await listener.CollectAsync();
            string text = "a   b |TInterface/x|t" + new string('z', 260);
            await speaker.SendChatAsync(ChatType.Say, Language.Common, text);
            Assert.Equal(text, (await listener.ReadChatAsync()).Text);
        }
    }

    [Fact]
    public async Task Say_IsStrippedOfInvisibleRuns_UnlessThePreventionIsOff()
    {
        (WorldTestHost host, WorldTestClient speaker, WorldTestClient listener) = await StartAsync();
        await using (host) await using (speaker) await using (listener)
        {
            await speaker.SendChatAsync(ChatType.Say, Language.Common, "hello   \t  there");
            Assert.Equal("hello there", (await listener.ReadChatAsync()).Text);

            host.WorldServices.GetRequiredService<ChatFeature>().Options.FakeMessagePreventing = false;
            await speaker.SendChatAsync(ChatType.Say, Language.Common, "hello   there");
            Assert.Equal("hello   there", (await listener.ReadChatAsync()).Text);
        }
    }

    [Fact]
    public async Task Say_WithAnInvalidLink_IsDropped_AValidOneIsDelivered_AndSeverityZeroTurnsTheCheckOff()
    {
        (WorldTestHost host, WorldTestClient speaker, WorldTestClient listener) = await StartAsync();
        await using (host) await using (speaker) await using (listener)
        {
            await speaker.SendChatAsync(ChatType.Say, Language.Common, "look |TInterface\\Icons\\x:32|t here");
            await speaker.SendChatAsync(ChatType.Say, Language.Common, Item);
            Assert.Equal(Item, (await listener.ReadChatAsync()).Text); // the invalid line before it never arrived

            ChatOptions options = host.WorldServices.GetRequiredService<ChatFeature>().Options;
            options.StrictLinkSeverity = 1;
            await speaker.SendChatAsync(ChatType.Say, Language.Common, "|h|c|Hitem:1|h[x]|h|r");
            Assert.Equal("|h|c|Hitem:1|h[x]|h|r", (await listener.ReadChatAsync()).Text);

            options.StrictLinkSeverity = 0;
            await speaker.SendChatAsync(ChatType.Say, Language.Common, "|Zodd");
            Assert.Equal("|Zodd", (await listener.ReadChatAsync()).Text);
        }
    }

    [Fact]
    public async Task AddonMessages_AreNotSanitised()
    {
        (WorldTestHost host, WorldTestClient speaker, WorldTestClient listener) = await StartAsync();
        await using (host) await using (speaker) await using (listener)
        {
            // An addon message is not run through the link check: a Battleground addon line with a bad
            // pipe still reaches the feature seam (here nobody serves it, so the point is only that
            // the connection stays healthy and the next message works).
            await speaker.SendChatAsync(ChatType.Battleground, Language.Addon, "|x   |y");
            await speaker.SendChatAsync(ChatType.Say, Language.Common, "after");
            Assert.Equal("after", (await listener.ReadChatAsync()).Text);
        }
    }

    [Fact]
    public async Task StrictLinkKick_DisconnectsThePlayerWhoSentTheInvalidMessage()
    {
        (WorldTestHost host, WorldTestClient speaker, WorldTestClient listener) = await StartAsync(o => o.StrictLinkKick = true);
        await using (host) await using (speaker) await using (listener)
        {
            await speaker.SendChatAsync(ChatType.Say, Language.Common, "bad |x link");

            Assert.True(await speaker.IsClosedByServerAsync());
            Assert.DoesNotContain(await listener.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);
        }
    }
}
