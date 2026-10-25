using System.Globalization;
using System.Numerics;
using System.Text;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Bots;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.EventScenarios;

/// <summary>One bot a scenario group has claimed, and how far it has got (memory only).</summary>
internal sealed class ScenarioClaim(Guid botId, string name, int slot)
{
    public Guid BotId { get; } = botId;

    public string Name { get; } = name;

    /// <summary>Its place in the group's start ring and its defend post (round robin).</summary>
    public int Slot { get; } = slot;

    public bool Online { get; set; }

    public bool Alive { get; set; }

    public bool AtLevel { get; set; }

    /// <summary>
    /// Stands at the group's start (seen so, or teleported there and arrived). Taken back for a siege group whose siege has not started when the
    /// bot is found away from the start again, so it is placed anew and neither counts ready nor is enlisted until it is back.
    /// </summary>
    public bool Placed { get; set; }

    /// <summary>Milliseconds on the engine clock since the claim was first seen held by another system or by a GM's defend post, -1 while free.</summary>
    public long BusySinceMs { get; set; } = -1;

    /// <summary>The bot holds the scenario's stay at the start as of the last pass (siege groups, while the siege waits).</summary>
    public bool StaySet { get; set; }

    /// <summary>Milliseconds on the engine clock since a placed bot was first seen away from the start, -1 while it is there.</summary>
    public long AwaySinceMs { get; set; } = -1;

    /// <summary>World time before which a refused stay is not tried again.</summary>
    public long NextStayMs { get; set; }

    /// <summary>The claim is given up (busy too long): removed at the end of the group's pass so a replacement can be claimed or created.</summary>
    public bool Released { get; set; }

    public bool DefendSet { get; set; }

    /// <summary>Milliseconds on the engine clock since the claim was last seen offline, -1 while online.</summary>
    public long OfflineSinceMs { get; set; } = -1;

    public long NextStartMs { get; set; }

    /// <summary>World time before which a refused defend post is not tried again.</summary>
    public long NextDefendMs { get; set; }

    public long TeleportAtMs { get; set; } = -1;

    public int TeleportAttempts { get; set; }

    /// <summary>Why the step the bot is stuck on was refused (<c>step: code</c>), null when the last attempt went through.</summary>
    public string? Refusal { get; set; }
}

/// <summary>The state of one group of a wanted event.</summary>
internal sealed class ScenarioGroupRun(EventScenarioGroup group)
{
    public EventScenarioGroup Group { get; } = group;

    public List<ScenarioClaim> Claims { get; } = [];

    /// <summary>Bots being created right now.</summary>
    public int Pending { get; set; }

    public string? CreateRefusal { get; set; }

    public long CreateRetryMs { get; set; }

    public int NameIndex { get; set; }

    public bool NameIndexSeeded { get; set; }

    public int NextSlot { get; set; }

    /// <summary>Bots matching the prefix that another system owns or another scenario claimed (last scan).</summary>
    public int Unavailable { get; set; }

    public string? UnavailableSample { get; set; }

    public bool NoRoom { get; set; }

    public IReadOnlyList<FleetSlot>? Ring { get; set; }

    public int ReadyCount { get; set; }

    /// <summary>The coordinator's id of the siege this group started, 0 when none runs from here.</summary>
    public int SiegeId { get; set; }

    /// <summary>How the siege this group started ended; the group does not start another.</summary>
    public string? SiegeOutcome { get; set; }

    public string? SiegeRefusal { get; set; }

    public long NextSiegeAttemptMs { get; set; }

    public int SiegeEnlisted { get; set; }

    /// <summary>Whether a siege already under way was looked at (a re-attached feature adopts the one its bots are in) at the run's first pass.</summary>
    public bool SiegeChecked { get; set; }

    /// <summary>Bots of the group's prefix are in the siege under way that was not adopted: no bot is created while that siege runs.</summary>
    public bool HoldCreates { get; set; }

    /// <summary>
    /// Bots this run created (begun, less the ones whose creation was refused: a refused creation gives its allowance back). The group may
    /// create at most its count plus <see cref="EventScenarioEngine.MaxReplacements"/> over the run.
    /// </summary>
    public int Created { get; set; }
}

/// <summary>The groups of one wanted event.</summary>
internal sealed class ScenarioEventRun(ushort eventId, ScenarioGroupRun[] groups)
{
    public ushort EventId { get; } = eventId;

    public ScenarioGroupRun[] Groups { get; } = groups;
}

/// <summary>
/// The desired-state reconciler of the bot event scenarios (docs/areas/playbots-scenarios-events.md). A wanted event (one that has
/// started) has a run per group; every <see cref="Tick"/> moves each group one idempotent step towards the rows: claim bots by name
/// prefix, create the missing ones, bring them online, boost, place, then give them their role (defend post or siege). Nothing is
/// persisted: the state is rebuilt from the tables and the set of running events, and a bot is placed once per claim.
/// <para>Thread affinity: world thread only. State ownership: the engine owns its runs and claims; it never keeps a reference to a
/// bot's <c>Player</c>. With no wanted event <see cref="Tick"/> is one comparison and allocates nothing.</para>
/// </summary>
internal sealed class EventScenarioEngine(IEventScenarioHost host, ILogger logger)
{
    /// <summary>Bots created per group per tick.</summary>
    internal const int CreatesPerGroupTick = 2;

