using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>Innkeepers and flight masters (vmangos HandleBinderActivateOpcode, Spell::EffectBind, TaxiHandler.cpp).</summary>
public sealed partial class QuestNpcServices
{
    /// <summary>
    /// CMSG_BINDER_ACTIVATE (vmangos HandleBinderActivateOpcode → SendBindPoint → spell 3286 →
    /// Spell::EffectBind): alive, an interactable innkeeper, not in an instance. The home bind
    /// moves to the player's position; SMSG_BINDPOINTUPDATE and SMSG_PLAYERBOUND follow and the
    /// gossip closes.
    /// </summary>
    /// <remarks>
    /// The bind spell's effect is applied here directly; its cast visual belongs to the spells
    /// owner. Without a map owner only the continents (maps 0 and 1) count as non-instanced.
    /// </remarks>
    public void BinderActivate(Player player, ObjectGuid guid)
    {
        if (Ready(player) is not { } s || !player.IsInWorld || !player.IsAlive)
        {
            return;
        }

        if (InteractableNpc(player, guid, NpcFlags.Innkeeper) is not { } npc)
        {
            LogMissing("BinderActivate", guid);
            return;
        }

        bool instanceable = Deps.Maps?.IsInstanceable(player.MapId) ?? player.MapId is not (0 or 1);
        if (instanceable)
        {
            return;
        }

        uint area = Deps.Maps?.GetAreaId(player.MapId, player.X, player.Y, player.Z) is { } a and not 0 ? a : player.ZoneId;
        player.Home = new HomeBind(player.MapId, area, player.X, player.Y, player.Z);
        _sink.CharacterChanged(player);
        Send(player, WorldOpcode.SmsgBindpointupdate, NpcPackets.BindPointUpdate(player.X, player.Y, player.Z, player.MapId, area));
        Send(player, WorldOpcode.SmsgPlayerbound, NpcPackets.PlayerBound(npc.Guid, area));
        CloseGossip(player);
        Flush(s);
    }

    /// <summary>CMSG_TAXINODE_STATUS_QUERY (vmangos SendTaxiStatus: any creature of the map, no interaction checks).</summary>
    public void TaxiNodeStatusQuery(Player player, ObjectGuid guid)
    {
        if (Ready(player) is not { } s || FindNpc(player, guid) is not { } npc)
        {
            return;
        }

        uint node = NearestTaxiNode(npc, player);
        if (node != 0)
        {
            Send(player, WorldOpcode.SmsgTaxinodeStatus, NpcPackets.TaxiNodeStatus(npc.Guid, IsNodeKnown(s, node)));
        }
    }

    /// <summary>CMSG_TAXIQUERYAVAILABLENODES (vmangos HandleTaxiQueryAvailableNodes): learn the node, else show the map.</summary>
    public void TaxiQueryAvailableNodes(Player player, ObjectGuid guid)
    {
        if (Ready(player) is not { } s)
        {
            return;
        }

        if (InteractableNpc(player, guid, NpcFlags.FlightMaster) is not { } npc)
        {
            LogMissing("TaxiQueryAvailableNodes", guid);
            return;
        }

        if (!LearnNewTaxiNode(s, npc))
        {
            SendTaxiMenu(s, npc);
        }
    }

    /// <summary>
    /// CMSG_ACTIVATETAXI (vmangos HandleActivateTaxiOpcode → Player::ActivateTaxiPathTo, flight-master
    /// case): not logging out or in combat, both nodes known, not mounted, the source node within
    /// 2×INTERACTION_DISTANCE (cubed, as vmangos compares), a path between the nodes, and the
    /// discounted price affordable. The flight owner then starts the flight and the price is taken.
    /// </summary>
    /// <remarks>
    /// Shapeshift and spell-cast checks need the auras/spells owners and the flight itself the
    /// movement owner (<see cref="ITaxiFlights"/>); without it a valid request is answered
    /// ERR_TAXIUNSPECIFIEDSERVERERROR and nothing is charged.
    /// </remarks>
    public void ActivateTaxi(Player player, ObjectGuid guid, uint sourceNode, uint destinationNode)
    {
        if (Ready(player) is not { } s)
        {
            return;
        }

        if (InteractableNpc(player, guid, NpcFlags.FlightMaster) is not { } npc)
        {
            LogMissing("ActivateTaxi", guid);
            return;
        }

        if (player.IsLoggingOut || (player.UnitFlags & UnitFlags.InCombat) != 0)
        {
            TaxiReply(player, ActivateTaxiReply.PlayerBusy);
            return;
        }

        // vmangos Player::ActivateTaxiPathTo rejects UNIT_FLAG_REMOVE_CLIENT_CONTROL.
        if ((player.UnitFlags & UnitFlags.RemoveClientControl) != 0)
        {
            return;
        }

        if (!IsNodeKnown(s, sourceNode) || !IsNodeKnown(s, destinationNode))
        {
            return;
        }

        if (player.GetUInt32(UpdateFields.UnitFieldMountdisplayid) != 0)
        {
            TaxiReply(player, ActivateTaxiReply.PlayerAlreadyMounted);
            return;
        }

        if (Npcs.Node(sourceNode) is not { } node)
        {
            TaxiReply(player, ActivateTaxiReply.NoSuchPath);
            return;
        }

        if (node.X == 0 && node.Y == 0 && node.Z == 0)
        {
            TaxiReply(player, ActivateTaxiReply.UnspecifiedServerError);
            return;
        }

        float dx = node.X - player.X;
        float dy = node.Y - player.Y;
        float dz = node.Z - player.Z;
        const float limit = 2 * InteractionDistance;
        if (node.MapId != player.MapId || (dx * dx) + (dy * dy) + (dz * dz) > limit * limit * limit)
        {
            TaxiReply(player, ActivateTaxiReply.TooFarAway);
            return;
        }

        if (Npcs.Path(sourceNode, destinationNode) is not { } path)
        {
            return; // vmangos logs and returns without a reply
        }

        uint mount = player.Team == Team.Alliance ? node.MountAlliance : node.MountHorde;
        uint cost = (uint)((path.Price * PriceDiscount(player, npc)) + 0.5f);
        if (player.Money < cost)
        {
            TaxiReply(player, ActivateTaxiReply.NotEnoughMoney);
            return;
        }

        if (Deps.Flights?.StartFlight(player, sourceNode, destinationNode, path.Id, mount) != true)
        {
            TaxiReply(player, ActivateTaxiReply.UnspecifiedServerError);
            return;
        }

        ModifyMoney(s, -(long)cost);
        Flush(s);
    }

