using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>The stages of one cast-and-teleport leg (<see cref="PlayerbotTravelCast"/>): the hearthstone, or a mage's own Teleport.</summary>
internal enum TravelCastStage : byte { Start, Casting, Done, Failed }

/// <summary>
/// One bot's cast-and-teleport leg: the spell, where it must end, and what was seen of the cast so far. Owned by the leg driver that began
/// it (<see cref="PlayerbotHearthstone"/>, <see cref="PlayerbotMageService"/>) and kept on the bot (<see cref="ConditionalWeakTable{TKey,TValue}"/>
/// keyed by the <see cref="Player"/>). Mutable, world thread only.
/// </summary>
internal sealed class TravelCastLeg(TravelMeans means, uint spellId, TravelPlace destination, uint nowMs)
{
    public TravelMeans Means { get; } = means;

    /// <summary>The spell the leg casts: the hearthstone's on-use spell, or the Teleport spell.</summary>
    public uint SpellId { get; } = spellId;

    /// <summary>Where the leg must end (the bind point, or the spell's <c>spell_target_position</c>).</summary>
    public TravelPlace Destination { get; } = destination;

    public TravelCastStage Stage { get; set; } = TravelCastStage.Start;

    /// <summary>When the leg began (world ms): the start of <see cref="PlayerbotTravelCast.StartBoundMs"/>. Settable for tests.</summary>
    public uint BeganMs { get; set; } = nowMs;

    /// <summary>When the cast was taken by the server (world ms), and how long cast plus teleport may take before the leg is overdue.</summary>
    public uint CastMs { get; set; }

    public uint BudgetMs { get; set; }

    /// <summary>Thinks the cast request was put off (the action budget spent, or the bot not standing still).</summary>
    public int Attempts { get; set; }

    /// <summary>The server's own record of the cast: a SMSG_SPELL_GO of the spell, and a teleport of the bot under way.</summary>
    public bool SawGo { get; set; }

    public bool SawTeleport { get; set; }

    /// <summary>The reason the cast was interrupted, when the server said one (SMSG_SPELL_FAILURE / SMSG_CAST_RESULT).</summary>
    public SpellCastResult? FailReason { get; set; }

    public string? Failure { get; set; }
}

/// <summary>
/// The shared steps of a leg that casts a spell which teleports the caster (the hearthstone through CMSG_USE_ITEM, a mage Teleport through
/// CMSG_CAST_SPELL): send the request standing still, confirm the server took it from its own packets or cast state, wait out the cast and the
/// teleport (acknowledging a near teleport the way a client does), and confirm the bot stands where the spell leads. Never moves the bot and
/// never writes its state. Thread affinity: world thread.
/// </summary>
internal static class PlayerbotTravelCast
{
    /// <summary>A bot this close to the destination, on its map, has arrived (a teleport lands on the spot).</summary>
    internal const float ArriveYards = 10f;

    /// <summary>Time past the spell's own cast time before the cast is overdue (a lagging world, the teleport and its acknowledgement).</summary>
    internal const uint GraceMs = 20_000;

    /// <summary>
    /// The cast request may be put off this many thinks in all (the action budget spent, or the bot not yet standing still) before the leg is
    /// given up (<see cref="Sent.BudgetStarved"/>, <see cref="Sent.NotStill"/>).
    /// </summary>
    internal const int MaxSendAttempts = 20;

    /// <summary>
    /// A leg whose cast the server has not taken this long after it began (<see cref="TravelCastLeg.BeganMs"/>) is given up, whatever put the
    /// request off: the bound of the start stage in world time, beside <see cref="MaxSendAttempts"/> in thinks.
    /// </summary>
    internal const uint StartBoundMs = 30_000;

    /// <summary>The outcome of sending the cast request.</summary>
    internal enum Sent : byte { Taken, Retry, BudgetStarved, NotStill, SessionRefused, Refused }

