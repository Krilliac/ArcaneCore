using System.Collections.Frozen;
using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Diagnostics;

namespace ArcaneCore.World.Gm.Diagnostics;

/// <summary>Limits of the GM stream.</summary>
public sealed class GmStreamOptions
{
    /// <summary>Lines one GM can receive per second across all channels (a token bucket that holds one second's worth, so a burst never exceeds this); the rest are dropped and counted (one notice follows).</summary>
    public int MaxLinesPerSecond { get; init; } = 5;

    /// <summary>A subscription turns itself off this long after it was last (re)subscribed.</summary>
    public TimeSpan AutoOffAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Channels one GM can follow at once.</summary>
    public int MaxChannelsPerGm { get; init; } = 8;

    /// <summary>
    /// How far from the GM (yards, 2D) an event counts as "in range" for the <see cref="GmStreamScope.Target"/> and <see cref="GmStreamScope.Near"/>
    /// scopes. The default is the map visibility distance (vmangos' default <c>MaxVisibleDistance</c>, 100 yards, the game's <c>Map.VisibilityRange</c>).
    /// </summary>
    public float ScopeRange { get; init; } = 100f;
}

/// <summary>A line queued for one GM by <see cref="GmStream"/>.</summary>
public readonly record struct GmStreamLine(ObjectGuid Gm, string Text);

/// <summary>One followed channel of a GM: its scope and the time it turns off.</summary>
public readonly record struct GmStreamSubscription(string Channel, GmStreamScope Scope, DateTimeOffset ExpiresAt);

/// <summary>
/// Where a GM is and what the GM has selected, as last read on the world thread by <see cref="GmStream.RefreshViewers"/>. The scope
/// test of a published event uses this snapshot, so a map thread never reads a player.
/// </summary>
public readonly record struct GmStreamViewer(ObjectGuid Selection, uint MapId, uint InstanceId, float X, float Y, float Z, float Range);

/// <summary>
/// Opt-in live feed of debug events to GMs (damage log, proc trace, spell trace, moves, anticheat findings; see <see cref="GmStreamChannels"/>): a GM turns a named
/// channel <c>on</c> or <c>off</c>, producers publish to the channel, and every subscribed GM whose scope matches the event gets the lines in chat.
/// <list type="bullet">
/// <item>Each subscription has a <see cref="GmStreamScope"/>: <c>target</c> (events of the GM or the GM's selected unit within range), <c>near</c> (any event within range) or <c>all</c>. Range is 2D, from the GM's last <see cref="GmStreamViewer"/> snapshot.</item>
/// <item>Rate limited per GM with a token bucket (<see cref="GmStreamOptions.MaxLinesPerSecond"/>): excess lines are dropped, never queued without bound, and one "N lines suppressed" notice follows once the bucket has refilled (sent by <see cref="Sweep"/> when publishing has stopped, or by the next publish). Notices are kept apart from lines, so a full outbox never drops one. Only a GM whose scope matches an event spends a token or counts a suppressed line for it.</item>
/// <item>A subscription ends after <see cref="GmStreamOptions.AutoOffAfter"/> (<see cref="Sweep"/>) or when the GM logs out (<see cref="UnsubscribeAll"/>); the GM is told when it times out.</item>
/// <item>Zero cost when nobody listens: <see cref="IsActive"/> is one volatile read and <see cref="HasSubscribers"/> one lock-free set lookup, so a producer guards decoding and formatting with them.</item>
/// </list>
/// <para>
/// Thread safety: all members may be called from any thread (map threads publish, the world thread subscribes). Publishing
/// never touches a session: lines go to an outbox that <see cref="Drain"/> hands to the world thread, which owns the player lookup
/// and the send (<see cref="GmStreamFeature"/>). Channel names are case-insensitive.
/// </para>
/// </summary>
public sealed class GmStream : IGmTraceSink
{
    private readonly record struct ChannelState(DateTimeOffset ExpiresAt, GmStreamScope Scope);

