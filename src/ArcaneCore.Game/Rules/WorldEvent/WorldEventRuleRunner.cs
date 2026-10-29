using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Rules.WorldEvent;

/// <summary>
/// Runs the <c>world_event</c> rules: it is told by the game-event service when an event starts or stops (<see cref="IGameEventListener"/>),
/// fires <see cref="WorldEventRuleIds.Update"/> for every running event on each <see cref="Update"/> (the world tick) and runs the delayed
/// actions that fell due. One subject and one <see cref="RuleSubjectState"/> per running event, bound to the global rules plus the rules
/// owned by <c>(game_event, id)</c>.
/// <para>
/// Thread affinity: world thread only (the game-event service, the world tick and the evaluator's <see cref="Random"/> share it). State: the
/// entries are owned here; a catalog swap (<c>.reload rules</c>) rebinds every running event to the new catalog with a fresh state, so a
/// rule that already fired may fire again and a delayed run in flight is dropped. A stopped event keeps its entry until its delayed
/// actions ran, so a stop rule's delays complete; a stop does not cancel the delayed runs parked earlier (they continue whichever rule
/// parked them), and a start of the same event again replaces the entry and drops whatever the previous run left pending. A change that
/// arrives past <see cref="MaxNesting"/> is still recorded in the entries and only fires no rules.
/// </para>
/// <para>
/// Fault and re-entrancy boundary (the engine core does neither): every evaluator call is wrapped in the fault-ledger idiom (a throwing
/// primitive is recorded and the tick goes on), and a start or stop that an action causes synchronously, which comes back through
/// <see cref="OnEventChanged"/>, is run up to <see cref="MaxNesting"/> levels deep and skipped (and reported) beyond that.
/// </para>
/// </summary>
public sealed class WorldEventRuleRunner : IGameEventListener
{
    /// <summary>How many rule firings may nest through event starts and stops an action causes.</summary>
    public const int MaxNesting = 4;

    private sealed class Entry(WorldEventSubject subject, RuleSubjectState state)
    {
        public WorldEventSubject Subject { get; } = subject;

        public RuleSubjectState State { get; set; } = state;

        public bool Running { get; set; } = true;
    }

    private readonly Func<RuleCatalog> _catalog;
    private readonly IWorldEventControl _control;
    private readonly RuleEvaluator _evaluator;
    private readonly Func<long> _clockMs;
    private readonly ILogger? _logger;
    private readonly Dictionary<ushort, Entry> _entries = [];
    private readonly List<Entry> _scratch = [];
    private int _nesting;

    /// <param name="catalog">The loaded rules, read at every use (a reload is seen at once).</param>
    /// <param name="control">What the actions may do to the game events.</param>
    /// <param name="evaluator">This host's evaluator (one per thread).</param>
    /// <param name="clockMs">The world clock in milliseconds (delays and cooldowns count on it).</param>
    /// <param name="logger">Where faults are logged; null in tests.</param>
    public WorldEventRuleRunner(Func<RuleCatalog> catalog, IWorldEventControl control, RuleEvaluator evaluator, Func<long> clockMs, ILogger? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _clockMs = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
        _logger = logger;
    }

    /// <summary>The events with an entry (running, or stopped with delayed actions left).</summary>
    public int EntryCount => _entries.Count;

    /// <summary>How many rule firings were skipped by the nesting limit.</summary>
    public int NestingSkips { get; private set; }

    public void OnEventChanged(ushort eventId, bool active, bool resume)
    {
        // Past the nesting limit the change is still recorded (the game-event service has applied it already, so the entries must agree
        // with it) but no rule fires for it.
        bool fire = _nesting < MaxNesting;
        if (!fire)
        {
            NestingSkips++;
            _logger?.LogWarning("world-event rules: event {EventId} {Change} fired no rules, they were nested {Depth} deep", eventId, active ? "start" : "stop", _nesting);
        }

        _nesting++;
        try
        {
            if (active)
            {
                Entry entry = Bind(eventId);
                _entries[eventId] = entry;
                if (fire)
                {
                    Fire(entry, WorldEventRuleIds.Started, new RuleEventArgs(resume ? 1 : 0));
                }
            }
            else if (_entries.TryGetValue(eventId, out Entry? entry) && entry.Running)
            {
                entry.Running = false;
                if (fire)
                {
                    Fire(entry, WorldEventRuleIds.Stopped, default);
                }

                // A rule fired above may have started the event again, which replaced this entry: that one stays.
                if (entry.State.PendingCount == 0 && _entries.TryGetValue(eventId, out Entry? current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(eventId);
                }
            }
        }
        finally
        {
            _nesting--;
        }
    }

    /// <summary>The world tick: fire <see cref="WorldEventRuleIds.Update"/> for each running event and run the delayed actions that are due.</summary>
    public void Update()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        long now = _clockMs();
        RuleCatalog catalog = _catalog();
        _scratch.Clear();
        _scratch.AddRange(_entries.Values);
        foreach (Entry entry in _scratch)
        {
            if (!ReferenceEquals(entry.State.Binding.Catalog, catalog))
            {
                entry.State = catalog.Bind(RuleHostKind.WorldEvent, new RuleOwner(RuleOwnerKind.GameEvent, entry.Subject.EventId)).CreateState();
            }

            if (entry.Running)
            {
                Fire(entry, WorldEventRuleIds.Update, default);
            }

            try
            {
                _evaluator.Update(entry.State, entry.Subject, now);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Faulted("update", entry, ex);
            }

            if (!entry.Running && entry.State.PendingCount == 0 && _entries.TryGetValue(entry.Subject.EventId, out Entry? current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(entry.Subject.EventId);
            }
        }

        _scratch.Clear();
    }

    private Entry Bind(ushort eventId)
    {
        RuleBinding binding = _catalog().Bind(RuleHostKind.WorldEvent, new RuleOwner(RuleOwnerKind.GameEvent, eventId));
        return new Entry(new WorldEventSubject(eventId, _control), binding.CreateState());
    }

    private void Fire(Entry entry, short eventType, in RuleEventArgs args)
    {
        try
        {
            _evaluator.Fire(entry.State, entry.Subject, eventType, args, _clockMs());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Faulted("fire", entry, ex);
        }
    }

    private void Faulted(string what, Entry entry, Exception ex)
    {
        if (_logger is null)
        {
            FaultLedger.Shared.Record("world-event.rules", ex);
        }
        else
        {
            _logger.LogFault("world-event.rules", ex, "world-event rule {What} for game event {EventId} failed", what, entry.Subject.EventId);
        }
    }
}
