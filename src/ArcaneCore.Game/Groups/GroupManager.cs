using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Social;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Groups;

/// <summary>
/// Parties and raids: invites, membership, leadership, loot settings, raid subgroups, ready
/// checks, target icons and out-of-range member stats. The rules follow vmangos
/// GroupHandler.cpp and Group.cpp (citations per method). World thread.
/// </summary>
public sealed class GroupManager(SocialContext context)
{
    /// <summary>vmangos HandleRandomRollOpcode: the largest accepted maximum.</summary>
    public const uint MaxRoll = 1_000_000;

    private readonly Dictionary<ObjectGuid, Group> _memberOf = [];
    private readonly Dictionary<ObjectGuid, Group> _invitedTo = [];
    private readonly Dictionary<ObjectGuid, MemberStats> _sentStats = [];
    private readonly HashSet<ObjectGuid> _pendingPetName = [];
    private readonly Dictionary<ObjectGuid, (uint Positive, ushort Negative)> _pendingPetAuras = [];
    private uint _nextId = 1;

    /// <summary>A member was added; the first is the leader when the group is created (world thread; instance binds follow it).</summary>
    public event Action<Group, ObjectGuid>? MemberAdded;

    /// <summary>A member left or was removed without disbanding the group (world thread).</summary>
    public event Action<Group, ObjectGuid>? MemberRemoved;

    /// <summary>The group is about to be disbanded; members are still listed. The GUID is the leaving member, or empty (world thread).</summary>
    public event Action<Group, ObjectGuid>? Disbanding;

    /// <summary>The leader changed; the GUID is the previous leader (world thread).</summary>
    public event Action<Group, ObjectGuid>? LeaderChanged;

    /// <summary>The group <paramref name="guid"/> is a member of (online or not).</summary>
    public Group? GetGroup(ObjectGuid guid) => _memberOf.GetValueOrDefault(guid);

    /// <summary>The group <paramref name="guid"/> has a pending invite from (or leads before creation).</summary>
    public Group? GetInvite(ObjectGuid guid) => _invitedTo.GetValueOrDefault(guid);

    /// <summary>Whether two players share a group (vmangos Player::IsInSameRaidWith).</summary>
    public bool AreInSameGroup(ObjectGuid a, ObjectGuid b) => a == b || (GetGroup(a) is { } g && g.IsMember(b));

    /// <summary>Queues the current player's pet-name bit for the next out-of-range stats pass.</summary>
    public void MarkPetNameChanged(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (GetGroup(player.Guid) is not null)
        {
            _pendingPetName.Add(player.Guid);
        }
    }