    private sealed class Subscriber
    {
        public readonly Dictionary<string, ChannelState> Channels = new(StringComparer.OrdinalIgnoreCase);
        public GmStreamViewer? Viewer;
        public double Tokens;
        public long RefilledAtTicks;
        public int Suppressed;
    }

    // A GM's bucket as it was when its last subscription ended, so turning the stream off and on again does not refill it.
    // Pruned by Sweep once the bucket would be full again; bounded by the number of GMs.
    private readonly record struct ParkedBucket(double Tokens, long RefilledAtTicks);

    private readonly record struct Sequenced(long Seq, GmStreamLine Line);

    private static readonly Func<string, string> Verbatim = static line => line;

    private readonly Lock _gate = new();
    private readonly Dictionary<ObjectGuid, Subscriber> _subscribers = [];
    private readonly Dictionary<ObjectGuid, ParkedBucket> _parked = [];
    private const int MaxOutbox = 1024;
    private readonly Queue<Sequenced> _outbox = new();
    private readonly Queue<Sequenced> _notices = new(); // timeout and suppression notices: bounded by the subscriptions, never dropped
    private long _sequence; // orders notices against lines in Drain
    private long _outboxDropped;
    private readonly TimeProvider _clock;
    private int _subscriptionCount;

    // The names of the channels any GM follows: an immutable set swapped (Volatile.Write, under _gate) whenever it changes, so
    // HasSubscribers, which a producer asks on every broadcast, takes no lock.
    private FrozenSet<string> _followed = FrozenSet<string>.Empty;