    /// <summary>Whether the bot stands at <paramref name="place"/> (same map, within <see cref="ArriveYards"/>).</summary>
    internal static bool IsAt(Player player, TravelPlace place)
        => player.MapId == place.MapId && Vector3.Distance(new Vector3(player.X, player.Y, player.Z), place.Position) <= ArriveYards;

    /// <summary>Whether the bot's cast slot holds <paramref name="spellId"/>, preparing or casting.</summary>
    internal static bool IsCasting(SpellSystem spells, Player player, uint spellId)
        => spells.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting } cast && cast.Spell.Id == spellId;

    /// <summary>Whether the server holds a teleport of the bot under way (near or far).</summary>
    internal static bool IsTeleporting(WorldSession session, Player player)
        => session.Services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) == true;

    /// <summary>
    /// Stop, stand, and send <paramref name="opcode"/> (CMSG_CAST_SPELL or CMSG_USE_ITEM) for <paramref name="leg"/>; the server took it when its
    /// SMSG_SPELL_START / GO names the spell, the cast slot holds it, or a teleport began (an instant cast). Spell packets nobody read are drained
    /// first so a stale one is not taken for the answer. <paramref name="reason"/> is the server's refusal reason when it said one. Every think
    /// the request is put off (<see cref="Sent.Retry"/>) counts in <see cref="TravelCastLeg.Attempts"/>: past <see cref="MaxSendAttempts"/> it
    /// answers <see cref="Sent.NotStill"/> (the bot would not stand still) or <see cref="Sent.BudgetStarved"/>.
    /// </summary>
    internal static Sent Send(WorldSession session, Player player, SpellSystem spells, TravelCastLeg leg, WorldOpcode opcode, byte[] payload,
        out SpellCastResult? reason)
    {
        reason = null;
        if (player.StandState != StandState.Stand)
        {
            session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Stand));
            if (player.StandState != StandState.Stand) return ++leg.Attempts > MaxSendAttempts ? Sent.NotStill : Sent.Retry;
        }

        // A cast with a cast time needs the bot standing still (SPELL_INTERRUPT_FLAG_MOVEMENT).
        if (player.Movement.HasFlag(MovementFlags.MaskMoving)
            && (!PlayerbotMovementControl.Stop(session, player) || player.Movement.HasFlag(MovementFlags.MaskMoving)))
            return ++leg.Attempts > MaxSendAttempts ? Sent.NotStill : Sent.Retry;
        session.DrainManagedPackets(
            WorldOpcode.SmsgSpellStart, WorldOpcode.SmsgSpellGo, WorldOpcode.SmsgSpellFailure, WorldOpcode.SmsgSpellFailedOther, WorldOpcode.SmsgCastResult);
        if (!session.TryManagedAction(opcode, payload))
        {
            if (session.ManagedBudget is { Remaining: <= 0 })
                return ++leg.Attempts > MaxSendAttempts ? Sent.BudgetStarved : Sent.Retry;
            return Sent.SessionRefused;
        }

        bool taken = false;
        foreach (ManagedSessionPacket packet in session.DrainManagedPackets(
            WorldOpcode.SmsgSpellStart, WorldOpcode.SmsgSpellGo, WorldOpcode.SmsgSpellFailure, WorldOpcode.SmsgSpellFailedOther, WorldOpcode.SmsgCastResult))
        {
            switch (packet.Opcode)
            {
                case WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo when SpellIdOfStartOrGo(packet.Payload) == leg.SpellId:
                    taken = true;
                    if (packet.Opcode == WorldOpcode.SmsgSpellGo) leg.SawGo = true;
                    break;
                case WorldOpcode.SmsgCastResult when CastResultOf(packet.Payload, leg.SpellId) is { } refused:
                    reason = refused;
                    break;
            }
        }

        if (IsTeleporting(session, player)) leg.SawTeleport = true;
        if (taken || leg.SawTeleport || IsCasting(spells, player, leg.SpellId)) return Sent.Taken;
        return Sent.Refused;
    }

    /// <summary>What <see cref="Watch"/> saw this think.</summary>
    internal enum Seen : byte { Waiting, Arrived, Interrupted, NotArrived, Overdue }

    /// <summary>
    /// One look at a leg whose cast the server took: arrived at the destination (and no teleport left), a teleport under way (a near one is
    /// acknowledged here as a client does; the brain's own <see cref="PlayerbotMovementControl.Update"/> may have done it already), the cast still
    /// running, or neither: interrupted when no SMSG_SPELL_GO of it and no teleport was seen, else the bot did not arrive. Overdue past the
    /// budget. Cost: a few dictionary probes and one drain of the spell packets per think.
    /// </summary>
    internal static Seen Watch(WorldSession session, Player player, SpellSystem spells, TravelCastLeg leg, uint now)
    {
        Observe(session, leg);
        bool teleporting = IsTeleporting(session, player);
        if (teleporting)
        {
            leg.SawTeleport = true;
            if (session.Services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleportedNear(player) == true)
                PlayerbotMovementControl.Update(session, player);
            teleporting = IsTeleporting(session, player);
        }

        if (!teleporting && IsAt(player, leg.Destination)) return Seen.Arrived;
        bool overdue = unchecked(now - leg.CastMs) > leg.BudgetMs;
        if (teleporting || IsCasting(spells, player, leg.SpellId)) return overdue ? Seen.Overdue : Seen.Waiting;
        return leg.SawGo || leg.SawTeleport ? Seen.NotArrived : Seen.Interrupted;
    }

    /// <summary>Read the spell packets of the leg's cast that arrived since the last look: its SMSG_SPELL_GO, or the reason it failed.</summary>
    internal static void Observe(WorldSession session, TravelCastLeg leg)
    {
        foreach (ManagedSessionPacket packet in session.DrainManagedPackets(
            WorldOpcode.SmsgSpellStart, WorldOpcode.SmsgSpellGo, WorldOpcode.SmsgSpellFailure, WorldOpcode.SmsgSpellFailedOther, WorldOpcode.SmsgCastResult))
        {
            switch (packet.Opcode)
            {
                case WorldOpcode.SmsgSpellGo when SpellIdOfStartOrGo(packet.Payload) == leg.SpellId:
                    leg.SawGo = true;
                    break;
                case WorldOpcode.SmsgSpellFailure when packet.Payload.Length >= 13 && BitConverter.ToUInt32(packet.Payload, 8) == leg.SpellId:
                    leg.FailReason = (SpellCastResult)packet.Payload[12];
                    break;
                case WorldOpcode.SmsgCastResult when CastResultOf(packet.Payload, leg.SpellId) is { } refused:
                    leg.FailReason = refused;
                    break;
            }
        }
    }

    /// <summary>
    /// A running cooldown of <paramref name="spellId"/> itself, or of <paramref name="category"/> (0: none), from the server's own cooldown
    /// state (<see cref="SpellSystem.GetActiveCooldowns"/>). The global cooldown is not counted: it passes within a second and a half.
    /// </summary>
    internal static bool OnCooldown(SpellSystem spells, Player player, uint spellId, uint category)
    {
        foreach (InitialSpellCooldown cooldown in spells.GetActiveCooldowns(player))
        {
            if ((cooldown.SpellId == spellId && cooldown.CooldownMs > 0)
                || (category != 0 && cooldown.Category == category && cooldown.CategoryCooldownMs > 0))
                return true;
        }

        return false;
    }

    /// <summary>The time the cast plus its teleport may take: the spell's cast time at the bot's level plus <see cref="GraceMs"/>.</summary>
    internal static uint BudgetOf(SpellInfo? spell, Player player)
        => GraceMs + (uint)Math.Clamp(spell?.GetCastTime(player.Level) ?? 0, 0, 600_000);

    /// <summary>CMSG_CAST_SPELL: u32 spell, then the targets (here the caster itself).</summary>
    internal static byte[] CastPayload(uint spellId, SpellCastTargets targets)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt32(spellId);
        targets.Write(writer);
        return writer.ToArray();
    }

    /// <summary>Cancel the running cast of <paramref name="spellId"/> the way a client does (CMSG_CANCEL_CAST, u32 spell), when it is running.</summary>
    internal static void CancelCast(WorldSession session, Player player, uint spellId)
    {
        if (session.Services.GetService<SpellFeature>()?.System is { } spells && IsCasting(spells, player, spellId))
            session.TryManagedAction(WorldOpcode.CmsgCancelCast, BitConverter.GetBytes(spellId));
    }

    /// <summary>SMSG_SPELL_START / SMSG_SPELL_GO: packed item-or-caster guid, packed caster guid, u32 spell.</summary>
    private static uint SpellIdOfStartOrGo(byte[] payload)
    {
        try
        {
            var reader = new PacketReader(payload);
            _ = reader.ReadPackedGuid();
            _ = reader.ReadPackedGuid();
            return reader.ReadUInt32();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // PacketReader's one malformed-packet exception (MalformedPacket.Throw): a short packet names no spell.
            FaultLedger.Shared.Record("playerbot.hearthstone", ex);
            return 0;
        }
    }

    /// <summary>SMSG_CAST_RESULT (u32 spell, u8 status, u8 reason when failed) of <paramref name="spellId"/> that says the cast failed; null otherwise.</summary>
    private static SpellCastResult? CastResultOf(byte[] payload, uint spellId)
        => payload.Length >= 6 && BitConverter.ToUInt32(payload, 0) == spellId && payload[4] != (byte)SpellCastResultStatus.Success
            ? (SpellCastResult)payload[5] : null;
}

