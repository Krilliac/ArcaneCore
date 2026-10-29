using System.Globalization;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Death;

/// <summary>
/// H1 outcome checks for player death: did what should follow a death actually happen? Every player killed in any map of the world
/// is watched from the kill (<see cref="MapCombat.UnitKilled"/>) until it is alive again or leaves the world; once a second
/// (<see cref="WorldRuntime.WorldTick"/>) <see cref="Sweep"/> compares the player's state with what the death rules promise and reports
/// each failure once per death through <see cref="AnomalyLedger"/> subsystem <c>death</c>:
/// <list type="bullet">
/// <item><c>death/durability-not-applied</c> (Info): a creature killed the player (no player owner, not a battleground) and every worn
/// item with a durability is still at its maximum, though a death costs each 10% (at least 1 point). Instant kills and the
/// no-durability spell attribute legitimately skip the loss and cannot be told apart here, hence Info.</item>
/// <item><c>death/stuck-dying</c>: the player is still in the just-died state 30 s after the kill (KillPlayer never ran).</item>
/// <item><c>death/release-overdue</c>: the player is still an unreleased body 30 s after the 6 minute auto release should have fired
/// (maps with the release-timer flag only: instances have no auto release).</item>
/// <item><c>death/corpse-missing</c>: a released spirit has no corpse to return to.</item>
/// <item><c>death/repop-stuck</c>: a released spirit's trip to its graveyard has not run 30 s after the release (the graveyard repop is
/// held back while a movement change is unanswered).</item>
/// </list>
/// Cost: a free event handler per map, one dictionary entry per dying player, and a walk over the dying players once a second.
/// <para>Thread affinity: world thread (map combat events and the world tick).</para>
/// </summary>
public sealed class DeathOutcomeWatcher : IDisposable
{
    /// <summary>How long a player may stay in the just-died state (the next player update turns it into a body).</summary>
    public const uint StuckDyingBudgetMs = 30_000;

    /// <summary>Grace after the 6 minute auto release before it counts as overdue.</summary>
    public const uint ReleaseGraceMs = 30_000;

    /// <summary>How long a released spirit may wait for its scheduled graveyard trip.</summary>
    public const uint RepopBudgetMs = 30_000;

    private const uint SweepIntervalMs = 1_000;

    private readonly WorldRuntime _world;
    private readonly Dictionary<ObjectGuid, Watch> _watched = [];
    private uint _sinceSweepMs;
    private bool _disposed;

    /// <param name="world">The world whose maps and players are watched.</param>
    public DeathOutcomeWatcher(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        world.MapCreated += Subscribe;
        world.MapUnloading += Unsubscribe;
        world.WorldTick += OnWorldTick;
        foreach (Map map in world.Maps)
        {
            Subscribe(map);
        }
    }

    /// <summary>Number of players currently watched.</summary>
    public int WatchedCount => _watched.Count;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _world.MapCreated -= Subscribe;
        _world.MapUnloading -= Unsubscribe;
        _world.WorldTick -= OnWorldTick;
        foreach (Map map in _world.Maps)
        {
            Unsubscribe(map);
        }