    public GmStream(GmStreamOptions? options = null, TimeProvider? clock = null)
    {
        Options = options ?? new GmStreamOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Options.MaxLinesPerSecond, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Options.MaxChannelsPerGm, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Options.AutoOffAfter, TimeSpan.Zero);
        _clock = clock ?? TimeProvider.System;
    }

    public GmStreamOptions Options { get; }

    /// <summary>Lines dropped because the outbox was full (nobody was draining it).</summary>
    public long OutboxDropped => Interlocked.Read(ref _outboxDropped);

    /// <summary>True while any GM follows any channel: the cheap guard before formatting a line.</summary>
    public bool IsActive => Volatile.Read(ref _subscriptionCount) != 0;

    /// <summary>Whether any GM follows <paramref name="channel"/>. Lock-free: one volatile read and a frozen-set lookup.</summary>
    public bool HasSubscribers(string channel) => IsActive && Volatile.Read(ref _followed).Contains(channel);

    /// <summary>
    /// Follow <paramref name="channel"/> as <paramref name="gm"/>; following an already followed channel renews its timeout and sets its
    /// <paramref name="scope"/> to the one given. False when the GM already follows <see cref="GmStreamOptions.MaxChannelsPerGm"/> other channels.
    /// </summary>
    public bool Subscribe(ObjectGuid gm, string channel, GmStreamScope scope = GmStreamScope.Target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        DateTimeOffset expires = _clock.GetUtcNow() + Options.AutoOffAfter;
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(gm, out Subscriber? subscriber))
            {
                subscriber = _parked.Remove(gm, out ParkedBucket parked)
                    ? new Subscriber { Tokens = parked.Tokens, RefilledAtTicks = parked.RefilledAtTicks }
                    : new Subscriber { Tokens = Options.MaxLinesPerSecond, RefilledAtTicks = _clock.GetUtcNow().UtcTicks };
                _subscribers[gm] = subscriber;
            }

            bool added = !subscriber.Channels.ContainsKey(channel);
            if (added)
            {
                if (subscriber.Channels.Count >= Options.MaxChannelsPerGm)
                {
                    DropIfEmpty(gm, subscriber);
                    return false;
                }

                Interlocked.Increment(ref _subscriptionCount);
            }

            subscriber.Channels[channel] = new ChannelState(expires, scope);
            if (added)
            {
                RebuildFollowed();
            }

            return true;
        }
    }

    /// <summary>Stop following <paramref name="channel"/>. False when the GM did not.</summary>
    public bool Unsubscribe(ObjectGuid gm, string channel)
    {
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(gm, out Subscriber? subscriber) || !subscriber.Channels.Remove(channel))
            {
                return false;
            }

            Interlocked.Decrement(ref _subscriptionCount);
            DropIfEmpty(gm, subscriber);
            RebuildFollowed();
            return true;
        }
    }

    /// <summary>Stop every subscription of <paramref name="gm"/> (logout). Returns how many ended.</summary>
    public int UnsubscribeAll(ObjectGuid gm)
    {
        lock (_gate)
        {
            if (!_subscribers.Remove(gm, out Subscriber? subscriber))
            {
                return 0;
            }

            int count = subscriber.Channels.Count;
            Interlocked.Add(ref _subscriptionCount, -count);
            Park(gm, subscriber);
            RebuildFollowed();
            return count;
        }
    }

    /// <summary>The channels <paramref name="gm"/> follows with the time each turns off, ordered by name.</summary>
    public IReadOnlyList<(string Channel, DateTimeOffset ExpiresAt)> Subscriptions(ObjectGuid gm)
    {
        lock (_gate)
        {
            return _subscribers.TryGetValue(gm, out Subscriber? subscriber)
                ? [.. subscriber.Channels.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => (c.Key, c.Value.ExpiresAt))]
                : [];
        }
    }

    /// <summary>The channels <paramref name="gm"/> follows with scope and the time each turns off, ordered by name.</summary>
    public IReadOnlyList<GmStreamSubscription> Details(ObjectGuid gm)
    {
        lock (_gate)
        {
            return _subscribers.TryGetValue(gm, out Subscriber? subscriber)
                ? [.. subscriber.Channels.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => new GmStreamSubscription(c.Key, c.Value.Scope, c.Value.ExpiresAt))]
                : [];
        }
    }

    /// <summary>The GUIDs of every GM that holds at least one subscription right now (a snapshot).</summary>
    public IReadOnlyList<ObjectGuid> Subscribers()
    {
        lock (_gate)
        {
            return [.. _subscribers.Keys];
        }
    }

    /// <summary>
    /// Replace the viewer snapshot of every subscribed GM with what <paramref name="resolve"/> reports (null: the GM is not in the
    /// world, so it has no viewer and only events that involve it by GUID or have the <see cref="GmStreamScope.All"/> scope match).
    /// <paramref name="resolve"/> runs outside the lock; call it from one thread (the world thread), once per tick.
    /// </summary>
    public void RefreshViewers(Func<ObjectGuid, GmStreamViewer?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ObjectGuid[] gms;
        lock (_gate)
        {
            if (_subscribers.Count == 0)
            {
                return;
            }

            gms = [.. _subscribers.Keys];
        }

        var viewers = new GmStreamViewer?[gms.Length];
        for (int i = 0; i < gms.Length; i++)
        {
            viewers[i] = resolve(gms[i]);
        }

        lock (_gate)
        {
            for (int i = 0; i < gms.Length; i++)
            {
                if (_subscribers.TryGetValue(gms[i], out Subscriber? subscriber))
                {
                    subscriber.Viewer = viewers[i];
                }
            }
        }
    }

    /// <summary>
    /// Offer a global line (no place, no actor) to every GM following <paramref name="channel"/> whatever its scope, within each GM's
    /// rate limit. Returns how many GMs it was queued for (the others were over their limit).
    /// </summary>
    public int Publish(string channel, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return PublishCore(channel, scoped: false, default, line, Verbatim);
    }

    /// <summary>
    /// Offer an event to every GM following <paramref name="channel"/> whose scope matches <paramref name="subject"/>, within each GM's
    /// rate limit; <paramref name="format"/> builds the line once, for the first matching GM that has a token, and is never called
    /// when no GM matches. A GM involved in the event (as <see cref="GmTraceSubject.Actor"/> or <see cref="GmTraceSubject.Other"/>) always
    /// matches. Otherwise <see cref="GmStreamScope.All"/> matches, <see cref="GmStreamScope.Target"/> needs the GM's selection to be the
    /// actor or the other party and (when the subject has a position) the event to be on the GM's map and instance within range, and
    /// <see cref="GmStreamScope.Near"/> needs a position on the GM's map and instance within range. Returns how many GMs it was queued for.
    /// </summary>
    public int Publish<TState>(string channel, in GmTraceSubject subject, TState state, Func<TState, string> format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return PublishCore(channel, scoped: true, subject, state, format);
    }

    private int PublishCore<TState>(string channel, bool scoped, in GmTraceSubject subject, TState state, Func<TState, string> format)
    {
        if (!HasSubscribers(channel))
        {
            return 0;
        }

        long now = _clock.GetUtcNow().UtcTicks;
        string? tagged = null; // built for the first matching GM that has a token: an event nobody matches allocates nothing
        int queued = 0;
        lock (_gate)
        {
            foreach ((ObjectGuid gm, Subscriber subscriber) in _subscribers)
            {
                if (!subscriber.Channels.TryGetValue(channel, out ChannelState channelState) || (scoped && !Matches(gm, subscriber, channelState.Scope, in subject)))
                {
                    continue;
                }

                Refill(subscriber, now);
                if (subscriber.Tokens < 1)
                {
                    subscriber.Suppressed++;
                    continue;
                }

                subscriber.Tokens -= 1;
                NoticeSuppressed(gm, subscriber);
                tagged ??= "[" + channel + "] " + format(state);
                Enqueue(new GmStreamLine(gm, tagged));
                queued++;
            }
        }

        return queued;
    }

    private bool Matches(ObjectGuid gm, Subscriber subscriber, GmStreamScope scope, in GmTraceSubject subject)
    {
        if (scope == GmStreamScope.All || subject.Actor == gm || subject.Other == gm)
        {
            return true;
        }

        if (subscriber.Viewer is not { } viewer)
        {
            return false;
        }

        if (scope == GmStreamScope.Target)
        {
            if (viewer.Selection.IsEmpty || (subject.Actor != viewer.Selection && subject.Other != viewer.Selection))
            {
                return false;
            }

            return !subject.HasPosition || InRange(in viewer, in subject);
        }

        return subject.HasPosition && InRange(in viewer, in subject);
    }

    // 2D (the map's visibility distance is a 2D test): same map and instance, and the squared horizontal distance within Range squared.
    private static bool InRange(in GmStreamViewer viewer, in GmTraceSubject subject)
    {
        if (viewer.MapId != subject.MapId || viewer.InstanceId != subject.InstanceId)
        {
            return false;
        }

        float dx = viewer.X - subject.X;
        float dy = viewer.Y - subject.Y;
        return (dx * dx) + (dy * dy) <= viewer.Range * viewer.Range;
    }

    /// <summary>End every subscription past its timeout and queue a notice for the GM; also tells a GM how many lines were suppressed once the bucket has refilled. Returns how many subscriptions ended.</summary>
    public int Sweep()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        int ended = 0;
        lock (_gate)
        {
            List<ObjectGuid>? empty = null;
            foreach ((ObjectGuid gm, Subscriber subscriber) in _subscribers)
            {
                foreach (string channel in subscriber.Channels.Where(c => c.Value.ExpiresAt <= now).Select(c => c.Key).ToArray())
                {
                    subscriber.Channels.Remove(channel);
                    Interlocked.Decrement(ref _subscriptionCount);
                    EnqueueNotice(new GmStreamLine(gm, $"[stream] {channel} turned off (timed out after {Options.AutoOffAfter.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture)} min)"));
                    ended++;
                }

                if (subscriber.Channels.Count == 0)
                {
                    NoticeSuppressed(gm, subscriber); // the subscriber is going away: say so now, the count would be lost
                    (empty ??= []).Add(gm);
                    continue;
                }

                if (subscriber.Suppressed > 0)
                {
                    Refill(subscriber, now.UtcTicks);
                    if (subscriber.Tokens >= 1)
                    {
                        NoticeSuppressed(gm, subscriber); // publishing stopped: say so now instead of waiting for a next line
                    }
                }
            }

            if (empty is not null)
            {
                foreach (ObjectGuid gm in empty)
                {
                    DropIfEmpty(gm, _subscribers[gm]);
                }
            }

            if (ended > 0)
            {
                RebuildFollowed();
            }

            List<ObjectGuid>? refilled = null;
            foreach ((ObjectGuid gm, ParkedBucket bucket) in _parked)
            {
                double tokens = bucket.Tokens + (double)(now.UtcTicks - bucket.RefilledAtTicks) / TimeSpan.TicksPerSecond * Options.MaxLinesPerSecond;
                if (tokens >= Options.MaxLinesPerSecond)
                {
                    (refilled ??= []).Add(gm);
                }
            }

            if (refilled is not null)
            {
                foreach (ObjectGuid gm in refilled)
                {
                    _parked.Remove(gm);
                }
            }
        }

        return ended;
    }

    /// <summary>Hand every queued line to <paramref name="deliver"/>, oldest first, outside the stream's lock. Returns the number delivered.</summary>
    public int Drain(Action<GmStreamLine> deliver)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        GmStreamLine[] lines;
        lock (_gate)
        {
            if (_outbox.Count == 0 && _notices.Count == 0)
            {
                return 0;
            }

            // Both queues are in sequence order; merge them so a notice never overtakes a line published before it.
            lines = new GmStreamLine[_outbox.Count + _notices.Count];
            for (int i = 0; i < lines.Length; i++)
            {
                bool fromNotices = _outbox.Count == 0 || (_notices.Count != 0 && _notices.Peek().Seq < _outbox.Peek().Seq);
                lines[i] = (fromNotices ? _notices.Dequeue() : _outbox.Dequeue()).Line;
            }
        }

        foreach (GmStreamLine line in lines)
        {
            deliver(line);
        }

        return lines.Length;
    }

    // Caller holds _gate. Publishes the set of followed channel names for the lock-free HasSubscribers.
    private void RebuildFollowed()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Subscriber subscriber in _subscribers.Values)
        {
            foreach (string channel in subscriber.Channels.Keys)
            {
                names.Add(channel);
            }
        }

        Volatile.Write(ref _followed, names.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }

    private void Enqueue(GmStreamLine line)
    {
        if (_outbox.Count >= MaxOutbox)
        {
            _outbox.Dequeue(); // nobody is draining: keep the newest
            Interlocked.Increment(ref _outboxDropped);
        }

        _outbox.Enqueue(new Sequenced(++_sequence, line));
    }

    private void EnqueueNotice(GmStreamLine line) => _notices.Enqueue(new Sequenced(++_sequence, line));

    private void Refill(Subscriber subscriber, long nowTicks)
    {
        long elapsed = nowTicks - subscriber.RefilledAtTicks;
        if (elapsed > 0)
        {
            subscriber.Tokens = Math.Min(Options.MaxLinesPerSecond, subscriber.Tokens + (double)elapsed / TimeSpan.TicksPerSecond * Options.MaxLinesPerSecond);
        }

        subscriber.RefilledAtTicks = nowTicks;
    }

    private void NoticeSuppressed(ObjectGuid gm, Subscriber subscriber)
    {
        if (subscriber.Suppressed > 0)
        {
            EnqueueNotice(new GmStreamLine(gm, string.Create(CultureInfo.InvariantCulture, $"[stream] {subscriber.Suppressed} lines suppressed (limit {Options.MaxLinesPerSecond}/s)")));
            subscriber.Suppressed = 0;
        }
    }

    private void DropIfEmpty(ObjectGuid gm, Subscriber subscriber)
    {
        if (subscriber.Channels.Count == 0)
        {
            NoticeSuppressed(gm, subscriber);
            _subscribers.Remove(gm);
            Park(gm, subscriber);
        }
    }

    private void Park(ObjectGuid gm, Subscriber subscriber)
    {
        Refill(subscriber, _clock.GetUtcNow().UtcTicks);
        if (subscriber.Tokens < Options.MaxLinesPerSecond)
        {
            _parked[gm] = new ParkedBucket(subscriber.Tokens, subscriber.RefilledAtTicks);
        }
    }
}
