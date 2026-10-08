namespace ArcaneCore.Game.Battlegrounds;

/// <summary>The battleground raid group of a player: its team's members in the match (join order) and the one who leads.</summary>
public sealed record BattlegroundChatTeam(IReadOnlyList<ObjectGuid> Members, ObjectGuid Leader);

/// <summary>
/// Who hears battleground chat (vmangos HandleChatMessageOpcode CHAT_MSG_BATTLEGROUND / _LEADER, ChatHandler.cpp:579-615: the
/// speaker's battleground raid group, <c>group->isBGGroup()</c>, and for the leader channel only its leader may speak).
/// </summary>
public interface IBattlegroundChatRoster
{
    /// <summary>The player's battleground team, or null when it is in no battleground.</summary>
    BattlegroundChatTeam? TeamOf(ObjectGuid player);
}

/// <summary>
/// The roster over the battleground manager: the player's bound match and team; the team's participants stand for vmangos'
/// battleground raid group (BattleGround::AddOrSetPlayerToCorrectBgGroup puts every joiner of a team into one raid, the
/// first joiner leading).
/// </summary>
public sealed class BattlegroundManagerChatRoster(BattlegroundManager manager) : IBattlegroundChatRoster
{
    public BattlegroundChatTeam? TeamOf(ObjectGuid player)
    {
        BattlegroundPlayerState state = manager.StateOf(player);
        if (!state.InBattleground || manager.GetBattleground(state.InstanceId, state.Type) is not { } battleground
            || battleground.PlayerTeam(player) is not { } team)
        {
            return null;
        }

        IReadOnlyList<ObjectGuid> members = battleground.TeamMembers(team);
        return members.Count == 0 ? null : new BattlegroundChatTeam(members, members[0]);
    }
}
