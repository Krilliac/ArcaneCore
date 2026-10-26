using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// The LFG intent reader and subject resolver (<see cref="PlayerbotLfgIntents"/>, docs/areas/playbots.md, Bot voice): what a line asks
/// ("any groups for SFK?", "inv", "ginv"), the role an "available" line states, which dungeon, raid, quest or zone it is about, and the
/// dungeon catalogue's consistency with the voice's map names. Pure and deterministic: no world.
/// </summary>
public sealed class PlayerbotLfgIntentsTests
{
    private static BotChatRole? Role(string? word) => word switch
    {
        "tank" => BotChatRole.Tank,
        "heal" => BotChatRole.Healer,
        "dps" => BotChatRole.Damage,
        _ => null,
    };

    // --- the intent table ---------------------------------------------------------------------------------------------------

    [Theory]
    // a group search, in a channel
    [InlineData("any groups for sfk?", false, "GroupSearch", null)]
    [InlineData("anyone doing deadmines", false, "GroupSearch", null)]
    [InlineData("Any group for the Deadmines?", false, "GroupSearch", null)]
    [InlineData("any grp for ZF", false, "GroupSearch", null)]
    [InlineData("LFG SFK 25 priest", false, "GroupSearch", null)]
    [InlineData("LF group for strat", false, "GroupSearch", null)]
    [InlineData("looking for a group for brd", false, "GroupSearch", null)]
    [InlineData("any raids for MC?", false, "GroupSearch", null)]
    [InlineData("is there a group for wailing caverns", false, "GroupSearch", null)]
    [InlineData("sfk anyone?", false, "GroupSearch", null)]
    [InlineData("anyone running scholo", false, "GroupSearch", null)]
    [InlineData("anyone going to uldaman", false, "GroupSearch", null)]
    [InlineData("any groups?", false, "GroupSearch", null)]
    [InlineData("any runs going?", false, "GroupSearch", null)]
    [InlineData("any group doing the Defias Brotherhood", false, "GroupSearch", null)]
    [InlineData("lf st", false, "GroupSearch", null)]
    [InlineData("any groups for sfk?", true, "GroupSearch", null)]
    // an offer to join, whispered to the poster
    [InlineData("inv", true, "Available", null)]
    [InlineData("inv pls", true, "Available", null)]
    [InlineData("invite me", true, "Available", null)]
    [InlineData("can i come", true, "Available", null)]
    [InlineData("i can tank", true, "Available", "tank")]
    [InlineData("I can heal", true, "Available", "heal")]
    [InlineData("inv, dps", true, "Available", "dps")]
    [InlineData("im free", true, "Available", null)]
    [InlineData("i'm available", true, "Available", null)]
    [InlineData("room for a dd?", true, "Available", "dps")]
    [InlineData("need a healer?", true, "Available", "heal")]
    [InlineData("still looking?", true, "Available", null)]
    [InlineData("me", true, "Available", null)]
    [InlineData("1", true, "Available", null)]
    [InlineData("+1", true, "Available", null)]
    [InlineData("x", true, "Available", null)]
    // the stricter channel form
    [InlineData("inv", false, "Available", null)]
    [InlineData("invite me please", false, "Available", null)]
    [InlineData("i can tank", false, "Available", "tank")]
    [InlineData("im down", false, "Available", null)]
    // guilds
    [InlineData("ginv", true, "GuildJoin", null)]
    [InlineData("g inv pls", true, "GuildJoin", null)]
    [InlineData("guild invite pls", true, "GuildJoin", null)]
    [InlineData("can i join your guild", true, "GuildJoin", null)]
    [InlineData("wanna join ur guild", false, "GuildJoin", null)]
    [InlineData("any guild recruiting?", false, "GuildSearch", null)]
    [InlineData("lf guild", false, "GuildSearch", null)]
    [InlineData("looking for a guild", false, "GuildSearch", null)]
    [InlineData("guild recruiting", false, "GuildSearch", null)]
    // the negatives
    [InlineData("me", false, "None", null)]
    [InlineData("1", false, "None", null)]
    [InlineData("x", false, "None", null)]
    [InlineData("+1", false, "None", null)]
    [InlineData("lfm sfk", false, "None", null)]
    [InlineData("LF2M SFK need tank and healer", false, "None", null)]
    [InlineData("looking for more for sfk", false, "None", null)]
    [InlineData("st", false, "None", null)]
    [InlineData("hello everyone", false, "None", null)]
    [InlineData("wtb linen cloth", false, "None", null)]
    [InlineData("anyone going to org", false, "None", null)]
    [InlineData("hi anyone", false, "None", null)]
    [InlineData("kys inv", true, "None", null)]
    [InlineData("", true, "None", null)]
    public void Read_SortsALine_ByWhatItAsks(string text, bool whisper, string intent, string? role)
    {
        Assert.Equal(Enum.Parse<PlayerbotLfgIntent>(intent), PlayerbotLfgIntents.Read(text, whisper, out BotChatRole? stated));
        Assert.Equal(Role(role), stated);
    }

