using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.AntiCheat;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.AntiCheat;

/// <summary>
/// The anticheat manager (docs/areas/anticheat.md), ported from the MaNGOS Zero fork's AntiCheatMgr: every finding of the
/// movement checks (<see cref="MovementAntiCheat"/>) and of the rejection points (unknown spell cast, trade-window item use,
/// out-of-range interaction) adds its weight to the character's decaying score, and the score escalates log → GM alert →
/// rubberband → kick, never above <see cref="AntiCheatOptions.Action"/> (default: log only). Kicks count towards the
/// account's autoban, applied through <see cref="IBanStore"/> with the 1 day / 7 days / permanent ladder. Findings are
/// written to <c>character_anticheat_log</c> in batches.
/// <para>
/// Never checked: staff at or above <see cref="AntiCheatOptions.ExemptSecurity"/>, a GM with GM mode on, and the server's
/// own managed playerbot sessions. Unlike the fork, the GM alert reaches the staff online in game. Every hook runs on the
/// world thread; only the log flush and the ban run on the thread pool.
/// </para>
/// </summary>
public sealed class AntiCheatFeature(IServiceProvider services, ILogger<AntiCheatFeature> logger) : IWorldFeature
{
    /// <summary>The author written into an autoban row (also how earlier autobans are counted).</summary>
    public const string BanAuthor = "AntiCheat";

    private const string AlertPrefix = "|cffff4040[AntiCheat]|r ";
    private const float NearbyShipYards = 200f;
    private const float NearbyElevatorYards = 600f;

    private readonly Dictionary<ObjectGuid, PlayerState> _players = [];
    private readonly AntiCheatScores _scores = new();
    private readonly AutobanLedger _autoban = new();
    private readonly AntiCheatLogQueue _log = new();
    private readonly LogGate _lineGate = new(TimeSpan.FromSeconds(10));
    private readonly LogGate _flushGate = new(TimeSpan.FromSeconds(60));
    private readonly List<AntiCheatFinding> _scratch = [];
    private AntiCheatOptions _options = new();
    private WorldRuntime? _world;
    private TimeProvider _time = TimeProvider.System;
    private Task _flush = Task.CompletedTask;
    private uint _sinceFlushMs;
    private uint _sincePruneMs;
    private bool _teleportsHooked;

    /// <summary>The options in force (replaced, never edited, on <c>.reload config</c> and <c>.anticheat set</c>).</summary>
    public AntiCheatOptions Options => _options;

    /// <summary>The live scores (GM commands, tests).</summary>
    public AntiCheatScores Scores => _scores;

    /// <summary>The queued violation log (tests, diagnostics).</summary>
    public AntiCheatLogQueue Log => _log;

    /// <summary>Findings recorded since start (diagnostics and tests; exempt players add nothing).</summary>
    public long Recorded { get; private set; }

    /// <summary>Raised on the world thread for every recorded finding (tests and tools; must not throw).</summary>
    public event Action<Player, AntiCheatFinding, float, AntiCheatAction>? FindingRecorded;

    public void Attach(WorldRuntime world)
    {
        _world = world;
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        AntiCheatOptions configured = services.GetService<IOptions<AntiCheatOptions>>()?.Value ?? new AntiCheatOptions();
        IReadOnlyList<string> problems = configured.Validate();
        if (problems.Count > 0)
        {
            // The startup check refuses these before the host is built; a host without it falls back to the defaults.
            logger.LogError("AntiCheat options are invalid ({Problems}); using the defaults", string.Join(" ", problems));
            configured = new AntiCheatOptions();
        }

        _options = configured.Clone();
        world.PlayerLoggingOut += player => _players.Remove(player.Guid);
        world.WorldTick += OnWorldTick;
        logger.LogInformation("AntiCheat {State} (action ceiling {Action})", _options.Enabled ? "enabled" : "disabled", _options.Action);
    }

    public async Task StopAsync()
    {
        await _flush.ConfigureAwait(false);
        if (_log.Count > 0)
        {
            await FlushAsync(_log.Take(int.MaxValue)).ConfigureAwait(false);
        }
    }

    /// <summary>Replace the options (world thread). Every player's checks are rebuilt with them on the next packet.</summary>
    public void ApplyOptions(AntiCheatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Clone();
        _players.Clear();
    }

    // --- exemption ------------------------------------------------------------------------------------------------

