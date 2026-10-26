using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Outcome checks for map combat (H1): states combat must never rest in, reported through <see cref="AnomalyLedger"/> as
/// subsystem <c>combat</c>. The sweep runs once a second from <see cref="Update"/>; a state must persist for its whole budget
/// before it is reported, and each episode is reported once (the episode ends when the state clears). A healthy unit costs a few
/// field reads per second and no allocation; an episode table is only created when a state first looks wrong.
/// World thread only.
/// </summary>
public sealed partial class MapCombat
{
    /// <summary>The anomaly subsystem of every check in this file.</summary>
    public const string AnomalySubsystem = "combat";

    /// <summary>Death was not processed: health 0 yet still alive, a living-only state on a dead unit, or a player stuck in JustDied.</summary>
    public const string DeathNotProcessedCode = "death-not-processed";

    /// <summary>A threat list holds a unit that is dead, left the map or was despawned.</summary>
    public const string StaleThreatCode = "stale-threat-entry";

    /// <summary>A unit is flagged in combat with nothing to fight for a long time.</summary>
    public const string CombatNeverLeftCode = "combat-never-left";

    /// <summary>An alive, out-of-combat creature that can regenerate has not gained health.</summary>
    public const string RegenStalledCode = "regen-stalled";

    /// <summary>An invariant check had to repair combat or life state (reported beside the existing warning log).</summary>
    public const string InvariantHealedCode = "invariant-healed";

    internal const uint AnomalySweepIntervalMs = 1000;
    internal const uint DeathNotProcessedBudgetMs = 3000;
    internal const uint StaleThreatBudgetMs = 3000;
    internal const uint CombatNeverLeftBudgetMs = 120_000;
    internal const uint RegenStalledBudgetMs = 30_000;

    /// <summary>A threat holder or attacker farther than this (yards, 2D) from a unit no longer counts as engaging it.</summary>
    internal const float EngagementRange = 200f;

    private sealed class Episode
    {
        public uint Ms;
        public uint Mark;
        public bool Reported;
    }

    private uint _anomalySweepMs;
    private Dictionary<Unit, Episode>? _deathEpisodes;
    private Dictionary<Unit, Episode>? _threatEpisodes;
    private Dictionary<Unit, Episode>? _stuckEpisodes;
    private Dictionary<Unit, Episode>? _regenEpisodes;

    private void SweepIfDue(uint diffMs)
    {
        _anomalySweepMs += diffMs;
        if (_anomalySweepMs < AnomalySweepIntervalMs)
        {
            return;
        }

        uint elapsed = _anomalySweepMs;
        _anomalySweepMs = 0;

        Player[] players = _map.RentPlayerSnapshot(out int playerCount);
        try
        {
            for (int i = 0; i < playerCount; i++)
            {
                Player player = players[i];
                if (player is not null && player.IsInWorld && ReferenceEquals(player.Map, _map) && !IsQuestSettlementPending(player))
                {
                    CheckUnitAnomalies(player, elapsed);
                }
            }
        }
        finally
        {
            Map.ReturnPlayerSnapshot(players);
        }

        foreach (Unit unit in _units)
        {
            if (unit is not null && unit.IsInWorld && ReferenceEquals(unit.Map, _map))
            {
                CheckUnitAnomalies(unit, elapsed);
            }
        }

        Prune(_deathEpisodes);
        Prune(_threatEpisodes);
        Prune(_stuckEpisodes);
        Prune(_regenEpisodes);
    }

    private void CheckUnitAnomalies(Unit unit, uint elapsed)
    {
        UnitCombat c = unit.Combat;
        DeathState state = c.DeathState;
        bool alive = state == DeathState.Alive;
        bool isPlayer = unit is Player;

        // Death that was never processed: health 0 while alive, a player left in JustDied (KillPlayer runs the same tick), or a dead unit
        // that still holds a melee victim or a threat list (SetDeathState clears both).
        bool deathBad = alive
            ? unit.Health == 0
            : (isPlayer && state == DeathState.JustDied) || (state != DeathState.JustAlived && (c.Victim is not null || c.HasThreatList));
        if (Advance(ref _deathEpisodes, unit, deathBad, elapsed, DeathNotProcessedBudgetMs, 0))
        {
            AnomalyLedger.Shared.Report(AnomalySubsystem, DeathNotProcessedCode, SubjectOf(unit),
                string.Create(CultureInfo.InvariantCulture, $"death was not processed: deathState={state} health={unit.Health}/{unit.MaxHealth} victim={c.Victim?.Guid.Value ?? 0} threatList={c.HasThreatList} for {DeathNotProcessedBudgetMs} ms{Where(unit)}"));
        }

        // A threat list that holds a unit which is dead, no longer in this map, or despawned (a creature's list; players hold none).
        bool threatBad = false;
        Unit? staleTarget = null;
        if (!isPlayer && alive && c.HasThreatList)
        {
            IReadOnlyList<ThreatEntry> entries = c.Threat.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                Unit target = entries[i].Target;
                if (!target.IsInWorld || !ReferenceEquals(target.Map, _map) || target.Combat.DeathState is DeathState.Corpse or DeathState.Dead)
                {
                    threatBad = true;
                    staleTarget = target;
                    break;
                }
            }
        }

