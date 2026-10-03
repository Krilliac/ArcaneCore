using System.Text;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using Xunit;

namespace ArcaneCore.World.Tests.Characters.Creation;

/// <summary>
/// Name normalisation as an exact port of vmangos wcharToUpper/wcharToLower (Util.h:268-316) and
/// normalizePlayerName (ObjectMgr.cpp:64-80), plus the raw-UTF-8 name path and the CharResult
/// wire values (gtker world_result.wowm:91-159).
/// </summary>
public sealed class CharacterNamesExactTests
{
    [Fact]
    public void FirstLetterSharpS_IsMappedToCapitalSharpS()
        => Assert.Equal("ẞeta", CharacterNames.Normalize("ßeta")); // Util.h:277-278

    [Fact]
    public void CapitalSharpS_InTheTail_IsLowered()
        => Assert.Equal("Aß", CharacterNames.Normalize("aẞ")); // Util.h:310-311

    [Fact]
    public void FullwidthForms_AreNotCaseMapped()
        => Assert.Equal("ＡＢＣ", CharacterNames.Normalize("ＡＢＣ")); // no mapping exists in Util.h

    [Theory] // characterization: these already equal the invariant-culture result
    [InlineData("ÉCOLE", "École")]
    [InlineData("ёЖ", "Ёж")]
    [InlineData("āĀ", "Āā")]
    [InlineData("ĲX", "Ĳx")] // U+0132 is past the 0x0100..0x012E lowering range: left alone
    public void ExistingBehaviourIsKept(string input, string expected)
        => Assert.Equal(expected, CharacterNames.Normalize(input));

    [Fact]
    public void Greek_IsNotMapped()
    {
        // vmangos maps only Basic/Extended Latin and Cyrillic; invariant culture would fold Greek.
        Assert.Equal("αΑ", CharacterNames.Normalize("αΑ"));
    }

    [Fact]
    public void InvalidUtf8_IsNoName()
        => Assert.Null(CharacterNames.NormalizeUtf8([0xFF, 0xFE]));

    [Fact]
    public void EmptyName_IsNoName()
        => Assert.Null(CharacterNames.NormalizeUtf8([]));

    [Fact]
    public void MoreThanFifteenCodePoints_IsNoName()
    {
        Assert.Null(CharacterNames.NormalizeUtf8(Encoding.UTF8.GetBytes("Abcdefghijklmnop")));
        Assert.Equal("Abcdefghijklmno", CharacterNames.NormalizeUtf8(Encoding.UTF8.GetBytes("ABCDEFGHIJKLMNO")));
    }

    [Fact]
    public void ValidateUtf8_MapsEveryLengthBand()
    {
        static CharResult? V(string s) => CharacterNames.ValidateUtf8(Encoding.UTF8.GetBytes(s), out _);
        Assert.Equal(CharResult.CharNameNoName, V("Abcdefghijklmnop")); // 16
        Assert.Equal(CharResult.CharNameTooLong, V("Abcdefghijklm")); // 13
        Assert.Equal(CharResult.CharNameTooShort, V("A"));
        Assert.Null(V("Abcdefghijkl")); // 12
        Assert.Equal(CharResult.CharNameNoName, CharacterNames.ValidateUtf8([0xC3], out _)); // truncated sequence
    }

    [Fact]
    public void ValidateUtf8_ReturnsTheNormalizedName()
    {
        Assert.Null(CharacterNames.ValidateUtf8(Encoding.UTF8.GetBytes("tHRALL"), out string? name));
        Assert.Equal("Thrall", name);
    }

    [Fact]
    public void LengthCountsCodePoints_NotUtf16Units()
    {
        // 12 supplementary-plane characters are 12 code points (vmangos wchar_t on Linux), so the
        // name is not too long; it is still rejected, but as a script mismatch.
        string name = string.Concat(Enumerable.Repeat("\U00020000", 12));
        Assert.Equal(CharResult.CharNameMixedLanguages, CharacterNames.Validate(name));
        Assert.Equal(CharResult.CharNameTooLong, CharacterNames.Validate(string.Concat(Enumerable.Repeat("\U00020000", 13))));
    }

    [Fact]
    public void UndecodableName_IsInvalidCharacter()
        => Assert.Equal(CharResult.CharNameInvalidCharacter, CharacterNames.Validate("Ab\uD800cd")); // lone surrogate: ObjectMgr.cpp:9582

    [Fact]
    public void ResultCodes_HaveTheGtkerWireValues()
    {
        Assert.Equal(0x33, (byte)CharResult.CharCreatePvpTeamsViolation);
        Assert.Equal(0x35, (byte)CharResult.CharCreateAccountLimit);
        Assert.Equal(0x4A, (byte)CharResult.CharNameProfane);
        Assert.Equal(0x4B, (byte)CharResult.CharNameReserved);
        Assert.Equal(0x4C, (byte)CharResult.CharNameInvalidApostrophe);
        Assert.Equal(0x4D, (byte)CharResult.CharNameMultipleApostrophes);
        Assert.Equal(0x4E, (byte)CharResult.CharNameThreeConsecutive);
        Assert.Equal(0x4F, (byte)CharResult.CharNameInvalidSpace);
        Assert.Equal(0x50, (byte)CharResult.CharNameSuccess);
    }
}