/// <summary>
/// Autonomous bots use their hearthstone (item 6948, on-use spell 8690: a ten-second cast whose TELEPORT_UNITS leads to the caster's bind point,
/// <c>Player.Home</c>) as a leg of a far trip (docs/areas/playbots.md, "Travel"). Through the real paths only: the bot stops, sends
/// CMSG_USE_ITEM for the stone's bag and slot (the item and spell handlers check the cooldown, the cast, the movement interruption and move
/// the bot), waits out the cast without moving, and confirms it stands at its bind point. The planner asks <see cref="IsReady"/> and
/// <see cref="BindPlace"/>; the trip begins the leg with <see cref="TryBegin"/> and the brain drives it with <see cref="Drive"/> before anything
/// else that moves the bot. A failure after the planner's checks (the stone gone, the cooldown running, the use refused, the cast
/// interrupted, the bot elsewhere afterwards) is reported once (anomaly area <c>hearth</c>); a refusal at <see cref="TryBegin"/> is not.
/// Thread affinity: world thread; per-bot state hangs off the <see cref="Player"/> and goes with it.
/// </summary>
internal static class PlayerbotHearthstone
{
    /// <summary>The Hearthstone (item_template 6948).</summary>
    internal const uint ItemEntry = 6948;

    /// <summary>The hearthstone's on-use spell in 1.12 (Spell.dbc 8690); the leg casts whatever on-use spell the item's data names.</summary>
    internal const uint SpellId = 8690;

