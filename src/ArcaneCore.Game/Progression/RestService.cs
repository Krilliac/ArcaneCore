using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Progression;

/// <summary>vmangos <c>RestType</c> (Player.h:768): where the character rests, which decides how the rest ends.</summary>
public enum RestType : byte
{
    /// <summary>REST_TYPE_NO.</summary>
    None = 0,

    /// <summary>REST_TYPE_IN_TAVERN: inside an inn's area trigger; it ends when the player is outdoors and outside the trigger.</summary>
    InTavern = 1,

    /// <summary>REST_TYPE_IN_CITY: in a capital city zone; it ends when the zone changes to a non-capital.</summary>
    InCity = 2,
}

/// <summary>The rested state a character carries across a logout (the shape the rest table stores).</summary>
/// <param name="RestBonus">The pool in experience points.</param>
/// <param name="UnixSeconds">When it was captured; offline accrual runs from here.</param>
/// <param name="WasResting">The character was resting (inn or capital city) then.</param>
public readonly record struct RestSnapshot(float RestBonus, long UnixSeconds, bool WasResting);

/// <summary>What the rest service needs from the world to end a tavern rest (implemented by the daemon's rest feature).</summary>
public interface IRestEnvironment
{
    /// <summary>The area trigger volume with this id, or null (a trigger that was unloaded ends the rest).</summary>
    AreaTriggerTemplate? FindAreaTrigger(uint triggerId);

    /// <summary>Whether the player stands outdoors (vmangos CheckAreaExploreAndOutdoor's isOutdoor; inns are indoors, so a player inside one never leaves it).</summary>
    bool IsOutdoors(Player player);
}

/// <summary>
/// Rested experience on the world thread: where a character rests (inn trigger, capital city), the pool it gains while
/// resting and while logged out, and what a login restores. The pool itself (clamp to one and a half levels, the rest
/// state byte, PLAYER_REST_STATE_EXPERIENCE, doubling of kill XP) is <see cref="PlayerProgression"/>'s; this service
/// only fills it. Reimplemented from the mangos reference (Player::SetRestType, ComputeRest, GetXPRestBonus and the
/// rest block of Player::Update, PlayerRest.cpp, Player.cpp:1247-1257, PlayerLoad.cpp:618-622).
/// <para>
/// State ownership: every call is on the world thread. The per-player state lives in a weak table keyed by the player,
/// and the resting players are also kept in one set, so the per-tick <see cref="Update"/> visits only resting players
/// and allocates nothing (the exits it finds are collected in a reused list and applied after the walk).
/// </para>
/// </summary>
public sealed class RestService
{
    /// <summary>How often a resting player's tavern trigger is re-checked (vmangos checks on movement; this is a poll, see docs/areas/rested-xp.md).</summary>
    public const uint TavernCheckIntervalMs = 1_000;

    /// <summary>The divisor of <see cref="ComputeRest"/>: 20 bubbles per level, one every 8 hours (28800 s), halved because the client doubles the value.</summary>
    private const float SecondsToXpDivisor = 1152000.0f;

    private readonly PlayerProgression _progression;
    private readonly ConditionalWeakTable<Player, State> _states = new();
    private readonly HashSet<Player> _resting = [];
    private readonly List<Player> _exits = [];