    /// <summary>Creations in flight at once, over all groups (each one is a character-create round trip).</summary>
    internal const int MaxCreatesInFlight = 4;

    /// <summary>Starts in flight at once, over all groups.</summary>
    internal const int MaxStartsInFlight = 6;

    /// <summary>Boosts per tick, over all groups (a boost teaches spells and gives a kit).</summary>
    internal const int BoostsPerTick = 4;

    /// <summary>Teleports per tick, over all groups.</summary>
    internal const int TeleportsPerTick = 10;

    /// <summary>A bot this close to the group's start counts as placed.</summary>
    internal const float PlacedYards = 60f;

    /// <summary>Radius of the stay a waiting siege bot keeps at the group's start (inside <see cref="PlacedYards"/>).</summary>
    internal const float StayRadiusYards = 30f;

    /// <summary>How long a stay lives without being renewed (renewed every pass, so a stopped engine frees the bots after this).</summary>
    internal const long StayLeaseMs = 30_000;

    /// <summary>A placed bot that has been away from the start this long is teleported back (its stay's walk-back had its chance first).</summary>
    internal const long DriftTeleportMs = 45_000;

    /// <summary>Bots a group may create beyond its count over the whole run, to replace given-up claims (a tenth, at least one).</summary>
    internal static int MaxReplacements(EventScenarioGroup def) => Math.Max(1, def.Count / 10);

    /// <summary>A waiting siege claim another system (a group, a script) or a GM's defend post has held this long is given up and replaced.</summary>
    internal const long BusyReleaseMs = 120_000;

    /// <summary>A teleport that has not brought the bot to its start after this long is tried again.</summary>
    internal const long TeleportRetryMs = 15_000;

    /// <summary>Teleports tried for one bot before the status says it never arrived (it keeps trying every retry).</summary>
    internal const int TeleportAttemptsReported = 5;

    /// <summary>A refused start, a refused create and a refused siege wait this long before the next try.</summary>
    internal const long RetryMs = 30_000;

    /// <summary>A refused siege start is tried again after this long.</summary>
    internal const long SiegeRetryMs = 4_000;

    /// <summary>A bot that should be coming online by itself (restore at startup) is started by hand after this long offline.</summary>
    internal const long RestoreWaitMs = 60_000;

    private readonly Dictionary<ushort, ScenarioEventRun> _runs = [];
    private readonly HashSet<ushort> _active = [];
    private readonly Dictionary<Guid, (ushort Event, uint Group)> _claimed = [];
    private readonly List<PlayerbotStatus> _candidates = [];
    private readonly List<Guid> _readyIds = [];
    private EventScenarioCatalog _catalog = EventScenarioCatalog.Empty;
    private IReadOnlyList<PlayerbotStatus>? _roster;
    private Dictionary<Guid, PlayerbotStatus>? _rosterIndex;
    private long _now;
    private uint _lastRaw;
    private bool _clockStarted;
    private int _creatingNow;
    private int _startingNow;
    private int _boostBudget;
    private int _teleportBudget;
    private bool _disposed;

    /// <summary>Events that are running and have a scenario (tests).</summary>
    internal IEnumerable<ushort> WantedEvents => _runs.Keys;

    /// <summary>The run of a group, or null (tests).</summary>
    internal ScenarioGroupRun? RunOf(ushort eventId, uint groupId)
        => _runs.TryGetValue(eventId, out ScenarioEventRun? run) ? run.Groups.FirstOrDefault(g => g.Group.GroupId == groupId) : null;

    /// <summary>Which scenario group has claimed the bot, if any (tests).</summary>
    internal (ushort Event, uint Group)? ClaimOf(Guid botId) => _claimed.TryGetValue(botId, out (ushort, uint) key) ? key : null;

    /// <summary>
    /// Use <paramref name="catalog"/> from now on. The runs of the events that are running are rebuilt: a run for every running event that
    /// has groups. Called once the validated rows are known.
    /// </summary>
    internal void SetCatalog(EventScenarioCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (ushort eventId in _runs.Keys.ToArray()) Teardown(eventId);
        _catalog = catalog;
        foreach (ushort eventId in _active.ToArray()) StartRun(eventId);
    }

    /// <summary>A game event started or stopped (or is running at startup).</summary>
    internal void EventChanged(ushort eventId, bool active)
    {
        if (_disposed) return;
        if (active)
        {
            _active.Add(eventId);
            StartRun(eventId);
        }
        else
        {
            _active.Remove(eventId);
            Teardown(eventId);
        }
    }

    /// <summary>The engine is done (the feature detached): late callbacks are ignored. Bots, posts and sieges are left as they are.</summary>
    internal void Dispose() => _disposed = true;