        _watched.Clear();
    }

    private void Subscribe(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat)
        {
            combat.UnitKilled += OnKilled;
        }
    }

    private void Unsubscribe(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat)
        {
            combat.UnitKilled -= OnKilled;
        }
    }

    private void OnWorldTick(uint diffMs)
    {
        if (_watched.Count == 0)
        {
            return;
        }

        _sinceSweepMs += diffMs;
        if (_sinceSweepMs < SweepIntervalMs)
        {
            return;
        }

        _sinceSweepMs = 0;
        Sweep(_world.NowMs);
    }

    /// <summary>A unit died; a player starts being watched and the durability check of its death runs now (the loss is applied before the event).</summary>
    private void OnKilled(Unit? killer, Unit victim)
    {
        if (victim is not Player player)
        {
            return;
        }

        _watched[player.Guid] = new Watch(player, _world.NowMs);
        CheckDurability(killer, player);
    }

    private static void CheckDurability(Unit? killer, Player victim)
    {
        if (killer is null or Player || killer.GetCharmerOrOwnerPlayerOrSelf() is not null || DuelRules.ControllingPlayer(killer) is not null
            || victim.Map?.Template is { IsBattleground: true } || !victim.Inventory.Options.DurabilityLossEnable
            || IsChannelingOnVictim(killer, victim))
        {
            return;
        }

        bool anyDurable = false;
        foreach ((byte _, Item item) in victim.Inventory.Equipped)
        {
            if (item.MaxDurability == 0)
            {
                continue;
            }

            anyDurable = true;
            if (item.Durability != item.MaxDurability)
            {
                return; // some wear was applied
            }
        }

        if (anyDurable)
        {
            ServerAnomalies.Report(
                victim,
                ServerAnomalies.Death,
                "durability-not-applied",
                string.Create(CultureInfo.InvariantCulture, $"killed by creature {killer.Guid.Entry}: every worn item is still at full durability, a death costs each 10%"),
                AnomalySeverity.Info);
        }
    }

    private static bool IsChannelingOnVictim(Unit killer, Player victim)
    {
        if (killer.GetUInt32(UpdateFields.UnitChannelSpell) == 0)
        {
            return false;
        }

        ulong channelTarget = killer.GetUInt64(UpdateFields.UnitFieldChannelObject);
        return channelTarget == 0 || new ObjectGuid(channelTarget) == victim.Guid;
    }

    /// <summary>
    /// Check every watched player against the world clock <paramref name="nowMs"/> (<see cref="WorldRuntime.NowMs"/>); one report per
    /// failure per death. Public so a test can move the clock; the world tick calls it once a second.
    /// </summary>
    public void Sweep(uint nowMs)
    {
        if (_watched.Count == 0)
        {
            return;
        }

        List<ObjectGuid>? done = null;
        foreach ((ObjectGuid guid, Watch watch) in _watched)
        {
            Player player = watch.Player;
            if (!ReferenceEquals(_world.FindOnlinePlayer(guid), player) || player.Combat.DeathState is DeathState.Alive or DeathState.JustAlived)
            {
                (done ??= []).Add(guid);
                continue;
            }

            if (player.Map is null || player.IsLoggingOut)
            {
                continue; // in transit or leaving: the logout path releases a dying player itself
            }

            Check(watch, player, unchecked(nowMs - watch.DiedMs), nowMs);
        }

        if (done is not null)
        {
            foreach (ObjectGuid guid in done)
            {
                _watched.Remove(guid);
            }
        }
    }

    private static void Check(Watch watch, Player player, uint ageMs, uint nowMs)
    {
        if (ageMs > int.MaxValue)
        {
            return; // the clock moved backwards for this entry
        }

        switch (player.Combat.DeathState)
        {
            case DeathState.JustDied:
                if (ageMs >= StuckDyingBudgetMs && !player.IsQuestSettlementPending && !watch.StuckReported)
                {
                    watch.StuckReported = true;
                    ServerAnomalies.Report(player, ServerAnomalies.Death, "stuck-dying", string.Create(CultureInfo.InvariantCulture, $"still in the just-died state {ageMs} ms after the kill"));
                }

                break;

            case DeathState.Corpse:
                if (!watch.ReleaseReported
                    && (player.GetByte(UpdateFields.PlayerFieldBytes, 0) & MapCombat.FieldByteReleaseTimer) != 0
                    && ageMs >= CombatConstants.CorpseRepopTimeMs + ReleaseGraceMs)
                {
                    watch.ReleaseReported = true;
                    ServerAnomalies.Report(
                        player,
                        ServerAnomalies.Death,
                        "release-overdue",
                        string.Create(CultureInfo.InvariantCulture, $"still an unreleased body {ageMs} ms after the kill; the auto release fires at {CombatConstants.CorpseRepopTimeMs} ms"));
                }

                break;

            case DeathState.Dead when (player.Flags & PlayerFlags.Ghost) != 0:
                CheckGhost(watch, player, nowMs);
                break;
        }
    }

    private static void CheckGhost(Watch watch, Player player, uint nowMs)
    {
        watch.GhostSeenMs ??= nowMs;
        if (player.Combat.Corpse is null && !watch.CorpseReported)
        {
            watch.CorpseReported = true;
            ServerAnomalies.Report(player, ServerAnomalies.Death, "corpse-missing", "the released spirit has no corpse");
        }

        uint ghostMs = unchecked(nowMs - watch.GhostSeenMs.Value);
        if (!player.RepopDestinationResolved && !watch.RepopReported && ghostMs >= RepopBudgetMs && ghostMs <= int.MaxValue)
        {
            watch.RepopReported = true;
            ServerAnomalies.Report(player, ServerAnomalies.Death, "repop-stuck", string.Create(CultureInfo.InvariantCulture, $"the graveyard trip of the released spirit has not run {ghostMs} ms after the release"));
        }
    }

    private sealed class Watch(Player player, uint diedMs)
    {
        public Player Player { get; } = player;

        public uint DiedMs { get; } = diedMs;

        public uint? GhostSeenMs { get; set; }

        public bool StuckReported { get; set; }

        public bool ReleaseReported { get; set; }

        public bool CorpseReported { get; set; }

        public bool RepopReported { get; set; }
    }
}