    [Fact]
    public void Read_NeverStatesARole_ForClassWords()
    {
        Assert.Equal(PlayerbotLfgIntent.Available, PlayerbotLfgIntents.Read("inv, priest here", true, out BotChatRole? role));
        Assert.Null(role);
    }

    [Fact]
    public void HasGroupCue_IsTheGateOfTheShortDungeonTokens()
    {
        foreach (string cue in new[] { "any group for st", "lf st", "lfg st", "lfm st", "st group", "st run", "anyone st", "st inv", "doing st", "any st" })
            Assert.True(PlayerbotLfgIntents.HasGroupCue(cue), cue);
        foreach (string none in new[] { "st", "the st of it", "sm" })
            Assert.False(PlayerbotLfgIntents.HasGroupCue(none), none);
    }

    // --- the invite words, split ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("inv", true, true)]
    [InlineData("invite me please", true, true)]
    [InlineData("can i join", true, true)]
    [InlineData("i can heal", true, true)]
    [InlineData("me", true, false)]
    [InlineData("1", true, false)]
    [InlineData("x", true, false)]
    [InlineData("+1", true, false)]
    [InlineData("hello there", false, false)]
    public void InviteWords_AreSplit_IntoAWhisperFormAndAStricterChannelForm(string text, bool whisper, bool channel)
    {
        Assert.Equal(whisper, PlayerbotVoiceFacts.AsksInvite(text));
        Assert.Equal(channel, PlayerbotVoiceFacts.AsksInviteInChannel(text));
    }

    // --- the subject ------------------------------------------------------------------------------------------------------------

    private static readonly PlayerbotLfgFacts NoFacts = new([], [], _ => 0);

    private static PlayerbotLfgFacts Facts(IReadOnlyList<PlayerbotLfgGoal>? goals = null, IReadOnlyList<(uint, string)>? quests = null,
        Func<string, uint>? zones = null) => new(goals ?? [], quests ?? [], zones ?? (_ => 0));

    [Theory]
    [InlineData("any groups for dm", 20, 36u)]
    [InlineData("any groups for dm", 58, 429u)]
    [InlineData("any groups for dm", 40, 36u)]
    [InlineData("lfg sfk", 25, 33u)]
    [InlineData("anyone doing the deadmines", 20, 36u)]
    [InlineData("any group for lbrs", 58, 229u)]
    [InlineData("any group for ubrs", 60, 229u)]
    [InlineData("is there a group for wailing caverns", 18, 43u)]
    [InlineData("any groups for zul'farrak", 46, 209u)]
    public void Resolve_NamesTheDungeon_TheAskersLevelFits(string text, int level, uint mapId)
    {
        PlayerbotLfgSubject subject = PlayerbotLfgIntents.Resolve(text, level, NoFacts);
        Assert.Equal(PlayerbotLfgSubjectKind.Dungeon, subject.Kind);
        Assert.Equal(mapId, subject.Key);
        Assert.NotEmpty(subject.Display);
    }

    [Fact]
    public void Resolve_PrefersTheDungeonWithLiveFacts_WhenNeitherFitsTheLevel()
    {
        var direMaul = new PlayerbotLfgGoal(PlayerbotGroupGoalKind.Dungeon, 429, 0, null, 0);
        PlayerbotLfgSubject subject = PlayerbotLfgIntents.Resolve("any groups for dm", 40, Facts([direMaul]));
        Assert.Equal(429u, subject.Key);
    }

    [Theory]
    [InlineData("any raids for mc", 409u)]
    [InlineData("anyone doing ony", 249u)]
    [InlineData("lfg bwl", 469u)]
    [InlineData("any raid for zg", 309u)]
    [InlineData("any raids for aq20", 509u)]
    [InlineData("any raids for aq40", 531u)]
    [InlineData("any raids for naxx", 533u)]
    public void Resolve_NamesTheRaid(string text, uint mapId)
    {
        PlayerbotLfgSubject subject = PlayerbotLfgIntents.Resolve(text, 60, NoFacts);
        Assert.Equal(PlayerbotLfgSubjectKind.Raid, subject.Kind);
        Assert.Equal(mapId, subject.Key);
    }

    [Fact]
    public void Resolve_AShortDungeonToken_NeedsAGroupCue()
    {
        Assert.Equal(PlayerbotLfgSubjectKind.Dungeon, PlayerbotLfgIntents.Resolve("any group for st", 52, NoFacts).Kind);
        Assert.Equal(109u, PlayerbotLfgIntents.Resolve("any group for st", 52, NoFacts).Key);
        PlayerbotLfgSubject bare = PlayerbotLfgIntents.Resolve("st", 52, NoFacts);
        Assert.NotEqual(PlayerbotLfgSubjectKind.Dungeon, bare.Kind);
        Assert.NotEqual(PlayerbotLfgSubjectKind.Dungeon, PlayerbotLfgIntents.Resolve("the st of things", 52, NoFacts).Kind);
    }

