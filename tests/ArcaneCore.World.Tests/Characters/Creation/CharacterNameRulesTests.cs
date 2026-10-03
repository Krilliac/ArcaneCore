using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Characters.Creation;
using Xunit;

namespace ArcaneCore.World.Tests.Characters.Creation;

/// <summary>
/// MinPlayerName, StrictPlayerNames and RealmZone (vmangos ObjectMgr.cpp:9507-9598,
/// RealmZone.h:23-61, World.cpp:615-621).
/// </summary>
public sealed class CharacterNameRulesTests
{
    private static NameRuleSettings S(int min = 2, uint strict = 0, int zone = 1, bool create = true) => new(min, strict, zone, create);

    [Fact]
    public void Strict1_AcceptsBasicLatinOnly()
    {
        Assert.Equal(CharResult.CharNameMixedLanguages, CharacterNameRules.Check("Élan", S(strict: 1)));
        Assert.Null(CharacterNameRules.Check("Elan", S(strict: 1)));
        Assert.Null(CharacterNameRules.Check("Élan", S(strict: 0)));
    }

    [Fact]
    public void Strict2_UsesTheRealmZoneScript()
    {
        // RealmZone 12 = Russian: Cyrillic only.
        Assert.Null(CharacterNameRules.Check("Иван", S(strict: 2, zone: 12)));
        Assert.Equal(CharResult.CharNameMixedLanguages, CharacterNameRules.Check("Ivan", S(strict: 2, zone: 12)));
        // RealmZone 8 = English: extended Latin.
        Assert.Null(CharacterNameRules.Check("Élan", S(strict: 2, zone: 8)));
        Assert.Equal(CharResult.CharNameMixedLanguages, CharacterNameRules.Check("Иван", S(strict: 2, zone: 8)));
        // RealmZone 6 = Korea: East Asian.
        Assert.Null(CharacterNameRules.Check("한글이름", S(strict: 2, zone: 6)));
        // RealmZone 1 = development: any script (LT_ANY), but still one script per name.
        Assert.Null(CharacterNameRules.Check("Иван", S(strict: 2, zone: 1)));
        Assert.Equal(CharResult.CharNameMixedLanguages, CharacterNameRules.Check("Aбв", S(strict: 2, zone: 1)));
    }

    [Fact]
    public void TournamentZone_IsBasicLatinOnlyWhenCreating()
    {
        // GetRealmLanguageType(create): LT_BASIC_LATIN = 0 matches no script by itself, so mask 2
        // alone rejects everything at create; mask 3 adds the basic Latin fallback (bit 1).
        Assert.Equal(CharResult.CharNameMixedLanguages, CharacterNameRules.Check("Elan", S(strict: 2, zone: 5, create: true)));
        Assert.Null(CharacterNameRules.Check("Elan", S(strict: 3, zone: 5, create: true)));
        Assert.Equal(CharResult.CharNameMixedLanguages, CharacterNameRules.Check("Élan", S(strict: 3, zone: 5, create: true)));
        // At login the tournament zones accept any script.
        Assert.Null(CharacterNameRules.Check("Élan", S(strict: 2, zone: 5, create: false)));
    }

    [Fact]
    public void Strict0_RejectsAMixOfScripts()
        => Assert.Equal(CharResult.CharNameMixedLanguages, CharacterNameRules.Check("Aб", S()));

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(99, 12)]
    public void MinPlayerName_IsClampedTo2Through12(int configured, int expected)
        => Assert.Equal(expected, new CharacterCreationOptions { MinPlayerName = configured }.EffectiveMinPlayerName);

    [Fact]
    public void MinPlayerName_ShortensTheAcceptedRange()
    {
        Assert.Equal(CharResult.CharNameTooShort, CharacterNameRules.Check("Abc", S(min: 4)));
        Assert.Null(CharacterNameRules.Check("Abcd", S(min: 4)));
    }

    [Fact]
    public void LengthBandsAreCheckedBeforeTheScript()
    {
        Assert.Equal(CharResult.CharNameTooShort, CharacterNameRules.Check("Ŋ", S()));
        Assert.Equal(CharResult.CharNameTooLong, CharacterNameRules.Check("Αλφαβητοςξυζω", S()));
    }
}
