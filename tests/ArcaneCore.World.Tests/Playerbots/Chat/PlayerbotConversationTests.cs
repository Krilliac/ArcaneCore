using ArcaneCore.Game;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// Bots talking: the conversation scripts (only real facts start one; who speaks when; the line cap), every scripted line filled from the
/// speakers' facts, the world channel reading (LFM, WTB, the asked role, the goods named, the price), the dungeons of the time, the new
/// built-in intents and their answers, the model's actions and which of them a line may carry, the chat events' own clock and the
/// conversation options. Pure and deterministic.
/// </summary>
public sealed class PlayerbotConversationTests
{
    private static ConversationSide Side(string name = "Alpha", byte level = 20, BotChatFacts? facts = null, string? quest = null, string? ding = null,
        string? loot = null, string? boss = null, bool died = false)
        => new(name, level, facts ?? new BotChatFacts(), quest, ding, loot, boss, boss is null ? null : "The Deadmines", died);

    // --- scripts -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Topics_PutNewsFirst_AndGreetOnlyInGuildChat()
    {
        ConversationSide a = Side(ding: "21", quest: "The Defias Brotherhood");
        List<ConversationTopic> party = PlayerbotConversationScripts.Topics(a, Side("Beta"), sameGroup: true, guild: false, new Random(3));
        Assert.Equal(ConversationTopic.Ding, party[0]);
        Assert.DoesNotContain(ConversationTopic.Greet, party);
        Assert.Contains(ConversationTopic.Quest, party);
        Assert.Contains(ConversationTopic.Greet, PlayerbotConversationScripts.Topics(a, Side("Beta"), sameGroup: false, guild: true, new Random(3)));
    }

    [Fact]
    public void Ding_TheOtherCongratulates_AndTheNewLevelIsTheRealOne()
    {
        ConversationScript script = PlayerbotConversationScripts.Script(ConversationTopic.Ding, Side(ding: "21"), Side("Beta"), sameGroup: true, maxLines: 4)!;
        Assert.Equal([1, 0, 1, 0], script.Steps.Select(s => s.Speaker));
        Assert.Equal("convo.ding.grats", script.Steps[0].Key);
        Assert.Equal("21", script.Values["newlevel"]);
    }

    [Fact]
    public void NoNews_NoNewsScript()
    {
        Assert.Null(PlayerbotConversationScripts.Script(ConversationTopic.Ding, Side(), Side("Beta"), true, 4));
        Assert.Null(PlayerbotConversationScripts.Script(ConversationTopic.Loot, Side(), Side("Beta"), true, 4));
        Assert.Null(PlayerbotConversationScripts.Script(ConversationTopic.Death, Side(), Side("Beta"), true, 4));
    }

    [Fact]
    public void Boss_OnlyBetweenGroupmates_AndTheOthersLootIsTold()
    {
        ConversationSide a = Side(boss: "Edwin VanCleef");
        Assert.Null(PlayerbotConversationScripts.Script(ConversationTopic.Boss, a, Side("Beta"), sameGroup: false, maxLines: 4));
        ConversationScript script = PlayerbotConversationScripts.Script(ConversationTopic.Boss, a, Side("Beta", loot: "Cruel Barb"), true, 4)!;
        Assert.Equal("convo.boss.answerloot", script.Steps[^1].Key);
        Assert.Equal("Cruel Barb", script.Values["loot"]);
        Assert.Equal("Edwin VanCleef", script.Values["boss"]);
    }

    [Fact]
    public void Need_OnlyWhenTheOtherCarriesEnough()
    {
        var thirsty = new BotChatFacts { Needs = [BotChatNeed.Water], ManaPct = 10 };
        Assert.Null(PlayerbotConversationScripts.Wanted(Side(facts: thirsty), Side("Beta", facts: new BotChatFacts { Drink = 2 })));
        ConversationScript script = PlayerbotConversationScripts.Script(ConversationTopic.Need, Side(facts: thirsty),
            Side("Beta", facts: new BotChatFacts { Drink = 12 }), sameGroup: true, maxLines: 4)!;
        Assert.Equal("water", script.Values["wanted"]);
        Assert.Equal("6", script.Values["count"]);
        Assert.Equal("convo.need.ask", script.Steps[0].Key);
        Assert.Equal(0, script.Steps[0].Speaker);
    }

