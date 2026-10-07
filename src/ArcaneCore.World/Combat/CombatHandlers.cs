using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Combat;

/// <summary>
/// Melee and death opcodes (vmangos CombatHandler.cpp, MiscHandler.cpp, QueryHandler.cpp).
/// Discovered through <see cref="IOpcodeHandlerGroup"/>; the logic lives in
/// <see cref="MapCombat"/>. World thread.
/// </summary>
public sealed class CombatHandlers : IOpcodeHandlerGroup
{
    /// <summary>SHEATH_STATE_UNARMED … RANGED (vmangos UnitDefines.h MAX_SHEATH_STATE = 3).</summary>
    private const uint MaxSheathState = 3;

    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgAttackswing, HandleAttackSwing);
        table.OnWorld(WorldOpcode.CmsgAttackstop, HandleAttackStop);
        table.OnWorld(WorldOpcode.CmsgSetsheathed, HandleSetSheathed);
        table.OnWorld(WorldOpcode.CmsgRepopRequest, HandleRepopRequest);
        table.OnWorld(WorldOpcode.CmsgReclaimCorpse, HandleReclaimCorpse);
        table.OnWorld(WorldOpcode.MsgCorpseQuery, HandleCorpseQuery);
        table.OnWorld(WorldOpcode.CmsgTogglePvp, HandleTogglePvp);
    }

    /// <summary>
    /// CMSG_ATTACKSWING: u64 target (vmangos HandleAttackSwingOpcode; gtker
    /// cmsg_attackswing.wowm). Unknown target → SMSG_ATTACKSTOP without a victim; a friendly,
    /// spawning/unselectable or dead target → SMSG_ATTACKSTOP naming it; otherwise Attack.
    /// </summary>
    private static void HandleAttackSwing(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        var guid = new ObjectGuid(reader.ReadUInt64());
        if (player.Map is not { } map)
        {
            return;
        }

        MapCombat combat = map.Combat;
        Unit? enemy = guid.Value == 0 ? null : combat.FindUnit(guid);
        if (enemy is null)
        {
            SendAttackStop(session, player, null);
            return;
        }

        if (combat.Hooks.IsFriendly(player, enemy) || (enemy.UnitFlags & (UnitFlags.Spawning | UnitFlags.NotSelectable)) != 0)
        {
            SendAttackStop(session, player, enemy);
            return;
        }

        if (!enemy.IsAlive)
        {
            // the client can swing at a known dead target when auto-switching from auto shot
            SendAttackStop(session, player, enemy);
            return;
        }

        if (!combat.Hooks.CanAttack(player, enemy))
        {
            // vmangos lets Attack() through and stops at the swing (IsValidAttackTarget in the
            // melee update); refusing here keeps unflagged enemy players out of melee.
            SendAttackStop(session, player, enemy);
            return;
        }

        combat.Attack(player, enemy, melee: true);
    }

    /// <summary>CMSG_ATTACKSTOP: empty (vmangos HandleAttackStopOpcode → AttackStop).</summary>
    private static void HandleAttackStop(WorldSession session, Player player, byte[] payload)
    {
        // vmangos WorldSession::HandleAttackStopOpcode / Player.cpp combat handler: cancelling
        // the auto-attack also discards pending Reckoning/extra-attack charges.
        player.Combat.ResetExtraAttacks();
        player.Map?.Combat.AttackStop(player);
    }

    /// <summary>CMSG_SETSHEATHED: u32 state (vmangos HandleSetSheathedOpcode → UNIT_FIELD_BYTES_2 byte 0).</summary>
    private static void HandleSetSheathed(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint state = reader.ReadUInt32();
        if (state >= MaxSheathState)
        {
            return;
        }

        player.SetByte(UpdateFields.UnitFieldBytes2, 0, (byte)state);
    }

    /// <summary>
    /// CMSG_REPOP_REQUEST: no body read ("recv_data.read_skip&lt;uint8&gt;(); client crash" —
    /// vmangos HandleRepopRequestOpcode). Ignored while alive or already a ghost.
    /// </summary>
    private static void HandleRepopRequest(WorldSession session, Player player, byte[] payload)
        => player.Map?.Combat.RepopPlayer(player);

    /// <summary>CMSG_RECLAIM_CORPSE: u64 corpse GUID, unused by the server (vmangos HandleReclaimCorpseOpcode).</summary>
    private static void HandleReclaimCorpse(WorldSession session, Player player, byte[] payload)
        => player.Map?.Combat.TryReclaimCorpse(player);

    /// <summary>MSG_CORPSE_QUERY: empty from the client (vmangos HandleCorpseQueryOpcode).</summary>
    private static void HandleCorpseQuery(WorldSession session, Player player, byte[] payload)
        => session.Send(WorldOpcode.MsgCorpseQuery, MapCombat.BuildCorpseQuery(player));

    /// <summary>
    /// CMSG_TOGGLE_PVP: optional u8 target state (vmangos TogglePvP packet: present only when
    /// the body is one byte; gtker pvp/cmsg_toggle_pvp.wowm lists the optional byte too).
    /// </summary>
    private static void HandleTogglePvp(WorldSession session, Player player, byte[] payload)
    {
        bool? state = payload.Length == 1 ? payload[0] != 0 : null;
        player.Map?.Combat.TogglePvp(player, state);
    }

    /// <summary>vmangos WorldSession::SendAttackStop: to this client only; the flag is the enemy's IsDead.</summary>
    private static void SendAttackStop(WorldSession session, Player player, Unit? enemy)
        => session.Send(WorldOpcode.SmsgAttackstop,
            CombatPackets.AttackStop(player.Guid, enemy?.Guid ?? default, enemy is not null && !enemy.IsAlive));
}
