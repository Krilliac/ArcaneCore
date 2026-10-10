using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Battlegrounds;

/// <summary>
/// The battleground opcodes (vmangos Handlers/BattleGroundHandler.cpp): the battlemaster hello and join, the battlefield list and port, leaving
/// a battlefield, the scoreboard and the map positions. CMSG_BATTLEFIELD_STATUS stays registered by <see cref="InactiveQueueHandlers"/>, which
/// answers through <see cref="BattlegroundFeature.SendStatusReports"/>. The portal join (CMSG_BATTLEFIELD_JOIN) and the area spirit healer
/// opcodes are not registered: vmangos' portal join passes an empty battlemaster and is refused, and there is no spirit healer wave here.
/// </summary>
public sealed class BattlegroundHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgBattlemasterHello, BattlemasterHello);
        table.OnWorld(WorldOpcode.CmsgBattlemasterJoin, BattlemasterJoin);
        table.OnWorld(WorldOpcode.CmsgBattlefieldList, BattlefieldList);
        table.OnWorld(WorldOpcode.CmsgBattlefieldPort, BattlefieldPort);
        table.OnWorld(WorldOpcode.CmsgLeaveBattlefield, LeaveBattlefield);
        table.OnWorld(WorldOpcode.MsgPvpLogData, PvpLogData);
        table.OnWorld(WorldOpcode.MsgBattlegroundPlayerPositions, PlayerPositions);
    }

    private static BattlegroundFeature? Feature(WorldSession session) => session.Services.GetService<BattlegroundFeature>();

    /// <summary>vmangos HandleBattlemasterHelloOpcode (BattleGroundHandler.cpp:40-73): the list of the battlemaster's battleground.</summary>
    private static void BattlemasterHello(WorldSession session, Player player, byte[] payload)
    {
        if (!BattlegroundPackets.TryParseGuid(payload, out ObjectGuid guid) || Feature(session) is not { } feature
            || session.Services.GetService<QuestNpcFeature>() is not { } npcs
            || npcs.Services.InteractableNpc(player, guid, NpcFlags.BattleMaster) is not { } npc
            || !feature.Battlemasters.TryGetValue(npc.Entry, out BattlegroundType type))
        {
            return;
        }

        if (feature.Manager.TemplateOf(type) is not { } template || player.Level < template.MinLevel)
        {
            session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(BattlegroundStrings.Mangos(715)!));
            return;
        }

        feature.SendBattlefieldList(player, guid, type);
    }

    /// <summary>vmangos HandleBattlefieldListOpcode (BattleGroundHandler.cpp:343-359): the list of the map's battleground, the player as source.</summary>
    private static void BattlefieldList(WorldSession session, Player player, byte[] payload)
    {
        if (!BattlegroundPackets.TryParseMap(payload, out uint mapId) || Feature(session) is not { } feature)
        {
            return;
        }

        BattlegroundType type = BattlegroundManager.TypeOfMap(mapId);
        if (type != BattlegroundType.None)
        {
            feature.SendBattlefieldList(player, player.Guid, type);
        }
    }

    /// <summary>
    /// vmangos HandleBattlemasterJoinOpcode → RequestBgJoinQueue (BattleGroundHandler.cpp:83-264): a battlemaster in reach (or the player itself
    /// at a portal, within 50 yards of its entry point), then the queue with the player's or its group's facts.
    /// </summary>
    private static void BattlemasterJoin(WorldSession session, Player player, byte[] payload)
    {
        if (!BattlegroundPackets.TryParseBattlemasterJoin(payload, out BattlemasterJoin join) || Feature(session) is not { } feature)
        {
            return;
        }

        BattlegroundType type = BattlegroundManager.TypeOfMap(join.MapId);
        if (type == BattlegroundType.None)
        {
            return;
        }

        bool atPortal = join.Guid == player.Guid;
        if (atPortal)
        {
            if (feature.EntryPointOf(player.Guid) is not { } point || point.MapId != player.MapId
                || Distance3d(player, point.X, point.Y, point.Z) > 50f)
            {
                return;
            }
        }
        else if (session.Services.GetService<QuestNpcFeature>()?.Services.InteractableNpc(player, join.Guid, NpcFlags.BattleMaster) is null)
        {
            return;
        }

        Group? group = join.JoinAsGroup ? session.Services.GetService<SocialFeature>()?.Context.Groups.GetGroup(player.Guid) : null;
        IReadOnlyList<BattlegroundMember> members = group is null ? [] : feature.MembersOf(group);
        bool asGroup = join.JoinAsGroup && group is not null;
        feature.Manager.JoinQueue(new BattlegroundJoinRequest(feature.MemberOf(player), type, join.InstanceId, asGroup, atPortal, members));
    }

    /// <summary>vmangos HandleBattleFieldPortOpcode (BattleGroundHandler.cpp:361-507): enter the invited match, or leave the queue.</summary>
    private static void BattlefieldPort(WorldSession session, Player player, byte[] payload)
    {
        if (!BattlegroundPackets.TryParseBattlefieldPort(payload, out BattlefieldPort port) || Feature(session) is not { } feature)
        {
            return;
        }

        feature.Manager.HandlePort(new BattlegroundPortRequest(player.Guid, port.MapId, port.Action, player.Level, feature.IsDeserter(player)));
    }

    /// <summary>vmangos HandleLeaveBattlefieldOpcode (BattleGroundHandler.cpp:509-525): not while in combat in a running match.</summary>
    private static void LeaveBattlefield(WorldSession session, Player player, byte[] payload)
    {
        if (!BattlegroundPackets.TryParseMap(payload, out uint mapId) || Feature(session) is not { } feature || player.MapId != mapId)
        {
            return;
        }

        if (player.Combat.IsInCombat && feature.BattlegroundOf(player.Guid) is { Status: not BattlegroundStatus.WaitLeave })
        {
            return;
        }

        feature.LeaveBattleground(player, teleportToEntryPoint: true);
    }

    /// <summary>vmangos HandlePVPLogDataOpcode (BattleGroundHandler.cpp:328-341): the live board, or the frozen one once the match ended.</summary>
    private static void PvpLogData(WorldSession session, Player player, byte[] payload)
    {
        if (Feature(session)?.BattlegroundOf(player.Guid) is not { } bg)
        {
            return;
        }

        PvpLogSnapshot log = bg.Status == BattlegroundStatus.WaitLeave && bg.FinalScore is { } final ? final : bg.BuildPvpLog();
        session.Send(WorldOpcode.MsgPvpLogData, BattlegroundPackets.BuildPvpLogData(log));
    }

    /// <summary>
    /// vmangos HandleBattleGroundPlayerPositionsOpcode (BattleGroundHandler.cpp:266-326): the viewer's team (online participants) and the flag
    /// carrier the match shows that team. Every participant is in its team's battleground raid, so the team list is the raid.
    /// </summary>
    private static void PlayerPositions(WorldSession session, Player player, byte[] payload)
    {
        if (Feature(session) is not { } feature || feature.BattlegroundOf(player.Guid) is not { } bg || bg.PlayerTeam(player.Guid) is not { } team)
        {
            return;
        }

        List<(ObjectGuid Guid, float X, float Y)> teammates = [];
        foreach ((ObjectGuid guid, Team playerTeam) in bg.Participants())
        {
            if (playerTeam == team && feature.World.FindOnlinePlayer(guid) is { } mate)
            {
                teammates.Add((guid, mate.X, mate.Y));
            }
        }

        List<(ObjectGuid Guid, float X, float Y)> carriers = [];
        if (bg.FlagCarrierShownTo(team) is { IsEmpty: false } carrierGuid && feature.World.FindOnlinePlayer(carrierGuid) is { } carrier)
        {
            carriers.Add((carrierGuid, carrier.X, carrier.Y));
        }

        session.Send(WorldOpcode.MsgBattlegroundPlayerPositions, BattlegroundPackets.BuildPlayerPositions(teammates, carriers));
    }

    private static float Distance3d(Player player, float x, float y, float z)
    {
        float dx = player.X - x;
        float dy = player.Y - y;
        float dz = player.Z - z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}