    [Fact]
    public void Quest_SameQuest_OffersToGroup_OtherQuest_SaysWhich_NoQuest_NoScript()
    {
        var progress = new BotChatFacts { Objective = "Defias Thug 3/15" };
        ConversationSide a = Side(quest: "Red Silk Bandanas");
        ConversationScript same = PlayerbotConversationScripts.Script(ConversationTopic.Quest, a, Side("Beta", facts: progress, quest: "Red Silk Bandanas"),
            sameGroup: false, maxLines: 6)!;
        Assert.Equal(["convo.quest.ask", "convo.quest.same", "convo.quest.group", "convo.quest.close"], same.Steps.Select(s => s.Key));
        ConversationScript grouped = PlayerbotConversationScripts.Script(ConversationTopic.Quest, a, Side("Beta", facts: progress, quest: "Red Silk Bandanas"),
            sameGroup: true, maxLines: 6)!;
        Assert.Equal(2, grouped.Steps.Count);
        ConversationScript other = PlayerbotConversationScripts.Script(ConversationTopic.Quest, a, Side("Beta", quest: "The Collector"), false, 6)!;
        Assert.Equal("convo.quest.other", other.Steps[1].Key);
        Assert.Null(PlayerbotConversationScripts.Script(ConversationTopic.Quest, a, Side("Beta"), false, 6));
    }

    [Fact]
    public void Dungeon_TheOtherSaysYesOnlyWhenItSuitsThemToo()
    {
        var deadmines = new BotChatFacts { SuitedDungeon = "The Deadmines" };
        ConversationScript yes = PlayerbotConversationScripts.Script(ConversationTopic.Dungeon, Side(facts: deadmines), Side("Beta", facts: deadmines), false, 4)!;
        Assert.Equal("convo.dungeon.yes", yes.Steps[1].Key);
        ConversationScript no = PlayerbotConversationScripts.Script(ConversationTopic.Dungeon, Side(facts: deadmines),
            Side("Beta", facts: new BotChatFacts { SuitedDungeon = "Shadowfang Keep" }), false, 4)!;
        Assert.Equal("convo.dungeon.no", no.Steps[1].Key);
        Assert.Null(PlayerbotConversationScripts.Script(ConversationTopic.Dungeon, Side(level: 10, facts: deadmines), Side("Beta"), false, 4));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(1)]
    public void Scripts_AreCutToTheLineCap_NeverBelowTwo(int cap)
    {
        ConversationScript script = PlayerbotConversationScripts.Script(ConversationTopic.Chat, Side(), Side("Beta"), false, cap)!;
        Assert.Equal(Math.Max(PlayerbotConversationScripts.MinLines, cap), script.Steps.Count);
    }

    [Theory]
    [InlineData((int)ConversationTopic.Ding)]
    [InlineData((int)ConversationTopic.Boss)]
    [InlineData((int)ConversationTopic.Loot)]
    [InlineData((int)ConversationTopic.Death)]
    [InlineData((int)ConversationTopic.Need)]
    [InlineData((int)ConversationTopic.Quest)]
    [InlineData((int)ConversationTopic.Dungeon)]
    [InlineData((int)ConversationTopic.Chat)]
    [InlineData((int)ConversationTopic.Greet)]
    public void EveryScriptedLine_FillsFromRealFacts(int topicValue)
    {
        var topic = (ConversationTopic)topicValue;
        BotChatFacts facts = ChatSamples.FullFacts() with { Drink = 12, Instance = null };
        ConversationSide a = Side("Chatbot", facts: facts, quest: "Wolves Across the Border", ding: "21", loot: "Cruel Barb", boss: "Edwin VanCleef", died: true);
        ConversationSide b = Side("Testplayer", facts: facts, quest: "Wolves Across the Border", loot: "Smelting Pants");
        ConversationScript script = PlayerbotConversationScripts.Script(topic, a, b, sameGroup: true, maxLines: 6)!;
        Assert.NotNull(script);
        var botChatTopic = new BotChatTopic("convo", string.Empty, script.Values, string.Empty);
        BotChatPersona persona = ChatSamples.Persona(level: 20) with { Facts = facts };
        foreach (ConversationStep step in script.Steps)
        {
            BotChatAsk ask = ChatSamples.Ask(string.Empty, persona, channel: ChatType.Party) with { Topic = botChatTopic, SenderIsBot = true };
            string? line = PlayerbotBuiltinChat.Pick(step.Key, ask, null, new Random(5));
            Assert.False(string.IsNullOrWhiteSpace(line), step.Key);
            Assert.DoesNotContain("{", line);
        }
    }

