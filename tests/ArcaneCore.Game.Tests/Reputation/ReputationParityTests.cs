using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using Xunit;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>
/// Retail-parity fixes to the reputation core: vmangos rand_dither mapping
/// (Random.cpp:80-83), the forced-peace exception compared on the relative standing
/// (ReputationMgr.cpp:334-336) and the en-US rank names.
/// </summary>
public sealed class ReputationParityTests
{
    // rand_dither(v) = copysign(floor(|v| + frand(0,1)), v): the roll is added, not compared.
    [Theory]
    [InlineData(2.3f, 0.9, 3)]
    [InlineData(2.3f, 0.1, 2)]
    [InlineData(-2.3f, 0.9, -3)]
    [InlineData(-2.3f, 0.1, -2)]
    [InlineData(5f, 0.0, 5)]
    [InlineData(5f, 0.999, 5)]
    [InlineData(-5f, 0.999, -5)]
    [InlineData(0f, 0.999, 0)]
    public void Dither_MatchesRandDither_ForFixedRolls(float value, double roll, int expected)
        => Assert.Equal(expected, ReputationMath.Dither(value, roll));

    private const uint ForcedPeaceFaction = 900;

    // A forced-peace faction whose race base is already Hated (-42000) while the relative
    // standing is 0: retail compares the RELATIVE standing (Neutral), so war stays refused.
    private static PlayerReputation HatedBasePeaceForced(bool effective)
    {
        var catalog = new FactionCatalog(
        [
            new FactionRecord(ForcedPeaceFaction, 3, [0, 0, 0, 0], [0, 0, 0, 0], [-42000, 0, 0, 0],
                [0x10, 0, 0, 0], 0, "Forced peace"),
        ]);
        return new PlayerReputation(catalog, Race.Human, Class.Warrior) { PeaceForcedUsesEffectiveStanding = effective };
    }

    [Fact]
    public void PeaceForced_UsesRelativeStanding_ByDefault()
    {
        PlayerReputation rep = HatedBasePeaceForced(effective: false);
        Assert.Equal(ReputationRank.Hated, rep.Rank(rep.Factions.Find(ForcedPeaceFaction)!));
        Assert.False(rep.SetAtWarByClient(3, true));
        Assert.False(rep.State(rep.Factions.Find(ForcedPeaceFaction)!)!.IsAtWar);
    }

    [Fact]
    public void PeaceForced_FlipsToEffectiveStanding_OnlyWhenTheOptionIsOn()
    {
        PlayerReputation rep = HatedBasePeaceForced(effective: true);
        Assert.True(rep.SetAtWarByClient(3, true));
        Assert.True(rep.State(rep.Factions.Find(ForcedPeaceFaction)!)!.IsAtWar);
    }

    [Fact]
    public void RankNames_AreEnUsAndIndexedByRank()
    {
        string[] expected = ["Hated", "Hostile", "Unfriendly", "Neutral", "Friendly", "Honored", "Revered", "Exalted"];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], ReputationRankNames.Name((ReputationRank)i));
        }

        Assert.Equal("Unknown", ReputationRankNames.Name((ReputationRank)99));
        Assert.True(ReputationRankNames.TryParsePrefix("hon", out ReputationRank rank));
        Assert.Equal(ReputationRank.Honored, rank);
        Assert.False(ReputationRankNames.TryParsePrefix("", out _));
        Assert.False(ReputationRankNames.TryParsePrefix("zzz", out _));
    }
}
