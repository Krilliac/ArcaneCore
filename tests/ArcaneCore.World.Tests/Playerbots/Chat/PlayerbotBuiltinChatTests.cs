using System.Text.RegularExpressions;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>The built-in provider: intents, answers from the bot's real state, orders only from the master, no repeats.</summary>
public sealed class PlayerbotBuiltinChatTests
{
    [Theory]
    [InlineData("hi there", "Greeting")]
    [InlineData("Chatbot, hello!", "Greeting")]
    [InlineData("thanks!", "Thanks")]
    [InlineData("ok bye", "Bye")]
    [InlineData("what are you doing?", "Doing")]
    [InlineData("where are you going", "Doing")]
    [InlineData("where are you?", "Where")]
    [InlineData("what level are you", "Who")]
    [InlineData("what class?", "Who")]
    [InlineData("are you a bot?", "BotQuestion")]
    [InlineData("want to group?", "Group")]
    [InlineData("inv?", "Group")]
    [InlineData("help", "Help")]
    [InlineData("you are an idiot", "Abuse")]
    [InlineData("kys", "Ignore")]
    [InlineData("the weather is strange today", "Unknown")]
    public void TheIntent_OfALine(string text, string expected)
        => Assert.Equal(expected, PlayerbotBuiltinChat.Classify(text, "Chatbot", out _).ToString());

    [Theory]
    [InlineData("Chatbot, follow me", PlayerbotPartyCommand.Follow)]
    [InlineData("wait here", PlayerbotPartyCommand.Stay)]
    [InlineData("stay", PlayerbotPartyCommand.Stay)]
    [InlineData("attack my target", PlayerbotPartyCommand.Attack)]
    [InlineData("help me kill this", PlayerbotPartyCommand.Attack)]
    [InlineData("stop attacking", PlayerbotPartyCommand.Stop)]
    [InlineData("come here", PlayerbotPartyCommand.Come)]
    public void AnOrder_IsReadAsItsPartyCommand(string text, PlayerbotPartyCommand expected)
    {
        Assert.Equal(BuiltinChatIntent.Command, PlayerbotBuiltinChat.Classify(text, "Chatbot", out PlayerbotPartyCommand? command));
        Assert.Equal(expected, command);
    }

    [Fact]
    public void AnOrder_IsCarriedOutOnlyForTheMaster_AndOthersGetAPoliteNo()
    {
        BotChatPersona grouped = ChatSamples.Persona(master: "Leader", inGroup: true);
        BuiltinChatAnswer fromMaster = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("follow me", grouped, fromMaster: true), null, true, new Random(1));
        Assert.Equal(PlayerbotPartyCommand.Follow, fromMaster.Command);
        Assert.Null(fromMaster.Text); // the party command acknowledges ("Following.")

