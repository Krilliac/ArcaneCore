using ArcaneCore.Game.Entities;
using ArcaneCore.World.Characters.Creation;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// Pins the byte-race to team wrappers (vmangos Player::TeamForRace) that must keep their results, including for
/// races outside 1..8, once they delegate to one shared rule.
/// </summary>
public sealed class RaceTeamWrapperCharacterisationTests
{
    [Theory]
    [InlineData((byte)1, Team.Alliance)]
    [InlineData((byte)2, Team.Horde)]
    [InlineData((byte)3, Team.Alliance)]
    [InlineData((byte)4, Team.Alliance)]
    [InlineData((byte)5, Team.Horde)]
    [InlineData((byte)6, Team.Horde)]
    [InlineData((byte)7, Team.Alliance)]
    [InlineData((byte)8, Team.Horde)]
    [InlineData((byte)0, Team.Horde)]
    [InlineData((byte)9, Team.Horde)]
    [InlineData((byte)11, Team.Horde)]
    [InlineData((byte)255, Team.Horde)]
    public void TheByteWrappersMapRacesToTeams(byte race, Team expected)
    {
        Assert.Equal(expected, RaceClassRules.TeamForRace(race));
        Assert.Equal(expected, PopulationRaceClassCache.TeamOf(race));
    }
}
