using System.Numerics;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>Which side of a defense call a responder takes.</summary>
internal enum DefenseSide : byte
{
    /// <summary>Same team as the caller.</summary>
    Defender,

    /// <summary>The other team, joining the attackers.</summary>
    Joiner,
}

/// <summary>What <see cref="PlayerbotDefenseBoard.Report"/> did with a report.</summary>
internal enum DefenseReportOutcome : byte
{
    /// <summary>A new call was opened.</summary>
    Opened,

    /// <summary>The caller already has an open call; it was refreshed.</summary>
    Refreshed,

    /// <summary>The caller named an attacker that an open call of its team already names; it joined that call.</summary>
    Joined,

    /// <summary>Refused: the caller opened a call less than the caller cooldown ago.</summary>
    CallerCooldown,

    /// <summary>Refused: this attacker already raised a call less than the provoke throttle ago.</summary>
    AttackerThrottled,

    /// <summary>Refused: the (map, zone) already holds the maximum of open calls.</summary>
    ZoneFull,

    /// <summary>Refused: the map instance already holds the maximum of open calls.</summary>
    MapFull,

    /// <summary>Refused: a position is not finite.</summary>
    Invalid,
}

/// <summary>The result of a report: the outcome and the call it opened or refreshed (0 when refused).</summary>
internal readonly record struct DefenseReport(DefenseReportOutcome Outcome, int CallId)
{
    /// <summary>True when the report opened or refreshed a call.</summary>
    public bool Accepted => Outcome is DefenseReportOutcome.Opened or DefenseReportOutcome.Refreshed or DefenseReportOutcome.Joined;
}

/// <summary>
/// One call for help. Only guids and positions: no <c>Player</c> reference, so a disconnected bot is never held alive by the board.
/// World thread only; owned and mutated by <see cref="PlayerbotDefenseBoard"/> alone.
/// </summary>
internal sealed class DefenseCall
{
    /// <summary>A caller counts as live for this long after its last report (design section 1.1).</summary>
    public const long LiveWindowMs = 10_000;

    /// <summary>A call takes on fresh responders only while a caller reported this recently (design section 1.6); responders already assigned keep going through grace.</summary>
    public const long FreshResponderWindowMs = 60_000;

    internal DefenseCall(int id, uint mapId, uint instanceId, uint zoneId, uint areaId, Team callerTeam, Vector3 origin, long openedMs, int level, long graceMs, long maxOpenMs)
    {
        Id = id;
        MapId = mapId;
        InstanceId = instanceId;
        ZoneId = zoneId;
        AreaId = areaId;
        CallerTeam = callerTeam;
        Origin = origin;
        Centre = origin;
        OpenedMs = openedMs;
        LastLiveMs = openedMs;
        Level = level;
        GraceMs = graceMs;
        MaxOpenMs = maxOpenMs;
    }

    /// <summary>Board-unique id, never reused.</summary>
    public int Id { get; }

    /// <summary>Map id.</summary>
    public uint MapId { get; }

    /// <summary>Map instance id.</summary>
    public uint InstanceId { get; }

    /// <summary>Zone id at the first report.</summary>
    public uint ZoneId { get; }

    /// <summary>Area id at the first report.</summary>
    public uint AreaId { get; }

    /// <summary>Team of the callers.</summary>
    public Team CallerTeam { get; }

    /// <summary>The first caller's position. The centre never strays more than <see cref="PlayerbotDefenseBoard.MaxCentreDriftYards"/> from it.</summary>
    public Vector3 Origin { get; }

    /// <summary>The call's location: the centroid of the live callers, clamped around <see cref="Origin"/>; kept as it was when no caller is live.</summary>
    public Vector3 Centre { get; internal set; }

    /// <summary>Clock reading when the call opened.</summary>
    public long OpenedMs { get; internal set; }

    /// <summary>Clock reading of the latest accepted report.</summary>
    public long LastLiveMs { get; internal set; }

    /// <summary>The first caller's level.</summary>
    public int Level { get; }

    /// <summary>Grace after the last live report in which the call stays open. The board re-stamps it from the live options at every use.</summary>
    public long GraceMs { get; internal set; }

    /// <summary>A call closes this long after it opened, however live it is. The board re-stamps it from the live options at every use.</summary>
    public long MaxOpenMs { get; internal set; }