    private void StartRun(ushort eventId)
    {
        if (_runs.ContainsKey(eventId)) return;
        IReadOnlyList<EventScenarioGroup> groups = _catalog.GroupsOf(eventId);
        if (groups.Count == 0) return;
        var runs = new ScenarioGroupRun[groups.Count];
        for (int i = 0; i < runs.Length; i++) runs[i] = new ScenarioGroupRun(groups[i]);
        _runs[eventId] = new ScenarioEventRun(eventId, runs);
        _areasVersion++;
        logger.LogInformation("Bot event scenario {Event} is wanted: {Groups} groups, {Bots} bots", eventId, runs.Length, groups.Sum(g => g.Count));
    }

    private int _areasVersion;

    /// <summary>The yards around a siege waypoint that count as the scenario's area (<see cref="ActiveAreas"/>).</summary>
    internal const float WaypointAreaYards = 40f;

    /// <summary>Changes every time <see cref="ActiveAreas"/> may have changed (a run began or ended). World thread.</summary>
    internal int AreasVersion => _areasVersion;

    /// <summary>
    /// The areas of the scenarios that run now: each defend post (map, centre, radius) and each siege waypoint (<see cref="WaypointAreaYards"/>).
    /// Read-only for the festival board, which keeps bots celebrating away from them. A new list per call: the caller caches it by
    /// <see cref="AreasVersion"/>. World thread.
    /// </summary>
    internal IReadOnlyList<(uint Map, Vector3 Centre, float Radius)> ActiveAreas()
    {
        var areas = new List<(uint, Vector3, float)>();
        foreach (ScenarioEventRun run in _runs.Values)
        {
            foreach (ScenarioGroupRun g in run.Groups)
            {
                foreach (EventScenarioPost post in g.Group.Posts) areas.Add((post.Map, post.Centre, post.Radius));
                if (g.Group.Siege is { } siege)
                    foreach (Vector3 waypoint in siege.Waypoints) areas.Add((siege.Map, waypoint, WaypointAreaYards));
            }
        }

        return areas;
    }

    /// <summary>The event stopped: posts cleared, the siege this scenario started stopped, claims forgotten. Bots are neither deleted nor moved.</summary>
    private void Teardown(ushort eventId)
    {
        if (!_runs.Remove(eventId, out ScenarioEventRun? run)) return;
        _areasVersion++;
        int cleared = 0;
        int stays = 0;
        foreach (ScenarioGroupRun g in run.Groups)
        {
            foreach (ScenarioClaim claim in g.Claims)
            {
                _claimed.Remove(claim.BotId);
                // Only a post this scenario made, and only while the bot still holds it: a GM's own post stays.
                if (g.Group.Role == EventScenarioRole.Defend && host.Probe(claim.BotId) is { DefendPostId: { } held } && EventScenarioPostIds.IsScenario(held)
                    && host.SetDefend(claim.BotId, null) is null)
                    cleared++;
                if (g.Group.Role == EventScenarioRole.Siege && host.Probe(claim.BotId) is { StayId: { } stayId } && EventScenarioPostIds.IsScenario(stayId)
                    && host.SetStay(claim.BotId, null) is null)
                    stays++;
            }

            if (g.SiegeId != 0 && host.RunningSiegeId == g.SiegeId && host.StopSiege("event stopped"))
                logger.LogInformation("Bot event scenario {Event} group {Group}: siege {Siege} stopped with the event", eventId, g.Group.GroupId, g.SiegeId);
        }

        logger.LogInformation("Bot event scenario {Event} stopped: {Cleared} defend posts and {Stays} stays cleared", eventId, cleared, stays);
    }

    private long Advance()
    {
        uint raw = host.NowMs;
        if (_clockStarted) _now += unchecked(raw - _lastRaw);
        _clockStarted = true;
        _lastRaw = raw;
        return _now;
    }

    private IReadOnlyList<PlayerbotStatus> Roster() => _roster ??= host.Roster();

    private Dictionary<Guid, PlayerbotStatus> RosterIndex()
    {
        if (_rosterIndex is { } index) return index;
        index = [];
        foreach (PlayerbotStatus status in Roster()) index[status.BotId] = status;
        return _rosterIndex = index;
    }

    /// <summary>Reconcile every wanted group once. With no wanted event: one comparison.</summary>
    internal void Tick()
    {
        if (_runs.Count == 0 || _disposed) return;
        long now = Advance();
        _roster = null;
        _rosterIndex = null;
        _boostBudget = BoostsPerTick;
        _teleportBudget = TeleportsPerTick;
        foreach (ScenarioEventRun run in _runs.Values)
            foreach (ScenarioGroupRun g in run.Groups)
                Reconcile(g, now);
    }

    private void Reconcile(ScenarioGroupRun g, long now)
    {
        if (g.Group.Role == EventScenarioRole.Siege && !g.SiegeChecked) AdoptSiegeUnderWay(g);
        DropGone(g);
        ClaimBots(g);
        CreateBots(g, now);
        g.ReadyCount = 0;
        for (int i = 0; i < g.Claims.Count; i++) Advance(g, g.Claims[i], now);
        ReleaseBusy(g);
        if (g.Group.Role == EventScenarioRole.Siege) RunSiege(g, now);
    }

