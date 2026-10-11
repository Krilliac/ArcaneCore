using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.World.Playerbots.Battlegrounds;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// Who does what in a match. The shares (WSG 40/30/30, AB 70/30; Alterac Valley is not played by bots) are this server's design, not a vmangos value; the tests pin
/// the arithmetic, the healer preference and that the split is the same for every bot that computes it.
/// </summary>
public sealed class PlayerbotBattlegroundRoleTests
{
    private static ObjectGuid Guid(ulong low) => new(low);

    private static List<(ObjectGuid, bool)> Team(int count, params int[] healerIndexes)
        => [.. Enumerable.Range(1, count).Select(i => (Guid((ulong)i), healerIndexes.Contains(i)))];

    [Theory]
    [InlineData(10, 4, 3, 3)]
    [InlineData(1, 1, 0, 0)]
    [InlineData(2, 1, 0, 1)]
    [InlineData(3, 2, 0, 1)]
    [InlineData(5, 2, 1, 2)]
    [InlineData(0, 0, 0, 0)]
    public void Wsg_RolesSplit40_30_30(int count, int attackers, int escorts, int defenders)
        => Assert.Equal((attackers, escorts, defenders), PlayerbotBattlegroundRoles.Shares(BattlegroundType.WarsongGulch, count));

    [Theory]
    [InlineData(BattlegroundType.ArathiBasin, 10, 7, 3)]
    [InlineData(BattlegroundType.ArathiBasin, 1, 1, 0)]
    public void Ab_HasNoEscorts(BattlegroundType type, int count, int attackers, int defenders)
        => Assert.Equal((attackers, 0, defenders), PlayerbotBattlegroundRoles.Shares(type, count));

    [Fact]
    public void Shares_AlwaysSumToTheTeamSize_AndNeverGoNegative()
    {
        foreach (BattlegroundType type in PlayerbotBattlegroundBrackets.Types)
        {
            for (int count = 0; count <= 80; count++)
            {
                (int a, int e, int d) = PlayerbotBattlegroundRoles.Shares(type, count);
                Assert.True(a >= 0 && e >= 0 && d >= 0, $"{type} {count}");
                Assert.Equal(count, a + e + d);
            }
        }

        Assert.Equal((0, 0, 0), PlayerbotBattlegroundRoles.Shares(BattlegroundType.WarsongGulch, -3));
    }

    [Fact]
    public void Assign_GivesEveryBotOneRole_MatchingTheShares()
    {
        foreach (BattlegroundType type in PlayerbotBattlegroundBrackets.Types)
        {
            Dictionary<ObjectGuid, PlayerbotBattlegroundRole> roles = PlayerbotBattlegroundRoles.Assign(type, Team(10));
            Assert.Equal(10, roles.Count);
            (int a, int e, int d) = PlayerbotBattlegroundRoles.Shares(type, 10);
            Assert.Equal((a, e, d), (roles.Values.Count(r => r == PlayerbotBattlegroundRole.Attacker), roles.Values.Count(r => r == PlayerbotBattlegroundRole.Escort),
                roles.Values.Count(r => r == PlayerbotBattlegroundRole.Defender)));
        }
    }

    [Fact]
    public void Healer_PrefersEscort()
    {
        // Bots 7 and 9 are healers: with three escort places, both take one and the lowest guid fills the third.
        Dictionary<ObjectGuid, PlayerbotBattlegroundRole> roles = PlayerbotBattlegroundRoles.Assign(BattlegroundType.WarsongGulch, Team(10, 7, 9));
        Assert.Equal(PlayerbotBattlegroundRole.Escort, roles[Guid(7)]);
        Assert.Equal(PlayerbotBattlegroundRole.Escort, roles[Guid(9)]);
        Assert.Equal(PlayerbotBattlegroundRole.Escort, roles[Guid(1)]);
        Assert.Equal(3, roles.Values.Count(r => r == PlayerbotBattlegroundRole.Escort));

        // More healers than escort places: the lowest guids among them get the places, the others fall back to attack and defence.
        roles = PlayerbotBattlegroundRoles.Assign(BattlegroundType.WarsongGulch, Team(10, 2, 4, 6, 8));
        Assert.Equal([PlayerbotBattlegroundRole.Escort, PlayerbotBattlegroundRole.Escort, PlayerbotBattlegroundRole.Escort],
            new[] { roles[Guid(2)], roles[Guid(4)], roles[Guid(6)] });
        Assert.NotEqual(PlayerbotBattlegroundRole.Escort, roles[Guid(8)]);
        Assert.Equal(3, roles.Values.Count(r => r == PlayerbotBattlegroundRole.Escort));
    }

    [Fact]
    public void Healer_WhereThereAreNoEscorts_StillGetsAnAttackOrDefenceRole()
    {
        Dictionary<ObjectGuid, PlayerbotBattlegroundRole> roles = PlayerbotBattlegroundRoles.Assign(BattlegroundType.ArathiBasin, Team(10, 1, 2));
        Assert.DoesNotContain(PlayerbotBattlegroundRole.Escort, roles.Values);
        Assert.Equal(10, roles.Count);
        Assert.Equal(PlayerbotBattlegroundRole.Attacker, roles[Guid(1)]); // lowest guids fill the attack places first
        Assert.Equal(PlayerbotBattlegroundRole.Defender, roles[Guid(10)]);
    }

    [Fact]
    public void Assign_IsDeterministic_RegardlessOfInputOrder()
    {
        List<(ObjectGuid, bool)> team = Team(14, 3, 11);
        Dictionary<ObjectGuid, PlayerbotBattlegroundRole> forward = PlayerbotBattlegroundRoles.Assign(BattlegroundType.WarsongGulch, team);
        team.Reverse();
        Dictionary<ObjectGuid, PlayerbotBattlegroundRole> reversed = PlayerbotBattlegroundRoles.Assign(BattlegroundType.WarsongGulch, team);
        Assert.Equal(forward.OrderBy(p => p.Key.Value), reversed.OrderBy(p => p.Key.Value));
    }

    [Fact]
    public void Assign_EmptyTeam_IsEmpty_AndNullIsRefused()
    {
        Assert.Empty(PlayerbotBattlegroundRoles.Assign(BattlegroundType.WarsongGulch, []));
        Assert.Throws<ArgumentNullException>(() => PlayerbotBattlegroundRoles.Assign(BattlegroundType.WarsongGulch, null!));
    }

    [Fact]
    public void LoneBot_IsAnAttacker()
        => Assert.Equal(PlayerbotBattlegroundRole.Attacker, PlayerbotBattlegroundRoles.Assign(BattlegroundType.WarsongGulch, Team(1))[Guid(1)]);
}
