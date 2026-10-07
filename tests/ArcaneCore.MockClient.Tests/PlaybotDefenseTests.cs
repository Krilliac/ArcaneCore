using ArcaneCore.Game;
using ArcaneCore.MockClient.Playbots;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class PlaybotDefenseTests
{
    private const uint AttackEntry = 6;
    private const ulong CharacterGuid = 0x0000000100000001;
    private const ulong AttackerGuid = (0xF130UL << 48) | (AttackEntry << 24) | 1;

    private static PlaybotState State => new(7, 60, 100, true, new(0, 0, 0), new(0, 0, 0),
        [new PlaybotObject(AttackerGuid, AttackEntry, 40, (uint)UnitFlags.InCombat, 0, new(2, 0, 0), CharacterGuid)],
        new HashSet<ulong>(), new HashSet<ulong>(), 0, null, false, false, false, false, 0, 0, CharacterGuid);

    [Fact]
    public void FindsNearbyEntryAttackerTargetingThisCharacter()
    {
        PlaybotCandidate? candidate = PlaybotDefense.FindCandidate(State, AttackEntry);
        Assert.NotNull(candidate);
        Assert.Equal($"7:defend-{AttackerGuid}", candidate!.Id);
        Assert.Equal(PlaybotActionKind.Attack, candidate.Kind);
        Assert.Equal(100, candidate.Priority);
        Assert.Equal(AttackerGuid, candidate.Target);
    }

    [Fact]
    public void RequiresKnownPlayerCombatHealthAndCharacterIdentity()
    {
        Assert.Null(PlaybotDefense.FindCandidate(State with { Health = null }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with { MaximumHealth = null }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with { InCombat = null }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with { AttackTarget = AttackerGuid }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with { CharacterGuid = 0 }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with { Released = true }, AttackEntry));
    }

    [Fact]
    public void RequiresKnownFullTargetGuidAndObservedTargetFields()
    {
        Assert.Null(PlaybotDefense.FindCandidate(State with { Objects = [State.Objects[0] with { TargetGuid = null }] }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with { Objects = [State.Objects[0] with { Flags = null }] }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with { Objects = [State.Objects[0] with { Health = null }] }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with { Objects = [State.Objects[0] with { Position = null }] }, AttackEntry));
    }

    [Fact]
    public void RequiresCreatureHighWordAndExactConfiguredEntry()
    {
        Assert.Null(PlaybotDefense.FindCandidate(State with
        {
            Objects = [State.Objects[0] with { Guid = (0xF129UL << 48) | (AttackEntry << 24) | 1 }]
        }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State, 0));
        Assert.Null(PlaybotDefense.FindCandidate(State, AttackEntry + 1));
        Assert.Null(PlaybotDefense.FindCandidate(State with { Objects = [State.Objects[0] with { Entry = AttackEntry + 1 }] }, AttackEntry));
    }

    [Fact]
    public void ExcludesNonAttackableOrNonCombatTargets()
    {
        UnitFlags[] excluded =
        [
            UnitFlags.Spawning, UnitFlags.NotAttackable1, UnitFlags.NonAttackable2,
            UnitFlags.NotSelectable, UnitFlags.ImmuneToPlayer, UnitFlags.PlayerControlled
        ];
        foreach (UnitFlags flag in excluded)
        {
            Assert.Null(PlaybotDefense.FindCandidate(State with
            {
                Objects = [State.Objects[0] with { Flags = (uint)(UnitFlags.InCombat | flag) }]
            }, AttackEntry));
        }

        Assert.Null(PlaybotDefense.FindCandidate(State with
        {
            Objects = [State.Objects[0] with { Flags = 0 }]
        }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with
        {
            Objects = [State.Objects[0] with { Health = 0 }]
        }, AttackEntry));
    }

    [Fact]
    public void RejectsPlayerHealthBelowSixtyPercentAndOutOfRangeOrNonFinitePositions()
    {
        Assert.Null(PlaybotDefense.FindCandidate(State with { Health = 59 }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with { MaximumHealth = 0 }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with { Objects = [State.Objects[0] with { Position = new(3.01f, 0, 0) }] }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with { Position = new(float.NaN, 0, 0) }, AttackEntry));
        Assert.Null(PlaybotDefense.FindCandidate(State with { Objects = [State.Objects[0] with { Position = new(float.PositiveInfinity, 0, 0) }] }, AttackEntry));
    }
}
