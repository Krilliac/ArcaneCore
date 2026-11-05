using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// The role a player states in an "inv" (<see cref="PlayerbotLfgIntents.RoleOf"/>, docs/areas/playbots.md, Bot voice), the whisper form of the invite
/// words, and the dungeon catalogue's consistency with the voice's map names. Pure and deterministic: no world.
/// </summary>
public sealed class PlayerbotLfgIntentsTests
{
    [Theory]
    [InlineData("inv, i can tank", "Tank")]
    [InlineData("inv MT here", "Tank")]
    [InlineData("invite me, healer", "Healer")]
    [InlineData("inv i heal", "Healer")]
    [InlineData("inv, dps", "Damage")]
    [InlineData("inv dd pls", "Damage")]
    public void RoleOf_ReadsTheStatedRole(string text, string expected)
        => Assert.Equal(Enum.Parse<BotChatRole>(expected), PlayerbotLfgIntents.RoleOf(text));

    [Theory]
    [InlineData("inv, priest here")]
    [InlineData("inv add me")]
    [InlineData("inv buddy")]
    [InlineData("inv smth")]
    [InlineData("inv healthy")]
    [InlineData("inv")]
    [InlineData("")]
    [InlineData("   ")]
    public void RoleOf_NeverStatesARole_ForClassWordsOrNoWord(string text)
        => Assert.Null(PlayerbotLfgIntents.RoleOf(text));

    [Fact]
    public void RoleOf_IgnoresALineOverTheLengthLimit()
        => Assert.Null(PlayerbotLfgIntents.RoleOf("tank " + new string('x', 300)));

    // --- the invite words ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("inv", true)]
    [InlineData("invite me please", true)]
    [InlineData("can i join", true)]
    [InlineData("i can heal", true)]
    [InlineData("me", true)]
    [InlineData("1", true)]
    [InlineData("x", true)]
    [InlineData("+1", true)]
    [InlineData("hello there", false)]
    public void InviteWords_AskForAnInvitation_InTheWhisperForm(string text, bool whisper)
        => Assert.Equal(whisper, PlayerbotVoiceFacts.AsksInvite(text));

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