    // --- world channels ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("LFM SFK need heals", (int)WorldLineKind.LookingForMore)]
    [InlineData("lf2m dm then go", (int)WorldLineKind.LookingForMore)]
    [InlineData("WTB Linen Cloth x20", (int)WorldLineKind.WantToBuy)]
    [InlineData("anyone selling linen? buying", (int)WorldLineKind.WantToBuy)]
    [InlineData("hello everyone", (int)WorldLineKind.None)]
    [InlineData("", (int)WorldLineKind.None)]
    public void WorldLines_AreRead(string text, int kind) => Assert.Equal((WorldLineKind)kind, PlayerbotWorldChatter.Read(text));

    [Theory]
    [InlineData("LFM SFK need tank", (int)BotChatRole.Tank)]
    [InlineData("lf1m wc heals", (int)BotChatRole.Healer)]
    [InlineData("lf2m dm dps", (int)BotChatRole.Damage)]
    public void LfmLines_NameTheRole(string text, int role) => Assert.Equal((BotChatRole)role, PlayerbotWorldChatter.AskedRole(text));

    [Fact]
    public void LfmLine_WithoutARole_AsksForAnyone() => Assert.Null(PlayerbotWorldChatter.AskedRole("LFM RFC"));

    [Fact]
    public void WtbLine_MatchesSpareGoodsByWholeName()
    {
        BotChatItem linen = new(2589, "Linen Cloth", 12, 1, 13);
        Assert.Equal(linen, PlayerbotWorldChatter.Wanted("WTB linen cloth, pst", [linen]));
        Assert.Null(PlayerbotWorldChatter.Wanted("WTB wool cloth", [linen]));
        Assert.Null(PlayerbotWorldChatter.Wanted("WTB linen cloth", []));
    }

    [Fact]
    public void Price_IsTwiceTheVendorValue_AtLeastASilver()
    {
        Assert.Equal("1 silver", PlayerbotWorldChatter.Price(new BotChatItem(1, "Linen Cloth", 1, 1, 13)));
        Assert.Equal("1 gold", PlayerbotWorldChatter.Price(new BotChatItem(2, "Blade", 1, 2, 5000)));
    }

    [Theory]
    [InlineData("lfm dm", 20, "The Deadmines")]
    [InlineData("lfm dm", 58, "Dire Maul")]
    [InlineData("LF1M SFK", 25, "Shadowfang Keep")]
    [InlineData("lfm stratholme", 59, "Stratholme")]
    public void Dungeons_AreNamedAsPlayersNamedThem(string text, int level, string name) => Assert.Equal(name, PlayerbotDungeons.Named(text, level)?.Name);

    [Fact]
    public void Dungeons_SuitTheLevel_AndRaidsNeverFit()
    {
        Assert.Null(PlayerbotDungeons.SuitedFor(10));
        Assert.Equal("Shadowfang Keep", PlayerbotDungeons.SuitedFor(22)?.Name);
        Assert.False(PlayerbotDungeons.Fits(PlayerbotDungeons.All.First(d => d.Raid), 60));
        Assert.True(PlayerbotDungeons.Fits(PlayerbotDungeons.All.First(d => d.Name == "Ragefire Chasm"), 12));
    }

    // --- the built-in's new intents -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("what spec are you?", (int)BuiltinChatIntent.Spec)]
    [InlineData("how much gold do you have", (int)BuiltinChatIntent.Gold)]
    [InlineData("can you spare some gold", (int)BuiltinChatIntent.GoldAsk)]
    [InlineData("do you need anything?", (int)BuiltinChatIntent.Needs)]
    [InlineData("what's your ilvl", (int)BuiltinChatIntent.Gear)]
    [InlineData("selling anything?", (int)BuiltinChatIntent.Surplus)]
    [InlineData("trade me", (int)BuiltinChatIntent.Trade)]
    [InlineData("can you share the quest", (int)BuiltinChatIntent.Share)]
    [InlineData("can you help me with The Fargodeep Mine", (int)BuiltinChatIntent.QuestHelp)]
    [InlineData("gj", (int)BuiltinChatIntent.Praise)]
    [InlineData("lol", (int)BuiltinChatIntent.Laugh)]
    [InlineData("hbu", (int)BuiltinChatIntent.FollowYou)]
    public void NewIntents_AreClassified(string text, int intent)
        => Assert.Equal((BuiltinChatIntent)intent, PlayerbotBuiltinChat.Classify(text, "Chatbot", out _));