    [Fact]
    public void Resolve_AQuestTitle_OfAWaitingGoalOrTheAskersLog()
    {
        var defias = new PlayerbotLfgGoal(PlayerbotGroupGoalKind.Quest, 1234, 155, "The Defias Brotherhood", 40);
        PlayerbotLfgSubject waiting = PlayerbotLfgIntents.Resolve("any group doing the defias brotherhood?", 20, Facts([defias]));
        Assert.Equal(PlayerbotLfgSubjectKind.Quest, waiting.Kind);
        Assert.Equal(155u, waiting.QuestId);
        Assert.Equal(1234u, waiting.Key);

        PlayerbotLfgSubject own = PlayerbotLfgIntents.Resolve("anyone for bounty on garrick padfoot", 20, Facts(quests: [(99u, "Bounty on Garrick Padfoot")]));
        Assert.Equal(PlayerbotLfgSubjectKind.Quest, own.Kind);
        Assert.Equal(99u, own.QuestId);

        // A title nobody waits for and the asker does not hold is not a subject.
        Assert.Equal(PlayerbotLfgSubjectKind.Unresolved, PlayerbotLfgIntents.Resolve("any group doing the defias brotherhood?", 20, NoFacts).Kind);
    }

    [Fact]
    public void Resolve_AZone_ByExactName_AfterDungeonAndQuest()
    {
        Func<string, uint> zones = name => name switch { "elwynn forest" => 12u, "westfall" => 40u, _ => 0u };
        PlayerbotLfgSubject zone = PlayerbotLfgIntents.Resolve("any groups in elwynn forest", 10, Facts(zones: zones));
        Assert.Equal(PlayerbotLfgSubjectKind.Zone, zone.Kind);
        Assert.Equal(12u, zone.ZoneId);

        Assert.Equal(PlayerbotLfgSubjectKind.Dungeon, PlayerbotLfgIntents.Resolve("any group for deadmines in westfall", 20, Facts(zones: zones)).Kind);
    }

    [Fact]
    public void Resolve_NoSubjectStated_IsNone_AndAnUnknownOneIsUnresolved()
    {
        Assert.Equal(PlayerbotLfgSubjectKind.None, PlayerbotLfgIntents.Resolve("any groups?", 20, NoFacts).Kind);
        Assert.Equal(PlayerbotLfgSubjectKind.None, PlayerbotLfgIntents.Resolve("lfg 25 priest", 25, NoFacts).Kind);
        Assert.Equal(PlayerbotLfgSubjectKind.Unresolved, PlayerbotLfgIntents.Resolve("anyone going to stormwind", 20, NoFacts).Kind);
    }

    // --- the zone index -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ZoneIndex_HoldsZonesByPlainName_NotSubAreas()
    {
        var areas = new AreaTable(
        [
            new AreaTemplate(12, 0, 0, 0, 0, 1, "Elwynn Forest", 0, 0),
            new AreaTemplate(9, 0, 12, 0, 0, 1, "Northshire Valley", 0, 0),
            new AreaTemplate(17, 1, 0, 0, 0, 1, "The Barrens", 0, 0),
        ], ArcaneCore.Game.Maps.Templates.MapRegistry.Default);
        var index = new PlayerbotLfgZoneIndex(areas);
        Assert.Equal(12u, index.IdOf("elwynn forest"));
        Assert.Equal(17u, index.IdOf("the barrens"));
        Assert.Equal(17u, index.IdOf("barrens"));
        Assert.Equal(0u, index.IdOf("northshire valley"));
        Assert.Equal(0u, index.IdOf("nowhere"));
    }

    // --- the catalogue ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Catalogue_EveryDungeonHasAMapId_WithANameInTheVoice()
    {
        var random = new Random(1);
        foreach (PlayerbotDungeon dungeon in PlayerbotDungeons.All)
        {
            Assert.True(dungeon.MapId != 0, dungeon.Name);
            Assert.NotNull(PlayerbotVoiceFacts.DungeonName(dungeon.MapId, null, random));
        }
    }

    [Fact]
    public void Catalogue_RaidsAreTheRaidMaps_AndLbrsAndUbrsShareOneMap()
    {
        uint[] raids = [.. PlayerbotDungeons.All.Where(d => d.Raid).Select(d => d.MapId).Order()];
        Assert.Equal(new uint[] { 249, 309, 409, 469, 509, 531, 533 }, raids);
        Assert.Equal(229u, PlayerbotDungeons.All.Single(d => d.Short == "LBRS").MapId);
        Assert.Equal(229u, PlayerbotDungeons.All.Single(d => d.Short == "UBRS").MapId);
        Assert.Equal(531u, PlayerbotDungeons.Named("lfm aq40", 60)?.MapId);
        Assert.Equal(533u, PlayerbotDungeons.Named("naxx anyone", 60)?.MapId);
    }
}