        if (Advance(ref _threatEpisodes, unit, threatBad, elapsed, StaleThreatBudgetMs, 0))
        {
            AnomalyLedger.Shared.Report(AnomalySubsystem, StaleThreatCode, SubjectOf(unit),
                string.Create(CultureInfo.InvariantCulture, $"threat list holds {(staleTarget is null ? "?" : SubjectOf(staleTarget))} (deathState={staleTarget?.Combat.DeathState}, inWorld={staleTarget?.IsInWorld}) for {StaleThreatBudgetMs} ms{Where(unit)}"));
        }

        // Flagged in combat with nothing to fight: no melee victim, nobody attacking, and no live holder/threat within range. A creature
        // the creature system is not updating (outside every active cell) cannot evade until a player comes near, as in vmangos, so its
        // clock holds: the live 2026-10-17 run reported 424 creatures, mostly pairs at their spawn points, whose fight ended (the bot
        // that pulled them died or left) in the same tick the cell went inactive.
        bool stuckBad = alive && c.IsInCombat && c.Victim is null && c.AttackersInternal.Count == 0 && !IsEngaged(unit, c, isPlayer);
        if (stuckBad && unit is Creatures.Creature { IsFrozenOutsideActiveCells: true })
        {
            // neither good nor bad: keep the episode, add no time
        }
        else if (Advance(ref _stuckEpisodes, unit, stuckBad, elapsed, CombatNeverLeftBudgetMs, 0))
        {
            AnomalyLedger.Shared.Report(AnomalySubsystem, CombatNeverLeftCode, SubjectOf(unit),
                string.Create(CultureInfo.InvariantCulture, $"in combat {CombatNeverLeftBudgetMs} ms with no victim, attacker or reachable threat holder (threatenedBy={c.ThreatenedByInternal.Count} threatList={c.HasThreatList}); {StuckCombatFacts(unit, c)}{Where(unit)}"));
        }