    /// <summary>Caller-side bots that reported, by guid, with their last position and report time.</summary>
    public Dictionary<ulong, (Vector3 Pos, long LastMs)> Callers { get; } = [];

    /// <summary>The provoking players the callers named.</summary>
    public HashSet<ulong> Attackers { get; } = [];

    /// <summary>Responders of the caller's side.</summary>
    public HashSet<ulong> Defenders { get; } = [];

    /// <summary>Responders of the other side.</summary>
    public HashSet<ulong> Joiners { get; } = [];

    /// <summary>True when a report arrived within <see cref="LiveWindowMs"/>.</summary>
    public bool IsLive(long now) => now - LastLiveMs < LiveWindowMs;

    /// <summary>
    /// Live, or inside the grace window, and not older than <see cref="MaxOpenMs"/>. The parentheses matter: <c>&amp;&amp;</c> binds tighter than <c>||</c>,
    /// so without them a live call would never be closed by the maximum age (review finding 2).
    /// </summary>
    public bool IsOpen(long now) => (IsLive(now) || now - LastLiveMs < GraceMs) && now - OpenedMs < MaxOpenMs;

    /// <summary>True while the latest report is less than <see cref="FreshResponderWindowMs"/> old: the call may take on fresh responders.</summary>
    public bool AcceptsResponders(long now) => now - LastLiveMs < FreshResponderWindowMs;

    /// <summary>Callers whose last report is within <see cref="LiveWindowMs"/> of <paramref name="now"/>.</summary>
    public int LiveCallerCount(long now)
    {
        int n = 0;
        foreach (var caller in Callers.Values)
        {
            if (now - caller.LastMs < LiveWindowMs)
                n++;
        }
        return n;
    }
}

/// <summary>
/// The call board of defense calls (design: war-mode-defense, sections 1.1, 1.2 and 1.6). A bot that is attacked reports here; the lane reads
/// <see cref="OpenCalls"/> and assigns responders. World thread only, memory only, no locks (like the world event board). Holds guids and positions,
/// no world objects, and reads time from the injected clock, so tests drive it with a manual clock. Grace and the maximum age come from the live options unless the constructor pins them.
/// Calls merge by distance when <see cref="PlayerbotPvpDefenseOptions.MergeRadiusYards"/> is above 0: a report joins an open call of its team within that radius, and
/// <see cref="Sweep"/> folds converging calls together with <see cref="Absorb"/>.
/// </summary>
internal sealed class PlayerbotDefenseBoard
{
    /// <summary>Callers kept per call; further callers are ignored.</summary>
    public const int MaxCallersPerCall = 8;

    /// <summary>Attackers kept per call.</summary>
    public const int MaxAttackersPerCall = 16;

    /// <summary>A call's centre stays within this many yards of its origin (the centroid is clamped to it).</summary>
    public const float MaxCentreDriftYards = 150f;

    /// <summary>A call closes this long after it opened, however live it is (design default 600 s).</summary>
    public const long DefaultMaxOpenMs = 600_000;

    private readonly Func<long> _clock;
    private readonly Func<PlayerbotPvpDefenseOptions> _options;
    private readonly long? _graceMs;
    private readonly long? _maxOpenMs;
    private readonly Dictionary<(uint Map, uint Instance), List<DefenseCall>> _byMap = [];
    private readonly Dictionary<int, int> _redirect = [];
    private readonly Dictionary<ulong, long> _callerNextNewCall = [];
    private readonly Dictionary<ulong, long> _attackerNextCall = [];
    private int _nextId = 1;

