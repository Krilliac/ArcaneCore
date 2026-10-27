using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// The bot voice (docs/areas/playbots.md, Bot voice): the pure facts it says (dungeon names, missing roles, prices, what may be sold,
/// battleground callouts, how a whisper reads), its board of calls and owed thanks, its built-in lines (every placeholder filled from the
/// topic, a phrasing without a value never used) and its options. Pure and deterministic: seeded randomness, time passed in by hand.
/// </summary>
public sealed class PlayerbotVoiceTests
{
    private static readonly Guid Bot = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    // --- names and roles -----------------------------------------------------------------------------------------------------

    [Fact]
    public void DungeonName_UsesThePlayersNames_ThenTheMapName()
    {
        var random = new Random(1);
        for (int i = 0; i < 20; i++) Assert.Contains(PlayerbotVoiceFacts.DungeonName(36, "The Deadmines", random), new[] { "DM", "VC", "Deadmines" });
        Assert.Equal("Some Crypt", PlayerbotVoiceFacts.DungeonName(9999, " Some Crypt ", random));
        Assert.Null(PlayerbotVoiceFacts.DungeonName(9999, null, random));
    }

    [Theory]
    [InlineData(new[] { Class.Warrior, Class.Mage }, 0, 1)]
    [InlineData(new[] { Class.Druid }, 0, 1)]
    [InlineData(new[] { Class.Paladin, Class.Warrior }, 0, 0)]
    [InlineData(new Class[0], 1, 1)]
    [InlineData(new[] { Class.Mage, Class.Rogue, Class.Hunter }, 1, 1)]
    public void MissingRoles_CountsWhatTheWaitingClassesCannotCover(Class[] waiting, int tanks, int healers)
        => Assert.Equal((tanks, healers), PlayerbotVoiceFacts.MissingRoles(waiting, (1, 1)));

    [Fact]
    public void RoleWords_ReadLikeChat()
    {
        Assert.Equal("tank and heals", PlayerbotVoiceFacts.RoleWords(1, 1));
        Assert.Equal("2 healers", PlayerbotVoiceFacts.RoleWords(0, 2));
        Assert.Equal("2 tanks", PlayerbotVoiceFacts.RoleWords(2, 0));
        Assert.Null(PlayerbotVoiceFacts.RoleWords(0, 0));
    }

    // --- prices and offers ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(20_000, "2g")]
    [InlineData(15_000, "1g 50s")]
    [InlineData(3_500, "35s")]
    [InlineData(3_549, "35s")]
    [InlineData(512, "5s 12c")]
    [InlineData(80, "80c")]
    [InlineData(123_456, "12g 30s")]
    [InlineData(0, "1c")]
    public void Price_IsRoundedAndWrittenTheOldWay(long copper, string expected) => Assert.Equal(expected, PlayerbotVoiceFacts.Price(copper));

    [Fact]
    public void AskingPrice_IsAMultipleOfTheVendorPrice_AtLeastASilverForAGreen()
    {
        Assert.Equal(200, PlayerbotVoiceFacts.AskingPrice(50, 2, 4));
        Assert.Equal(100, PlayerbotVoiceFacts.AskingPrice(10, 2, 4));
        Assert.Equal(40, PlayerbotVoiceFacts.AskingPrice(10, 1, 4));
        Assert.Equal(10, PlayerbotVoiceFacts.AskingPrice(10, 1, 0));
    }

    private static ItemTemplate Template(uint @class, uint quality, uint sellPrice, uint bonding = 0, uint startQuest = 0)
        => new() { Entry = 1000, Name = "Thing", Class = @class, Quality = quality, SellPrice = sellPrice, Bonding = bonding, StartQuest = startQuest };