    /// <summary>The anomaly area of every report in this file (subsystem <c>bot-hearth</c>).</summary>
    internal const string Area = "hearth";

    private static readonly ConditionalWeakTable<Player, TravelCastLeg> Legs = [];

    /// <summary>The bot's leg, done or not (inspection and tests).</summary>
    internal static TravelCastLeg? LegOf(Player player) => Legs.TryGetValue(player, out TravelCastLeg? leg) ? leg : null;

    /// <summary>Whether a hearthstone leg is under way (not done, not failed).</summary>
    internal static bool IsActive(Player player)
        => Legs.TryGetValue(player, out TravelCastLeg? leg) && leg.Stage is TravelCastStage.Start or TravelCastStage.Casting;

    /// <summary>The reason the bot's last leg failed (inspection), null after a leg that arrived.</summary>
    internal static string? LastFailure(Player player)
        => Legs.TryGetValue(player, out TravelCastLeg? leg) && leg.Stage == TravelCastStage.Failed ? leg.Failure : null;

    /// <summary>Where the hearthstone leads: the bot's bind point, or null when it has none.</summary>
    internal static TravelPlace? BindPlace(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player.Home.IsUnset ? null : new TravelPlace(player.Home.MapId, new Vector3(player.Home.X, player.Home.Y, player.Home.Z));
    }