        // Regeneration stalled: an alive creature out of combat, below full health, whose health did not move.
        bool regenBad = !isPlayer && alive && !c.IsInCombat && c.Victim is null && unit.Health > 0 && unit.Health < unit.MaxHealth
            && (unit is not ICombatCreature creature || creature.RegeneratesHealth)
            && !Environment.IsPolymorphed(unit);
        if (Advance(ref _regenEpisodes, unit, regenBad, elapsed, RegenStalledBudgetMs, unit.Health))
        {
            AnomalyLedger.Shared.Report(AnomalySubsystem, RegenStalledCode, SubjectOf(unit),
                string.Create(CultureInfo.InvariantCulture, $"health stayed {unit.Health}/{unit.MaxHealth} for {RegenStalledBudgetMs} ms out of combat{Where(unit)}"));
        }
    }

    /// <summary>
    /// Whether anything still keeps <paramref name="unit"/> in combat. A creature is engaged while it holds a threat list; a player
    /// while a living creature that holds it on its threat list is in this map within <see cref="EngagementRange"/>.
    /// </summary>
    private bool IsEngaged(Unit unit, UnitCombat c, bool isPlayer)
    {
        if (!isPlayer)
        {
            return c.HasThreatList;
        }

        float limit = EngagementRange * EngagementRange;
        foreach (Unit holder in c.ThreatenedByInternal)
        {
            if (holder.IsInWorld && ReferenceEquals(holder.Map, _map) && IsAliveState(holder))
            {
                float dx = holder.X - unit.X;
                float dy = holder.Y - unit.Y;
                if ((dx * dx) + (dy * dy) <= limit)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Track one episode. While <paramref name="bad"/> the time accumulates (restarting when <paramref name="mark"/> changes);
    /// true exactly once, when the budget is reached. A good reading ends the episode.
    /// </summary>
    private static bool Advance(ref Dictionary<Unit, Episode>? table, Unit unit, bool bad, uint elapsed, uint budget, uint mark)
    {
        if (!bad)
        {
            if (table is { Count: > 0 })
            {
                table.Remove(unit);
            }

            return false;
        }

        table ??= new Dictionary<Unit, Episode>(ReferenceEqualityComparer.Instance);
        if (!table.TryGetValue(unit, out Episode? episode))
        {
            episode = new Episode { Mark = mark };
            table.Add(unit, episode);
        }

        if (episode.Mark != mark)
        {
            episode.Mark = mark;
            episode.Ms = 0;
            episode.Reported = false;
        }

        episode.Ms += elapsed;
        if (episode.Ms < budget || episode.Reported)
        {
            return false;
        }

        episode.Reported = true;
        return true;
    }

    private void Prune(Dictionary<Unit, Episode>? table)
    {
        if (table is not { Count: > 0 })
        {
            return;
        }

        List<Unit>? gone = null;
        foreach (Unit unit in table.Keys)
        {
            if (!unit.IsInWorld || !ReferenceEquals(unit.Map, _map))
            {
                (gone ??= []).Add(unit);
            }
        }

        if (gone is not null)
        {
            foreach (Unit unit in gone)
            {
                table.Remove(unit);
            }
        }
    }

    private void ForgetEpisodes(Unit unit)
    {
        _deathEpisodes?.Remove(unit);
        _threatEpisodes?.Remove(unit);
        _stuckEpisodes?.Remove(unit);
        _regenEpisodes?.Remove(unit);
    }

    /// <summary>
    /// What a combat/combat-never-left report needs to find the cause: who last put the unit in combat and how often since the flag was
    /// last cleared, whether the combat timer can end it, and for a creature its entry, summon kind, AI, motion and owner (live 2026-10-18:
    /// the remaining reports are on player pets, GUID entry = pet number, which the combat timer should have ended).
    /// </summary>
    private string StuckCombatFacts(Unit unit, UnitCombat c)
    {
        string facts = string.Create(CultureInfo.InvariantCulture,
            $"set {c.DescribeLastCombatSet()}, pvp timer {UsesPvpCombatTimer(unit)} ({c.CombatTimer} ms)");
        if (unit is not Creatures.Creature creature)
        {
            return facts;
        }

        ObjectGuid ownerGuid = creature.CharmerOrOwnerGuid;
        string owner = "none";
        if (!ownerGuid.IsEmpty)
        {
            Unit? found = _map.FindPlayer(ownerGuid) ?? _map.FindObject(ownerGuid) as Unit;
            owner = found is null
                ? string.Create(CultureInfo.InvariantCulture, $"{ownerGuid} not on this map")
                : string.Create(CultureInfo.InvariantCulture,
                    $"{SubjectOf(found)} {found.Combat.DeathState} combat={found.Combat.IsInCombat} victim={(found.Combat.Victim is null ? "none" : "yes")} thr={found.Combat.ThreatenedByInternal.Count} {MathF.Sqrt(((found.X - unit.X) * (found.X - unit.X)) + ((found.Y - unit.Y) * (found.Y - unit.Y))):F0}yd");
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{facts}; entry {creature.Entry} {creature.Summon?.Kind.ToString() ?? "no summon"} {creature.AI?.GetType().Name ?? "no AI"} {creature.Motion.CurrentType}; owner {owner}");
    }

    /// <summary>Stable identity of a unit for the ledger (no positions or counters).</summary>
    internal static string SubjectOf(Unit unit)
        => unit is Player player
            ? string.Create(CultureInfo.InvariantCulture, $"player (guid {player.Guid.Low})")
            : string.Create(CultureInfo.InvariantCulture, $"creature {unit.Guid.Entry} spawn {unit.Guid.Counter}");

    private string Where(Unit unit)
        => string.Create(CultureInfo.InvariantCulture, $" | map {_map.MapId} instance {_map.InstanceId} at {unit.X:F1},{unit.Y:F1},{unit.Z:F1}");

    /// <summary>Report that an invariant check repaired state; <paramref name="what"/> names the repair (the existing warning stays beside it).</summary>
    private void ReportHealed(Unit unit, string what, string detail)
        => AnomalyLedger.Shared.Report(AnomalySubsystem, InvariantHealedCode + "-" + what, SubjectOf(unit), detail + Where(unit));
}