    /// <summary>Whether this player is never checked: checks off, staff, GM mode, or a managed playerbot session.</summary>
    public bool IsExempt(WorldSession session, Player player)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        AntiCheatOptions options = _options;
        return !options.Enabled
            || (options.ExemptSecurity > AccountSecurity.Player && session.Security >= options.ExemptSecurity)
            || player.IsGameMaster
            || (options.ExemptManagedBots && session.IsManaged);
    }

    /// <summary>Let a player assert movement flags no aura grants (a GM tool flying or water-walking someone); <paramref name="on"/> false takes it back.</summary>
    public void Grant(Player player, MovementFlags flags, bool on)
    {
        ArgumentNullException.ThrowIfNull(player);
        PlayerState state = StateOf(player);
        state.ExplicitGrants = on ? state.ExplicitGrants | flags : state.ExplicitGrants & ~flags;
    }

    // --- movement hooks (MovementHandlers and the acknowledgement handlers) -------------------------------------------

    /// <summary>A client movement block of the player's own unit, before it is stored.</summary>
    public void BeforeMovement(WorldSession session, Player player, WorldOpcode opcode, in MovementInfo movement)
    {
        if (IsExempt(session, player))
        {
            return;
        }

        PlayerState state = StateOf(player);
        state.Checks.CheckServerPosition(player.X, player.Y, player.Z);
        var sample = new MovementSample
        {
            Opcode = opcode,
            Movement = movement,
            ReceivedMs = session.CurrentPacketReceivedMs,
            LatencyMs = session.LatencyMs,
            AllowedSpeed = AllowedSpeed(player),
            Alive = player.IsAlive,
            Rooted = player.IsRooted && player.Movement.HasFlag(MovementFlags.Root) && !player.Locomotion.Pending.HasPendingOfType(MovementChangeType.Root),
            GrantedFlags = GrantedFlags(player, state),
            Transport = ClaimOf(player, movement),
            Terrain = _options.TerrainChecks && player.Map is { } map ? state.TerrainOf(map) : null,
        };

        _scratch.Clear();
        state.Checks.Observe(sample, _scratch);
        RecordAll(session, player, state);
    }

    /// <summary>After a block (movement or acknowledgement) was stored: the position the server now holds.</summary>
    public void AfterMovement(Player player)
    {
        if (_players.TryGetValue(player.Guid, out PlayerState? state))
        {
            state.Checks.NotifyStored(player.X, player.Y, player.Z);
        }
    }

    /// <summary>An honoured movement acknowledgement: its timestamp is checked, and a knockback starts a new baseline.</summary>
    public void OnAcknowledgement(WorldSession session, Player player, uint clientTime, bool knockback)
    {
        if (IsExempt(session, player))
        {
            return;
        }

        PlayerState state = StateOf(player);
        _scratch.Clear();
        state.Checks.NotifyAcknowledgement(clientTime, session.CurrentPacketReceivedMs, _scratch);
        if (knockback)
        {
            state.Checks.NotifyKnockback();
        }

        RecordAll(session, player, state);
    }

    /// <summary>CMSG_MOVE_TIME_SKIPPED for the player's own unit.</summary>
    public void OnTimeSkipped(WorldSession session, Player player, uint skippedMs)
    {
        if (IsExempt(session, player))
        {
            return;
        }

        PlayerState state = StateOf(player);
        _scratch.Clear();
        state.Checks.NotifyTimeSkip(skippedMs, session.CurrentPacketReceivedMs, _scratch);
        RecordAll(session, player, state);
    }

    /// <summary>The server moved the player (teleport events): the next client packet is trusted.</summary>
    public void OnServerRelocation(Player player)
    {
        if (_players.TryGetValue(player.Guid, out PlayerState? state))
        {
            state.Checks.NotifyServerRelocation();
        }
    }

    // --- rejection points ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A request the server already refused that a legitimate client never sends (a spell the character does not know, an
    /// item from the trade window, a game object far out of reach). Scored like a movement finding.
    /// </summary>
    public void OnRejected(WorldSession session, Player player, AntiCheatViolation type, float weight, string detail)
    {
        if (IsExempt(session, player))
        {
            return;
        }

        _scratch.Clear();
        _scratch.Add(new AntiCheatFinding(type, weight, detail));
        RecordAll(session, player, StateOf(player));
    }

    /// <summary>
    /// CMSG_GAMEOBJ_USE refused as too far: scored only when the object is far beyond any interaction distance (20 yards plus
    /// what the run speed covers in twice the latency), so a player at the edge of the range is never scored.
    /// </summary>
    public void OnGameObjectTooFar(WorldSession session, Player player, GameObject gameObject)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        float dx = player.X - gameObject.X, dy = player.Y - gameObject.Y, dz = player.Z - gameObject.Z;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        float slack = 20f + (player.RunSpeed * 2f * session.LatencyMs / 1000f);
        if (distance > slack)
        {
            OnRejected(session, player, AntiCheatViolation.Interact, 15f, "game object use far out of range");
        }
    }

    // --- scoring and escalation ---------------------------------------------------------------------------------------

    private void RecordAll(WorldSession session, Player player, PlayerState state)
    {
        foreach (AntiCheatFinding finding in _scratch)
        {
            Record(session, player, state, finding);
        }

        _scratch.Clear();
    }

    private void Record(WorldSession session, Player player, PlayerState state, AntiCheatFinding finding)
    {
        AntiCheatOptions options = _options;
        uint now = _world?.NowMs ?? 0;
        int characterId = (int)player.Guid.Low;
        float score = _scores.Add(characterId, finding.Weight, now, options.DecayPerSecond);
        Recorded++;
        AntiCheatAction action = AntiCheatEscalation.Decide(score, options);
        FindingRecorded?.Invoke(player, finding, score, action);
        if (action == AntiCheatAction.None)
        {
            return;
        }

        if (options.Log.Persist)
        {
            long unix = _time.GetUtcNow().ToUnixTimeSeconds();
            _log.Add(new AntiCheatLogEntry(characterId, session.AccountId, (byte)finding.Type, finding.Weight, score, 1, player.MapId,
                player.X, player.Y, player.Z, finding.Detail, unix, unix), finding.Type, now, options.Log);
        }

        if (_lineGate.TryEnter(out int suppressed))
        {
            logger.LogInformation("AntiCheat: {Player} (account {Account}) {Type} '{Detail}' weight {Weight:0.#} score {Score:0} -> {Action} ({Suppressed} more findings since the last line)",
                player.Name, session.AccountId, finding.Type, finding.Detail, finding.Weight, score, action, suppressed);
        }

        Escalate(session, player, state, finding, score, action);
    }

    /// <summary>The escalation itself (the fork's Apply, the only place a countermeasure happens), already capped by the ceiling.</summary>
    private void Escalate(WorldSession session, Player player, PlayerState state, AntiCheatFinding finding, float score, AntiCheatAction action)
    {
        AntiCheatOptions options = _options;
        uint now = _world?.NowMs ?? 0;
        int characterId = (int)player.Guid.Low;
        if (action >= AntiCheatAction.GmAlert && Elapsed(ref state.LastAlertMs, now, (uint)options.GmAlertIntervalSeconds * 1000))
        {
            AlertStaff(player, finding, score, action);
        }

        if (action == AntiCheatAction.Rubberband && Elapsed(ref state.LastRubberbandMs, now, (uint)options.RubberbandIntervalMs))
        {
            Rubberband(player, state);
        }

        if (action == AntiCheatAction.Kick)
        {
            logger.LogWarning("AntiCheat: kicking {Player} (account {Account}) at score {Score:0} ({Type} '{Detail}')", player.Name, session.AccountId, score, finding.Type, finding.Detail);
            int account = session.AccountId;
            string name = player.Name;
            session.Kick();
            _scores.Set(characterId, 0f, now, options.DecayPerSecond);
            if (options.Autoban.Enabled && _autoban.AddKick(account, _time.GetUtcNow().ToUnixTimeSeconds(), options.Autoban))
            {
                _ = Task.Run(() => AutobanAsync(account, name, options.Autoban.Clone()));
            }
        }
    }

    private static bool Elapsed(ref uint? last, uint now, uint interval)
    {
        if (last is { } previous && unchecked(now - previous) < interval)
        {
            return false;
        }

        last = now;
        return true;
    }

    /// <summary>The GM alert, unlike the fork's (which only logs): a system message to every staff member online.</summary>
    private void AlertStaff(Player player, AntiCheatFinding finding, float score, AntiCheatAction action)
    {
        if (_world is null)
        {
            return;
        }

        string text = string.Create(CultureInfo.InvariantCulture,
            $"{AlertPrefix}{player.Name}: {finding.Detail} ({finding.Type}), score {score:0}, action {action}. Map {player.MapId} ({player.X:0.0}, {player.Y:0.0}, {player.Z:0.0}).");
        byte[] packet = ChatPackets.BuildSystemMessage(text);
        foreach (Player staff in _world.OnlinePlayers)
        {
            if (staff.Security > AccountSecurity.Player)
            {
                staff.Session.Send(WorldOpcode.SmsgMessagechat, packet);
            }
        }
    }

    /// <summary>Move the player back to the last position that passed every check (a near teleport; the next packet is trusted).</summary>
    public bool Rubberband(Player player) => Rubberband(player, StateOf(player));

    private bool Rubberband(Player player, PlayerState state)
    {
        if (!state.Checks.TryGetLastValid(out float x, out float y, out float z, out float o)
            || services.GetService<TeleportFeature>() is not { } teleports)
        {
            return false;
        }

        state.Checks.NotifyServerRelocation();
        return teleports.Teleports.TeleportTo(player, player.MapId, x, y, z, o);
    }

    private async Task AutobanAsync(int accountId, string characterName, AntiCheatAutobanOptions options)
    {
        try
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IBanStore>() is not { } bans)
            {
                logger.LogWarning("AntiCheat: autoban of account {Account} skipped: no ban store", accountId);
                return;
            }

            long since = _time.GetUtcNow().ToUnixTimeSeconds() - ((long)options.HistoryDays * 86400);
            IReadOnlyList<AccountBanRecord> history = await bans.GetHistoryAsync(accountId).ConfigureAwait(false);
            int earlier = history.Count(r => string.Equals(r.BannedBy, BanAuthor, StringComparison.Ordinal) && r.BanDate >= since);
            long duration = options.DurationFor(earlier);
            await bans.BanAccountAsync(new BanRequest(accountId, duration, $"AntiCheat: repeated kicks ({characterName})", BanAuthor)).ConfigureAwait(false);
            logger.LogWarning("AntiCheat: account {Account} banned {Duration} after repeated kicks (character {Character}, {Earlier} earlier autoban(s))",
                accountId, duration == 0 ? "permanently" : $"for {duration} s", characterName, earlier);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AntiCheat: autoban of account {Account} failed", accountId);
        }
    }

    // --- world tick: log flush, score pruning, teleport events -----------------------------------------------------

    private void OnWorldTick(uint diffMs)
    {
        if (!_teleportsHooked && services.GetService<TeleportFeature>() is { } teleports)
        {
            _teleportsHooked = true;
            try
            {
                teleports.Teleports.TeleportCompleted += OnServerRelocation;
                teleports.Teleports.FarTeleportExecuting += OnServerRelocation;
                teleports.Teleports.NearTeleportStarting += (player, _) => OnServerRelocation(player);
            }
            catch (InvalidOperationException)
            {
                // the teleport feature is not attached in this host: the position check still re-baselines
            }
        }

        AntiCheatOptions options = _options;
        _sincePruneMs += diffMs;
        if (_sincePruneMs >= 60_000)
        {
            _sincePruneMs = 0;
            _scores.Prune(_world?.NowMs ?? 0, options.DecayPerSecond);
        }

        _sinceFlushMs += diffMs;
        if (_sinceFlushMs < (uint)options.Log.FlushIntervalSeconds * 1000 || _log.Count == 0 || !_flush.IsCompleted)
        {
            return;
        }

        _sinceFlushMs = 0;
        IReadOnlyList<AntiCheatLogEntry> batch = _log.Take(options.Log.MaxRowsPerFlush);
        _flush = Task.Run(() => FlushAsync(batch));
    }

    /// <summary>Write one batch (one round trip). A failure is logged at most once a minute and the batch is dropped: the log never grows without bound.</summary>
    private async Task FlushAsync(IReadOnlyList<AntiCheatLogEntry> batch)
    {
        try
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IAntiCheatLogStore>() is { } store)
            {
                await store.AppendAsync(batch).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (_flushGate.TryEnter(out int suppressed))
            {
                logger.LogWarning(ex, "AntiCheat: could not write {Rows} violation log row(s); they are dropped ({Suppressed} more failures since the last line)", batch.Count, suppressed);
            }
        }
    }

    /// <summary>Write what is queued now and wait for it (<c>.anticheat report</c> reads the store; tests).</summary>
    public Task FlushNowAsync()
    {
        IReadOnlyList<AntiCheatLogEntry> batch = _log.Take(int.MaxValue);
        Task previous = _flush;
        _flush = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            await FlushAsync(batch).ConfigureAwait(false);
        });
        return _flush;
    }

    // --- GM tools ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Overwrite a character's score and apply what it warrants now under the ceiling (<c>.anticheat score</c>, the fork's
    /// SetScore: a GM drives a score to a threshold to see the response). Exemptions do not apply: the GM asked for it.
    /// </summary>
    public AntiCheatAction SetScore(Player player, float score)
    {
        ArgumentNullException.ThrowIfNull(player);
        uint now = _world?.NowMs ?? 0;
        _scores.Set((int)player.Guid.Low, score, now, _options.DecayPerSecond);
        float settled = _scores.Score((int)player.Guid.Low, now, _options.DecayPerSecond);
        AntiCheatAction action = AntiCheatEscalation.Decide(settled, _options);
        if (player.Session is WorldSession session && action >= AntiCheatAction.GmAlert)
        {
            Escalate(session, player, StateOf(player), new AntiCheatFinding(AntiCheatViolation.None, 0f, "score set by a GM"), settled, action);
        }

        return action;
    }

    /// <summary>The decayed score of a character now.</summary>
    public float ScoreOf(int characterId) => _scores.Score(characterId, _world?.NowMs ?? 0, _options.DecayPerSecond);

    /// <summary>Forget a character's score and queued rows (<c>.anticheat delete</c> also deletes the stored rows).</summary>
    public void Forget(int characterId)
    {
        _scores.Remove(characterId);
        _log.Remove(characterId);
    }

    private PlayerState StateOf(Player player)
    {
        if (!_players.TryGetValue(player.Guid, out PlayerState? state) || !ReferenceEquals(state.Checks.Options, _options))
        {
            MovementFlags grants = state?.ExplicitGrants ?? MovementFlags.None;
            state = new PlayerState(new MovementAntiCheat(_options)) { ExplicitGrants = grants };
            _players[player.Guid] = state;
        }

        return state;
    }

    /// <summary>The fastest speed the server allows: every move type, and every speed change still waiting for its ack.</summary>
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

    /// <summary>
    /// The flags the server stands behind: an aura that grants them, any order of that kind still waiting for its ack (the
    /// client may assert the flag until it has the order), and the explicit grants of <see cref="Grant"/>.
    /// </summary>
    private static MovementFlags GrantedFlags(Player player, PlayerState state)
    {
        MovementFlags granted = state.ExplicitGrants;
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

    /// <summary>
    /// What the block's transport claim is worth: a ship (MO_TRANSPORT GUID) must be one the transport system sails on this
    /// map near the player; an elevator or tram (a TRANSPORT game object) must exist on the map near the player; any other
    /// GUID is fake. Without a transport system or game object system the claim cannot be judged and is never scored.
    /// </summary>
    private TransportClaim ClaimOf(Player player, in MovementInfo movement)
    {
        if (!movement.HasFlag(MovementFlags.OnTransport))
        {
            return TransportClaim.None;
        }

        var guid = new ObjectGuid(movement.TransportGuid);
        if (player.Map is not { } map || _world is null)
        {
            return TransportClaim.Unknown;
        }

        switch (guid.High)
        {
            case HighGuid.MoTransport:
                if (TransportSystem.Of(_world) is not { } ships)
                {
                    return TransportClaim.Unknown;
                }

                if (player.Transport is { } aboard && aboard.Guid == guid)
                {
                    return TransportClaim.Known;
                }

                return ships.Find(map, guid) is { } ship && Near(ship, movement, NearbyShipYards) ? TransportClaim.Known : TransportClaim.Fake;
            case HighGuid.Transport:
                if (map.FindUpdater<GameObjectMapSystem>() is not { } objects)
                {
                    return TransportClaim.Unknown;
                }

                return objects.Find(guid) is { Type: GameObjectType.Transport } elevator && Near(elevator, movement, NearbyElevatorYards)
                    ? TransportClaim.Known : TransportClaim.Fake;
            default:
                return TransportClaim.Fake;
        }
    }

    private static bool Near(WorldObject transport, in MovementInfo movement, float yards)
    {
        float dx = transport.X - movement.X, dy = transport.Y - movement.Y;
        return (dx * dx) + (dy * dy) <= yards * yards;
    }

    private sealed class PlayerState(MovementAntiCheat checks)
    {
        public MovementAntiCheat Checks { get; } = checks;

        public MovementFlags ExplicitGrants { get; set; }

        public uint? LastAlertMs;

        public uint? LastRubberbandMs;

        private MapAntiCheatTerrain? _terrain;
        private Map? _terrainMap;

        /// <summary>The terrain probe of the player's map, kept while the player stays on it.</summary>
        public MapAntiCheatTerrain TerrainOf(Map map)
        {
            if (!ReferenceEquals(_terrainMap, map) || _terrain is null)
            {
                _terrain = new MapAntiCheatTerrain(map);
                _terrainMap = map;
            }

            return _terrain;
        }
    }
}
