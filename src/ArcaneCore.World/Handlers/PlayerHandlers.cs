using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// Small in-world requests about the player itself (vmangos MiscHandler.cpp and friends):
/// played time, stand state, selection, action bar, zone, mover, and the status polls the
/// client sends after login. World thread.
/// </summary>
public sealed class PlayerHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgPlayedTime, HandlePlayedTime);
        table.OnWorld(WorldOpcode.CmsgStandstatechange, HandleStandStateChange);
        table.OnWorld(WorldOpcode.CmsgSetSelection, HandleSetSelection);
        table.OnWorld(WorldOpcode.CmsgSetActionButton, HandleSetActionButton);
        table.OnWorld(WorldOpcode.CmsgSetActionbarToggles, HandleSetActionBarToggles);
        table.OnWorld(WorldOpcode.CmsgZoneupdate, HandleZoneUpdate);
        table.OnWorld(WorldOpcode.CmsgSetActiveMover, HandleSetActiveMover);
    }

    /// <summary>CMSG_PLAYED_TIME (empty) → SMSG_PLAYED_TIME (vmangos HandlePlayedTime).</summary>
    private static void HandlePlayedTime(WorldSession session, Player player, byte[] payload)
    {
        uint now = session.World.NowMs;
        session.Send(WorldOpcode.SmsgPlayedTime, QueryPackets.BuildPlayedTime(player.PlayedTimeAt(now), player.LevelPlayedTimeAt(now)));
    }

    /// <summary>
    /// CMSG_STANDSTATECHANGE: u32 state. Only stand, sit, sleep and kneel may be chosen by the
    /// client, and not while UNIT_FLAG_PREVENT_ANIM is set (vmangos HandleStandStateChangeOpcode).
    /// </summary>
    private static void HandleStandStateChange(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint state = reader.ReadUInt32();
        if (state is not ((uint)StandState.Stand or (uint)StandState.Sit or (uint)StandState.Sleep or (uint)StandState.Kneel)
            || (player.UnitFlags & UnitFlags.PreventAnim) != 0)
        {
            return;
        }

        player.SetStandState((StandState)state);
    }

    /// <summary>CMSG_SET_SELECTION: u64 GUID (vmangos HandleSetSelectionOpcode → SetSelectionGuid).</summary>
    private static void HandleSetSelection(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        player.Selection = new ObjectGuid(reader.ReadUInt64());

        // ranged (autorepeat lane): a running Auto Shot / Shoot follows the selection or stops (MiscHandler.cpp:416-428).
        session.Services.GetService<SpellFeature>()?.System.RetargetAutoRepeat(player, player.Selection);
    }

    /// <summary>
    /// CMSG_SET_ACTION_BUTTON: u8 slot, u32 packed action (0 clears). Unknown types and slots
    /// are ignored (vmangos HandleSetActionButtonOpcode). Spell and item ids are not checked
    /// against content until spells (M12) and items (M9) exist.
    /// </summary>
    private static void HandleSetActionButton(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        byte button = reader.ReadByte();
        uint packed = reader.ReadUInt32();
        if (!player.SetActionButton(button, packed))
        {
            session.Logger.LogDebug("[{Endpoint}] ignored action button {Button} = 0x{Packed:X8}", session.RemoteEndpoint, button, packed);
        }
    }

    /// <summary>CMSG_SET_ACTIONBAR_TOGGLES: u8 mask stored in PLAYER_FIELD_BYTES byte 2 (vmangos).</summary>
    private static void HandleSetActionBarToggles(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        player.ActionBarToggles = reader.ReadByte();
    }

    /// <summary>
    /// CMSG_ZONEUPDATE: u32 zone. vmangos ignores the value and derives the zone from terrain
    /// data; until terrain is loaded (M8) the client's zone is accepted. A changed zone gets
    /// its world states, as vmangos Player::UpdateZone → SendInitWorldStates does.
    /// </summary>
    private static void HandleZoneUpdate(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint zone = reader.ReadUInt32();
        // docs/areas/world-state.md: the zone is derived server-side (vmangos MiscHandler.cpp:381-386);
        // the client value is used only in client-zone mode (no area data / ClientZoneTrust=Always).
        if (session.Services.GetService<WorldState.ZoneAreaFeature>() is { } zones)
        {
            zones.HandleClientZone(player, zone);
            return;
        }

        if (zone == 0)
        {
            return;
        }

        bool changed = zone != player.ZoneId;
        player.ZoneId = zone; // also confirms an unchanged zone after a map change (item limit checks wait for it)
        if (!changed)
        {
            return;
        }

        session.Send(WorldOpcode.SmsgInitWorldStates, LoginPackets.BuildInitWorldStates(player.MapId, zone));
    }

    /// <summary>
    /// CMSG_SET_ACTIVE_MOVER: u64 GUID of the unit the client now moves (vmangos HandleSetActiveMoverOpcode, MovementHandler.cpp:851-891,
    /// through <see cref="Game.Pets.Control.CharmService.HandleSetActiveMover"/>): it must be the server's mover (the player, or what it
    /// possesses); a mismatch is logged and the server's mover is kept. Leaving a pet moved with Eyes of the Beast hands the pet back to
    /// its AI (and dismisses it beyond the visibility distance).
    /// </summary>
    private static void HandleSetActiveMover(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        var guid = new ObjectGuid(reader.ReadUInt64());
        Game.Pets.SummonService? summons = session.Services.GetService<Pets.PetsFeature>()?.Service;
        bool accepted = summons is not null
            ? summons.Charms.HandleSetActiveMover(player, guid, summons)
            : guid.IsEmpty || guid == player.Guid;
        if (!accepted)
        {
            session.Logger.LogWarning("[{Endpoint}] active mover 0x{Guid:X16} is not the mover of {Player}", session.RemoteEndpoint, guid.Value, player.Name);
        }
    }
}