public sealed partial class BattlegroundFeature
{
    /// <summary>The queue facts of a player (vmangos reads them from the Player in RequestBgJoinQueue).</summary>
    internal BattlegroundMember MemberOf(Player player)
        => new(player.Guid, player.Team, player.Level, player.IsInWorld, IsDeserter(player));

    /// <summary>The members of a group for a group join; an offline member is not in the world.</summary>
    internal IReadOnlyList<BattlegroundMember> MembersOf(Group group)
        => [.. group.Members.Select(m => World.FindOnlinePlayer(m.Guid) is { } online
            ? MemberOf(online)
            : new BattlegroundMember(m.Guid, Team.Alliance, 0, InWorld: false, Deserter: false))];

    /// <summary>vmangos <c>Player::CanJoinToBattleground</c>: the Deserter debuff (26013).</summary>
    internal bool IsDeserter(Player player)
        => Services.GetService<SpellFeature>()?.System.HasAura(player, BattlegroundConstants.SpellDeserter) == true;

    /// <summary>
    /// SMSG_BATTLEFIELD_LIST (vmangos <c>BuildBattleGroundListPacket</c>, BattleGroundMgr.cpp:1166-1213): the map, the player's bracket and
    /// the client instance ids of that bracket.
    /// </summary>
    internal void SendBattlefieldList(Player player, ObjectGuid source, BattlegroundType type)
    {
        if (Manager.TemplateOf(type) is not { } template)
        {
            return;
        }

        int bracket = BattlegroundConstants.BracketOfLevel(player.Level, template.MinLevel);
        IReadOnlyList<uint> ids = bracket < 0 ? [] : Manager.ClientInstanceIds(type, bracket);
        player.Session.Send(WorldOpcode.SmsgBattlefieldList,
            BattlegroundPackets.BuildBattlefieldList(source, template.MapId, (byte)Math.Max(bracket, 0), ids));
    }

    /// <summary>The answer to CMSG_BATTLEFIELD_STATUS (vmangos HandleBattlefieldStatusOpcode): one status per occupied queue slot.</summary>
    public void SendStatusReports(Player player) => _manager?.SendStatusReports(player.Guid, player.Level);
}