    /// <summary>
    /// A feature that was detached and attached again has a new engine while the siege it started still runs. The group adopts a siege whose
    /// raids consist (at least 90 percent) of bots of its own prefix: those bots become its claims even though the raids hold them (the
    /// ordinary claim refuses a held bot), so no bot is created for them, and the siege is neither started a second time when it ends nor
    /// left running when the event stops. A siege of other bots (a GM's, with a few prefix bots among them) is not adopted: this group
    /// waits for it and starts its own once it is over.
    /// </summary>
    private void AdoptSiegeUnderWay(ScenarioGroupRun g)
    {
        if (host.RunningSiegeId is not { } running)
        {
            g.SiegeChecked = true;
            return;
        }

        // A siege is running but its members are not known yet (the roster is not published): look again next pass, and make no bot meanwhile.
        IReadOnlyCollection<Guid> members = host.SiegeMembers();
        if (members.Count == 0 || Roster().Count == 0) return;
        g.SiegeChecked = true;
        var inRaids = new HashSet<Guid>(members);
        var mine = new List<PlayerbotStatus>();
        int prefixInRaids = 0;
        foreach (PlayerbotStatus status in Roster())
        {
            if (!status.Name.StartsWith(g.Group.NamePrefix, StringComparison.OrdinalIgnoreCase) || !inRaids.Contains(status.BotId)) continue;
            prefixInRaids++;
            if (!_claimed.ContainsKey(status.BotId)) mine.Add(status);
        }

        g.HoldCreates = prefixInRaids > 0; // bots of the prefix are in that siege (held, so unclaimable): making more would double the group
        if (mine.Count == 0 || mine.Count * 10 < members.Count * 9) return;
        mine.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (PlayerbotStatus status in mine)
        {
            if (g.Claims.Count >= g.Group.Count) break;
            g.Claims.Add(new ScenarioClaim(status.BotId, status.Name, g.NextSlot++) { Placed = true }); // the siege placed them: they are not walked back to the start
            _claimed[status.BotId] = (g.Group.EventId, g.Group.GroupId);
        }

        g.SiegeId = running;
        g.SiegeEnlisted = g.Claims.Count;
        logger.LogInformation("Bot event scenario {Event} group {Group}: siege {Siege} is already under way with {Bots} of its bots and is taken over", g.Group.EventId, g.Group.GroupId, running, g.Claims.Count);
    }

    /// <summary>A claim whose bot was deleted from the registry is forgotten (a stopped or offline bot stays claimed).</summary>
    private void DropGone(ScenarioGroupRun g)
    {
        bool anyOffline = false;
        foreach (ScenarioClaim claim in g.Claims)
            if (!claim.Online) { anyOffline = true; break; }
        if (!anyOffline) return;
        Dictionary<Guid, PlayerbotStatus> index = RosterIndex();
        for (int i = g.Claims.Count - 1; i >= 0; i--)
        {
            ScenarioClaim claim = g.Claims[i];
            if (claim.Online || index.ContainsKey(claim.BotId)) continue;
            g.Claims.RemoveAt(i);
            _claimed.Remove(claim.BotId);
            logger.LogInformation("Bot event scenario {Event} group {Group}: {Bot} left the registry and is no longer claimed", g.Group.EventId, g.Group.GroupId, claim.Name);
        }
    }

    // --- 1. claim ----------------------------------------------------------------------------------------------------------