    [Fact]
    public void Offer_SellsOnlyRealSurplus()
    {
        // Trade goods: a stack of five or more.
        Assert.Equal(PlayerbotVoiceFacts.TradeOffer.Stack, PlayerbotVoiceFacts.Offer(Template(7, 1, 100), 20, false, false, 200));
        Assert.Equal(PlayerbotVoiceFacts.TradeOffer.None, PlayerbotVoiceFacts.Offer(Template(7, 1, 100), 4, false, false, 200));
        // A green weapon, but not bound, an upgrade, bind on pickup, a quest starter or a white.
        Assert.Equal(PlayerbotVoiceFacts.TradeOffer.Single, PlayerbotVoiceFacts.Offer(Template(2, 2, 500), 1, false, false, 200));
        Assert.Equal(PlayerbotVoiceFacts.TradeOffer.None, PlayerbotVoiceFacts.Offer(Template(2, 2, 500), 1, true, false, 200));
        Assert.Equal(PlayerbotVoiceFacts.TradeOffer.None, PlayerbotVoiceFacts.Offer(Template(2, 2, 500), 1, false, true, 200));
        Assert.Equal(PlayerbotVoiceFacts.TradeOffer.None, PlayerbotVoiceFacts.Offer(Template(2, 2, 500, bonding: 1), 1, false, false, 200));
        Assert.Equal(PlayerbotVoiceFacts.TradeOffer.None, PlayerbotVoiceFacts.Offer(Template(2, 2, 500, startQuest: 5), 1, false, false, 200));
        Assert.Equal(PlayerbotVoiceFacts.TradeOffer.None, PlayerbotVoiceFacts.Offer(Template(2, 1, 500), 1, false, false, 200));
        // Greys, quest items and cheap things never.
        Assert.Equal(PlayerbotVoiceFacts.TradeOffer.None, PlayerbotVoiceFacts.Offer(Template(2, 0, 500), 1, false, false, 200));
        Assert.Equal(PlayerbotVoiceFacts.TradeOffer.None, PlayerbotVoiceFacts.Offer(Template(12, 2, 500), 1, false, false, 200));
        Assert.Equal(PlayerbotVoiceFacts.TradeOffer.None, PlayerbotVoiceFacts.Offer(Template(2, 2, 100), 1, false, false, 200));
    }

    [Fact]
    public void Tradeable_IsTradeGoodsOrReagents_NotBound()
    {
        Assert.True(PlayerbotVoiceFacts.Tradeable(Template(7, 1, 10)));
        Assert.True(PlayerbotVoiceFacts.Tradeable(Template(5, 1, 10)));
        Assert.False(PlayerbotVoiceFacts.Tradeable(Template(7, 0, 10)));
        Assert.False(PlayerbotVoiceFacts.Tradeable(Template(7, 1, 10, bonding: 1)));
        Assert.False(PlayerbotVoiceFacts.Tradeable(Template(2, 2, 10)));
    }

    // --- battlegrounds -------------------------------------------------------------------------------------------------------

    [Fact]
    public void CarrierWhere_SplitsTheFieldInThree()
    {
        Vector3 ours = Vector3.Zero, theirs = new(100, 0, 0);
        Assert.Equal("in our base", PlayerbotVoiceFacts.CarrierWhere(new Vector3(10, 0, 0), ours, theirs));
        Assert.Equal("mid", PlayerbotVoiceFacts.CarrierWhere(new Vector3(50, 0, 0), ours, theirs));
        Assert.Equal("near their base", PlayerbotVoiceFacts.CarrierWhere(new Vector3(90, 0, 0), ours, theirs));
    }

    [Theory]
    [InlineData(ArathiBasin.StatusAllianceOccupied, ArathiBasin.StatusHordeContested, true, "voice.bg.inc")]
    [InlineData(ArathiBasin.StatusHordeContested, ArathiBasin.StatusHordeOccupied, true, "voice.bg.lost")]
    [InlineData(ArathiBasin.StatusHordeContested, ArathiBasin.StatusHordeOccupied, false, null)]
    [InlineData(ArathiBasin.StatusAllianceContested, ArathiBasin.StatusAllianceOccupied, false, "voice.bg.ours")]
    [InlineData(ArathiBasin.StatusNeutral, ArathiBasin.StatusAllianceContested, false, "voice.bg.capping")]
    [InlineData(ArathiBasin.StatusHordeOccupied, ArathiBasin.StatusAllianceContested, false, "voice.bg.capping")]
    [InlineData(ArathiBasin.StatusAllianceOccupied, ArathiBasin.StatusAllianceOccupied, true, null)]
    public void AbCall_ForTheAlliance(byte before, byte after, bool ours, string? expected)
        => Assert.Equal(expected, PlayerbotVoiceFacts.AbCall(before, after, Team.Alliance, ours));