    /// <summary>vmangos WorldSession::SendTaxiMenu.</summary>
    internal void SendTaxiMenu(PlayerNpcState s, NpcInfo npc)
    {
        uint node = NearestTaxiNode(npc, s.Quests.Player);
        if (node != 0)
        {
            Send(s.Quests.Player, WorldOpcode.SmsgShowtaxinodes, NpcPackets.ShowTaxiNodes(npc.Guid, node, s.TaxiMask));
        }
    }

    /// <summary>
    /// vmangos WorldSession::SendLearnNewTaxiNode: true when the nearest node was just learned
    /// (SMSG_NEW_TAXI_PATH + SMSG_TAXINODE_STATUS sent) or when there is no node at all.
    /// </summary>
    internal bool LearnNewTaxiNode(PlayerNpcState s, NpcInfo npc)
    {
        Player player = s.Quests.Player;
        uint node = NearestTaxiNode(npc, player);
        if (node == 0)
        {
            return true;
        }

        if (!SetTaxiMaskNode(s, node))
        {
            return false;
        }

        player.Session.Send(WorldOpcode.SmsgNewTaxiPath, []);
        Send(player, WorldOpcode.SmsgTaxinodeStatus, NpcPackets.TaxiNodeStatus(npc.Guid, true));
        return true;
    }

    /// <summary>
    /// vmangos ObjectMgr::GetNearestTaxiNode: the closest node on the creature's map that has a
    /// mount for the player's team.
    /// </summary>
    private uint NearestTaxiNode(NpcInfo npc, Player player)
    {
        bool alliance = player.Team == Team.Alliance;
        uint best = 0;
        float bestDist = 0;
        foreach (TaxiNode node in Npcs.Nodes)
        {
            if (node.MapId != npc.MapId || (alliance ? node.MountAlliance : node.MountHorde) == 0)
            {
                continue;
            }

            float dx = node.X - npc.X;
            float dy = node.Y - npc.Y;
            float dz = node.Z - npc.Z;
            float dist = (dx * dx) + (dy * dy) + (dz * dz);
            if (best == 0 || dist < bestDist)
            {
                best = node.Id;
                bestDist = dist;
            }
        }

        return best;
    }

    /// <summary>vmangos PlayerTaxi::IsTaximaskNodeKnown.</summary>
    private static bool IsNodeKnown(PlayerNpcState s, uint node)
    {
        if (node == 0)
        {
            return false;
        }

        uint field = (node - 1) / 32;
        return field < s.TaxiMask.Length && (s.TaxiMask[field] & (1u << (int)((node - 1) % 32))) != 0;
    }

    /// <summary>vmangos PlayerTaxi::SetTaximaskNode: true when the bit was newly set (then saved).</summary>
    private bool SetTaxiMaskNode(PlayerNpcState s, uint node)
    {
        if (node == 0 || IsNodeKnown(s, node))
        {
            return false;
        }

        uint field = (node - 1) / 32;
        if (field >= s.TaxiMask.Length)
        {
            return false;
        }

        s.TaxiMask[field] |= 1u << (int)((node - 1) % 32);
        _sink.TaxiMaskChanged(s.Quests.Player, [.. s.TaxiMask]);
        return true;
    }

    private static void TaxiReply(Player player, ActivateTaxiReply reply)
        => Send(player, WorldOpcode.SmsgActivatetaxireply, NpcPackets.ActivateTaxiReply(reply));
}
