using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Groups;

/// <summary>
/// The battleground raid of each team (vmangos BattleGround::AddOrSetPlayerToCorrectBgGroup, BattleGround.cpp; Player::SetBattleGroundRaid /
/// RemoveFromBattleGroundRaid, Player.cpp). A participant's normal group is parked as its original group (vmangos m_originalGroup): it keeps
/// the member's slot and is still stored, but sends that member nothing until the battleground raid is left, when it becomes current again.
/// A battleground raid is never stored, raises no member events (no instance binds) and does not disband below two members.
/// </summary>
public sealed partial class GroupManager
{
    private readonly Dictionary<ObjectGuid, Group> _originalOf = [];

    /// <summary>The parked normal group of a player in a battleground raid (vmangos <c>GetOriginalGroup</c>), or null.</summary>
    public Group? GetOriginalGroup(ObjectGuid guid) => _originalOf.GetValueOrDefault(guid);

    /// <summary>
    /// Put a participant into its team's battleground raid (vmangos AddOrSetPlayerToCorrectBgGroup): <paramref name="raid"/> is the raid of a
    /// teammate, or null for the first of the team, who creates and leads a new one. Returns the raid, or null when it is full.
    /// </summary>
    public Group? AddToBattlegroundRaid(Player player, Group? raid)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (GetGroup(player.Guid) is { IsBattlegroundGroup: true } current)
        {
            return current;
        }

        if (raid is not null && (!raid.IsBattlegroundGroup || raid.IsFull))
        {
            return null;
        }

        UninviteFromGroup(player.Guid);
        if (_memberOf.Remove(player.Guid, out Group? original))
        {
            // vmangos SetBattleGroundRaid: the normal group becomes the original group; the client's party frame is cleared.
            _originalOf[player.Guid] = original;
            player.Session.Send(WorldOpcode.SmsgGroupList, GroupPackets.BuildEmptyGroupList());
        }

        if (raid is null)
        {
            raid = new Group(_nextId++)
            {
                IsCreated = true,
                Type = GroupType.Raid,
                IsBattlegroundGroup = true,
                LeaderGuid = player.Guid,
                LeaderName = player.Name,
                LeaderLastOnlineUnixSeconds = UnixSecondsClock(),
                LooterGuid = player.Guid,
                LootMethod = LootMethod.FreeForAll,
            };
            raid.ConvertToRaid();
        }

        raid.AddMemberSlot(player.Guid, player.Name);
        _memberOf[player.Guid] = raid;
        _sentStats.Remove(player.Guid);
        SendUpdate(raid);
        UpdateLeaderFlag(player);
        return raid;
    }

    /// <summary>
    /// Take a player out of its battleground raid (vmangos RemoveFromBattleGroundRaid → Group::RemoveMember of the BG group, then
    /// SetOriginalGroup back): the raid goes once empty; the original group, if it still has the player, is current again.
    /// </summary>
    public void RemoveFromBattlegroundRaid(ObjectGuid guid)
    {
        if (GetGroup(guid) is not { IsBattlegroundGroup: true } raid)
        {
            return;
        }

        if (raid.Find(guid) is { } slot)
        {
            raid.RemoveMemberSlot(slot);
        }

        _memberOf.Remove(guid);
        _sentStats.Remove(guid);
        Player? player = context.World.FindOnlinePlayer(guid);
        if (raid.MemberCount == 0)
        {
            raid.Clear();
        }
        else
        {
            if (raid.LeaderGuid == guid)
            {
                ChooseLeader(raid);
                Broadcast(raid, WorldOpcode.SmsgGroupSetLeader, GroupPackets.BuildName(raid.LeaderName));
            }

            SendUpdate(raid);
        }

        if (_originalOf.Remove(guid, out Group? original) && original.IsMember(guid))
        {
            _memberOf[guid] = original;
            SendUpdate(original);
        }
        else
        {
            player?.Session.Send(WorldOpcode.SmsgGroupList, GroupPackets.BuildEmptyGroupList());
        }

        if (player is not null)
        {
            UpdateLeaderFlag(player);
        }
    }

    /// <summary>Whether <paramref name="group"/> is the group the player sees now (vmangos Group::SendUpdate skips <c>GetGroup() != this</c>).</summary>
    private bool IsCurrentGroup(ObjectGuid guid, Group group) => !_memberOf.TryGetValue(guid, out Group? current) || current == group;

    /// <summary>Drop the player's link to <paramref name="group"/>; true when it was the player's current group.</summary>
    private bool ForgetMembership(ObjectGuid guid, Group group)
    {
        if (_memberOf.TryGetValue(guid, out Group? current) && current == group)
        {
            _memberOf.Remove(guid);
            return true;
        }

        if (_originalOf.TryGetValue(guid, out Group? original) && original == group)
        {
            _originalOf.Remove(guid);
        }

        return false;
    }
}