    [Fact]
    public void AbCall_ForTheHorde_IsTheMirror()
        => Assert.Equal("voice.bg.inc", PlayerbotVoiceFacts.AbCall(ArathiBasin.StatusHordeOccupied, ArathiBasin.StatusAllianceContested, Team.Horde, true));

    private const int A = 0, H = 1, Assaulted = AlteracValley.PointAssaulted, Controlled = AlteracValley.PointControlled;

    [Theory]
    [InlineData(A, Controlled, H, Assaulted, true, A, "voice.av.attacked")]
    [InlineData(H, Assaulted, H, Controlled, true, A, "voice.av.down")]
    [InlineData(H, Assaulted, H, Controlled, false, A, "voice.bg.lost")]
    [InlineData(H, Controlled, A, Assaulted, false, A, "voice.av.assaulted")]
    [InlineData(2, Controlled, A, Assaulted, false, A, "voice.av.assaulted")]
    [InlineData(A, Assaulted, A, Controlled, true, A, "voice.av.burned")]
    [InlineData(A, Assaulted, A, Controlled, false, A, "voice.av.taken")]
    [InlineData(A, Controlled, H, Assaulted, true, H, "voice.av.assaulted")]
    [InlineData(A, Controlled, A, Controlled, true, A, null)]
    public void AvCall_FromOwnerAndState(int ownerBefore, int stateBefore, int ownerAfter, int stateAfter, bool tower, int team, string? expected)
        => Assert.Equal(expected, PlayerbotVoiceFacts.AvCall(ownerBefore, stateBefore, ownerAfter, stateAfter, tower, team));

    [Fact]
    public void Nodes_HaveNamesForEveryNode_AndNoneBeyond()
    {
        var random = new Random(2);
        for (int node = 0; node < ArathiBasin.NodeCount; node++) Assert.False(string.IsNullOrWhiteSpace(PlayerbotVoiceFacts.AbNode(node, random)));
        for (int node = 0; node < AlteracValley.NodeCount; node++) Assert.False(string.IsNullOrWhiteSpace(PlayerbotVoiceFacts.AvNode(node, random)));
        Assert.Null(PlayerbotVoiceFacts.AbNode(ArathiBasin.NodeCount, random));
        Assert.Null(PlayerbotVoiceFacts.AvNode(-1, random));
    }

    // --- reading whispers --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("inv", true)]
    [InlineData("inv pls", true)]
    [InlineData("can i join?", true)]
    [InlineData("i can heal", true)]
    [InlineData("hello there", false)]
    [InlineData("lol", false)]
    [InlineData("", false)]
    public void AsksInvite(string text, bool expected) => Assert.Equal(expected, PlayerbotVoiceFacts.AsksInvite(text));

    [Theory]
    [InlineData("how much?", true)]
    [InlineData("linen still", true)]
    [InlineData("nice bolt", true)]
    [InlineData("hi", false)]
    public void AsksAbout(string text, bool expected) => Assert.Equal(expected, PlayerbotVoiceFacts.AsksAbout(text, "Bolt of Linen Cloth"));

    [Theory]
    [InlineData("ding", true)]
    [InlineData("DING!", true)]
    [InlineData("ding 30", true)]
    [InlineData("30 ding", true)]
    [InlineData("dinged", true)]
    [InlineData("ding dong", false)]
    [InlineData("i hear a ding", false)]
    public void IsDing(string text, bool expected) => Assert.Equal(expected, PlayerbotVoiceFacts.IsDing(text));

    // --- the board -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Board_KeepsTheLatestCall_ForItsLife()
    {
        var board = new PlayerbotVoiceBoard();
        board.Post(Bot, new PlayerbotVoicePost(PlayerbotVoicePostKind.LookingForGroup, 1_000, "DM"));
        board.Post(Bot, new PlayerbotVoicePost(PlayerbotVoicePostKind.Selling, 2_000, "Bolt of Linen Cloth", 2996, "40s", 10));
        Assert.Equal(1, board.Count);
        Assert.Equal(PlayerbotVoicePostKind.Selling, board.Find(Bot, 5_000, 600_000)!.Kind);
        Assert.Null(board.Find(Bot, 602_000, 600_000));
        Assert.Equal(0, board.Count);
        board.Post(Bot, new PlayerbotVoicePost(PlayerbotVoicePostKind.Buying, 0, "Copper Ore"));
        board.Forget(Bot);
        Assert.Null(board.Find(Bot, 0, 600_000));
    }