    private void ClaimBots(ScenarioGroupRun g)
    {
        EventScenarioGroup def = g.Group;
        if (g.Claims.Count >= def.Count) return;
        _candidates.Clear();
        int matching = 0;
        int unavailable = 0;
        string? sample = null;
        foreach (PlayerbotStatus status in Roster())
        {
            if (!status.Name.StartsWith(def.NamePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            matching++;
            if (_claimed.TryGetValue(status.BotId, out (ushort Event, uint Group) owner))
            {
                if (owner.Event != def.EventId || owner.Group != def.GroupId)
                {
                    unavailable++;
                    sample ??= string.Create(CultureInfo.InvariantCulture, $"{status.Name}: claimed by event {owner.Event} group {owner.Group}");
                }

                continue;
            }

            _candidates.Add(status);
        }

        if (!g.NameIndexSeeded)
        {
            g.NameIndex = matching;
            g.NameIndexSeeded = true;
        }

        _candidates.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (PlayerbotStatus status in _candidates)
        {
            if (g.Claims.Count >= def.Count)
            {
                break;
            }

            if (host.Probe(status.BotId) is { } probe)
            {
                string? why = probe.HeldBy ?? (probe.Team != def.Team ? "is on the other team" : null);
                if (why is null && def.Role == EventScenarioRole.Siege && probe.DefendPostId is { } gmPost && !EventScenarioPostIds.IsScenario(gmPost)) why = "holds a GM's defend post";
                if (why is not null)
                {
                    unavailable++;
                    sample ??= $"{status.Name}: {why}";
                    continue;
                }
            }

            g.Claims.Add(new ScenarioClaim(status.BotId, status.Name, g.NextSlot++));
            _claimed[status.BotId] = (def.EventId, def.GroupId);
        }

        g.Unavailable = unavailable;
        g.UnavailableSample = sample;
    }

    // --- 2. create ---------------------------------------------------------------------------------------------------------

    private void CreateBots(ScenarioGroupRun g, long now)
    {
        EventScenarioGroup def = g.Group;
        int missing = def.Count - g.Claims.Count - g.Pending;
        g.NoRoom = false;
        // A group whose bots keep being taken does not fill the registry with new ones: over the run it creates its count plus a tenth.
        int creationRoom = def.Count + MaxReplacements(def) - g.Created;
        if (creationRoom <= 0) { g.NoRoom = true; return; }
        missing = Math.Min(missing, creationRoom);

        if (missing <= 0 || !def.Provision || g.SiegeId != 0 || ((g.HoldCreates || (def.Role == EventScenarioRole.Siege && !g.SiegeChecked)) && host.RunningSiegeId is not null)) return; // while a siege that holds the group's bots runs (or may), no bot is made
        if (now < g.CreateRetryMs) return;
        int room = host.CreateRoom() - _creatingNow;
        if (room <= 0)
        {
            g.NoRoom = true;
            return;
        }

        int make = Math.Min(Math.Min(missing, room), Math.Min(CreatesPerGroupTick, MaxCreatesInFlight - _creatingNow));
        if (room < missing) g.NoRoom = true;
        for (int i = 0; i < make; i++)
        {
            EventScenarioClassSlot slot = def.ClassSpec[(g.Claims.Count + g.Pending) % def.ClassSpec.Count];
            int index = g.NameIndex;
            string name = EventScenarioNames.For(def.NamePrefix, ref index);
            g.NameIndex = index;
            g.Pending++;
            g.Created++;
            _creatingNow++;
            host.BeginCreate(name, slot.Race, slot.Class, result => Created(g, name, result));
        }
    }

    private void Created(ScenarioGroupRun g, string name, PlayerbotOperationResult result)
    {
        g.Pending = Math.Max(0, g.Pending - 1);
        _creatingNow = Math.Max(0, _creatingNow - 1);
        if (!result.Success) g.Created = Math.Max(0, g.Created - 1); // a refused creation (a taken name, a full registry) uses no allowance
        if (_disposed) return;
        if (result.Success)
        {
            g.CreateRefusal = null;
            return;
        }

        g.CreateRefusal = result.Code;
        // A taken name is only a bad draw: the next suffix is tried at once. Anything else waits before the next try.
        if (result.Code != "name-in-use") g.CreateRetryMs = _now + RetryMs;
        logger.LogInformation("Bot event scenario {Event} group {Group}: creating {Name} refused ({Code})", g.Group.EventId, g.Group.GroupId, name, result.Code);
    }

    // --- 3. online, boost, place, role -------------------------------------------------------------------------------------

    private void Advance(ScenarioGroupRun g, ScenarioClaim claim, long now)
    {
        EventScenarioGroup def = g.Group;
        if (host.Probe(claim.BotId) is not { } probe)
        {
            claim.Online = false;
            claim.Alive = false;
            claim.DefendSet = false; // a restarted bot has a new brain, and the post lived in the old one
            claim.StaySet = false; // and so did the stay
            claim.BusySinceMs = -1; // the busy and away clocks run only while the bot is seen online
            claim.AwaySinceMs = -1;
            Restore(g, claim, now);
            return;
        }

        claim.Online = true;
        claim.OfflineSinceMs = -1;
        claim.Alive = probe.Alive;
        claim.AtLevel = def.Level == 0 || probe.Level >= def.Level;
        if (!claim.AtLevel)
        {
            if (probe.HeldBy is { } heldBy) claim.Refusal = "boost: " + heldBy; // another system owns it since it was claimed
            else if (!probe.Alive || probe.InCombat) claim.Refusal = probe.Alive ? "boost: bot-in-combat" : "boost: bot-dead";
            else if (_boostBudget > 0)
            {
                _boostBudget--;
                string? refusal = host.Boost(claim.BotId, def.Level);
                claim.Refusal = refusal is null ? null : "boost: " + refusal;
                if (refusal is null && host.Probe(claim.BotId) is { } boosted)
                {
                    probe = boosted;
                    claim.AtLevel = probe.Level >= def.Level;
                }
            }
        }

        bool waiting = def.Role == EventScenarioRole.Siege && g.SiegeId == 0 && g.SiegeOutcome is null;
        bool busy = IsBusy(probe);
        if (waiting)
        {
            if (!busy) claim.BusySinceMs = -1;
            else if (claim.BusySinceMs < 0) claim.BusySinceMs = now;
            else if (now - claim.BusySinceMs >= BusyReleaseMs) claim.Released = true;
        }

        // A waiting bot keeps a stay at the start (renewed every pass), so its own play does not carry it off while the group fills. Even so
        // a reactive mover (a retreat, a fight) may: it walks back by itself, and one still away after the grace is teleported back (placed
        // again). A busy bot is not moved from under its owner (Place refuses it).
        if (waiting && claim.Placed)
        {
            if (AtStart(def, probe)) claim.AwaySinceMs = -1;
            else if (claim.AwaySinceMs < 0) claim.AwaySinceMs = now;
            else if (now - claim.AwaySinceMs >= DriftTeleportMs)
            {
                claim.Placed = false;
                claim.TeleportAtMs = -1;
                claim.TeleportAttempts = 0;
                claim.AwaySinceMs = -1;
            }
        }

        if (claim.AtLevel && !claim.Placed) Place(g, claim, probe, now);
        if (waiting && claim.AtLevel) Muster(g, claim, busy, now); // also while the teleport budget keeps the bot waiting: it stands still
        if (claim.Placed && claim.Alive && claim.AtLevel && !busy && (def.Role != EventScenarioRole.Siege || claim.StaySet)) g.ReadyCount++;
        if (def.Role == EventScenarioRole.Defend && claim.Placed) Defend(g, claim, probe, now);
    }

    /// <summary>
    /// Renew the bot's stay at the group's start (every pass: the lease is short, so an engine that stops frees its bots). A busy bot gets none
    /// (the coordinators or a GM own it); a refusal is tried again at the usual retry cadence.
    /// </summary>
    private void Muster(ScenarioGroupRun g, ScenarioClaim claim, bool busy, long now)
    {
        EventScenarioGroup def = g.Group;
        if (busy)
        {
            if (claim.StaySet) host.SetStay(claim.BotId, null); // clearing is always allowed; the refusal string does not matter
            claim.StaySet = false;
            return;
        }
        if (now < claim.NextStayMs) return;
        var stay = new PlayerbotStay(def.StartMap, def.Start, StayRadiusYards, EventScenarioPostIds.For(def.EventId, def.GroupId, 0), unchecked(host.NowMs + (uint)StayLeaseMs));
        string? refusal = host.SetStay(claim.BotId, stay);
        claim.StaySet = refusal is null;
        if (refusal is not null)
        {
            claim.NextStayMs = now + RetryMs;
            claim.Refusal = "stay: " + refusal;
        }
        else if (claim.Refusal is { } old && old.StartsWith("stay:", StringComparison.Ordinal)) claim.Refusal = null;
    }

    private static bool AtStart(EventScenarioGroup def, ScenarioBotProbe probe)
        => probe.MapId == def.StartMap && Vector3.Distance(probe.Position, def.Start) <= PlacedYards;

    /// <summary>A claim another system holds is not ready; neither is one with a GM's defend post (the coordinator turns both away as busy).</summary>
    private static bool IsBusy(ScenarioBotProbe probe)
        => probe.HeldBy is not null || (probe.DefendPostId is { } post && !EventScenarioPostIds.IsScenario(post));

    private void ReleaseBusy(ScenarioGroupRun g)
    {
        for (int i = g.Claims.Count - 1; i >= 0; i--)
        {
            ScenarioClaim claim = g.Claims[i];
            if (!claim.Released) continue;
            g.Claims.RemoveAt(i);
            _claimed.Remove(claim.BotId);
            host.SetStay(claim.BotId, null);
            logger.LogInformation("Bot event scenario {Event} group {Group}: {Bot} stayed busy elsewhere and is given up; a replacement is claimed or created", g.Group.EventId, g.Group.GroupId, claim.Name);
        }
    }

    /// <summary>An offline claim: a stopped bot nobody will bring up is started; one that should come up by itself (restore) is waited for.</summary>
    private void Restore(ScenarioGroupRun g, ScenarioClaim claim, long now)
    {
        if (claim.OfflineSinceMs < 0) claim.OfflineSinceMs = now;
        if (!RosterIndex().TryGetValue(claim.BotId, out PlayerbotStatus? status) || status.State is not ManagedPlayerbotState.Stopped) return;
        bool restoreComing = status.DesiredEnabled && now - claim.OfflineSinceMs < RestoreWaitMs;
        if (restoreComing || now < claim.NextStartMs || _startingNow >= MaxStartsInFlight) return;
        claim.NextStartMs = now + RetryMs;
        _startingNow++;
        host.BeginStart(claim.BotId, result =>
        {
            _startingNow = Math.Max(0, _startingNow - 1);
            if (_disposed) return;
            claim.Refusal = result.Success || result.Code == "already-running" ? null : "start: " + result.Code;
            if (claim.Refusal is not null)
                logger.LogInformation("Bot event scenario {Event} group {Group}: starting {Bot} refused ({Code})", g.Group.EventId, g.Group.GroupId, claim.Name, result.Code);
        });
    }

    private void Place(ScenarioGroupRun g, ScenarioClaim claim, ScenarioBotProbe probe, long now)
    {
        EventScenarioGroup def = g.Group;
        if (AtStart(def, probe))
        {
            claim.Placed = true;
            if (claim.Refusal is { } placedOld && !placedOld.StartsWith("stay:", StringComparison.Ordinal)) claim.Refusal = null;
            return;
        }

        // A bot another system took since it was claimed is not moved from under it.
        if (probe.HeldBy is { } held) { claim.Refusal = "place: " + held; return; }
        if (def.Role == EventScenarioRole.Siege && probe.DefendPostId is { } post && !EventScenarioPostIds.IsScenario(post)) { claim.Refusal = "place: post-set-by-gm"; return; }
        if (!probe.Alive) { claim.Refusal = "place: bot-dead"; return; }
        if (claim.TeleportAtMs >= 0 && now - claim.TeleportAtMs < TeleportRetryMs) return;
        if (_teleportBudget <= 0) return;
        _teleportBudget--;
        g.Ring ??= host.Ring(def.StartMap, def.Start, def.StartOrientation, def.Count);
        Vector3 at = def.Start;
        float orientation = def.StartOrientation;
        if (g.Ring.Count > 0)
        {
            FleetSlot slot = g.Ring[claim.Slot % g.Ring.Count];
            at = new Vector3(slot.X, slot.Y, slot.Z);
            orientation = slot.Orientation;
        }

        string? refusal = host.Teleport(claim.BotId, def.StartMap, at, orientation);
        claim.TeleportAtMs = now;
        claim.TeleportAttempts++;
        claim.Refusal = refusal is not null ? "place: " + refusal
            : claim.TeleportAttempts >= TeleportAttemptsReported ? "place: not arrived"
            : claim.Refusal is { } stayRefusal && stayRefusal.StartsWith("stay:", StringComparison.Ordinal) ? stayRefusal : null;
    }

    /// <summary>
    /// Keep the bot's post as the table says, judged each pass from the post its brain holds now: a bot that restarted has none and gets it
    /// again, a bot holding a GM's post is left alone and reported.
    /// </summary>
    private void Defend(ScenarioGroupRun g, ScenarioClaim claim, ScenarioBotProbe probe, long now)
    {
        EventScenarioGroup def = g.Group;
        EventScenarioPost post = def.Posts[claim.Slot % def.Posts.Count];
        int wanted = EventScenarioPostIds.For(def.EventId, def.GroupId, post.PostId);
        if (probe.DefendPostId == wanted)
        {
            claim.DefendSet = true;
            if (claim.Refusal is { } old && old.StartsWith("defend:", StringComparison.Ordinal)) claim.Refusal = null;
            return;
        }

        if (probe.DefendPostId is { } other && !EventScenarioPostIds.IsScenario(other))
        {
            claim.DefendSet = false;
            claim.Refusal = "defend: post-set-by-gm";
            return;
        }

        if (now < claim.NextDefendMs) return; // a refused post is tried again at the usual retry cadence, not every pass
        var defend = new PlayerbotDefendPost(post.Map, post.Centre, post.Radius, wanted);
        string? refusal = host.SetDefend(claim.BotId, defend);
        claim.DefendSet = refusal is null;
        if (refusal is not null) claim.NextDefendMs = now + RetryMs;
        claim.Refusal = refusal is null ? null : "defend: " + refusal;
    }

    // --- 4. siege ----------------------------------------------------------------------------------------------------------

    private void RunSiege(ScenarioGroupRun g, long now)
    {
        EventScenarioGroup def = g.Group;
        EventScenarioSiege siege = def.Siege!;
        if (g.SiegeId != 0)
        {
            if (host.RunningSiegeId == g.SiegeId) return;
            g.SiegeOutcome = host.SiegeOutcome ?? "ended";
            logger.LogInformation("Bot event scenario {Event} group {Group}: siege {Siege} ended ({Outcome}); it is not started again", def.EventId, def.GroupId, g.SiegeId, g.SiegeOutcome);
            g.SiegeId = 0;
            return;
        }

        if (g.SiegeOutcome is not null) return;
        int readyPct = g.ReadyCount * 100 / def.Count;
        if (readyPct < siege.MinReadyPct || g.ReadyCount < siege.Raids * 2)
        {
            g.SiegeRefusal = null;
            return;
        }

        if (now < g.NextSiegeAttemptMs) return;
        g.NextSiegeAttemptMs = now + SiegeRetryMs;
        _readyIds.Clear();
        // A last look at every bot at the moment of the enlist: nobody far from the start, held elsewhere or on a GM's post is sent in.
        foreach (ScenarioClaim claim in g.Claims)
        {
            if (!claim.Online || !claim.Alive || !claim.AtLevel || !claim.Placed) continue;
            if (host.Probe(claim.BotId) is not { } enlistProbe || IsBusy(enlistProbe)) continue;
            if (!AtStart(def, enlistProbe)) continue; // away right now: not sent in; the grace teleports it back if it stays away
            _readyIds.Add(claim.BotId);
        }

        if (_readyIds.Count * 100 / def.Count < siege.MinReadyPct || _readyIds.Count < siege.Raids * 2) return; // the look found it short: the next pass places again
        ScenarioSiegeResult result = host.StartSiege(_readyIds, siege);
        if (!result.Started)
        {
            g.SiegeRefusal = result.Refusal ?? "refused";
            return;
        }

        g.SiegeId = result.SiegeId;
        g.SiegeRefusal = null;
        foreach (ScenarioClaim claim in g.Claims)
        {
            if (!claim.StaySet) continue;
            claim.StaySet = false;
            host.SetStay(claim.BotId, null); // the siege owns the enlisted bots; the turned-away ones are free again
        }

        g.SiegeEnlisted = result.Enlisted;
        logger.LogInformation("Bot event scenario {Event} group {Group}: siege {Siege} started with {Enlisted} bots ({Skipped} turned away)",
            def.EventId, def.GroupId, result.SiegeId, result.Enlisted, result.Skipped);
    }

    // --- status ------------------------------------------------------------------------------------------------------------

    /// <summary>One line per event that has a scenario, and whether it is wanted.</summary>
    internal IReadOnlyList<string> ListLines(Func<ushort, string?> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);
        var lines = new List<string>();
        foreach (ushort eventId in _catalog.Events)
        {
            IReadOnlyList<EventScenarioGroup> groups = _catalog.GroupsOf(eventId);
            string state = _runs.ContainsKey(eventId) ? "running" : "not running";
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"Event {eventId} \"{describe(eventId) ?? "?"}\": {groups.Count} {(groups.Count == 1 ? "group" : "groups")}, {groups.Sum(g => g.Count)} bots, {state}."));
        }

