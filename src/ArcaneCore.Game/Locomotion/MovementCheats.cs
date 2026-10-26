using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// The server side of the GM movement cheats <c>.cheat fly</c>, <c>.cheat fixedz</c> and <c>.cheat featherfall</c>. 1.12 has no real
/// flight: the client flies only by swimming in the air, so the server sets the flag word the client would send while swimming,
/// tells everyone, and the checks that would call it a hack stand down while the cheat is on (<see cref="Player.HasMovementCheat"/>).
/// <para>
/// Fly and fixed Z port vmangos <c>Player::SetCheatFly</c> / <c>SetFly</c> (Player.cpp:2733-2742, 4531-4551) and
/// <c>SetCheatFixedZ</c> (:2744-2760): record the cheat, set the movement flags, <c>RejectMovementPacketsFor(100)</c>, then
/// <c>SendHeartBeat(true)</c>, which is MSG_MOVE_HEARTBEAT with the player's packed GUID and movement block to everyone who sees the
/// player and to the player itself (Unit.cpp:511-527). Status: emulator-sourced; not checked against a 1.12.1 client. vmangos does
/// not clear the cheat on a teleport (nothing outside CharacterCommands.cpp and MovementAnticheat.cpp reads it), so neither does this.
/// Feather fall has no vmangos command; the order is the existing feather fall change (SMSG_MOVE_FEATHER_FALL 242 / SMSG_MOVE_NORMAL_FALL
/// 243, mangoszero <c>Player::SetFeatherFall</c>, PlayerMovement.cpp:157, via <see cref="MovementControl"/>).
/// </para>
/// <para>Thread affinity: world thread only. State lives on the <see cref="Player"/> (<see cref="Player.Cheats"/>), is never saved, and goes with the player at logout.</para>
/// </summary>
public static class MovementCheats
{
    /// <summary>
    /// The flag word of fly mode (Player::SetFly, Player.cpp:4543): MOVEFLAG_LEVITATING | MOVEFLAG_SWIMMING | MOVEFLAG_MOVED |
    /// MOVEFLAG_FLYING = 0x01A00400.
    /// </summary>
    public const MovementFlags FlyFlags = MovementFlags.Levitating | MovementFlags.Swimming | MovementFlags.Moved | MovementFlags.Flying;

    /// <summary>The window in milliseconds client movement is dropped after the server changes the flags (RejectMovementPacketsFor(100)).</summary>
    public const uint RejectWindowMs = 100;

    /// <summary>
    /// <c>.cheat fly on|off</c>. On: the player leaves a transport (Player::SetFly), the flags become <see cref="FlyFlags"/>; off: the
    /// flags become none (vmangos assigns the whole word, so Root, Water Walking, Hover and Safe Fall are dropped too). Either way client
    /// movement is rejected for <see cref="RejectWindowMs"/> and a heartbeat goes to the set and to the player.
    /// </summary>
    public static void SetFly(Player player, bool on)
    {
        ArgumentNullException.ThrowIfNull(player);
        player.Cheats = on ? player.Cheats | PlayerCheats.Fly : player.Cheats & ~PlayerCheats.Fly;
        if (on && player.Transport is { } ship)
        {
            // "Remove client from transport ... otherwise camera will bug out" (Player.cpp:4535-4540). StopMoving(true) is not ported:
            // there is no spline stop for a player here, and the heartbeat below tells the client the new state.
            ship.RemovePassenger(player);
        }

        player.SetMovementFlags(on ? FlyFlags : MovementFlags.None);
        RejectAndHeartbeat(player);
    }

    /// <summary><c>.cheat fixedz on|off</c> (Player::SetCheatFixedZ): adds or removes MOVEFLAG_FIXED_Z (0x800), then the same reject window and heartbeat.</summary>
    public static void SetFixedZ(Player player, bool on)
    {
        ArgumentNullException.ThrowIfNull(player);
        player.Cheats = on ? player.Cheats | PlayerCheats.FixedZ : player.Cheats & ~PlayerCheats.FixedZ;
        if (on)
        {
            player.AddMovementFlags(MovementFlags.FixedZ);
        }
        else
        {
            player.RemoveMovementFlags(MovementFlags.FixedZ);
        }

        RejectAndHeartbeat(player);
    }

    /// <summary>
    /// <c>.cheat featherfall on|off</c>: records the cheat (fall damage is skipped while it is on) and orders the client with the feather
    /// fall change. Turning it off while a Slow Fall or Feather Fall aura is on keeps the flag: the aura owns it.
    /// </summary>
    public static void SetFeatherFall(Player player, bool on)
    {
        ArgumentNullException.ThrowIfNull(player);
        player.Cheats = on ? player.Cheats | PlayerCheats.FeatherFall : player.Cheats & ~PlayerCheats.FeatherFall;
        if (on || !player.Locomotion.Auras.Has(AuraType.FeatherFall))
        {
            MovementControl.Request(player, MovementChangeType.FeatherFall, on);
        }
    }

    /// <summary>
    /// The movement flags the cheats stand behind, for the anticheat's "flag without a grant" test: a water walk or feather fall the GM
    /// gave is not a hack. (Fly and fixed Z need no grant: the anticheat does not judge a player while either is on.)
    /// </summary>
    public static MovementFlags GrantedFlags(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        MovementFlags granted = MovementFlags.None;
        if (player.HasCheat(PlayerCheats.WaterWalk))
        {
            granted |= MovementFlags.WaterWalking;
        }

        if (player.HasCheat(PlayerCheats.FeatherFall))
        {
            granted |= MovementFlags.SafeFall;
        }

        return granted;
    }

    /// <summary>
    /// vmangos Unit::SendHeartBeat(includingSelf) (Unit.cpp:511-527): MSG_MOVE_HEARTBEAT, packed GUID and the stored movement block
    /// (its time re-stamped as a server block) to every player who sees the unit and, with <paramref name="includingSelf"/>, to the
    /// player itself.
    /// </summary>
    public static void SendHeartbeat(Player player, bool includingSelf)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Map is { } map)
        {
            player.StampServerMovementTime(map.World.NowMs);
        }

        var writer = new PacketWriter(64);
        writer.WritePackedGuid(player.Guid.Value);
        player.Movement.Write(writer);
        if (includingSelf)
        {
            player.Session.Send(WorldOpcode.MsgMoveHeartbeat, writer.AsSpan());
        }

        player.Map?.BroadcastToObservers(player, WorldOpcode.MsgMoveHeartbeat, writer.AsSpan());
    }

    private static void RejectAndHeartbeat(Player player)
    {
        player.Locomotion.RejectMovementPacketsFor(unchecked((uint)Clock.Milliseconds()), RejectWindowMs);
        SendHeartbeat(player, includingSelf: true);
    }
}