    [Fact]
    public void Board_ThanksForAResurrection_OnceAndOnlyWhileFresh()
    {
        var board = new PlayerbotVoiceBoard();
        var caster = new ObjectGuid(0x99);
        board.NoteResurrection(Bot, caster, 10_000);
        Assert.True(board.OwesThanks(Bot));
        Assert.True(board.TakeResurrection(Bot, 20_000, out ObjectGuid got));
        Assert.Equal(caster, got);
        Assert.False(board.TakeResurrection(Bot, 20_000, out _));
        board.NoteResurrection(Bot, caster, 0);
        Assert.False(board.TakeResurrection(Bot, PlayerbotVoiceBoard.ResurrectionThanksMs, out _));
        Assert.False(board.OwesThanks(Bot));
    }

    // --- the built-in lines ----------------------------------------------------------------------------------------------------

    private static BotChatAsk Topic(string key, params (string Key, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        return ChatSamples.Ask("", ChatSamples.Persona(level: 24, @class: Class.Mage), channel: ChatType.Channel) with
        {
            Topic = new BotChatTopic(key, "instruction", map, string.Empty),
        };
    }

    [Fact]
    public void Pick_FillsTheVoicePlaceholders_FromTheTopic()
    {
        for (int seed = 0; seed < 30; seed++)
        {
            string line = PlayerbotBuiltinChat.Pick("voice.wts", Topic("voice.wts", ("item", "Bolt of Linen Cloth"), ("price", "40s")), null, new Random(seed))!;
            Assert.Contains("Bolt of Linen Cloth", line, StringComparison.Ordinal);
            Assert.DoesNotContain("{", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Pick_NeverUsesAPhrasingWithoutItsValue()
    {
        // Every stack phrasing names {count}: without it nothing is said rather than a hole.
        Assert.Null(PlayerbotBuiltinChat.Pick("voice.wts.stack", Topic("voice.wts.stack", ("item", "Copper Ore"), ("price", "5s")), null, new Random(1)));
        // "first time here" needs no zone; the others do.
        for (int seed = 0; seed < 30; seed++)
            Assert.Equal("first time here", PlayerbotBuiltinChat.Pick("voice.newzone", Topic("voice.newzone"), null, new Random(seed)));
    }

    [Fact]
    public void EveryVoiceLine_FillsCompletely_WithEveryValueGiven()
    {
        (string, string)[] all =
        [
            ("dungeon", "SFK"), ("roles", "tank and heals"), ("count", "2"), ("task", "The Defias Brotherhood"), ("item", "Copper Ore"),
            ("price", "5s"), ("place", "Ironforge"), ("mob", "Defias Pillager"), ("boss", "Van Cleef"), ("who", "Testplayer"), ("newlevel", "25"),
            ("newzone", "Duskwood"), ("node", "BS"), ("where", "mid"), ("minlevel", "18"),
        ];
        foreach ((string key, string[] lines) in PlayerbotVoiceTemplates.Lines)
            foreach (string line in lines)
            {
                string? filled = PlayerbotBuiltinChat.Fill(line, Topic(key, all));
                Assert.True(filled is not null, $"{key}: \"{line}\" could not be filled");
                Assert.DoesNotContain("{", filled, StringComparison.Ordinal);
                Assert.True(filled.Length <= 120, $"{key}: \"{filled}\" is too long for a chat line");
            }
    }

    // --- options -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Options_AreOnByDefault_AndChecked()
    {
        var options = new PlayerbotVoiceOptions();
        Assert.True(options.Enabled && options.LookingForGroup && options.Trade && options.GroupCalls && options.RunCalls
            && options.BattlegroundCalls && options.QuestHelp && options.AnswerPosts);
        options.Validate();
        new PlayerbotChatOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new PlayerbotVoiceOptions { BotsPerPass = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotVoiceOptions { GeneralSharePercent = 101 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotVoiceOptions { PriceMultiplier = 0 }.Validate());
    }
}