        BuiltinChatAnswer fromOther = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("follow me", grouped), null, true, new Random(1));
        Assert.Null(fromOther.Command);
        Assert.Contains("Leader", fromOther.Text);

        BuiltinChatAnswer ungrouped = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("follow me"), null, true, new Random(1));
        Assert.Null(ungrouped.Command);
        Assert.NotNull(ungrouped.Text);

        BuiltinChatAnswer noLanguageOrders = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("follow me", grouped, fromMaster: true), null, false, new Random(1));
        Assert.Null(noLanguageOrders.Command);
        Assert.Equal(PlayerbotChatCommands.Help, noLanguageOrders.Text);
    }

    [Fact]
    public void TheAnswers_ComeFromTheBotsRealState()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            string doing = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("what are you doing?"), null, true, random).Text!;
            Assert.Contains("Wolves Across the Border", doing);

            string grinding = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("what are you up to",
                ChatSamples.Persona(goal: PlayerbotGoalKind.Grind, quest: null)), null, true, random).Text!;
            Assert.Contains("hunting around Elwynn Forest", grinding, StringComparison.OrdinalIgnoreCase);

            string where = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("where are you?"), null, true, random).Text!;
            Assert.Contains("Northshire Valley", where);
            Assert.Contains("Elwynn Forest", where);

            string zoneOnly = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("where are you?", ChatSamples.Persona(subzone: null)), null, true, random).Text!;
            Assert.Contains("Elwynn Forest", zoneOnly);

            string nowhere = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("where are you?", ChatSamples.Persona(zone: null, subzone: null)), null, true, random).Text!;
            Assert.DoesNotContain("{", nowhere);

            string who = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("what level are you?", ChatSamples.Persona(level: 34, @class: Class.Mage)), null, true, random).Text!;
            Assert.Matches(@"\b34\b", who);
            Assert.Contains("mage", who, StringComparison.OrdinalIgnoreCase);

            string idle = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("what are you doing?", ChatSamples.Persona(goal: null, quest: null)), null, true, random).Text!;
            Assert.DoesNotContain("{", idle);
        }
    }

    [Fact]
    public void TheGroupAnswer_FollowsTheInvitePolicy()
    {
        Assert.Contains("invite", PlayerbotBuiltinChat.Answer(ChatSamples.Ask("want to group?", inviteAllowed: true), null, true, new Random(3)).Text!,
            StringComparison.OrdinalIgnoreCase);
        string refused = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("want to group?", inviteAllowed: false), null, true, new Random(3)).Text!;
        Assert.Contains(refused, PlayerbotChatTemplates.Lines["group.no"]);
        string grouped = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("inv?", ChatSamples.Persona(inGroup: true), inviteAllowed: true), null, true, new Random(3)).Text!;
        Assert.Contains(grouped, PlayerbotChatTemplates.Lines["group.already"]);
    }

    [Fact]
    public void SevereAbuse_IsIgnored_AndAnInsultGetsABrushOff()
    {
        BuiltinChatAnswer severe = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("kys"), null, true, new Random(1));
        Assert.Null(severe.Text);
        Assert.Null(severe.Command);
        Assert.Contains(PlayerbotBuiltinChat.Answer(ChatSamples.Ask("you are stupid"), null, true, new Random(1)).Text!, PlayerbotChatTemplates.Lines["abuse"]);
    }

    [Fact]
    public void TheSameLine_IsNeverSaidTwiceInARow_ToThePlayer()
    {
        var random = new Random(7);
        string? last = null;
        for (int i = 0; i < 50; i++)
        {
            string line = PlayerbotBuiltinChat.Answer(ChatSamples.Ask("hello"), last, true, random).Text!;
            Assert.NotEqual(last, line);
            last = line;
        }
    }

    [Fact]
    public void TheVariants_ByRaceClassAndLevel_AreUsed()
    {
        var seen = new HashSet<string>();
        for (int seed = 0; seed < 200; seed++)
            seen.Add(PlayerbotBuiltinChat.Answer(ChatSamples.Ask("hello", ChatSamples.Persona(race: Race.Orc)), null, true, new Random(seed)).Text!);
        Assert.Contains("Lok'tar, Nathan.", seen);

        var veteran = new HashSet<string>();
        for (int seed = 0; seed < 200; seed++)
            veteran.Add(PlayerbotBuiltinChat.Answer(ChatSamples.Ask("what level?", ChatSamples.Persona(level: 60, @class: Class.Priest)), null, true, new Random(seed)).Text!);
        Assert.Contains("Level 60 priest. Can't get any higher than that.", veteran);
        Assert.Contains("Level 60 priest. Stay in range and I'll keep you standing.", veteran);
    }

    [Fact]
    public void EveryTemplate_UsesOnlyKnownPlaceholders_AndFitsAChatLine()
    {
        var known = new HashSet<string> { "{player}", "{name}", "{level}", "{race}", "{class}", "{zone}", "{subzone}", "{goal}", "{Goal}", "{quest}", "{master}" };
        BotChatAsk full = ChatSamples.Ask("x", ChatSamples.Persona(master: "Leader", inGroup: true));
        foreach ((string key, string[] lines) in PlayerbotChatTemplates.Lines)
        {
            Assert.True(lines.Length >= 1, key);
            foreach (string line in lines)
            {
                foreach (Match placeholder in Regex.Matches(line, @"\{[A-Za-z]+\}")) Assert.Contains(placeholder.Value, known);
                string filled = PlayerbotBuiltinChat.Fill(line, full)!;
                Assert.NotNull(filled);
                Assert.True(filled.Length <= PlayerbotChatPrompts.MaxReplyLength, key);
                Assert.DoesNotContain("|", filled);
            }
        }
    }
}
