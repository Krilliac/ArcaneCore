using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;

namespace ArcaneCore.World.Net.SessionTrace;

/// <summary>
/// The read-only anti-cheat shadow of a traced character the anti-cheat feature never checks (checks off, staff, GM mode, a managed bot).
/// For such a player the feature records nothing, so a pentest run with a GM character would show no verdict at all; the shadow runs the same
/// <see cref="MovementAntiCheat"/> checks on the same movement blocks and writes what they would have found, marked as exempt and not enforced.
/// For a player the feature does check, the shadow writes nothing: the feature records the finding and
/// <see cref="AntiCheatFeature.FindingRecorded"/> writes the enforced line.
/// <para>
/// Only public surface of the feature is used (<see cref="AntiCheatFeature.Options"/>, <see cref="AntiCheatFeature.IsExempt"/>), and nothing
/// that changes it: no score, no log row, no kick, no rubberband, no grant. The shadow keeps its own <see cref="MovementAntiCheat"/>, rebuilt when
/// the feature's options object changes, and re-expresses the feature's private sample helpers (allowed speed, granted flags, rooted).
/// </para>
/// <para>
/// Known gaps: the feature's explicit grants (<c>.cheat</c> tools) are private to it, so a flag granted that way is judged here as ungranted; a
/// transport claim is never scored (it is passed as unknown); a server-side move other than a teleport order the client was sent
/// (<c>SMSG_NEW_WORLD</c>, <c>MSG_MOVE_TELEPORT_ACK</c>) is only noticed when the position differs from the one stored after the last block.
/// World thread, except <see cref="NoteServerRelocation"/>, which any sending thread may call.
/// </para>
/// </summary>
internal sealed class SessionTraceShadow(SessionTrace trace)
{
    private readonly List<AntiCheatFinding> _findings = [];
    private MovementAntiCheat? _checks;
    private int _relocated;
    private Map? _terrainMap;
    private MapAntiCheatTerrain? _terrain;

    /// <summary>The server sent the client to a new place (<c>SMSG_NEW_WORLD</c>, <c>MSG_MOVE_TELEPORT_ACK</c>): the next block is trusted. Any thread.</summary>
    internal void NoteServerRelocation() => Volatile.Write(ref _relocated, 1);

    /// <summary>
    /// Why the feature never checks <paramref name="player"/> (<c>checks-off</c>, <c>staff</c>, <c>gm-mode</c>, <c>managed-bot</c>, in that
    /// order), or null when it checks the player. The verdict is the feature's own <see cref="AntiCheatFeature.IsExempt"/>.
    /// </summary>
    internal static string? ExemptReason(AntiCheatFeature feature, WorldSession session, Player player)
    {
        if (!feature.IsExempt(session, player))
        {
            return null;
        }

        AntiCheatOptions options = feature.Options;
        if (!options.Enabled)
        {
            return "checks-off";
        }

        if (options.ExemptSecurity > AccountSecurity.Player && session.Security >= options.ExemptSecurity)
        {
            return "staff";
        }

        if (player.IsGameMaster)
        {
            return "gm-mode";
        }

        return "managed-bot";
    }

    /// <summary>A client movement block of the traced character, before the server stores it.</summary>
    internal void OnMovement(AntiCheatFeature feature, WorldSession session, Player player, WorldOpcode opcode, in MovementInfo movement)
    {
        if (ExemptReason(feature, session, player) is not { } reason)
        {
            _checks = null;
            return;
        }

        AntiCheatOptions options = feature.Options;
        MovementAntiCheat checks = ChecksFor(options);
        if (Interlocked.Exchange(ref _relocated, 0) != 0)
        {
            checks.NotifyServerRelocation();
        }

        // The server holds the position the previous block left (the player has not been moved by this block yet).
        if (checks.HasBaseline)
        {
            checks.NotifyStored(player.X, player.Y, player.Z);
        }

        checks.CheckServerPosition(player.X, player.Y, player.Z);
        var sample = new MovementSample
        {
            Opcode = opcode,
            Movement = movement,
            ReceivedMs = session.CurrentPacketReceivedMs,
            LatencyMs = session.LatencyMs,
            AllowedSpeed = AllowedSpeed(player),
            Alive = player.IsAlive,
            Rooted = player.IsRooted && player.Movement.HasFlag(MovementFlags.Root) && !player.Locomotion.Pending.HasPendingOfType(MovementChangeType.Root),
            GrantedFlags = GrantedFlags(player),
            ServerControlled = (player.UnitFlags & UnitFlags.ServerControlled) != 0,
            Transport = movement.HasFlag(MovementFlags.OnTransport) ? TransportClaim.Unknown : TransportClaim.None,
            Terrain = options.TerrainChecks && player.Map is { } map ? TerrainOf(map) : null,
        };

        _findings.Clear();
        checks.Observe(sample, _findings);
        Report(reason);
    }