    public RestService(PlayerProgression progression, RestOptions options)
    {
        _progression = progression ?? throw new ArgumentNullException(nameof(progression));
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public RestOptions Options { get; }

    /// <summary>
    /// The gain is applied once at least this long has passed since the last one (vmangos: 10 seconds, "Freeze update";
    /// <see cref="RestOptions.AccrualIntervalSeconds"/>).
    /// </summary>
    public uint AccrualIntervalMs => Math.Max(1u, Options.AccrualIntervalSeconds) * 1000u;

    /// <summary>Players with a rest in progress (the per-tick walk of <see cref="Update"/>).</summary>
    public int RestingCount => _resting.Count;

    /// <summary>vmangos Player::ComputeRest without the rate: <paramref name="seconds"/> × next-level XP / 1152000.</summary>
    public static float ComputeRest(uint nextLevelXp, double seconds)
        => seconds <= 0 ? 0 : (float)(seconds * (nextLevelXp / SecondsToXpDivisor));

    /// <summary>The rested experience <paramref name="seconds"/> of resting in the world give <paramref name="player"/> (Rate.Rest.InGame applied).</summary>
    public float GainWhileResting(Player player, double seconds)
        => ComputeRest(NextLevelXp(player), seconds) * Rate(Options.RateInGame);

    /// <summary>
    /// The rested experience <paramref name="seconds"/> of being logged out give <paramref name="player"/>: the in-rest-place rate
    /// when it logged out resting, otherwise the wilderness rate divided by four (Player::ComputeRest, offline branch).
    /// </summary>
    public float GainWhileOffline(Player player, double seconds, bool restedPlace)
    {
        float gain = ComputeRest(NextLevelXp(player), seconds);
        return restedPlace ? gain * Rate(Options.RateOfflineInTavernOrCity) : gain * (Rate(Options.RateOfflineInWilderness) / 4.0f);
    }

    public RestType GetRestType(Player player) => _states.TryGetValue(player, out State? s) ? s.Type : RestType.None;

    /// <summary>The inn trigger a tavern rest started in (0 for none).</summary>
    public uint TavernTrigger(Player player) => _states.TryGetValue(player, out State? s) ? s.TriggerId : 0;

    /// <summary>PLAYER_FLAGS_RESTING.</summary>
    public static bool IsResting(Player player) => (player.Flags & PlayerFlags.Resting) != 0;

    /// <summary>
    /// vmangos Player::SetRestType: <see cref="RestType.None"/> clears PLAYER_FLAGS_RESTING; any other type sets it, records the
    /// trigger and restarts the gain clock at <paramref name="nowMs"/> (so setting the type again, as each capital-city zone
    /// entry does, forgets a gain that was less than ten seconds old, as the reference does).
    /// </summary>
    public void SetRestType(Player player, RestType type, uint triggerId, uint nowMs)
    {
        ArgumentNullException.ThrowIfNull(player);
        State state = _states.GetValue(player, _ => new State());
        state.Type = type;
        if (type == RestType.None)
        {
            player.Flags &= ~PlayerFlags.Resting;
            _resting.Remove(player);
            return;
        }

        player.Flags |= PlayerFlags.Resting;
        state.TriggerId = triggerId;
        state.EnterMs = nowMs;
        state.NextTavernCheckMs = nowMs + TavernCheckIntervalMs;
        _resting.Add(player);
    }

    /// <summary>
    /// The capital-city half of vmangos Player::UpdateZone (PlayerZone.cpp:343-349): a capital zone is a city rest; leaving it
    /// (any other zone) ends the rest unless it is an inn's.
    /// </summary>
    public void OnZoneEntered(Player player, AreaTemplate? zoneEntry, uint nowMs)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (zoneEntry is not null && (zoneEntry.Flags & (uint)AreaFlags.Capital) != 0)
        {
            SetRestType(player, RestType.InCity, 0, nowMs);
        }
        else if (IsResting(player) && GetRestType(player) != RestType.InTavern)
        {
            SetRestType(player, RestType.None, 0, nowMs);
        }
    }

