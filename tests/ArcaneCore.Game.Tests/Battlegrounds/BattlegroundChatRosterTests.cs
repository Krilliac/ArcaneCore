using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// Who hears battleground chat: the speaker's team in its match, standing for vmangos' battleground raid group
/// (BattleGround::AddOrSetPlayerToCorrectBgGroup: the first joiner of a team leads, the next one when the leader leaves).
/// </summary>
public sealed class BattlegroundChatRosterTests
{
    private static (BattlegroundManager Manager, Battleground Match) InMatch(params ObjectGuid[] entering)
    {
        var host = new RecordingManagerHost();
        var ports = new RecordingPorts();
        var manager = new BattlegroundManager(new BattlegroundOptions(), host, ports.ToPorts(new RecordingHost()));
        manager.RegisterTemplate(WsgTemplate(2, 10));
        foreach ((ObjectGuid guid, Team team) in new[] { (Alliance[0], Team.Alliance), (Alliance[1], Team.Alliance), (Horde[0], Team.Horde), (Horde[1], Team.Horde) })
        {
            manager.JoinQueue(new BattlegroundJoinRequest(new BattlegroundMember(guid, team, 60, true, false), BattlegroundType.WarsongGulch, 0, AsGroup: false, QueuedAtPortal: false, []));
        }

        manager.Update(1000);
        Battleground match = host.Created.Single();
        foreach (ObjectGuid guid in entering)
        {
            manager.HandlePort(new BattlegroundPortRequest(guid, 489, 1, 60, false));
            Assert.True(manager.EnterBattleground(guid));
        }

        return (manager, match);
    }

    [Fact]
    public void ATeam_IsItsParticipantsInJoinOrder_LedByTheFirst_AndTheOtherTeamIsSeparate()
    {
        (BattlegroundManager manager, _) = InMatch(Alliance[1], Horde[0], Alliance[0]);
        var roster = new BattlegroundManagerChatRoster(manager);

        BattlegroundChatTeam alliance = roster.TeamOf(Alliance[0])!;
        Assert.Equal([Alliance[1], Alliance[0]], alliance.Members);
        Assert.Equal(Alliance[1], alliance.Leader);
        Assert.Equal([Horde[0]], roster.TeamOf(Horde[0])!.Members);
        Assert.Null(roster.TeamOf(Horde[1]));   // invited, never entered
        Assert.Null(roster.TeamOf(Horde[5]));   // not in a battleground at all
    }

    [Fact]
    public void WhenTheLeaderLeaves_TheNextJoinerLeads()
    {
        (BattlegroundManager manager, Battleground match) = InMatch(Alliance[1], Alliance[0]);
        var roster = new BattlegroundManagerChatRoster(manager);

        match.RemovePlayerAtLeave(Alliance[1], teleportToEntryPoint: false, sendStatus: false);

        BattlegroundChatTeam alliance = roster.TeamOf(Alliance[0])!;
        Assert.Equal([Alliance[0]], alliance.Members);
        Assert.Equal(Alliance[0], alliance.Leader);
    }
}
