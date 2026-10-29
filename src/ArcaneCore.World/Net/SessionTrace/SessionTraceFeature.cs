using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net.SessionTrace;

/// <summary>How a request to trace a character ended.</summary>
public enum SessionTraceStartStatus
{
    /// <summary>A new trace was started.</summary>
    Started,

    /// <summary>The character was already traced; its expiry was replaced.</summary>
    Replaced,

    /// <summary>Session tracing is switched off (<see cref="SessionTraceOptions.Enabled"/>).</summary>
    Disabled,

    /// <summary>The cap on traced characters (<see cref="SessionTraceOptions.MaxTracedCharacters"/>) is reached.</summary>
    TooMany,
}

/// <summary>The result of <see cref="SessionTraceFeature"/>.Start: the trace, and the minutes it will run (<paramref name="Clamped"/>: the request was changed to fit the limits).</summary>
public readonly record struct SessionTraceStartResult(SessionTraceStartStatus Status, SessionTrace? Trace, int Minutes, bool Clamped);

/// <summary>
/// The GM per-session trace (<c>.debug trace session</c>, <see cref="SessionTraceCommands"/>): a registry of traced characters and the world
/// events that feed their lines. A trace follows the character, not the connection: it survives a relog or a dropped link and ends only on
/// <c>off</c>, on expiry, or when the server stops.
/// <para>
/// Cost when nothing is traced: the world tick handler and the game-event handlers return after one dictionary count, with no allocation. The
/// game-event subscriptions (spell auras, kills, game object use, anti-cheat findings, map creation) are made on the first <c>Start</c> and
/// never before, so a server where nobody traces pays nothing for them. The registry is replaced, never edited, on each change, so the
/// handlers (which parallel map updates may run on several threads) read a consistent snapshot without a lock.
/// </para>
/// <para>
/// Threads: <c>Start</c>, <c>Stop</c>, the once-per-second work and the login and logout handlers run on the world thread. The lines themselves
/// are written from the reader, sender and world threads (<see cref="SessionTrace"/>). Time comes from the registered <see cref="TimeProvider"/>.
/// </para>
/// </summary>
public sealed class SessionTraceFeature : IWorldFeature
{
    /// <summary>The log category of the trace lines. Nothing but trace lines is written to it.</summary>
    public const string LogCategory = "ArcaneCore.SessionTrace";

    private static readonly Dictionary<ObjectGuid, SessionTrace> NoTraces = [];

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger _lines;
    private readonly ILogger _faults;
    private readonly HashSet<MapCombat> _combats = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<GameObjectMapSystem> _objectSystems = new(ReferenceEqualityComparer.Instance);
    private Dictionary<ObjectGuid, SessionTrace> _traces = NoTraces;
    private WorldRuntime? _world;
    private bool _subscribed;
    private long _lastSecond;

