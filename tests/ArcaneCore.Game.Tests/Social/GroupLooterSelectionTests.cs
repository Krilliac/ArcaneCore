using ArcaneCore.Game.Groups;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// Group::UpdateLooterGuid (vmangos Group.cpp:2474-2543) and the master looter fallback
/// (Unit.cpp:1041-1063), hand-simulated.
/// </summary>
public sealed class GroupLooterSelectionTests
{
    private static readonly ObjectGuid A = ObjectGuid.Player(1);
    private static readonly ObjectGuid B = ObjectGuid.Player(2);
    private static readonly ObjectGuid C = ObjectGuid.Player(3);

    private static Group MakeGroup(LootMethod method, ObjectGuid looter, params ObjectGuid[] members)
    {
        var group = new Group(1) { LootMethod = method, IsCreated = true, LeaderGuid = members[0], LooterGuid = looter };
        foreach (ObjectGuid member in members)
        {
            group.AddMemberSlot(member, "m" + member.Value);
        }

        return group;
    }

    [Fact]
    public void KeepsTheCurrentLooter_OnlyWhenIfNeededAndStillEligible()
    {
        Group group = MakeGroup(LootMethod.RoundRobin, A, A, B, C);
        Assert.Equal(new LooterSelection(A, false), GroupLooterSelection.Next(group, A, true, _ => true));
        Assert.Equal(new LooterSelection(B, true), GroupLooterSelection.Next(group, A, false, _ => true));
    }

    [Fact]
    public void ALooterOutOfReach_LosesTheTurn()
    {
        Group group = MakeGroup(LootMethod.RoundRobin, A, A, B, C);
        Assert.Equal(new LooterSelection(B, true), GroupLooterSelection.Next(group, A, true, g => g != A));
    }

    [Fact]
    public void TwoCallsPerKill_LeaderFirst_ThenEachMemberInTurn_AndWrap()
    {
        Group group = MakeGroup(LootMethod.GroupLoot, A, A, B, C);
        var looters = new List<ObjectGuid>();
        ObjectGuid pointer = A;
        for (int kill = 0; kill < 4; kill++)
        {
            pointer = GroupLooterSelection.Next(group, pointer, true, _ => true).Looter;
            looters.Add(pointer);
            pointer = GroupLooterSelection.Next(group, pointer, false, _ => true).Looter;
        }

        Assert.Equal([A, B, C, A], looters);
    }

    [Fact]
    public void WrapsAroundAndLandsOnTheCurrentLooterWhenHeIsTheOnlyOneInReach()
    {
        Group group = MakeGroup(LootMethod.RoundRobin, B, A, B, C);
        Assert.Equal(new LooterSelection(B, false), GroupLooterSelection.Next(group, B, false, g => g == B));
        Assert.Equal(new LooterSelection(A, true), GroupLooterSelection.Next(group, C, false, g => g == A));
    }

    [Fact]
    public void NobodyInReach_ClearsTheLooter()
    {
        Group group = MakeGroup(LootMethod.RoundRobin, A, A, B);
        Assert.Equal(new LooterSelection(default, true), GroupLooterSelection.Next(group, A, true, _ => false));
        Assert.Equal(new LooterSelection(default, false), GroupLooterSelection.Next(group, default, false, _ => false));
    }

    [Fact]
    public void AnEmptyOrForeignLooter_SearchesFromTheStartOfTheMemberList()
    {
        Group group = MakeGroup(LootMethod.RoundRobin, default, A, B, C);
        Assert.Equal(new LooterSelection(A, true), GroupLooterSelection.Next(group, default, true, _ => true));
        Assert.Equal(new LooterSelection(B, true), GroupLooterSelection.Next(group, ObjectGuid.Player(99), true, g => g != A));
    }

    [Theory]
    [InlineData(LootMethod.MasterLoot)]
    [InlineData(LootMethod.FreeForAll)]
    public void MasterLootAndFreeForAll_NeverMoveThePointer(LootMethod method)
    {
        Group group = MakeGroup(method, B, A, B, C);
        Assert.Equal(new LooterSelection(B, false), GroupLooterSelection.Next(group, B, false, _ => true));
        Assert.Equal(new LooterSelection(B, false), GroupLooterSelection.Next(group, B, true, _ => false));
    }

    [Fact]
    public void MasterLooterFallback_IsTheOnlineLeaderOrAssistantOtherThanTheMaster()
    {
        Group group = MakeGroup(LootMethod.MasterLoot, B, A, B, C);
        Assert.Equal(A, GroupLooterSelection.MasterLooterFallback(group, _ => true));
        Assert.Null(GroupLooterSelection.MasterLooterFallback(group, g => g != A));

        group.Members[2].Assistant = true;
        Assert.Equal(C, GroupLooterSelection.MasterLooterFallback(group, g => g != A));

        group.LooterGuid = A; // the master is the leader: the assistant is next in line
        Assert.Equal(C, GroupLooterSelection.MasterLooterFallback(group, _ => true));
    }
}
