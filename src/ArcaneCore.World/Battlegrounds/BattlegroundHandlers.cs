using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Battlegrounds;

/// <summary>
/// The battleground opcodes (vmangos Handlers/BattleGroundHandler.cpp): the battlemaster hello and join, the battlefield list and port, leaving
/// a battlefield, the scoreboard and the map positions. CMSG_BATTLEFIELD_STATUS stays registered by <see cref="InactiveQueueHandlers"/>, which
/// answers through <see cref="BattlegroundFeature.SendStatusReports"/>. The old portal join (CMSG_BATTLEFIELD_JOIN) is refused and logged:
/// vmangos passes an empty battlemaster, which no NPC check accepts on 1.12. The area spirit healer query and queue answer from the spirit guide's wave
/// (<see cref="MatchRuntime"/> keeps it channelling Spirit Heal Channel).
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
        table.OnWorld(WorldOpcode.CmsgBattlefieldJoin, BattlefieldJoin);
        table.OnWorld(WorldOpcode.CmsgAreaSpiritHealerQuery, AreaSpiritHealerQuery);
        table.OnWorld(WorldOpcode.CmsgAreaSpiritHealerQueue, AreaSpiritHealerQueue);
    }

    private static BattlegroundFeature? Feature(WorldSession session) => session.Services.GetService<BattlegroundFeature>();

    /// <summary>
    /// vmangos HandleBattlefieldJoinOpcode → RequestBgJoinQueue(ObjectGuid(), 0, mapId, false) (BattleGroundHandler.cpp:76-79, 88-125): with an
    /// empty battlemaster the 1.12 path fails GetNPCIfCanInteractWith and only reports a passive anticheat action ("invalid BG type" for an
    /// unknown map, else "invalid creature"). Nothing is queued; the attempt is logged.
    /// </summary>
    private static void BattlefieldJoin(WorldSession session, Player player, byte[] payload)
    {
        if (!BattlegroundPackets.TryParseMap(payload, out uint mapId))
        {
            return;
        }

        bool invalidType = BattlegroundManager.TypeOfMap(mapId) == BattlegroundType.None;
        string reason = invalidType ? "Attempt to queue for invalid BG type" : "Attempt to queue for BG through invalid creature";

        // Every packet is a passive anticheat event in the anomaly ledger (vmangos reports a passive action per packet); the warning line
        // is throttled per session so a client sending this at frame rate cannot flood the log.
        AnomalyLedger.Shared.Report("anticheat", "battlefield-join", $"character {player.Guid.Low}",
            $"CMSG_BATTLEFIELD_JOIN map {mapId}: {reason}");
        JoinLogState state = JoinLogs.GetValue(session, static _ => new JoinLogState());
        long now = Environment.TickCount64;
        if (state.Logged && now - state.LastMs < JoinLogWindowMs)
        {
            state.Suppressed++;
            return;
        }

        session.Logger.LogWarning("[{Endpoint}] PassiveAnticheat: {Reason} (CMSG_BATTLEFIELD_JOIN map {Map}) by {Player}; {Suppressed} more suppressed",
            session.RemoteEndpoint, reason, mapId, player.Name, state.Suppressed);
        state.Logged = true;
        state.LastMs = now;
        state.Suppressed = 0;
    }

    /// <summary>The window in which a session logs at most one CMSG_BATTLEFIELD_JOIN warning.</summary>
    internal const long JoinLogWindowMs = 60_000;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSession, JoinLogState> JoinLogs = new();

    private sealed class JoinLogState
    {
        public bool Logged { get; set; }
        public long LastMs { get; set; }
        public long Suppressed { get; set; }
    }

    /// <summary>
    /// The spirit service the area spirit healer opcodes name (vmangos BattleGroundHandler.cpp:579-612): the player is in a battleground and the
    /// guid is a creature on its map with a spirit healer or spirit guide flag (<c>IsSpiritService</c>).
    /// </summary>
    private static Creature? SpiritService(WorldSession session, Player player, byte[] payload)
        => BattlegroundPackets.TryParseGuid(payload, out ObjectGuid guid) && Feature(session)?.BattlegroundOf(player.Guid) is not null
            && player.Map?.FindObject(guid) is Creature unit && (unit.NpcFlags & (uint)(NpcFlags.SpiritHealer | NpcFlags.SpiritGuide)) != 0
                ? unit
                : null;

    /// <summary>
    /// vmangos HandleAreaSpiritHealerQueryOpcode → Creature::SendAreaSpiritHealerQueryOpcode (Creature.cpp:3553-3564): the time left on the
    /// guide's channel, 0 when it is not channelling.
    /// </summary>
    private static void AreaSpiritHealerQuery(WorldSession session, Player player, byte[] payload)
    {
        if (SpiritService(session, player, payload) is not { } unit)
        {
            return;
        }

        uint next = session.Services.GetService<SpellFeature>()?.System.ChannelRemainingMs(unit) ?? 0;
        session.Send(WorldOpcode.SmsgAreaSpiritHealerTime, BattlegroundPackets.BuildAreaSpiritHealerTime(unit.Guid, next));
    }

    /// <summary>
    /// vmangos HandleAreaSpiritHealerQueueOpcode → GossipHello_npc_spirit_guide (battleground.cpp:109-113): the player casts Waiting to
    /// Resurrect on itself, which the guide's next Spirit Heal looks for.
    /// </summary>
    private static void AreaSpiritHealerQueue(WorldSession session, Player player, byte[] payload)
    {
        if (SpiritService(session, player, payload) is not null && session.Services.GetService<SpellFeature>() is { } spells)
        {
            spells.System.CastSpell(player, BattlegroundConstants.SpellWaitingToResurrect, SpellCastTargets.ForSelf(), triggered: true);
        }
    }

    /// <summary>
    /// vmangos HandleBattlemasterHelloOpcode (BattleGroundHandler.cpp:40-73): the battlemaster pauses its movement (:50-52), then the list
    /// of its battleground.
    /// </summary>
    private static void BattlemasterHello(WorldSession session, Player player, byte[] payload)
    {
        if (!BattlegroundPackets.TryParseGuid(payload, out ObjectGuid guid) || Feature(session) is not { } feature
            || session.Services.GetService<QuestNpcFeature>() is not { } npcs
            || npcs.Services.InteractableNpc(player, guid, NpcFlags.BattleMaster) is not { } npc)
        {
            return;
        }

        npcs.Services.PauseInteractionMovement(player, npc);
        if (!feature.Battlemasters.TryGetValue(npc.Entry, out BattlegroundType type))
        {
            return;
        }

        if (feature.Manager.TemplateOf(type) is not { } template || player.Level < template.MinLevel)
        {
            session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(BattlegroundStrings.Mangos(715) ?? string.Empty));
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
        if (!Invariant.Check(
            float.IsFinite(player.X) && float.IsFinite(player.Y) && float.IsFinite(player.Z)
            && float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z),
            $"non-finite battleground portal distance coordinates ({player.X}, {player.Y}, {player.Z}) -> ({x}, {y}, {z})"))
        {
            return float.PositiveInfinity;
        }

        float dx = player.X - x;
        float dy = player.Y - y;
        float dz = player.Z - z;
        float dist = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        return float.IsFinite(dist) ? dist : float.PositiveInfinity;
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