    /// <summary>
    /// The tavern half of vmangos HandleAreaTriggerOpcode (MiscHandler.cpp:784-790): entering an inn's trigger starts a tavern
    /// rest, unless the player is resting in a city (a city rest is not overwritten). Whether the trigger is an inn is the caller's.
    /// </summary>
    public void OnTavernTrigger(Player player, uint triggerId, uint nowMs)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (GetRestType(player) != RestType.InCity)
        {
            SetRestType(player, RestType.InTavern, triggerId, nowMs);
        }
    }

    /// <summary>
    /// The rest block of vmangos Player::Update for every resting player: once ten seconds have passed since the last gain, the
    /// elapsed time is converted to rested experience and added to the pool (clamped by <see cref="PlayerProgression.SetRestBonus"/>);
    /// a tavern rest also ends here when the player is outdoors and no longer inside the trigger it started in.
    /// Per-tick cost: one set walk over resting players, no allocation.
    /// </summary>
    public void Update(uint nowMs, IRestEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (_resting.Count == 0)
        {
            return;
        }

        uint accrualMs = AccrualIntervalMs;
        foreach (Player player in _resting)
        {
            if (!_states.TryGetValue(player, out State? state))
            {
                continue; // cannot happen: a player enters the set through SetRestType, which creates its state
            }

            uint elapsed = unchecked(nowMs - state.EnterMs);
            if (elapsed >= accrualMs)
            {
                _progression.SetRestBonus(player, _progression.RestBonus(player) + GainWhileResting(player, elapsed / 1000.0));
                state.EnterMs = nowMs;
            }

            if (state.Type == RestType.InTavern && unchecked(nowMs - state.NextTavernCheckMs) < uint.MaxValue / 2)
            {
                state.NextTavernCheckMs = nowMs + TavernCheckIntervalMs;
                if (LeftTavern(player, state, environment))
                {
                    _exits.Add(player);
                }
            }
        }

        foreach (Player player in _exits)
        {
            SetRestType(player, RestType.None, 0, nowMs);
        }

        _exits.Clear();
    }

    /// <summary>
    /// What a login restores (vmangos PlayerLoad.cpp:618-622): the stored pool plus the offline gain for the time since the stored
    /// second, at the rate of the place the character logged out in. Call after the player's next-level experience is set
    /// (<see cref="PlayerProgression.InitializeLoadedPlayer"/>), because the pool is clamped by it. No stored state is a fresh pool.
    /// A stored second in the future (the clock was moved back) gives no gain.
    /// </summary>
    public void ApplyLogin(Player player, RestSnapshot? stored, long nowUnixSeconds)
    {
        ArgumentNullException.ThrowIfNull(player);
        float pool = stored?.RestBonus ?? 0;
        if (stored is { } s && nowUnixSeconds > s.UnixSeconds && float.IsFinite(pool))
        {
            pool += GainWhileOffline(player, nowUnixSeconds - s.UnixSeconds, s.WasResting);
        }

        _progression.SetRestBonus(player, pool);
    }

    /// <summary>The state to persist for <paramref name="player"/> now (vmangos Player::SaveToDB: pool, time, resting flag).</summary>
    public RestSnapshot Capture(Player player, long nowUnixSeconds)
    {
        ArgumentNullException.ThrowIfNull(player);
        return new RestSnapshot(_progression.RestBonus(player), nowUnixSeconds, IsResting(player));
    }

    /// <summary>Drop the rest state of a player that left the world (the pool lives in <see cref="PlayerProgression"/>).</summary>
    public void Forget(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _resting.Remove(player);
        _states.Remove(player);
    }

    private static bool LeftTavern(Player player, State state, IRestEnvironment environment)
    {
        if (!environment.IsOutdoors(player))
        {
            return false;
        }

        AreaTriggerTemplate? trigger = environment.FindAreaTrigger(state.TriggerId);
        return trigger is null || !AreaTriggerZone.Contains(trigger, player.MapId, player.X, player.Y, player.Z);
    }

    private static uint NextLevelXp(Player player) => player.GetUInt32(UpdateFields.PlayerNextLevelXp);

    /// <summary>A rate from configuration: negative or non-finite values count as 0 (no gain).</summary>
    private static float Rate(float rate) => float.IsFinite(rate) && rate > 0 ? rate : 0;

    private sealed class State
    {
        public RestType Type;
        public uint TriggerId;
        public uint EnterMs;
        public uint NextTavernCheckMs;
    }
}