    /// <param name="clock">Milliseconds on the one clock all board times use.</param>
    /// <param name="options">The live options, read at every use.</param>
    /// <param name="graceMs">Pins the grace after the last live report; null reads <see cref="PlayerbotPvpDefenseOptions.GraceSeconds"/> live.</param>
    /// <param name="maxOpenMs">Pins the maximum age of a call; null reads <see cref="PlayerbotPvpDefenseOptions.MaxOpenSeconds"/> live.</param>
    public PlayerbotDefenseBoard(Func<long> clock, Func<PlayerbotPvpDefenseOptions> options, long? graceMs = null, long? maxOpenMs = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        if (graceMs is { } pinnedGrace) ArgumentOutOfRangeException.ThrowIfNegative(pinnedGrace);
        if (maxOpenMs is { } pinnedMaxOpen) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pinnedMaxOpen);
        _clock = clock;
        _options = options;
        _graceMs = graceMs;
        _maxOpenMs = maxOpenMs;
    }

    /// <summary>
    /// A bot that is being attacked reports. Refreshes the caller's open call, or joins the open call of its team that already names the attacker, otherwise opens a new one subject to the per-caller cooldown, the per-attacker
    /// provoke throttle, the per-(map, zone) cap and the per-map cap. Refreshing and joining are never limited.
    /// </summary>
    public DefenseReport Report(ulong caller, ulong attacker, Team team, uint mapId, uint instanceId, uint zoneId, uint areaId, Vector3 position, int level)
    {
        if (!IsFinite(position))
            return new DefenseReport(DefenseReportOutcome.Invalid, 0);

        long now = _clock();
        var options = _options();
        var key = (mapId, instanceId);

        if (_byMap.TryGetValue(key, out var calls))
        {
            foreach (var call in calls)
            {
                Stamp(call, options);
                if (!call.IsOpen(now))
                    continue;
                bool known = call.Callers.ContainsKey(caller);
                // Same fight: a caller of the same team that names an attacker the call already names joins it, with no radius test (merging by distance is a later phase).
                if (known || (call.CallerTeam == team && call.Attackers.Contains(attacker)))
                {
                    Touch(call, caller, attacker, position, now);
                    return new DefenseReport(known ? DefenseReportOutcome.Refreshed : DefenseReportOutcome.Joined, call.Id);
                }
            }

            // Merge on report: the first open call of the team within the merge radius (ground plane); the list is in id order, so the lowest id wins.
            if (options.MergeRadiusYards > 0)
            {
                foreach (var call in calls)
                {
                    if (call.IsOpen(now) && call.CallerTeam == team && Within(call.Centre, position, options.MergeRadiusYards))
                    {
                        Touch(call, caller, attacker, position, now);
                        return new DefenseReport(DefenseReportOutcome.Joined, call.Id);
                    }
                }
            }
        }

        if (_callerNextNewCall.TryGetValue(caller, out long callerNext) && now < callerNext)
            return new DefenseReport(DefenseReportOutcome.CallerCooldown, 0);
        if (_attackerNextCall.TryGetValue(attacker, out long attackerNext) && now < attackerNext)
            return new DefenseReport(DefenseReportOutcome.AttackerThrottled, 0);

        int inZone = 0;
        int inMap = 0;
        if (calls is not null)
        {
            foreach (var call in calls)
            {
                if (!call.IsOpen(now))
                    continue;
                inMap++;
                if (call.ZoneId == zoneId)
                    inZone++;
            }
        }
        if (inZone >= options.MaxActiveCallsPerZone)
            return new DefenseReport(DefenseReportOutcome.ZoneFull, 0);
        if (inMap >= options.MaxOpenCallsPerMap)
            return new DefenseReport(DefenseReportOutcome.MapFull, 0);

        if (calls is null)
            _byMap[key] = calls = [];
        var opened = new DefenseCall(_nextId++, mapId, instanceId, zoneId, areaId, team, position, now, level, GraceOf(options), MaxOpenOf(options));
        calls.Add(opened);
        Touch(opened, caller, attacker, position, now);
        _callerNextNewCall[caller] = now + options.CallerCooldownSeconds * 1000L;
        _attackerNextCall[attacker] = now + options.ProvokeThrottleSeconds * 1000L;
        return new DefenseReport(DefenseReportOutcome.Opened, opened.Id);
    }

    /// <summary>The board's clock now (the one every call time uses): lets a responder ask <see cref="DefenseCall.IsOpen"/> on the right clock.</summary>
    public long NowMs => _clock();

    /// <summary>The call with this id, following the redirect of an absorbed call; null when it is gone (swept). The call may be closed: check <see cref="DefenseCall.IsOpen"/>.</summary>
    public DefenseCall? Find(int id)
    {
        // A redirect chain is at most as long as the number of redirects; the bound guards against a cycle.
        for (int hops = 0; hops <= _redirect.Count; hops++)
        {
            if (!_redirect.TryGetValue(id, out int next))
                break;
            id = next;
        }
        foreach (var calls in _byMap.Values)
        {
            foreach (var call in calls)
            {
                if (call.Id == id)
                {
                    Stamp(call, _options());
                    return call;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Records <paramref name="guid"/> as a responder of the call. False when the call is gone, closed or has had no live report for <see cref="DefenseCall.FreshResponderWindowMs"/>, the bot already answers it, or the side is full:
    /// <see cref="PlayerbotPvpDefenseOptions.MaxRespondersPerSide"/> for defenders, and for joiners also defenders + live callers + 1, so reinforcements
    /// do not gank the caller before defenders arrive.
    /// </summary>
    public bool Assign(int id, ulong guid, DefenseSide side)
    {
        var call = Find(id);
        if (call is null)
            return false;
        long now = _clock();
        if (!call.IsOpen(now) || !call.AcceptsResponders(now))
            return false;
        if (call.Defenders.Contains(guid) || call.Joiners.Contains(guid))
            return false;

        int max = _options().MaxRespondersPerSide;
        if (side == DefenseSide.Defender)
            return call.Defenders.Count < max && call.Defenders.Add(guid);

        int joinerCap = Math.Min(max, call.Defenders.Count + call.LiveCallerCount(now) + 1);
        return call.Joiners.Count < joinerCap && call.Joiners.Add(guid);
    }

    /// <summary>Drops <paramref name="guid"/> from the responders of the call (following the redirect). No effect when it is not one.</summary>
    public void Release(int id, ulong guid)
    {
        var call = Find(id);
        if (call is null)
            return;
        call.Defenders.Remove(guid);
        call.Joiners.Remove(guid);
    }

    /// <summary>
    /// Folds call <paramref name="absorbedId"/> into <paramref name="survivorId"/> (the lower id should survive): the survivor takes the union of callers,
    /// attackers and responders (caller and attacker caps re-applied, no responder evicted), keeps the earlier open time and the later live time, and the
    /// absorbed id redirects to it. False when either call is missing, they are the same call, or they are on different map instances.
    /// </summary>
    public bool Absorb(int absorbedId, int survivorId)
    {
        var absorbed = Find(absorbedId);
        var survivor = Find(survivorId);
        if (absorbed is null || survivor is null || ReferenceEquals(absorbed, survivor)
            || absorbed.MapId != survivor.MapId || absorbed.InstanceId != survivor.InstanceId)
            return false;

        foreach (var (guid, caller) in absorbed.Callers)
        {
            if (survivor.Callers.TryGetValue(guid, out var mine))
            {
                if (caller.LastMs > mine.LastMs)
                    survivor.Callers[guid] = caller;
            }
            else if (survivor.Callers.Count < MaxCallersPerCall)
                survivor.Callers[guid] = caller;
        }
        foreach (ulong attacker in absorbed.Attackers)
        {
            if (survivor.Attackers.Count >= MaxAttackersPerCall)
                break;
            survivor.Attackers.Add(attacker);
        }
        survivor.Defenders.UnionWith(absorbed.Defenders);
        survivor.Joiners.UnionWith(absorbed.Joiners);
        survivor.OpenedMs = Math.Min(survivor.OpenedMs, absorbed.OpenedMs);
        survivor.LastLiveMs = Math.Max(survivor.LastLiveMs, absorbed.LastLiveMs);
        survivor.Centre = Centroid(survivor, _clock());

        int absorbedKey = absorbed.Id;
        _byMap[(absorbed.MapId, absorbed.InstanceId)].Remove(absorbed);
        // Anything that pointed at the absorbed call now points at the survivor.
        foreach (int from in _redirect.Where(r => r.Value == absorbedKey).Select(r => r.Key).ToList())
            _redirect[from] = survivor.Id;
        _redirect[absorbedKey] = survivor.Id;
        return true;
    }

    /// <summary>Appends the open calls, in id order, to <paramref name="into"/> (a scratch list the caller owns and clears).</summary>
    public void OpenCalls(List<DefenseCall> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        long now = _clock();
        var options = _options();
        int start = into.Count;
        foreach (var calls in _byMap.Values)
        {
            foreach (var call in calls)
            {
                Stamp(call, options);
                if (call.IsOpen(now))
                    into.Add(call);
            }
        }
        into.Sort(start, into.Count - start, Comparer<DefenseCall>.Create(static (a, b) => a.Id.CompareTo(b.Id)));
    }

    /// <summary>
    /// Once per pass: drops closed calls, redirects whose target is gone, and cooldown and throttle entries that have expired, so no dictionary grows
    /// without bound.
    /// </summary>
    public void Sweep()
    {
        long now = _clock();
        var options = _options();
        List<(uint Map, uint Instance)>? emptied = null;
        foreach (var (key, calls) in _byMap)
        {
            foreach (var call in calls)
                Stamp(call, options);
            calls.RemoveAll(c => !c.IsOpen(now));
            MergeConverging(calls, options.MergeRadiusYards);
            if (calls.Count == 0)
                (emptied ??= []).Add(key);
        }
        if (emptied is not null)
        {
            foreach (var key in emptied)
                _byMap.Remove(key);
        }

        if (_redirect.Count > 0)
        {
            var live = new HashSet<int>();
            foreach (var calls in _byMap.Values)
            {
                foreach (var call in calls)
                    live.Add(call.Id);
            }
            foreach (int from in _redirect.Where(r => !live.Contains(r.Value)).Select(r => r.Key).ToList())
                _redirect.Remove(from);
        }

        foreach (ulong guid in _callerNextNewCall.Where(e => e.Value <= now).Select(e => e.Key).ToList())
            _callerNextNewCall.Remove(guid);
        foreach (ulong guid in _attackerNextCall.Where(e => e.Value <= now).Select(e => e.Key).ToList())
            _attackerNextCall.Remove(guid);
    }

    /// <summary>Entries held in the redirect, cooldown and throttle dictionaries (for the sweep tests).</summary>
    internal (int Redirects, int CallerCooldowns, int AttackerThrottles) BookkeepingCounts => (_redirect.Count, _callerNextNewCall.Count, _attackerNextCall.Count);

    /// <summary>
    /// Folds every call into the lowest-id call of its team whose centre is within <paramref name="radius"/> yards (ground plane). After a fold the scan of that survivor
    /// restarts, because the moved centre can bring a third call into range. <paramref name="calls"/> is in id order, so index <c>i</c> is always the lower id.
    /// </summary>
    private void MergeConverging(List<DefenseCall> calls, int radius)
    {
        if (radius <= 0)
            return;
        for (int i = 0; i < calls.Count; i++)
        {
            int j = i + 1;
            while (j < calls.Count)
            {
                DefenseCall survivor = calls[i];
                DefenseCall other = calls[j];
                if (survivor.CallerTeam == other.CallerTeam && Within(survivor.Centre, other.Centre, radius) && Absorb(other.Id, survivor.Id))
                    j = i + 1; // Absorb removed calls[j] from the list
                else
                    j++;
            }
        }
    }

    private long GraceOf(PlayerbotPvpDefenseOptions options) => _graceMs ?? options.GraceSeconds * 1000L;

    private long MaxOpenOf(PlayerbotPvpDefenseOptions options) => _maxOpenMs ?? options.MaxOpenSeconds * 1000L;

    /// <summary>Applies the live grace and maximum age to a call, so a reload takes effect on calls already open.</summary>
    private void Stamp(DefenseCall call, PlayerbotPvpDefenseOptions options)
    {
        call.GraceMs = GraceOf(options);
        call.MaxOpenMs = MaxOpenOf(options);
    }

    /// <summary>True when the two points are no more than <paramref name="yards"/> apart in the ground plane.</summary>
    private static bool Within(Vector3 a, Vector3 b, int yards)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy) <= (float)yards * yards;
    }

    private static void Touch(DefenseCall call, ulong caller, ulong attacker, Vector3 position, long now)
    {
        if (call.Callers.ContainsKey(caller) || call.Callers.Count < MaxCallersPerCall)
            call.Callers[caller] = (position, now);
        if (call.Attackers.Count < MaxAttackersPerCall)
            call.Attackers.Add(attacker);
        call.LastLiveMs = now;
        call.Centre = Centroid(call, now);
    }

    /// <summary>
    /// The centroid of the live callers, clamped to <see cref="MaxCentreDriftYards"/> around the origin. With no live caller the centre is kept (never NaN:
    /// no division by a zero count).
    /// </summary>
    private static Vector3 Centroid(DefenseCall call, long now)
    {
        Vector3 sum = Vector3.Zero;
        int n = 0;
        foreach (var caller in call.Callers.Values)
        {
            if (now - caller.LastMs >= DefenseCall.LiveWindowMs)
                continue;
            sum += caller.Pos;
            n++;
        }
        if (n == 0)
            return call.Centre;

        Vector3 centroid = sum / n;
        Vector3 drift = centroid - call.Origin;
        float length = drift.Length();
        if (length > MaxCentreDriftYards)
            centroid = call.Origin + drift * (MaxCentreDriftYards / length);
        return centroid;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
