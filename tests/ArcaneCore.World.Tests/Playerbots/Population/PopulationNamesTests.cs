using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>The name generator for bot characters: it must only ever offer names the realm's own name rules accept.</summary>
public sealed class PopulationNamesTests
{
    [Fact]
    public void Names_AreLettersOnly_TwoToTwelve_NoThreeIdenticalLetters()
    {
        // The realm rule (vmangos ObjectMgr::CheckPlayerName): 2..12 letters of one alphabet, no letter three times in a row
        // (CharNameThreeConsecutive); a name starts with a capital and goes on in lower case.
        var names = new PopulationNames(20261010);
        for (int i = 0; i < 20000; i++)
        {
            string name = names.Next();
            Assert.InRange(name.Length, 2, 12);
            Assert.InRange(name.Length, PopulationNames.MinLength, PopulationNames.MaxLength);
            Assert.All(name, c => Assert.True(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z', $"'{name}' holds '{c}'"));
            Assert.True(char.IsUpper(name[0]), name);
            Assert.All(name.Skip(1), c => Assert.True(char.IsLower(c), name));
            for (int k = 2; k < name.Length; k++)
                Assert.False(char.ToLowerInvariant(name[k]) == char.ToLowerInvariant(name[k - 1]) && char.ToLowerInvariant(name[k]) == char.ToLowerInvariant(name[k - 2]), name);
        }
    }

    [Fact]
    public void TheSequence_IsDeterministicForASeed()
    {
        string[] first = Take(new PopulationNames(7), 200);
        Assert.Equal(first, Take(new PopulationNames(7), 200));
        Assert.NotEqual(first, Take(new PopulationNames(8), 200));
        // The names vary: a population of bots does not share a handful of names.
        Assert.True(Take(new PopulationNames(9), 1000).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 600);
    }

    [Fact]
    public void AProcessSeededGenerator_AlsoMakesValidNames()
    {
        PopulationNames names = PopulationNames.Create();
        foreach (string name in Take(names, 500))
        {
            Assert.InRange(name.Length, PopulationNames.MinLength, PopulationNames.MaxLength);
            Assert.All(name, c => Assert.True(char.IsAsciiLetter(c), name));
        }
    }

    private static string[] Take(PopulationNames names, int count) => [.. Enumerable.Range(0, count).Select(_ => names.Next())];
}