    /// <summary>Resolved by the feature host: <paramref name="services"/> for the lazily resolved features, <paramref name="loggers"/> for the trace category.</summary>
    public SessionTraceFeature(IServiceProvider services, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loggers);
        _services = services;
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _lines = loggers.CreateLogger(LogCategory);
        _faults = loggers.CreateLogger(typeof(SessionTraceFeature).FullName!);
        var options = new SessionTraceOptions();
        services.GetService<IConfiguration>()?.GetSection(SessionTraceOptions.SectionName).Bind(options);
        options.Normalize();
        Options = options;
    }

    /// <summary>The options in force (bound once, when the feature is created).</summary>
    internal SessionTraceOptions Options { get; }

    /// <summary>The traces that are on right now (a snapshot; world thread).</summary>
    internal IReadOnlyCollection<SessionTrace> Active => Volatile.Read(ref _traces).Values;

    /// <summary>Whether <paramref name="guid"/> is traced right now.</summary>
    internal bool IsTraced(ObjectGuid guid) => Volatile.Read(ref _traces).ContainsKey(guid);

    /// <inheritdoc />
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        world.Updated += OnUpdated;
        world.PlayerLoggedIn += OnPlayerLoggedIn;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
    }

    // --- start and stop (world thread) ---------------------------------------------------------------------------------

    /// <summary>
    /// Turns the trace of <paramref name="player"/> on, or replaces the expiry of one that is on. <paramref name="minutes"/> is clamped to
    /// 1 to <see cref="SessionTraceOptions.MaxMinutes"/> (null means <see cref="SessionTraceOptions.DefaultMinutes"/>). Re-tracing a traced
    /// character does not count against the cap. World thread.
    /// </summary>
    internal SessionTraceStartResult Start(Player player, int? minutes)
    {
        ArgumentNullException.ThrowIfNull(player);
        SessionTraceOptions options = Options;
        if (!options.Enabled)
        {
            return new SessionTraceStartResult(SessionTraceStartStatus.Disabled, null, 0, false);
        }

        Dictionary<ObjectGuid, SessionTrace> traces = _traces;
        bool known = traces.TryGetValue(player.Guid, out SessionTrace? trace);
        if (!known && traces.Count >= options.MaxTracedCharacters)
        {
            return new SessionTraceStartResult(SessionTraceStartStatus.TooMany, null, 0, false);
        }

        int requested = minutes ?? options.DefaultMinutes;
        int effective = Math.Clamp(requested, 1, options.MaxMinutes);
        DateTimeOffset expiresAt = _clock.GetUtcNow().AddMinutes(effective);
        EnsureSubscribed();
        if (trace is null)
        {
            if (traces.Count == 0)
            {
                _lastSecond = _clock.GetTimestamp();
            }

            trace = new SessionTrace(player.Guid, player.Name, options, _clock, _lines, _faults, expiresAt);
            Publish(new Dictionary<ObjectGuid, SessionTrace>(traces) { [player.Guid] = trace });
        }
        else
        {
            trace.SetExpiry(expiresAt);
        }

        trace.Security = player.Security;
        if (player.Session is WorldSession session)
        {
            trace.Attach(session);
        }

        trace.PollState(player);
        trace.WriteControl("trace", string.Create(CultureInfo.InvariantCulture, $"on minutes={effective}{(known ? " replaced" : string.Empty)}"));
        return new SessionTraceStartResult(known ? SessionTraceStartStatus.Replaced : SessionTraceStartStatus.Started, trace, effective, effective != requested);
    }

    /// <summary>Turns the trace of <paramref name="guid"/> off (the character may be offline). False when it was not traced. World thread.</summary>
    internal bool Stop(ObjectGuid guid, out SessionTrace? stopped)
    {
        Dictionary<ObjectGuid, SessionTrace> traces = _traces;
        if (!traces.TryGetValue(guid, out stopped))
        {
            return false;
        }

        Remove(traces, guid);
        stopped.WriteControl("trace", string.Create(CultureInfo.InvariantCulture, $"off lines={stopped.Lines} dropped={stopped.Dropped}"));
        stopped.DetachCurrent();
        return true;
    }

    /// <summary>The trace of the character named <paramref name="name"/> (case-insensitive), traced online or off; null when none.</summary>
    internal SessionTrace? FindByName(string name)
    {
        foreach (SessionTrace trace in Volatile.Read(ref _traces).Values)
        {
            if (string.Equals(trace.Character, name, StringComparison.OrdinalIgnoreCase))
            {
                return trace;
            }
        }

        return null;
    }

    /// <summary>One <c>.debug trace session list</c> row per active trace the caller may see (<paramref name="visible"/>), ordered by character name.</summary>
    internal IReadOnlyList<string> DescribeActive(Func<SessionTrace, bool> visible)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        return [.. Volatile.Read(ref _traces).Values
            .Where(visible)
            .OrderBy(t => t.Character, StringComparer.OrdinalIgnoreCase)
            .Select(t => string.Create(
                CultureInfo.InvariantCulture,
                $"{t.Character} (guid {t.Guid.Low}) {t.Endpoint}, {Math.Max(0, (int)Math.Ceiling((t.ExpiresAt - now).TotalMinutes))} min left, {t.Lines} lines, {t.Dropped} dropped"))];
    }

    private void Remove(Dictionary<ObjectGuid, SessionTrace> traces, ObjectGuid guid)
    {
        if (traces.Count == 1)
        {
            Publish(NoTraces);
            return;
        }

        var next = new Dictionary<ObjectGuid, SessionTrace>(traces);
        next.Remove(guid);
        Publish(next);
    }

    private void Publish(Dictionary<ObjectGuid, SessionTrace> next) => Volatile.Write(ref _traces, next);

    // --- the world tick: once per clock second ---------------------------------------------------------------------------

    /// <summary>
    /// The <see cref="WorldRuntime.Updated"/> handler. With nothing traced it returns at once. Otherwise, once the <see cref="TimeProvider"/>
    /// clock has advanced a second since the last pass (the tick length <paramref name="diffMs"/> is not what counts), it expires traces
    /// whose time is up, polls the state of each traced player and writes the report of dropped lines.
    /// </summary>
    internal void OnUpdated(uint diffMs)
    {
        Dictionary<ObjectGuid, SessionTrace> traces = Volatile.Read(ref _traces);
        if (traces.Count == 0)
        {
            return;
        }

        long now = _clock.GetTimestamp();
        if (now - _lastSecond < _clock.TimestampFrequency)
        {
            return;
        }

        _lastSecond = now;
        try
        {
            DateTimeOffset utcNow = _clock.GetUtcNow();
            foreach (SessionTrace trace in traces.Values)
            {
                if (utcNow >= trace.ExpiresAt)
                {
                    Expire(trace);
                    continue;
                }

                if (_world?.FindOnlinePlayer(trace.Guid) is { } player)
                {
                    trace.PollState(player);
                }

                trace.ReportDrops(); // last, so the numbers include whatever this pass dropped
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _faults.LogFault("session-trace.tick", ex, "The session trace once-per-second pass failed after {DiffMs} ms", diffMs);
        }
    }

    private void Expire(SessionTrace trace)
    {
        Remove(Volatile.Read(ref _traces), trace.Guid);
        trace.WriteControl("trace", string.Create(CultureInfo.InvariantCulture, $"expired lines={trace.Lines} dropped={trace.Dropped}"));
        trace.DetachCurrent();
    }

    // --- login and logout (world thread) -----------------------------------------------------------------------------------

    private void OnPlayerLoggedIn(Player player)
    {
        if (Volatile.Read(ref _traces).TryGetValue(player.Guid, out SessionTrace? trace) && player.Session is WorldSession session)
        {
            trace.Attach(session);
            trace.WriteControl("attached", "session");
            trace.PollState(player);
        }
    }

    private void OnPlayerLoggingOut(Player player)
    {
        if (Volatile.Read(ref _traces).TryGetValue(player.Guid, out SessionTrace? trace))
        {
            trace.WriteControl("logout", string.Empty);
            if (player.Session is WorldSession ended && trace.IsAttachedTo(ended))
            {
                trace.DetachCurrent(); // the connection goes back to the character screen; whatever it does next is not this character
            }
        }
    }

    // --- game events (subscribed on the first Start) -------------------------------------------------------------------------

    private void EnsureSubscribed()
    {
        if (_subscribed || _world is not { } world)
        {
            return;
        }

        _subscribed = true;
        if (_services.GetService<SpellFeature>() is { } spells)
        {
            spells.System.HolderAdded += OnHolderAdded;
            spells.System.HolderRemoved += OnHolderRemoved;
        }

        if (_services.GetService<AntiCheatFeature>() is { } antiCheat)
        {
            antiCheat.FindingRecorded += OnFindingRecorded;
        }

        world.MapCreated += SubscribeMap;
        world.MapUnloading += UnsubscribeMap;
        foreach (Map map in world.Maps)
        {
            SubscribeMap(map);
        }
    }

    private void SubscribeMap(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat && _combats.Add(combat))
        {
            combat.UnitKilled += OnUnitKilled;
        }

        if (map.FindUpdater<GameObjectMapSystem>() is { } objects && _objectSystems.Add(objects))
        {
            objects.Used += OnObjectUsed;
        }
    }

    private void UnsubscribeMap(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat && _combats.Remove(combat))
        {
            combat.UnitKilled -= OnUnitKilled;
        }

        if (map.FindUpdater<GameObjectMapSystem>() is { } objects && _objectSystems.Remove(objects))
        {
            objects.Used -= OnObjectUsed;
        }
    }

    /// <summary><c>SpellSystem.HolderAdded</c>: an <c>aura+</c> line when the holder's target is traced.</summary>
    internal void OnHolderAdded(SpellAuraHolder holder)
    {
        Dictionary<ObjectGuid, SessionTrace> traces = Volatile.Read(ref _traces);
        if (traces.Count == 0)
        {
            return;
        }

        try
        {
            if (traces.TryGetValue(holder.Target.Guid, out SessionTrace? trace))
            {
                trace.Write("aura+", string.Create(CultureInfo.InvariantCulture, $"spell={holder.Spell.Id} caster={FormatGuid(holder.CasterGuid)}"));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _faults.LogFault("session-trace.holder-added", ex, "The session trace failed on an aura added to {Target}", holder.Target.Guid);
        }
    }

    /// <summary><c>SpellSystem.HolderRemoved</c>: an <c>aura-</c> line with the removal mode when the holder's target is traced.</summary>
    internal void OnHolderRemoved(SpellAuraHolder holder)
    {
        Dictionary<ObjectGuid, SessionTrace> traces = Volatile.Read(ref _traces);
        if (traces.Count == 0)
        {
            return;
        }

        try
        {
            if (traces.TryGetValue(holder.Target.Guid, out SessionTrace? trace))
            {
                trace.Write("aura-", string.Create(CultureInfo.InvariantCulture, $"spell={holder.Spell.Id} reason={holder.RemoveMode}"));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _faults.LogFault("session-trace.holder-removed", ex, "The session trace failed on an aura removed from {Target}", holder.Target.Guid);
        }
    }

    /// <summary><c>MapCombat.UnitKilled</c>: a <c>death</c> line with the killer when the victim is traced.</summary>
    internal void OnUnitKilled(Unit? killer, Unit victim)
    {
        Dictionary<ObjectGuid, SessionTrace> traces = Volatile.Read(ref _traces);
        if (traces.Count == 0)
        {
            return;
        }

        try
        {
            if (traces.TryGetValue(victim.Guid, out SessionTrace? trace))
            {
                trace.Write("death", killer is null ? "killer=none" : "killer=" + FormatGuid(killer.Guid));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _faults.LogFault("session-trace.unit-killed", ex, "The session trace failed on the death of {Victim}", victim.Guid);
        }
    }

    private void OnObjectUsed(Player player, GameObject gameObject)
    {
        Dictionary<ObjectGuid, SessionTrace> traces = Volatile.Read(ref _traces);
        if (traces.Count == 0)
        {
            return;
        }

        try
        {
            if (traces.TryGetValue(player.Guid, out SessionTrace? trace))
            {
                trace.Write("use-object", string.Create(CultureInfo.InvariantCulture, $"entry={gameObject.Entry} guid={FormatGuid(gameObject.Guid)}"));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _faults.LogFault("session-trace.object-used", ex, "The session trace failed on a game object use by {Player}", player.Guid);
        }
    }

    /// <summary><c>AntiCheatFeature.FindingRecorded</c>: the enforced verdict of a checked, traced player (an exempt player is the shadow's).</summary>
    private void OnFindingRecorded(Player player, AntiCheatFinding finding, float score, AntiCheatAction action)
    {
        Dictionary<ObjectGuid, SessionTrace> traces = Volatile.Read(ref _traces);
        if (traces.Count == 0)
        {
            return;
        }

        try
        {
            if (traces.TryGetValue(player.Guid, out SessionTrace? trace))
            {
                trace.Write("anticheat", string.Create(CultureInfo.InvariantCulture,
                    $"{finding.Type} weight={finding.Weight:0.##} detail={finding.Detail} action={action} score={score:0.##}"));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _faults.LogFault("session-trace.finding", ex, "The session trace failed on an anti-cheat finding of {Player}", player.Guid);
        }
    }

    /// <summary>A player's GUID is its character id; every other kind keeps its kind and full value.</summary>
    private static string FormatGuid(ObjectGuid guid) => guid.IsPlayer ? guid.Low.ToString(CultureInfo.InvariantCulture) : guid.ToString();
}