    /// <summary>An honoured movement acknowledgement of the traced character.</summary>
    internal void OnAcknowledgement(AntiCheatFeature feature, WorldSession session, Player player, uint clientTime, bool knockback)
    {
        if (ExemptReason(feature, session, player) is not { } reason)
        {
            return;
        }

        MovementAntiCheat checks = ChecksFor(feature.Options);
        _findings.Clear();
        checks.NotifyAcknowledgement(clientTime, session.CurrentPacketReceivedMs, _findings);
        if (knockback)
        {
            checks.NotifyKnockback();
        }

        Report(reason);
    }

    /// <summary>CMSG_MOVE_TIME_SKIPPED of the traced character's own unit.</summary>
    internal void OnTimeSkip(AntiCheatFeature feature, WorldSession session, Player player, uint skippedMs)
    {
        if (ExemptReason(feature, session, player) is not { } reason)
        {
            return;
        }

        MovementAntiCheat checks = ChecksFor(feature.Options);
        _findings.Clear();
        checks.NotifyTimeSkip(skippedMs, session.CurrentPacketReceivedMs, _findings);
        Report(reason);
    }

    private MovementAntiCheat ChecksFor(AntiCheatOptions options)
        => _checks is { } existing && ReferenceEquals(existing.Options, options) ? existing : (_checks = new MovementAntiCheat(options));

    private void Report(string reason)
    {
        foreach (AntiCheatFinding finding in _findings)
        {
            trace.Write("anticheat", string.Create(CultureInfo.InvariantCulture,
                $"{finding.Type} weight={finding.Weight:0.##} detail={finding.Detail} exempt reason={reason} (not enforced)"));
        }

        _findings.Clear();
    }

    private MapAntiCheatTerrain TerrainOf(Map map)
    {
        if (!ReferenceEquals(_terrainMap, map) || _terrain is null)
        {
            _terrain = new MapAntiCheatTerrain(map);
            _terrainMap = map;
        }

        return _terrain;
    }

    // The two helpers below re-express AntiCheatFeature.AllowedSpeed and AntiCheatFeature.GrantedFlags (private there), minus the explicit grants.

    private static float AllowedSpeed(Player player)
    {
        float allowed = Math.Max(Math.Max(player.RunSpeed, player.WalkSpeed), Math.Max(Math.Max(player.RunBackSpeed, player.SwimSpeed), player.SwimBackSpeed));
        foreach (PendingMovementChange change in player.Locomotion.Pending.Changes)
        {
            if (UnitSpeed.IsSpeedChange(change.Type))
            {
                allowed = Math.Max(allowed, change.NewValue);
            }
        }

        return allowed;
    }

    private static MovementFlags GrantedFlags(Player player)
    {
        MovementFlags granted = MovementCheats.GrantedFlags(player);
        LocomotionState locomotion = player.Locomotion;
        if (locomotion.Auras.Has(AuraType.WaterWalk) || locomotion.Pending.HasPendingOfType(MovementChangeType.WaterWalk))
        {
            granted |= MovementFlags.WaterWalking;
        }

        if (locomotion.Auras.Has(AuraType.Hover) || locomotion.Pending.HasPendingOfType(MovementChangeType.Hover))
        {
            granted |= MovementFlags.Hover;
        }

        if (locomotion.Auras.Has(AuraType.FeatherFall) || locomotion.Pending.HasPendingOfType(MovementChangeType.FeatherFall))
        {
            granted |= MovementFlags.SafeFall;
        }

        return granted;
    }
}
