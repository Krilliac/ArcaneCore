using ArcaneCore.Game.Guilds;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>vmangos ObjectMgr::IsValidCharterName (ObjectMgr.cpp:9600-9616) over isValidString (:9532-9566) and Util.h:115-231.</summary>
public sealed class CharterNameRulesTests
{
    private static readonly GuildOptions Defaults = new();

    [Theory]
    [InlineData("Arcane Order", true)]
    [InlineData("A", false)]                       // MinCharterName 2
    [InlineData("AB", true)]
    [InlineData("", false)]
    [InlineData("Guild!", false)]                  // punctuation is in no script
    [InlineData("Müller Gilde", true)]        // 0xFC is in the 0xF8-0xFE extended Latin range
    [InlineData("Guild×Two", false)]          // 0xD7 is excluded
    [InlineData("Guild÷Two", false)]          // 0xF7 is excluded
    [InlineData("Гильдия 9", true)]            // Cyrillic + digit + space (numericOrSpace)
    [InlineData("Guild Гильдия", false)]       // mixed scripts: one script for the whole string
    [InlineData("Order 66", true)]                 // digits and spaces are allowed
    [InlineData("ギルドの会", true)]                          // Katakana/Hiragana/CJK
    public void Validity_FollowsTheScriptRules(string name, bool valid)
        => Assert.Equal(valid, CharterNameRules.IsValid(name, Defaults, null));

    [Fact]
    public void TheLengthLimit_IsTwentyFourCodePoints()
    {
        Assert.True(CharterNameRules.IsValid(new string('a', 24), Defaults, null));
        Assert.False(CharterNameRules.IsValid(new string('a', 25), Defaults, null));
        Assert.True(CharterNameRules.IsValid(string.Concat(Enumerable.Repeat("漢", 24)), Defaults, null));
        Assert.False(CharterNameRules.IsValid(string.Concat(Enumerable.Repeat("漢", 25)), Defaults, null));
    }

    [Fact]
    public void AStrictMaskOfOne_AcceptsOnlyBasicLatin()
    {
        var strict = new GuildOptions { StrictCharterNames = 1 };

        Assert.True(CharterNameRules.IsValid("Arcane Order 2", strict, null));
        Assert.False(CharterNameRules.IsValid("Müller Gilde", strict, null));
        Assert.False(CharterNameRules.IsValid("Гильдия", strict, null));
    }

    [Fact]
    public void TheRealmZoneBit_AcceptsTheRealmZonesScripts_LikeVmangosIsValidString()
    {
        // vmangos isValidString(..., numericOrSpace true, create false) with GetRealmLanguageType (ObjectMgr.cpp:9515-9578).
        var development = new GuildOptions { StrictCharterNames = 2, RealmZone = 1 };     // any language
        Assert.True(CharterNameRules.IsValid("Гильдия 2", development, null));
        Assert.True(CharterNameRules.IsValid("Müller Gilde", development, null));
        Assert.False(CharterNameRules.IsValid("Gilde Гильдия", development, null));       // still one script

        var english = new GuildOptions { StrictCharterNames = 2, RealmZone = 8 };         // extended Latin
        Assert.True(CharterNameRules.IsValid("Müller Gilde", english, null));
        Assert.False(CharterNameRules.IsValid("Гильдия", english, null));
        Assert.False(CharterNameRules.IsValid("漢字", english, null));

        var russian = new GuildOptions { StrictCharterNames = 2, RealmZone = 12 };        // Cyrillic
        Assert.True(CharterNameRules.IsValid("Гильдия", russian, null));
        Assert.False(CharterNameRules.IsValid("Arcane Order", russian, null));
        Assert.True(CharterNameRules.IsValid("Arcane Order", new GuildOptions { StrictCharterNames = 3, RealmZone = 12 }, null)); // bit 1

        var korea = new GuildOptions { StrictCharterNames = 2, RealmZone = 6 };           // East Asian
        Assert.True(CharterNameRules.IsValid("漢字", korea, null));
        Assert.False(CharterNameRules.IsValid("Arcane", korea, null));

        // An unknown zone gives basic Latin at character creation but any language otherwise: charters are not a creation.
        Assert.True(CharterNameRules.IsValid("Гильдия", new GuildOptions { StrictCharterNames = 2, RealmZone = 99 }, null));
    }

    [Fact]
    public void TheConfiguredMinimum_IsHonoured()
        => Assert.False(CharterNameRules.IsValid("Abc", new GuildOptions { MinCharterNameLength = 4 }, null));

    [Fact]
    public void AReservedName_IsRejected_CaseInsensitively()
    {
        var blacklist = new Blacklist("gamemaster");

        Assert.False(CharterNameRules.IsValid("GameMaster", Defaults, blacklist));
        Assert.True(CharterNameRules.IsValid("GameMasters", Defaults, blacklist));
    }

    [Fact]
    public void LoneSurrogates_AreInvalid()
        => Assert.False(CharterNameRules.IsValid("Ab\ud800", Defaults, null));

    private sealed class Blacklist(string reserved) : ICharterNameBlacklist
    {
        public bool IsReserved(string lowercaseName) => lowercaseName == reserved;
    }
}
