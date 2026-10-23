using ArcaneCore.Game.Entities;
using ArcaneCore.World.Characters.Creation;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// Pins the race to team answer of the character-create rules and of the population race cache, which must stay equal
/// after the copies are unified (human, dwarf, night elf and gnome are Alliance; every other byte is Horde).
/// </summary>
public sealed class RaceTeamMappingTests
{
    public static TheoryData<byte, Team> Races => new()
    {
        { 0, Team.Horde },
        { 1, Team.Alliance },
        { 2, Team.Horde },
        { 3, Team.Alliance },
        { 4, Team.Alliance },
        { 5, Team.Horde },
        { 6, Team.Horde },
        { 7, Team.Alliance },
        { 8, Team.Horde },
        { 9, Team.Horde },
        { 10, Team.Horde },
        { 32, Team.Horde },
        { 255, Team.Horde },
    };

    [Theory]
    [MemberData(nameof(Races))]
    public void TheCharacterCreateRulesGiveTheTeamOfARace(byte race, Team expected)
    {
        Assert.Equal(expected, RaceClassRules.TeamForRace(race));
    }

    [Theory]
    [MemberData(nameof(Races))]
    public void ThePopulationRaceCacheGivesTheTeamOfARace(byte race, Team expected)
    {
        Assert.Equal(expected, PopulationRaceClassCache.TeamOf(race));
    }
}
