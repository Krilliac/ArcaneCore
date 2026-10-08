using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// The duel state machine of one world (vmangos Player::DuelComplete / UpdateDuelFlag / CheckDuelDistance and
/// WorldSession::HandleDuelAcceptedOpcode / HandleDuelCancelledOpcode). The state itself lives on the players
/// (<see cref="Player.Duel"/>); this class owns the rules that change it. Created by the world feature and found with
/// <see cref="Find"/>; a world without one cannot have a duel, and the damage path then treats every player as not dueling.
/// World thread only. Times are whole Unix seconds from <see cref="Clock"/> (vmangos <c>time(nullptr)</c>).
/// </summary>
public sealed partial class DuelService
{
    /// <summary>The "Grovel" stun the loser of a duel gets (vmangos Handlers/DuelHandler.cpp:60, Unit.cpp:964: <c>CastSpell(he, 7267, true)</c>, "beg").</summary>
    public const uint GrovelSpellId = 7267;

    private static readonly ConditionalWeakTable<WorldRuntime, DuelService> s_registered = new();

    /// <param name="options">The configured rules (<see cref="DuelOptions"/>).</param>
    /// <param name="clock">Whole Unix seconds; the world's death clock in production, a stepping clock in tests.</param>
    public DuelService(DuelOptions options, Func<long> clock)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public DuelOptions Options { get; }

    public Func<long> Clock { get; }

    /// <summary>The spell system: Grovel, negative aura removal and cast interruption. Without it those parts are skipped.</summary>
    public SpellSystem? Spells { get; set; }

    /// <summary>The combo point service: points aimed at the opponent are dropped at completion. Without it that part is skipped.</summary>
    public ComboPointService? Combos { get; set; }

    /// <summary>Late binding for <see cref="Combos"/> when the combo feature attaches after this service. Consulted only while <see cref="Combos"/> is null.</summary>
    public Func<ComboPointService?>? CombosProvider { get; set; }