    [Fact]
    public void SpecAnswer_UsesTheRealSpec()
    {
        BotChatAsk ask = ChatSamples.Ask("what spec are you?", ChatSamples.Persona(level: 20) with { Facts = ChatSamples.FullFacts() });
        BuiltinChatAnswer answer = PlayerbotBuiltinChat.Answer(ask, null, true, new Random(1));
        Assert.NotNull(answer.Text);
        Assert.Equal(BuiltinChatIntent.Spec, answer.Intent);
    }

    [Fact]
    public void Fill_ReadsFacts_AndRefusesAnUnknownSlot()
    {
        BotChatAsk ask = ChatSamples.Ask("x", ChatSamples.Persona() with { Facts = ChatSamples.FullFacts() });
        Assert.Equal("heal holy, Linen Cloth x12", PlayerbotBuiltinChat.Fill("{role} {spec}, {surplus} x{surpluscount}", ask));
        Assert.Null(PlayerbotBuiltinChat.Fill("{nonsense}", ask));
        Assert.Null(PlayerbotBuiltinChat.Fill("{spec}", ChatSamples.Ask("x")));
    }

    // --- actions -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{\"intent\":\"invite\",\"reply\":\"sure\"}", (int)BotChatAction.Invite)]
    [InlineData("ok {\"intent\": \"trade-open\"}", (int)BotChatAction.OpenTrade)]
    [InlineData("{\"intent\":\"share_quest\"}", (int)BotChatAction.ShareQuest)]
    [InlineData("{\"intent\":\"join-lfg\"}", (int)BotChatAction.JoinLfg)]
    [InlineData("{\"intent\":\"dance\"}", (int)BotChatAction.None)]
    [InlineData("no json here", (int)BotChatAction.None)]
    [InlineData("{broken", (int)BotChatAction.None)]
    public void ModelActions_AreRead(string answer, int action) => Assert.Equal((BotChatAction)action, PlayerbotChatPrompts.ReadAction(answer));

    [Fact]
    public void SocialActions_NeverForBots_OrTopics_AndQuestShareOnlyForTheMaster()
    {
        BotChatAsk player = ChatSamples.Ask("x");
        Assert.Equal(BotChatAction.Invite, PlayerbotChat.SocialAction(player, BotChatAction.Invite));
        Assert.Equal(BotChatAction.None, PlayerbotChat.SocialAction(player with { SenderIsBot = true }, BotChatAction.Invite));
        Assert.Equal(BotChatAction.None, PlayerbotChat.SocialAction(player, BotChatAction.ShareQuest));
        Assert.Equal(BotChatAction.ShareQuest, PlayerbotChat.SocialAction(player with { FromMaster = true }, BotChatAction.ShareQuest));
        Assert.Equal(BotChatAction.None, PlayerbotChat.SocialAction(player, BotChatAction.Follow));
    }

    // --- events and options ------------------------------------------------------------------------------------------------

    [Fact]
    public void ChatEvents_NotedOnTheirOwnClock_AreFoundAndRepeatsAreHeldBack()
    {
        var events = new PlayerbotChatEvents();
        events.NoteNow(BotChatEventKind.BossKill, "Edwin VanCleef", "The Deadmines");
        events.NoteNow(BotChatEventKind.BossKill, "Edwin VanCleef", "The Deadmines");
        Assert.Single(events.Events(events.LastNowMs));
        Assert.Equal("Edwin VanCleef", events.LatestNow(BotChatEventKind.BossKill, 60_000)?.Value);
        Assert.Null(events.LatestNow(BotChatEventKind.Ding, 60_000));
    }

    [Fact]
    public void ConversationOptions_DefaultOn_AndChecked()
    {
        var options = new PlayerbotConversationOptions();
        Assert.True(options.Enabled);
        Assert.True(options.WorldReactions);
        options.Validate();
        Assert.Throws<InvalidOperationException>(() => new PlayerbotConversationOptions { MaxLines = 7 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotConversationOptions { ReactionsPerLine = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotChatOptions { Conversation = new PlayerbotConversationOptions { MaxActive = 0 } }.Validate());
    }
}