    /// <summary>
    /// Whether the bot could hearth now: hearthstone use on (<see cref="PlayerbotTravelOptions.Hearthstone"/>), alive, in the world and out
    /// of combat, a bind point, the stone carried in its bags, its on-use spell known to the server and off cooldown (the spell's own and its
    /// category, the item's category taking the spell's place as the server picks it, <see cref="ItemSpellCooldowns.Pick"/>). No leg begun.
    /// </summary>
    internal static bool IsReady(WorldSession session, Player player, PlayerbotOptions options) => Check(session, player, options, out _, out _, out _) is null;

    /// <summary>Begin a hearthstone leg; false (with why) when <see cref="IsReady"/> does not hold. A leg under way answers true.</summary>
    internal static bool TryBegin(WorldSession session, Player player, PlayerbotOptions options, out string? why)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        why = null;
        if (IsActive(player)) return true;
        if (Check(session, player, options, out _, out SpellInfo? spell, out TravelPlace bind) is { } refused)
        {
            why = "hearth: " + refused;
            return false;
        }

        if (PlayerbotTravel.JourneyOf(player) is { State: JourneyState.Active } journey && journey.Current?.Means != TravelMeans.Hearthstone)
            PlayerbotTravel.Cancel(player);
        PlayerbotShip.Cancel(player);
        PlayerbotMageService.Cancel(session, player);
        PlayerbotMageService.CancelMage(session, player);
        PlayerbotTaxi.Cancel(player);
        PlayerbotTram.Cancel(player);
        PlayerbotElevator.Cancel(player);
        Legs.AddOrUpdate(player, new TravelCastLeg(TravelMeans.Hearthstone, spell!.Id, bind, session.World.NowMs));
        return true;
    }

    /// <summary>
    /// One think of the leg, before anything else that moves the bot. <see cref="TravelLegStep.Idle"/>: no leg (or one dropped: the bot died,
    /// the switch went off before the cast, or a fight began before the teleport: a running cast is cancelled). The start is bounded
    /// (<see cref="PlayerbotTravelCast.StartBoundMs"/>, <see cref="PlayerbotTravelCast.MaxSendAttempts"/>), the cast by its budget. <see cref="TravelLegStep.Traveling"/>: the cast or the teleport under way, the
    /// bot must not move. <see cref="TravelLegStep.Arrived"/> and <see cref="TravelLegStep.Failed"/> are answered once; <paramref name="failure"/>
    /// then says why.
    /// </summary>
    internal static TravelLegStep Drive(WorldSession session, Player player, PlayerbotOptions options, uint elapsedMs, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        _ = elapsedMs;
        failure = null;
        if (!Legs.TryGetValue(player, out TravelCastLeg? leg) || leg.Stage is TravelCastStage.Done or TravelCastStage.Failed) return TravelLegStep.Idle;
        if (!player.IsInWorld || player.Map is null) return TravelLegStep.Traveling; // between maps: the far teleport's acknowledgement is PlayerbotMotion.Pump's
        if (!player.IsAlive)
        {
            Legs.Remove(player);
            return TravelLegStep.Idle;
        }

        if (session.Services.GetService<SpellFeature>()?.System is not { } spells)
            return Fail(player, leg, "services-unavailable", "the spell system is not available", AnomalySeverity.Warning, out failure);
        uint now = session.World.NowMs;
        if (leg.Stage == TravelCastStage.Start)
        {
            // The safe point of the leg: before the cast, a switch turned off or a fight drops it without a report.
            if (!options.Travel.Hearthstone || player.Combat.IsInCombat)
            {
                Legs.Remove(player);
                return TravelLegStep.Idle;
            }

            if (leg.Attempts == 0) leg.BeganMs = now;
            if (unchecked(now - leg.BeganMs) > PlayerbotTravelCast.StartBoundMs)
                return Fail(player, leg, "use-refused", $"the hearthstone use was not taken within {PlayerbotTravelCast.StartBoundMs} ms ({leg.Attempts} attempts)",
                    AnomalySeverity.Warning, out failure);
            return Use(session, player, spells, leg, now, out failure);
        }

        // Attacked during the cast: the cast is cancelled the way a client does and the leg dropped (no report), so the brain fights; the
        // journey begins the leg again after the fight. A teleport the cast already began is the server's and goes on.
        if (player.Combat.IsInCombat && !PlayerbotTravelCast.IsTeleporting(session, player))
        {
            Cancel(session, player);
            return TravelLegStep.Idle;
        }

        switch (PlayerbotTravelCast.Watch(session, player, spells, leg, now))
        {
            case PlayerbotTravelCast.Seen.Arrived:
                leg.Stage = TravelCastStage.Done;
                return TravelLegStep.Arrived;
            case PlayerbotTravelCast.Seen.Interrupted:
                return Fail(player, leg, "cast-interrupted", $"the hearthstone cast ended without a teleport{Reason(leg)}",
                    player.Combat.IsInCombat ? AnomalySeverity.Info : AnomalySeverity.Warning, out failure);
            case PlayerbotTravelCast.Seen.NotArrived:
                return Fail(player, leg, "not-arrived", $"the hearthstone cast went off but the bot stands at map {player.MapId} ({player.X:F0},{player.Y:F0},{player.Z:F0}), not at its bind point {Describe(leg.Destination)}",
                    AnomalySeverity.Warning, out failure);
            case PlayerbotTravelCast.Seen.Overdue:
                PlayerbotTravelCast.CancelCast(session, player, leg.SpellId);
                return Fail(player, leg, "not-arrived", $"the hearthstone cast or its teleport still runs {unchecked(now - leg.CastMs)} ms after the use (budget {leg.BudgetMs} ms)",
                    AnomalySeverity.Warning, out failure);
            default:
                return TravelLegStep.Traveling;
        }
    }

    /// <summary>
    /// Drop the leg (death, combat, a controller taking the bot over, the trip given up). A cast already running is cancelled the way a client
    /// does (CMSG_CANCEL_CAST); a teleport already begun is the server's and goes on. No report.
    /// </summary>
    internal static void Cancel(WorldSession session, Player player)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        if (!Legs.TryGetValue(player, out TravelCastLeg? leg)) return;
        if (leg.Stage == TravelCastStage.Casting) PlayerbotTravelCast.CancelCast(session, player, leg.SpellId);
        Legs.Remove(player);
    }

    private static TravelLegStep Use(WorldSession session, Player player, SpellSystem spells, TravelCastLeg leg, uint now, out string? failure)
    {
        failure = null;
        if (FindStone(player) is not { } stone)
            return Fail(player, leg, "no-hearthstone", "the hearthstone is no longer in the bags", AnomalySeverity.Warning, out failure);
        if (OnCooldown(spells, player, stone, leg.SpellId))
            return Fail(player, leg, "on-cooldown", "the hearthstone is on cooldown at the use", AnomalySeverity.Warning, out failure);
        PlayerbotThinkProbe.Phase("hearth");
        SpellInfo? spell = spells.Store.Get(leg.SpellId);
        switch (PlayerbotTravelCast.Send(session, player, spells, leg, WorldOpcode.CmsgUseItem, PlayerbotNavigation.UseItemPayload(stone.BagSlot, stone.Slot),
                    out SpellCastResult? reason))
        {
            case PlayerbotTravelCast.Sent.Taken:
                leg.Stage = TravelCastStage.Casting;
                leg.CastMs = now;
                leg.BudgetMs = PlayerbotTravelCast.BudgetOf(spell, player);
                return TravelLegStep.Traveling;
            case PlayerbotTravelCast.Sent.Retry:
                return TravelLegStep.Traveling;
            case PlayerbotTravelCast.Sent.BudgetStarved:
                return Fail(player, leg, "use-refused", "the action budget never allowed the hearthstone use", AnomalySeverity.Warning, out failure);
            case PlayerbotTravelCast.Sent.NotStill:
                return Fail(player, leg, "use-refused", "the bot could not be stood still for the hearthstone use", AnomalySeverity.Warning, out failure);
            case PlayerbotTravelCast.Sent.SessionRefused:
                return Fail(player, leg, "use-refused", "the session refused the hearthstone use (closed, settling a quest reward, or not in the world)",
                    AnomalySeverity.Warning, out failure);
            default:
                return Fail(player, leg, "use-refused", $"the server refused the hearthstone use{(reason is { } r ? ": " + r : string.Empty)}",
                    AnomalySeverity.Warning, out failure);
        }
    }

    /// <summary>Why the bot cannot hearth now, or null when it can; the stone, its spell and the bind point it leads to.</summary>
    private static string? Check(WorldSession session, Player player, PlayerbotOptions options, out Item? stone, out SpellInfo? spell, out TravelPlace bind)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        stone = null;
        spell = null;
        bind = default;
        if (!options.Travel.Hearthstone) return "hearthstone use is off";
        if (!player.IsInWorld || player.Map is null || !player.IsAlive) return "not alive in the world";
        if (player.Combat.IsInCombat) return "in combat";
        if (BindPlace(player) is not { } home) return "no bind point";
        bind = home;
        if (session.Services.GetService<SpellFeature>()?.System is not { } spells) return "no spell system";
        if (FindStone(player) is not { } found) return "no hearthstone";
        stone = found;
        if (UseSpellOf(found) is not { } spellId || spells.Store.Get(spellId) is not { } info) return "the hearthstone has no known on-use spell";
        spell = info;
        if (OnCooldown(spells, player, found, info.Id)) return "the hearthstone is on cooldown";
        return null;
    }

    /// <summary>The hearthstone in the bot's bags (not the bank), or null.</summary>
    private static Item? FindStone(Player player)
    {
        foreach (Item item in player.Inventory.AllItems)
        {
            if (item.Entry == ItemEntry && item.Count > 0 && InventorySlots.IsInventoryPos(item.BagSlot, item.Slot)) return item;
        }

        return null;
    }

    /// <summary>The item's on-use spell (trigger 0), or null.</summary>
    private static uint? UseSpellOf(Item item)
    {
        foreach (ItemSpell use in item.Template.Spells)
        {
            if (use.SpellId != 0 && use.Trigger == 0) return use.SpellId;
        }

        return null;
    }

    /// <summary>A running cooldown of the spell, or of the category the server applies to it when cast from <paramref name="stone"/>.</summary>
    private static bool OnCooldown(SpellSystem spells, Player player, Item stone, uint spellId)
        => PlayerbotTravelCast.OnCooldown(spells, player, spellId, spells.Store.Get(spellId) is { } spell ? ItemSpellCooldowns.Pick(spell, stone).Category : 0);

    private static TravelLegStep Fail(Player player, TravelCastLeg leg, string code, string why, AnomalySeverity severity, out string? failure)
    {
        leg.Stage = TravelCastStage.Failed;
        leg.Failure = "hearth: " + why;
        failure = leg.Failure;
        PlayerbotAnomalies.Report(player, Area, code, why, severity);
        return TravelLegStep.Failed;
    }

    private static string Reason(TravelCastLeg leg) => leg.FailReason is { } reason ? $" ({reason})" : string.Empty;

    internal static string Describe(TravelPlace place) => $"map {place.MapId} ({place.Position.X:F0},{place.Position.Y:F0},{place.Position.Z:F0})";
}