    /// <summary>Make <paramref name="service"/> the duel service of <paramref name="world"/> (before the world thread starts).</summary>
    public static void Register(WorldRuntime world, DuelService service)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(service);
        s_registered.AddOrUpdate(world, service);
    }

    /// <summary>The duel service of <paramref name="world"/>, or null when none is registered.</summary>
    public static DuelService? Find(WorldRuntime world)
        => s_registered.TryGetValue(world, out DuelService? service) ? service : null;

    // --- client requests ---------------------------------------------------------------------

    /// <summary>
    /// CMSG_DUEL_ACCEPTED (vmangos DuelHandler.cpp:30-47). Ignored unless the player holds a duel it did not start, the opponent
    /// holds one too and neither has started; then both start timers are set to now and both clients get the 3000 ms countdown.
    /// A player whose quest settlement is pending is ignored too (it cannot act, docs/areas/quest-settlement).
    /// </summary>
    public void Accept(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Duel is not { } duel || player.IsQuestSettlementPending)
        {
            return;
        }

        Player opponent = duel.Opponent;
        if (ReferenceEquals(player, duel.Initiator) || opponent.Duel is not { } other || ReferenceEquals(player, opponent)
            || duel.StartTimeSeconds != 0 || other.StartTimeSeconds != 0 || duel.Finished || other.Finished)
        {
            return;
        }

        long now = Clock();
        duel.StartTimerSeconds = now;
        other.StartTimerSeconds = now;
        byte[] countdown = DuelPackets.Countdown(DuelPackets.CountdownMilliseconds);
        player.Session.Send(WorldOpcode.SmsgDuelCountdown, countdown);
        opponent.Session.Send(WorldOpcode.SmsgDuelCountdown, countdown);
    }

    /// <summary>
    /// CMSG_DUEL_CANCELLED (vmangos DuelHandler.cpp:49-71). Started: /forfeit, the canceller loses (combat stops, Grovel, WON from
    /// its point of view, so the opponent is the winner). Not started: the dialog was discarded or /forfeit came before the countdown ended (INTERRUPTED).
    /// </summary>
    public void Cancel(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Duel is not { } duel)
        {
            return;
        }

        if (duel.StartTimeSeconds != 0)
        {
            CombatStopWithPets(player, includingCast: true);
            CombatStopWithPets(duel.Opponent, includingCast: true);
            Spells?.CastSpell(player, GrovelSpellId, SpellCastTargets.ForSelf(), triggered: true);
            Complete(player, DuelCompleteType.Won);
            return;
        }

        Complete(player, DuelCompleteType.Interrupted);
    }

    // --- completion --------------------------------------------------------------------------

    /// <summary>
    /// vmangos <c>Player::DuelComplete</c> (Player.cpp:6726-6822), called on the player that lost or left: both clients get
    /// SMSG_DUEL_COMPLETE (started = type is not Interrupted); a decided duel announces SMSG_DUEL_WINNER (fled = type is not Won, winner = the
    /// opponent, loser = this player) to this player's set; the flag object goes; the negative auras each side cast on the other since the start
    /// are removed; combo points aimed at the other player are cleared; arbiter and team are reset; both halves are marked finished (each owner
    /// drops its object on its next map update, so the rest of this tick still sees the duel, vmangos Player.cpp:1132-1137). Idempotent.
    /// Limits: reflected holders are not removed (no reflected flag on this base), pets are not looked at (no pets).
    /// </summary>
    public void Complete(Player player, DuelCompleteType type)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Duel is not { Finished: false } duel)
        {
            return;
        }

        Player opponent = duel.Opponent;
        byte[] complete = DuelPackets.Complete(started: type != DuelCompleteType.Interrupted);
        player.Session.Send(WorldOpcode.SmsgDuelComplete, complete);
        opponent.Session.Send(WorldOpcode.SmsgDuelComplete, complete);

        if (type != DuelCompleteType.Interrupted)
        {
            SendWinner(player, opponent, DuelPackets.Winner(fled: type != DuelCompleteType.Won, winnerName: opponent.Name, loserName: player.Name));
        }

        RemoveFlag(player, opponent);

        // vmangos removes from the opponent the negative auras THIS player cast, and from this player those the opponent cast, that
        // were applied at or after this half's startTime (0 for a duel that never started: everything).
        RemoveHostileAuras(opponent, player.Guid, duel.StartTimeSeconds);
        RemoveHostileAuras(player, opponent.Guid, duel.StartTimeSeconds);

        if ((Combos ?? CombosProvider?.Invoke()) is { } combos)
        {
            if (combos.GetComboTarget(player) == opponent.Guid)
            {
                combos.ClearComboPoints(player);
            }

            if (combos.GetComboTarget(opponent) == player.Guid)
            {
                combos.ClearComboPoints(opponent);
            }
        }

        if (type != DuelCompleteType.Interrupted)
        {
            player.Combat.ResetExtraAttacks();
            opponent.Combat.ResetExtraAttacks();
        }
        player.DuelArbiter = 0;
        player.DuelTeam = 0;
        opponent.DuelArbiter = 0;
        opponent.DuelTeam = 0;
        if (opponent.Duel is { } otherHalf)
        {
            otherHalf.Finished = true;
        }

        duel.Finished = true;
    }

    /// <summary>
    /// vmangos <c>Unit::CombatStopWithPets(includingCast)</c> (Unit.cpp:4627-4667): leave all fighting and, with <paramref name="includingCast"/>,
    /// interrupt a non-melee cast. Pets, guardians and charmed units are not handled: this base has none (docs/areas/duels.md).
    /// </summary>
    public void CombatStopWithPets(Unit unit, bool includingCast)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (includingCast && Spells is { } spells)
        {
            spells.CancelCast(unit, 0);
            spells.CancelChannel(unit);
        }

        unit.Map?.Combat.CombatStop(unit);
    }

    private static void SendWinner(Player player, Player opponent, byte[] winner)
    {
        player.Session.Send(WorldOpcode.SmsgDuelWinner, winner);
        bool opponentSeen = false;
        if (player.Map is { } map)
        {
            foreach (Player observer in map.ObserversOf(player))
            {
                if (ReferenceEquals(observer, player))
                {
                    continue;
                }

                opponentSeen |= ReferenceEquals(observer, opponent);
                observer.Session.Send(WorldOpcode.SmsgDuelWinner, winner);
            }
        }

        if (!opponentSeen)
        {
            opponent.Session.Send(WorldOpcode.SmsgDuelWinner, winner);
        }
    }

    private static void RemoveFlag(Player player, Player opponent)
    {
        Map? map = player.Map ?? opponent.Map;
        ulong arbiter = player.DuelArbiter != 0 ? player.DuelArbiter : opponent.DuelArbiter;
        if (map is null || arbiter == 0 || map.FindObject(new ObjectGuid(arbiter)) is not GameObject flag)
        {
            return;
        }

        map.FindUpdater<GameObjectMapSystem>()?.Remove(flag);
    }

    private void RemoveHostileAuras(Unit target, ObjectGuid caster, long since)
    {
        if (Spells is not { } spells)
        {
            return;
        }

        List<uint>? spellIds = null;
        foreach (SpellAuraHolder holder in spells.GetAuras(target))
        {
            if (!holder.IsPositive && holder.CasterGuid == caster && holder.AppliedAtUnixSeconds >= since)
            {
                (spellIds ??= []).Add(holder.Spell.Id);
            }
        }

        if (spellIds is null)
        {
            return;
        }

        foreach (uint spellId in spellIds)
        {
            spells.RemoveAuras(target, spellId);
        }
    }

    // --- per-tick rules (called by MapDuel) -------------------------------------------------

    /// <summary>
    /// vmangos <c>Player::UpdateDuelFlag</c> (Player.cpp:17248-17263): once the start delay after the accept has passed, this player
    /// gets team 1 and its opponent team 2 (the first player updated is team 1, as in vmangos), and both halves record the start.
    /// </summary>
    internal void UpdateDuelFlag(Player player, long now)
    {
        if (player.Duel is not { Finished: false, StartTimerSeconds: not 0 } duel || now < duel.StartTimerSeconds + Options.StartDelaySeconds)
        {
            return;
        }

        Player opponent = duel.Opponent;
        player.DuelTeam = 1;
        opponent.DuelTeam = 2;
        duel.StartTimerSeconds = 0;
        duel.StartTimeSeconds = now;
        if (opponent.Duel is { } other)
        {
            other.StartTimerSeconds = 0;
            other.StartTimeSeconds = now;
        }
    }

    /// <summary>
    /// vmangos <c>Player::CheckDuelDistance</c> (Player.cpp:6671-6718). The flag object missing from the player's map ends the duel as
    /// fled (an unaccepted request too, unless <see cref="DuelOptions.ExpiredRequestIsSilent"/>). Distance is the 3D distance with both bounding
    /// radii (WorldObject::IsWithinDist, Object.cpp:1738-1752) against <see cref="DuelOptions.OutOfBoundsYards"/>, or the smaller
    /// <see cref="DuelOptions.ReturnInBoundsYards"/> once out. First leaving sends SMSG_DUEL_OUTOFBOUNDS, coming back SMSG_DUEL_INBOUNDS, and
    /// <see cref="DuelOptions.OutOfBoundsGraceSeconds"/> out ends it as fled. A duel requested aboard a ship (<see cref="DuelInfo.TransportGuid"/>)
    /// has no distance: the player is inside while it rides that ship and outside as soon as it is off it (Player.cpp:6685-6689).
    /// </summary>
    internal void CheckDistance(Player player, long now)
    {
        if (player.Duel is not { Finished: false } duel)
        {
            return;
        }

        if (player.Map is not { } map || map.FindObject(new ObjectGuid(player.DuelArbiter)) is not GameObject flag)
        {
            Complete(player, Options.ExpiredRequestIsSilent && duel.StartTimeSeconds == 0 && duel.StartTimerSeconds == 0
                ? DuelCompleteType.Interrupted
                : DuelCompleteType.Fled);
            return;
        }

        bool inRange;
        if (duel.TransportGuid != 0)
        {
            inRange = player.Transport is { } ship && ship.Guid.Low == duel.TransportGuid;
        }
        else
        {
            float limit = duel.OutOfBoundSeconds != 0 ? Options.ReturnInBoundsYards : Options.OutOfBoundsYards;
            float dx = player.X - flag.X;
            float dy = player.Y - flag.Y;
            float dz = player.Z - flag.Z;
            float max = limit + player.BoundingRadius + flag.BoundingRadius;
            inRange = (dx * dx) + (dy * dy) + (dz * dz) < max * max;
        }

        if (duel.OutOfBoundSeconds == 0)
        {
            if (!inRange)
            {
                duel.OutOfBoundSeconds = now;
                player.Session.Send(WorldOpcode.SmsgDuelOutofbounds, DuelPackets.OutOfBounds());
            }

            return;
        }

        if (inRange)
        {
            duel.OutOfBoundSeconds = 0;
            player.Session.Send(WorldOpcode.SmsgDuelInbounds, DuelPackets.InBounds());
        }
        else if (now >= duel.OutOfBoundSeconds + Options.OutOfBoundsGraceSeconds)
        {
            CombatStopWithPets(player, includingCast: true);
            CombatStopWithPets(duel.Opponent, includingCast: true);
            Complete(player, DuelCompleteType.Fled);
        }
    }
}
