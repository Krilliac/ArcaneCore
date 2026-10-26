using System.Text;
using ArcaneCore.World.Playerbots;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Commands;
using ArcaneCore.World.Tests.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>What the bot remembers of a player reaches its prompt and its built-in greeting, and the GM command prints it.</summary>
public sealed class PlayerbotSocialSpeechTests
{
    private static BotChatRelation Relation(int familiarity, int affinity, string? topic = "greeting", string? summary = "Hello there.")
        => new(familiarity, affinity, PlayerbotSocialBands.Of(affinity), false, BotChatRole.Healer, topic, summary, 2, 0);

    private static BotChatAsk AskWith(BotChatRelation? relation, string text = "hello")
    {
        BotChatPersona persona = ChatSamples.Persona() with { Relation = relation };
        return ChatSamples.Ask(text, persona);
    }

    [Fact]
    public void Prompt_HasTheRelationLine_OnlyWhenARelationExists()
    {
        string without = PlayerbotChatPrompts.DynamicSystem(AskWith(null));
        string with = PlayerbotChatPrompts.DynamicSystem(AskWith(Relation(4, 30)));
        Assert.DoesNotContain("You know", without);
        Assert.Contains("You know Testplayer: met 4 times, you like them", with);
        Assert.Contains("last time: greeting", with);
        Assert.Contains("(Hello there.)", with);
        Assert.Contains("heal in a group", with);
    }

    [Theory]
    [InlineData(-30, "you dislike them")]
    [InlineData(0, "you feel neutral about them")]
    [InlineData(30, "you like them")]
    [InlineData(80, "you are close friends")]
    public void RelationLine_ReadsTheBand(int affinity, string words)
    {
        var text = new StringBuilder();
        PlayerbotChatPrompts.AppendRelation(text, Relation(1, affinity, null, null), "Sam");
        Assert.Contains(words, text.ToString());
        Assert.Contains("met 1 time,", text.ToString());
    }

    [Fact]
    public void RelationLine_NamesARival()
    {
        var text = new StringBuilder();
        PlayerbotChatPrompts.AppendRelation(text, Relation(2, -60) with { Rival = true }, "Sam");
        Assert.Contains("rival", text.ToString());
    }

    [Theory]
    [InlineData(3, 0, "greeting.back")]
    [InlineData(10, 50, "greeting.back")]
    [InlineData(2, 50, null)]
    [InlineData(5, -10, null)]
    [InlineData(5, -40, "greeting.curt")]
    [InlineData(0, -60, "greeting.curt")]
    public void GreetingVariant_SwitchesOnFamiliarityAndAffinity(int familiarity, int affinity, string? expected)
        => Assert.Equal(expected, PlayerbotBuiltinChat.GreetingVariant(Relation(familiarity, affinity)));

    [Fact]
    public void GreetingVariant_NoRelation_IsThePlainGreeting()
        => Assert.Null(PlayerbotBuiltinChat.GreetingVariant(null));

    [Fact]
    public void BuiltinGreeting_UsesTheWelcomeBackAndCurtLines()
    {
        var random = new Random(1);
        string[] back = [.. PlayerbotChatTemplates.Lines["greeting.back"].Select(l => l.Replace("{player}", "Testplayer", StringComparison.Ordinal))];
        string[] curt = [.. PlayerbotChatTemplates.Lines["greeting.curt"].Select(l => l.Replace("{player}", "Testplayer", StringComparison.Ordinal))];
        for (int i = 0; i < 20; i++)
        {
            Assert.Contains(PlayerbotBuiltinChat.Answer(AskWith(Relation(5, 20)), null, false, random).Text, back);
            Assert.Contains(PlayerbotBuiltinChat.Answer(AskWith(Relation(5, -50)), null, false, random).Text, curt);
            string plain = PlayerbotBuiltinChat.Answer(AskWith(null), null, false, random).Text!;
            Assert.DoesNotContain(plain, back);
            Assert.DoesNotContain(plain, curt);
        }
    }

    [Fact]
    public void SocialCommand_ListsTraitsAndTopRelations()
    {
        PlayerbotRelation[] top =
        [
            new(Guid.Empty, 1, 0x2A, false, 20, 5, 500, PlayerbotRelationFlags.Grouped, 1_000_000, "greeting", "Hi."),
            new(Guid.Empty, 1, 0x2B, true, -60, 2, 500, PlayerbotRelationFlags.Rival, 1_000_000 - 90_000_000, "", ""),
        ];
        string[] lines = [.. PlayerbotExtraCommands.SocialLines("Relbot", PlayerbotTraits.For(9), true, top,
            guid => guid == 0x2A ? "Testplayer" : null, 1_000_000)];
        Assert.StartsWith("Relbot: sociability", lines[0]);
        Assert.Contains("Testplayer (player): affinity 20, familiarity 5, trust 500, flags Grouped, topic greeting, seen 0m ago", lines[1]);
        Assert.Contains("guid 2B (bot): affinity -60", lines[2]);
        Assert.Contains("flags Rival, topic -, seen 1d ago", lines[2]);
        Assert.Equal("No relations yet.", PlayerbotExtraCommands.SocialLines("Relbot", PlayerbotTraits.For(9), true, [], _ => null, 0).Last());
    }

    [Fact]
    public async Task AHeardLineListener_BuildsNoHeardLines_WhileTheMemoryIsDisabled_AndDoesAgainWhenItIsSwitchedOn()
    {
        var options = new PlayerbotOptions();
        options.Validate();
        await using var chat = new PlayerbotChat(options, new FakeBotChatClient(), new FakeBotChatEnvironment(new()), new ManualTime(), new ListLogger());
        chat.Social = new PlayerbotSocialRecorder(new PlayerbotSocialMemory(options.Social.Memory, () => 0, options.Social.WarMode),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        chat.LineHeard += _ => { };

        options.Social.Memory.Enabled = false;
        Assert.False(chat.HasHearers); // nobody would use the lines: the party AI builds none
        options.Social.Memory.Enabled = true; // a live reload
        Assert.True(chat.HasHearers);
    }
}
