using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>Load-time validation against Faction.dbc (ObjectMgr.cpp:8935-8957, 5702-5750, 6026-6039).</summary>
public sealed class ReputationContentValidatorTests
{
    private static ReputationOnKillEntry Kill(uint creature, uint f1, uint f2 = 0, byte max1 = 7)
        => new(creature, f1, f2, max1, false, 10, 7, false, 10, TeamDependent: false);

    [Fact]
    public void OnKill_RowsWithAMissingFaction_AreSkippedWholeAndNamed()
    {
        ReputationOnKillValidation result = ReputationContentValidator.FilterOnKill(
            [Kill(1, BootyBay), Kill(2, UnknownFaction), Kill(3, BootyBay, UnknownFaction), Kill(4, 0, Stormwind), Kill(5, BootyBay, 0, max1: 9)], Factions);

        Assert.Equal([1u, 4u, 5u], result.Entries.Select(e => e.CreatureEntry));
        Assert.Equal(3, result.Warnings.Count);
        Assert.Contains("creature 2 uses faction 4242", result.Warnings[0]);
        Assert.Contains("creature 3 uses faction 4242", result.Warnings[1]);
        Assert.Contains("creature 5 has a MaxStanding that is not a rank", result.Warnings[2]);
    }

    [Fact]
    public void Quests_NameTheColumnsThatNoPlayerCanSatisfy()
    {
        QuestTemplate[] quests =
        [
            new() { Entry = 10, RewRepFaction1 = BootyBay, RewRepValue1 = 250 },                                   // fine
            new() { Entry = 11, RewRepFaction1 = UnknownFaction, RewRepValue1 = 250, RewRepFaction2 = BootyBay }, // unknown faction + zero value
            new() { Entry = 12, RepObjectiveFaction = UnknownFaction, RequiredMinRepFaction = UnknownFaction, RequiredMaxRepFaction = UnknownFaction },
            new() { Entry = 13, RequiredMinRepFaction = Stormwind, RequiredMinRepValue = 43000 },
            new() { Entry = 14, RequiredMinRepFaction = Stormwind, RequiredMinRepValue = 3000, RequiredMaxRepFaction = Stormwind, RequiredMaxRepValue = 3000 },
        ];

        IReadOnlyList<string> warnings = ReputationContentValidator.ValidateQuests(quests, Factions);

        Assert.Equal(7, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith("Quest 11 has RewRepFaction1 = 4242 but the faction does not exist", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("Quest 11 has RewRepFaction2 = 21 but RewRepValue2 = 0", StringComparison.Ordinal));
        Assert.Equal(3, warnings.Count(w => w.StartsWith("Quest 12 ", StringComparison.Ordinal)));
        Assert.Contains(warnings, w => w.Contains("Quest 13 has RequiredMinRepValue = 43000 but max reputation is 42999", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("Quest 14 has RequiredMaxRepValue = 3000", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.StartsWith("Quest 10 ", StringComparison.Ordinal));
    }
}