        if (lines.Count == 0) lines.Add("No bot event scenario is configured (no valid game_event_playerbot_group row).");
        if (_catalog.Issues.Count > 0)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{_catalog.Issues.Count} scenario rows were rejected at load; see the world log (\"game_event_playerbot\")."));
        return lines;
    }

    /// <summary>The state of every group of one event (or of all events that have a scenario).</summary>
    internal IReadOnlyList<string> StatusLines(ushort? eventId, Func<ushort, string?> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);
        var lines = new List<string>();
        foreach (ushort id in _catalog.Events)
        {
            if (eventId is { } wanted && wanted != id) continue;
            bool running = _runs.TryGetValue(id, out ScenarioEventRun? run);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Event {id} \"{describe(id) ?? "?"}\": {(running ? "running" : "not running")}."));
            foreach (EventScenarioGroup group in _catalog.GroupsOf(id))
                lines.Add(running ? GroupLine(run!.Groups.First(g => g.Group.GroupId == group.GroupId)) : IdleLine(group));
        }

        if (lines.Count == 0)
            lines.Add(eventId is { } none ? string.Create(CultureInfo.InvariantCulture, $"Event {none} has no bot scenario.") : "No bot event scenario is configured.");
        return lines;
    }

    private static string IdleLine(EventScenarioGroup group)
        => string.Create(CultureInfo.InvariantCulture, $"  [{group.GroupId}] {group.NamePrefix}: {group.Count} bots, role {RoleName(group.Role)}, level {group.Level}, team {(group.Team == 0 ? "Alliance" : "Horde")}.");

    private static string RoleName(EventScenarioRole role) => role switch { EventScenarioRole.Defend => "defend", EventScenarioRole.Siege => "siege", _ => "none" };

    private static string GroupLine(ScenarioGroupRun g)
    {
        EventScenarioGroup def = g.Group;
        int online = 0, atLevel = 0, placed = 0, posted = 0, held = 0;
        var refusals = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (ScenarioClaim claim in g.Claims)
        {
            if (claim.Online) online++;
            if (claim.Online && claim.AtLevel) atLevel++;
            if (claim.Placed) placed++;
            if (claim.DefendSet) posted++;
            if (claim.BusySinceMs >= 0) held++;
            if (claim.Refusal is { } refusal) refusals[refusal] = refusals.GetValueOrDefault(refusal) + 1;
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"  [{def.GroupId}] {def.NamePrefix} role {RoleName(def.Role)}: claimed {g.Claims.Count}/{def.Count}, online {online}, at level {atLevel}, placed {placed}");
        if (def.Role == EventScenarioRole.Defend) text.Append(CultureInfo.InvariantCulture, $", posts set {posted}");
        int shortBy = def.Count - g.Claims.Count;
        if (g.Pending > 0) text.Append(CultureInfo.InvariantCulture, $", creating {g.Pending}");
        if (shortBy > 0) text.Append(CultureInfo.InvariantCulture, $", short {shortBy}{(g.NoRoom ? ", no room" : !def.Provision ? ", not provisioned" : string.Empty)}");
        if (g.CreateRefusal is { } create) text.Append(CultureInfo.InvariantCulture, $", create refused: {create}");
        if (g.Created > def.Count) text.Append(CultureInfo.InvariantCulture, $", replacements {g.Created - def.Count}/{MaxReplacements(def)}");
        if (g.Unavailable > 0) text.Append(CultureInfo.InvariantCulture, $", unavailable {g.Unavailable} ({g.UnavailableSample})");
        if (def.Role == EventScenarioRole.Siege && held > 0) text.Append(CultureInfo.InvariantCulture, $", held {held}");
        if (def.Role == EventScenarioRole.Siege) text.Append(", siege ").Append(SiegeText(g));
        if (refusals.Count > 0)
            text.Append(", refused: ").AppendJoin("; ", refusals.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Key} x{r.Value}")));
        text.Append('.');
        return text.ToString();
    }

    private static string SiegeText(ScenarioGroupRun g)
    {
        EventScenarioSiege siege = g.Group.Siege!;
        if (g.SiegeId != 0) return string.Create(CultureInfo.InvariantCulture, $"{g.SiegeId} running ({g.SiegeEnlisted} bots)");
        if (g.SiegeOutcome is { } outcome) return "ended: " + outcome;
        if (g.SiegeRefusal is { } refusal) return $"refused: {refusal} (retrying)";
        return string.Create(CultureInfo.InvariantCulture, $"waiting: {g.ReadyCount}/{g.Group.Count} ready, starts at {siege.MinReadyPct}%");
    }
}