    public void MarkPetAuraChanged(Player player, byte slot)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (GetGroup(player.Guid) is null || player.GetPet() is null || slot >= SpellSystem.MaxAuras) return;
        _pendingPetAuras.TryGetValue(player.Guid, out (uint Positive, ushort Negative) masks);
        if (slot < SpellSystem.MaxPositiveAuras) masks.Positive |= 1u << slot;
        else masks.Negative |= (ushort)(1u << (slot - SpellSystem.MaxPositiveAuras));
        _pendingPetAuras[player.Guid] = masks;
    }

    // --- invites -------------------------------------------------------------------------------

    /// <summary>
    /// CMSG_GROUP_INVITE (vmangos HandleGroupInviteOpcode): target online; same faction unless
    /// the inviter is a GM or two-side groups are allowed; target not ignoring the inviter;
    /// target not grouped or invited; inviter leads or assists a group that is not full.
    /// </summary>
    public void Invite(Player inviter, string name)
    {
        Player? target = name.Length == 0 ? null : context.World.FindOnlinePlayer(name);
        if (target is null)
        {
            SendResult(inviter, PartyOperation.Invite, name, PartyResult.BadPlayerName);
            return;
        }

        if (!inviter.IsGameMaster && !context.Options.AllowTwoSideGroup && inviter.Team != target.Team)
        {
            SendResult(inviter, PartyOperation.Invite, name, PartyResult.WrongFaction);
            return;
        }

        if (context.IsIgnoring(target, inviter.Guid))
        {
            SendResult(inviter, PartyOperation.Invite, name, PartyResult.IgnoringYou);
            return;
        }

        if (GetGroup(target.Guid) is not null || GetInvite(target.Guid) is not null)
        {
            SendResult(inviter, PartyOperation.Invite, name, PartyResult.AlreadyInGroup);
            return;
        }

        Group? group = GetGroup(inviter.Guid);
        if (group is not null)
        {
            if (!group.IsLeaderOrAssistant(inviter.Guid))
            {
                SendResult(inviter, PartyOperation.Invite, string.Empty, PartyResult.NotLeader);
                return;
            }

            if (group.IsFull)
            {
                SendResult(inviter, PartyOperation.Invite, string.Empty, PartyResult.GroupFull);
                return;
            }
        }

        if (group is null)
        {
            // vmangos: a new, not yet created group led by the inviter (AddLeaderInvite), who
            // is itself an invitee until the first accept. AddLeaderInvite fails — silently —
            // while the inviter has a pending invite, so a second invite before the first
            // accept is dropped exactly as vmangos drops it.
            if (GetInvite(inviter.Guid) is not null)
            {
                return;
            }

            group = new Group(_nextId++) { LeaderGuid = inviter.Guid, LeaderName = inviter.Name };
            AddInvite(group, inviter.Guid);
        }

        AddInvite(group, target.Guid);
        target.Session.Send(WorldOpcode.SmsgGroupInvite, GroupPackets.BuildName(inviter.Name));
        SendResult(inviter, PartyOperation.Invite, name, PartyResult.Ok);
    }

    /// <summary>
    /// CMSG_GROUP_ACCEPT (vmangos HandleGroupAcceptOpcode): drop the invite, refuse a full
    /// group, create the group on the first accept (leader first, Group::Create), then add.
    /// </summary>
    public void Accept(Player player)
    {
        if (GetInvite(player.Guid) is not { } group || group.IsLeader(player.Guid))
        {
            return;
        }

        RemoveInvite(group, player.Guid);
        if (group.IsFull)
        {
            SendResult(player, PartyOperation.Invite, string.Empty, PartyResult.GroupFull);
            return;
        }

        if (!group.IsCreated)
        {
            RemoveInvite(group, group.LeaderGuid);
            group.IsCreated = true;
            group.LooterGuid = group.LeaderGuid;
            if (!AddMember(group, group.LeaderGuid, group.LeaderName))
            {
                return;
            }

            if (context.World.FindOnlinePlayer(group.LeaderGuid) is { } leader)
            {
                UpdateLeaderFlag(leader);
            }
        }

        AddMember(group, player.Guid, player.Name);
    }

    /// <summary>CMSG_GROUP_DECLINE (vmangos HandleGroupDeclineOpcode): uninvite, then SMSG_GROUP_DECLINE to the leader.</summary>
    public void Decline(Player player)
    {
        if (GetInvite(player.Guid) is not { } group)
        {
            return;
        }

        Player? leader = context.World.FindOnlinePlayer(group.LeaderGuid);
        UninviteFromGroup(player.Guid);
        leader?.Session.Send(WorldOpcode.SmsgGroupDecline, GroupPackets.BuildName(player.Name));
    }

    /// <summary>
    /// vmangos Player::UninviteFromGroup: drop a pending invite; a group left with at most one
    /// member is disbanded (created) or loses all invites (not created).
    /// </summary>
    public void UninviteFromGroup(ObjectGuid guid)
    {
        if (GetInvite(guid) is not { } group)
        {
            return;
        }

        RemoveInvite(group, guid);
        if (group.MemberCount <= 1)
        {
            if (group.IsCreated)
            {
                Disband(group, hideDestroy: true);
            }
            else
            {
                foreach (ObjectGuid invitee in group.Invitees.ToArray())
                {
                    RemoveInvite(group, invitee);
                }
            }
        }
    }

    /// <summary>
    /// A character was deleted (vmangos Player::DeleteFromDB → Player::RemoveFromGroup): its
    /// pending invite goes and it leaves its group (offline members keep their membership until
    /// then). A two-member group disbands. Returns whether it was a member.
    /// </summary>
    public bool OnCharacterDeleted(ObjectGuid guid)
    {
        UninviteFromGroup(guid);
        if (GetGroup(guid) is not { } group)
        {
            return false;
        }

        RemoveFromGroup(group, guid, kicked: false);
        return true;
    }

    // --- removal and leadership -----------------------------------------------------------------

    /// <summary>
    /// CMSG_GROUP_UNINVITE (vmangos HandleGroupUninviteOpcode): a member by name (needs
    /// leader/assistant, never the leader), else an invitee, else TARGET_NOT_IN_GROUP.
    /// </summary>
    public void UninviteByName(Player player, string name)
    {
        if (name.Length == 0 || name == player.Name || GetGroup(player.Guid) is not { } group)
        {
            return;
        }

        if (group.FindByName(name) is { } slot)
        {
            PartyResult res = CanUninvite(player, slot.Guid);
            if (res != PartyResult.Ok)
            {
                SendResult(player, PartyOperation.Leave, string.Empty, res);
                return;
            }

            RemoveFromGroup(group, slot.Guid, kicked: true);
            return;
        }

        Player? invited = context.World.FindOnlinePlayer(name);
        if (invited is not null && group.IsInvited(invited.Guid))
        {
            PartyResult res = CanUninvite(player, invited.Guid);
            if (res != PartyResult.Ok)
            {
                SendResult(player, PartyOperation.Leave, string.Empty, res);
                return;
            }

            UninviteFromGroup(invited.Guid);
            return;
        }

        SendResult(player, PartyOperation.Leave, name, PartyResult.TargetNotInGroup);
    }

    /// <summary>CMSG_GROUP_UNINVITE_GUID (vmangos HandleGroupUninviteGuidOpcode).</summary>
    public void UninviteByGuid(Player player, ObjectGuid guid)
    {
        if (guid == player.Guid)
        {
            return;
        }

        PartyResult res = CanUninvite(player, guid);
        if (res != PartyResult.Ok)
        {
            SendResult(player, PartyOperation.Leave, string.Empty, res);
            return;
        }

        Group group = GetGroup(player.Guid)!;
        if (group.IsMember(guid))
        {
            RemoveFromGroup(group, guid, kicked: true);
        }
        else if (group.IsInvited(guid))
        {
            UninviteFromGroup(guid);
        }
        else
        {
            SendResult(player, PartyOperation.Leave, string.Empty, PartyResult.TargetNotInGroup);
        }
    }

    /// <summary>CMSG_GROUP_DISBAND = leave (vmangos HandleGroupDisbandOpcode): PARTY_OP_LEAVE result with the own name, then remove.</summary>
    public void Leave(Player player)
    {
        if (GetGroup(player.Guid) is not { } group)
        {
            return;
        }

        SendResult(player, PartyOperation.Leave, player.Name, PartyResult.Ok);
        RemoveFromGroup(group, player.Guid, kicked: false);
    }

    /// <summary>CMSG_GROUP_SET_LEADER (vmangos HandleGroupSetLeaderOpcode → Group::ChangeLeader).</summary>
    public void SetLeader(Player player, ObjectGuid target)
    {
        if (GetGroup(player.Guid) is not { } group || target == player.Guid || !group.IsLeader(player.Guid)
            || context.World.FindOnlinePlayer(target) is null || group.Find(target) is not { } slot)
        {
            return;
        }

        ChangeLeader(group, slot);
        Broadcast(group, WorldOpcode.SmsgGroupSetLeader, GroupPackets.BuildName(slot.Name));
        SendUpdate(group);
    }

    /// <summary>CMSG_GROUP_ASSISTANT_LEADER (vmangos HandleGroupAssistantLeaderOpcode → Group::SetAssistant, raids only).</summary>
    public void SetAssistant(Player player, ObjectGuid target, bool assistant)
    {
        if (GetGroup(player.Guid) is not { IsRaid: true } group || target == player.Guid || !group.IsLeader(player.Guid)
            || context.World.FindOnlinePlayer(target) is null || group.Find(target) is not { } slot)
        {
            return;
        }

        slot.Assistant = assistant;
        SendUpdate(group);
    }

    /// <summary>
    /// CMSG_LOOT_METHOD (vmangos HandleLootMethodOpcode): leader only, method ≤ need-before-
    /// greed; master loot needs a member as looter, other methods clear it.
    /// </summary>
    public void SetLootMethod(Player player, uint method, ObjectGuid master, uint threshold)
    {
        if (method > (uint)LootMethod.NeedBeforeGreed || GetGroup(player.Guid) is not { } group || !group.IsLeader(player.Guid))
        {
            return;
        }

        if ((LootMethod)method == LootMethod.MasterLoot)
        {
            if (!group.IsMember(master))
            {
                return;
            }
        }
        else
        {
            master = ObjectGuid.Empty;
        }

        group.LootMethod = (LootMethod)method;
        group.LooterGuid = master;
        group.LootThreshold = (byte)Math.Min(threshold, byte.MaxValue);
        SendUpdate(group);
    }

    // --- raids ----------------------------------------------------------------------------------

    /// <summary>CMSG_GROUP_RAID_CONVERT (vmangos HandleGroupRaidConvertOpcode): leader, at least two members.</summary>
    public void ConvertToRaid(Player player)
    {
        if (GetGroup(player.Guid) is not { } group || group.IsRaid || !group.IsLeader(player.Guid)
            || group.MemberCount < Group.MinMemberCount)
        {
            return;
        }

        SendResult(player, PartyOperation.Invite, string.Empty, PartyResult.Ok);
        group.ConvertToRaid();
        SendUpdate(group);
    }

    /// <summary>CMSG_GROUP_CHANGE_SUB_GROUP (vmangos HandleGroupChangeSubGroupOpcode → ChangeMembersGroup).</summary>
    public void ChangeSubGroup(Player player, string name, byte subGroup)
    {
        if (subGroup >= Group.MaxRaidSubGroups || GetGroup(player.Guid) is not { IsRaid: true } group
            || !group.IsLeaderOrAssistant(player.Guid) || !group.HasFreeSlotSubGroup(subGroup)
            || group.FindByName(name) is not { } slot || slot.SubGroup == subGroup)
        {
            return;
        }

        group.MoveToSubGroup(slot, subGroup);
        SendUpdate(group);
    }

    /// <summary>CMSG_GROUP_SWAP_SUB_GROUP (vmangos HandleGroupSwapSubGroupOpcode → SwapMembersGroup).</summary>
    public void SwapSubGroup(Player player, string name, string otherName)
    {
        if (GetGroup(player.Guid) is not { IsRaid: true } group || !group.IsLeaderOrAssistant(player.Guid)
            || group.FindByName(name) is not { } a || group.FindByName(otherName) is not { } b || a.SubGroup == b.SubGroup)
        {
            return;
        }

        byte first = a.SubGroup;
        group.MoveToSubGroup(a, b.SubGroup);
        group.MoveToSubGroup(b, first);
        SendUpdate(group);
    }

    /// <summary>
    /// MSG_RAID_READY_CHECK (vmangos HandleRaidReadyCheckOpcode): an empty request from the
    /// leader or an assistant goes to the whole group; an answer (u8) goes to the leader as
    /// u64 member + u8 state.
    /// </summary>
    public void ReadyCheck(Player player, byte? answer)
    {
        if (GetGroup(player.Guid) is not { } group)
        {
            return;
        }

        if (answer is null)
        {
            if (group.IsLeaderOrAssistant(player.Guid))
            {
                Broadcast(group, WorldOpcode.MsgRaidReadyCheck, []);
            }
        }
        else if (context.World.FindOnlinePlayer(group.LeaderGuid) is { } leader)
        {
            leader.Session.Send(WorldOpcode.MsgRaidReadyCheck, GroupPackets.BuildReadyCheckResponse(player.Guid, answer.Value));
        }
    }

    /// <summary>
    /// MSG_RAID_TARGET_UPDATE (vmangos HandleRaidTargetUpdateOpcode / Group::SetTargetIcon):
    /// icon 0xFF requests the list; setting needs leader or assistant and moves an icon off
    /// any other target first.
    /// </summary>
    public void TargetIcon(Player player, byte icon, ObjectGuid target)
    {
        if (GetGroup(player.Guid) is not { } group)
        {
            return;
        }

        if (icon == 0xFF)
        {
            player.Session.Send(WorldOpcode.MsgRaidTargetUpdate, GroupPackets.BuildTargetIconList(group.TargetIcons));
            return;
        }

        if (!group.IsLeaderOrAssistant(player.Guid))
        {
            return;
        }

        SetTargetIcon(group, icon, target);
    }

    /// <summary>MSG_MINIMAP_PING (vmangos HandleMinimapPingOpcode): to the group, not the pinger.</summary>
    public void MinimapPing(Player player, float x, float y)
    {
        if (GetGroup(player.Guid) is { } group)
        {
            Broadcast(group, WorldOpcode.MsgMinimapPing, GroupPackets.BuildMinimapPing(player.Guid, x, y), ignore: player.Guid);
        }
    }

    /// <summary>MSG_RANDOM_ROLL (vmangos HandleRandomRollOpcode): min ≤ max ≤ 1,000,000; to the group or, alone, to self.</summary>
    public void RandomRoll(Player player, uint min, uint max)
    {
        if (min > max || max > MaxRoll)
        {
            return;
        }

        uint roll = (uint)Random.Shared.NextInt64(min, (long)max + 1);
        byte[] packet = GroupPackets.BuildRandomRoll(min, max, roll, player.Guid);
        if (GetGroup(player.Guid) is { } group)
        {
            Broadcast(group, WorldOpcode.MsgRandomRoll, packet);
        }
        else
        {
            player.Session.Send(WorldOpcode.MsgRandomRoll, packet);
        }
    }

    /// <summary>
    /// CMSG_REQUEST_PARTY_MEMBER_STATS (vmangos HandleRequestPartyMemberStatsOpcode): the full
    /// stats of an online group mate, otherwise "offline".
    /// </summary>
    public void RequestMemberStats(Player player, ObjectGuid guid)
    {
        Player? member = context.World.FindOnlinePlayer(guid);
        byte[] packet = member is not null && AreInSameGroup(player.Guid, guid)
            ? GroupPackets.BuildPartyMemberStats(member, GroupUpdateFlags.Full)
            : GroupPackets.BuildPartyMemberStatsOffline(guid);
        player.Session.Send(WorldOpcode.SmsgPartyMemberStatsFull, packet);
    }

    // --- chat -----------------------------------------------------------------------------------

    /// <summary>
    /// Deliver a prebuilt group chat line (vmangos HandleChatMessageOpcode): party chat goes to
    /// the speaker's subgroup; raid chat needs a raid; raid leader chat needs the leader; raid
    /// warnings need the leader or an assistant. False when the speaker may not use the channel.
    /// </summary>
    public bool BroadcastChat(Player player, ChatType type, ReadOnlySpan<byte> packet)
    {
        if (GetGroup(player.Guid) is not { } group)
        {
            return false;
        }

        switch (type)
        {
            case ChatType.Party:
                Broadcast(group, WorldOpcode.SmsgMessagechat, packet, subGroup: group.Find(player.Guid)!.SubGroup);
                return true;
            case ChatType.Raid when group.IsRaid:
            case ChatType.RaidLeader when group.IsRaid && group.IsLeader(player.Guid):
            case ChatType.RaidWarning when group.IsRaid && group.IsLeaderOrAssistant(player.Guid):
                Broadcast(group, WorldOpcode.SmsgMessagechat, packet);
                return true;
            default:
                return false;
        }
    }

    // --- world events ---------------------------------------------------------------------------

    /// <summary>
    /// A member logged in (vmangos HandlePlayerLogin → Group::UpdatePlayerOnlineStatus): the
    /// leader flag, a fresh group list for everyone and the member's stats for those out of range.
    /// </summary>
    public void OnLoggedIn(Player player)
    {
        UpdateLeaderFlag(player);
        if (GetGroup(player.Guid) is not { } group)
        {
            return;
        }

        SendUpdate(group);
        _sentStats.Remove(player.Guid);
        SendStatsOutOfRange(group, player, GroupUpdateFlags.Full);
        _sentStats[player.Guid] = MemberStats.Of(player);
    }

    /// <summary>
    /// A member is leaving the world (vmangos WorldSession::LogoutPlayer): pending invites are
    /// dropped (UninviteFromGroup) and the group sees the member offline. Membership stays.
    /// </summary>
    public void OnLoggingOut(Player player)
    {
        UninviteFromGroup(player.Guid);
        _pendingPetName.Remove(player.Guid);
        _pendingPetAuras.Remove(player.Guid);
        _sentStats.Remove(player.Guid);
        if (GetGroup(player.Guid) is { } group)
        {
            SendUpdate(group, offline: player.Guid);
        }
    }

    /// <summary>
    /// Send changed stats of every grouped online player to group mates that cannot see it
    /// (vmangos Player::SendUpdateToOutOfRangeGroupMembers → Group::UpdatePlayerOutOfRange).
    /// The world feature calls this about once a second.
    /// </summary>
    public void UpdateOutOfRangeStats()
    {
        foreach ((ObjectGuid guid, Group group) in _memberOf)
        {
            if (context.World.FindOnlinePlayer(guid) is not { } player)
            {
                continue;
            }

            MemberStats now = MemberStats.Of(player);
            GroupUpdateFlags changed = _sentStats.TryGetValue(guid, out MemberStats before) ? now.Diff(before) : GroupUpdateFlags.Full;
            _sentStats[guid] = now;
            if (_pendingPetName.Remove(guid))
            {
                changed |= GroupUpdateFlags.PetName;
            }
            uint petPositive = 0;
            ushort petNegative = 0;
            if (_pendingPetAuras.Remove(guid, out (uint Positive, ushort Negative) auraMasks))
            {
                petPositive = auraMasks.Positive;
                petNegative = auraMasks.Negative;
                if (petPositive != 0) changed |= GroupUpdateFlags.PetAuras;
                if (petNegative != 0) changed |= GroupUpdateFlags.PetAurasNegative;
            }
            if ((changed & GroupUpdateFlags.PetGuid) != 0)
            {
                // Pending slots may belong to the former pet. Zero selects the
                // new current pet's complete live baseline in the packet builder.
                petPositive = 0;
                petNegative = 0;
                changed |= GroupUpdateFlags.PetAuras | GroupUpdateFlags.PetAurasNegative;
            }
            if (changed != GroupUpdateFlags.None)
            {
                SendStatsOutOfRange(group, player, changed, petPositive, petNegative);
            }
        }
    }

    /// <summary>Send a packet to the online members (vmangos Group::BroadcastPacket).</summary>
    public void Broadcast(Group group, WorldOpcode opcode, ReadOnlySpan<byte> payload, int subGroup = -1, ObjectGuid ignore = default)
    {
        foreach (GroupMemberSlot member in group.Members)
        {
            if (member.Guid == ignore || (subGroup >= 0 && member.SubGroup != subGroup))
            {
                continue;
            }

            context.World.FindOnlinePlayer(member.Guid)?.Session.Send(opcode, payload);
        }
    }

    /// <summary>
    /// SMSG_GROUP_LIST to every online member (vmangos Group::SendUpdate); outside raids the
    /// target icons are resent because a group list clears them on the client.
    /// </summary>
    public void SendUpdate(Group group, ObjectGuid offline = default)
    {
        byte[]? icons = !group.IsRaid && group.TargetIcons.Any(i => !i.IsEmpty)
            ? GroupPackets.BuildTargetIconList(group.TargetIcons)
            : null;
        GroupMemberStatus StatusOf(ObjectGuid guid)
            => guid == offline ? GroupMemberStatus.Offline : GroupPackets.StatusOf(context.World.FindOnlinePlayer(guid));
        foreach (GroupMemberSlot member in group.Members)
        {
            if (member.Guid == offline || context.World.FindOnlinePlayer(member.Guid) is not { } player)
            {
                continue;
            }

            player.Session.Send(WorldOpcode.SmsgGroupList, GroupPackets.BuildGroupList(group, member, StatusOf));
            if (icons is not null)
            {
                player.Session.Send(WorldOpcode.MsgRaidTargetUpdate, icons);
            }
        }
    }

    // --- internals ------------------------------------------------------------------------------

    /// <summary>vmangos Player::CanUninviteFromGroup.</summary>
    private PartyResult CanUninvite(Player player, ObjectGuid target)
    {
        if (GetGroup(player.Guid) is not { } group)
        {
            return PartyResult.NotInGroup;
        }

        if (group.LeaderGuid == target || !group.IsLeaderOrAssistant(player.Guid))
        {
            return PartyResult.NotLeader;
        }

        return PartyResult.Ok;
    }

    /// <summary>vmangos Group::AddMember: slot, group list update, the leader flag.</summary>
    private bool AddMember(Group group, ObjectGuid guid, string name)
    {
        if (!group.AddMemberSlot(guid, name))
        {
            return false;
        }

        _memberOf[guid] = group;
        SendUpdate(group);
        if (context.World.FindOnlinePlayer(guid) is { } player)
        {
            UpdateLeaderFlag(player);

            // vmangos: SetGroupUpdateFlag(GROUP_UPDATE_FULL) — the new member's stats go out.
            _sentStats.Remove(guid);
        }

        MemberAdded?.Invoke(group, guid);
        return true;
    }

    /// <summary>
    /// vmangos Player::RemoveFromGroup → Group::RemoveMember: above the minimum size the member
    /// leaves (a kick sends SMSG_GROUP_UNINVITE; the leaver gets an empty group list; a new
    /// leader is announced); at the minimum size the group is disbanded silently.
    /// </summary>
    private void RemoveFromGroup(Group group, ObjectGuid guid, bool kicked)
    {
        if (group.MemberCount <= Group.MinMemberCount)
        {
            Disband(group, hideDestroy: true, initiator: guid);
            return;
        }

        GroupMemberSlot slot = group.Find(guid)!;
        group.RemoveMemberSlot(slot);
        _memberOf.Remove(guid);
        _pendingPetName.Remove(guid);
        _pendingPetAuras.Remove(guid);
        _sentStats.Remove(guid);
        bool leaderChanged = group.LeaderGuid == guid;
        if (leaderChanged)
        {
            ChooseLeader(group);
        }

        if (context.World.FindOnlinePlayer(guid) is { } player)
        {
            if (kicked)
            {
                player.Session.Send(WorldOpcode.SmsgGroupUninvite, []);
            }

            player.Session.Send(WorldOpcode.SmsgGroupList, GroupPackets.BuildEmptyGroupList());
            UpdateLeaderFlag(player);
        }

        if (leaderChanged)
        {
            Broadcast(group, WorldOpcode.SmsgGroupSetLeader, GroupPackets.BuildName(group.LeaderName));
        }

        SendUpdate(group);
        MemberRemoved?.Invoke(group, guid);
    }

    /// <summary>vmangos Group::Disband: SMSG_GROUP_DESTROYED (unless hidden) and an empty group list for each online member.</summary>
    private void Disband(Group group, bool hideDestroy, ObjectGuid initiator = default)
    {
        Disbanding?.Invoke(group, initiator);
        var members = group.Members.Select(m => m.Guid).ToArray();
        foreach (ObjectGuid invitee in group.Invitees.ToArray())
        {
            RemoveInvite(group, invitee);
        }

        group.Clear();
        foreach (ObjectGuid guid in members)
        {
            _memberOf.Remove(guid);
            _pendingPetName.Remove(guid);
            _pendingPetAuras.Remove(guid);
            _sentStats.Remove(guid);
            if (context.World.FindOnlinePlayer(guid) is { } player)
            {
                if (!hideDestroy)
                {
                    player.Session.Send(WorldOpcode.SmsgGroupDestroyed, []);
                }

                player.Session.Send(WorldOpcode.SmsgGroupList, GroupPackets.BuildEmptyGroupList());
                UpdateLeaderFlag(player);
            }
        }
    }

    /// <summary>vmangos Group::_chooseLeader: an online member, in raids an assistant first; otherwise the first member.</summary>
    private void ChooseLeader(Group group)
    {
        GroupMemberSlot? first = null;
        GroupMemberSlot? chosen = null;
        foreach (GroupMemberSlot member in group.Members)
        {
            if (context.World.FindOnlinePlayer(member.Guid) is null)
            {
                continue;
            }

            if (group.IsRaid && !member.Assistant)
            {
                first ??= member;
                continue;
            }

            chosen = member;
            break;
        }

        chosen ??= first ?? group.Members.FirstOrDefault();
        if (chosen is not null)
        {
            ChangeLeader(group, chosen);
        }
    }

    /// <summary>vmangos Group::_setLeader: move the leader flag.</summary>
    private void ChangeLeader(Group group, GroupMemberSlot slot)
    {
        ObjectGuid old = group.LeaderGuid;
        group.LeaderGuid = slot.Guid;
        group.LeaderName = slot.Name;
        if (context.World.FindOnlinePlayer(old) is { } oldLeader)
        {
            UpdateLeaderFlag(oldLeader);
        }

        if (context.World.FindOnlinePlayer(slot.Guid) is { } newLeader)
        {
            UpdateLeaderFlag(newLeader);
        }

        if (old != slot.Guid)
        {
            LeaderChanged?.Invoke(group, old);
        }
    }

    /// <summary>vmangos Group::SetTargetIcon.</summary>
    private void SetTargetIcon(Group group, byte icon, ObjectGuid target)
    {
        if (icon >= Group.TargetIconCount)
        {
            return;
        }

        if (!target.IsEmpty)
        {
            for (byte i = 0; i < Group.TargetIconCount; i++)
            {
                if (group.TargetIcons[i] == target)
                {
                    SetTargetIcon(group, i, ObjectGuid.Empty);
                }
            }
        }

        group.TargetIcons[icon] = target;
        Broadcast(group, WorldOpcode.MsgRaidTargetUpdate, GroupPackets.BuildTargetIconDelta(icon, target));
    }

    /// <summary>vmangos Player::UpdateGroupLeaderFlag: PLAYER_FLAGS_GROUP_LEADER while leading a (created) group.</summary>
    private void UpdateLeaderFlag(Player player)
    {
        bool leads = GetGroup(player.Guid) is { } group && group.IsLeader(player.Guid);
        PlayerFlags flags = player.Flags;
        if (leads != ((flags & PlayerFlags.GroupLeader) != 0))
        {
            player.Flags = leads ? flags | PlayerFlags.GroupLeader : flags & ~PlayerFlags.GroupLeader;
        }
    }

    private void SendStatsOutOfRange(Group group, Player player, GroupUpdateFlags mask, uint petPositive = 0, ushort petNegative = 0)
    {
        if ((mask & GroupUpdateFlags.PowerType) != 0)
        {
            mask |= GroupUpdateFlags.CurrentPower | GroupUpdateFlags.MaxPower; // vmangos BuildPartyMemberStatsChangedPacket
        }

        byte[]? packet = null;
        foreach (GroupMemberSlot member in group.Members)
        {
            if (member.Guid == player.Guid || context.World.FindOnlinePlayer(member.Guid) is not { } mate
                || mate.VisibleObjects.Contains(player.Guid))
            {
                continue;
            }

            packet ??= GroupPackets.BuildPartyMemberStats(player, mask, petPositive, petNegative);
            mate.Session.Send(WorldOpcode.SmsgPartyMemberStats, packet);
        }
    }

    private void AddInvite(Group group, ObjectGuid guid)
    {
        group.AddInvite(guid);
        _invitedTo[guid] = group;
    }

    private void RemoveInvite(Group group, ObjectGuid guid)
    {
        group.RemoveInvite(guid);
        if (_invitedTo.GetValueOrDefault(guid) == group)
        {
            _invitedTo.Remove(guid);
        }
    }

    private static void SendResult(Player player, PartyOperation operation, string name, PartyResult result)
        => player.Session.Send(WorldOpcode.SmsgPartyCommandResult, GroupPackets.BuildPartyCommandResult(operation, name, result));

    private readonly record struct PetStats(ObjectGuid Guid, uint Model, uint Hp, uint MaxHp, PowerType Power, uint CurPower, uint MaxPower)
        {
            public static PetStats Of(Player p)
            {
                Creature? pet = p.GetPet();
                if (pet is null) return default;
                int index = (int)pet.PowerType <= 4 ? (int)pet.PowerType : 0;
                return new(pet.Guid, pet.GetUInt32(UpdateFields.UnitFieldDisplayid), pet.Health, pet.MaxHealth,
                    pet.PowerType, pet.GetUInt32(UpdateFields.UnitFieldPower1 + index), pet.GetUInt32(UpdateFields.UnitFieldMaxpower1 + index));
            }
        }

    /// <summary>The stats last sent for a member, to find what changed (vmangos m_groupUpdateMask).</summary>
    private readonly record struct MemberStats(
        GroupMemberStatus Status, uint Hp, uint MaxHp, PowerType Power, uint CurPower, uint MaxPower, byte Level, uint Zone, short X, short Y, PetStats Pet)
    {
        public static MemberStats Of(Player p)
        {
            int index = (int)p.PowerType <= 4 ? (int)p.PowerType : 0;
            return new(GroupPackets.StatusOf(p), p.Health, p.MaxHealth, p.PowerType,
                p.GetUInt32(UpdateFields.UnitFieldPower1 + index), p.GetUInt32(UpdateFields.UnitFieldMaxpower1 + index),
                p.Level, p.ZoneId, (short)p.X, (short)p.Y, PetStats.Of(p));
        }

        public GroupUpdateFlags Diff(MemberStats o)
        {
            GroupUpdateFlags f = GroupUpdateFlags.None;
            if (Status != o.Status)
            {
                f |= GroupUpdateFlags.Status;
            }

            if (Hp != o.Hp)
            {
                f |= GroupUpdateFlags.CurrentHp;
            }

            if (MaxHp != o.MaxHp)
            {
                f |= GroupUpdateFlags.MaxHp;
            }

            if (Power != o.Power)
            {
                f |= GroupUpdateFlags.PowerType;
            }

            if (CurPower != o.CurPower)
            {
                f |= GroupUpdateFlags.CurrentPower;
            }

            if (MaxPower != o.MaxPower)
            {
                f |= GroupUpdateFlags.MaxPower;
            }

            if (Level != o.Level)
            {
                f |= GroupUpdateFlags.Level;
            }

            if (Zone != o.Zone)
            {
                f |= GroupUpdateFlags.Zone;
            }

            if (X != o.X || Y != o.Y)
            {
                f |= GroupUpdateFlags.Position;
            }

            if (Pet.Guid != o.Pet.Guid)
            {
                f |= GroupUpdateFlags.PetGuid | GroupUpdateFlags.PetName | GroupUpdateFlags.PetModelId
                    | GroupUpdateFlags.PetCurrentHp | GroupUpdateFlags.PetMaxHp | GroupUpdateFlags.PetPowerType
                    | GroupUpdateFlags.PetCurrentPower | GroupUpdateFlags.PetMaxPower
                    | GroupUpdateFlags.PetAuras | GroupUpdateFlags.PetAurasNegative;
            }
            else
            {
                if (Pet.Model != o.Pet.Model) f |= GroupUpdateFlags.PetModelId;
                if (Pet.Hp != o.Pet.Hp) f |= GroupUpdateFlags.PetCurrentHp;
                if (Pet.MaxHp != o.Pet.MaxHp) f |= GroupUpdateFlags.PetMaxHp;
                if (Pet.Power != o.Pet.Power)
                    f |= GroupUpdateFlags.PetPowerType | GroupUpdateFlags.PetCurrentPower | GroupUpdateFlags.PetMaxPower;
                else
                {
                    if (Pet.CurPower != o.Pet.CurPower) f |= GroupUpdateFlags.PetCurrentPower;
                    if (Pet.MaxPower != o.Pet.MaxPower) f |= GroupUpdateFlags.PetMaxPower;
                }
            }

            return f;
        }
    }
}
