using System.Globalization;
using ArcaneCore.Game;

namespace ArcaneCore.World.Gm.Diagnostics;

/// <summary>Limits of the GM stream.</summary>
public sealed class GmStreamOptions
{
    /// <summary>Lines one GM can receive per second across all channels; the rest are dropped and counted (one notice follows).</summary>
    public int MaxLinesPerSecond { get; init; } = 5;

    /// <summary>A subscription turns itself off this long after it was last (re)subscribed.</summary>
    public TimeSpan AutoOffAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Channels one GM can follow at once.</summary>
    public int MaxChannelsPerGm { get; init; } = 8;
}

/// <summary>A line queued for one GM by <see cref="GmStream"/>.</summary>
public readonly record struct GmStreamLine(ObjectGuid Gm, string Text);

/// <summary>
/// Opt-in live feed of debug events to GMs (damage log, proc trace, spell trace, moves, anticheat findings): a GM turns a named
/// channel <c>on</c> or <c>off</c>, producers <see cref="Publish"/> to the channel, and every subscribed GM gets the lines in chat.
/// <list type="bullet">
/// <item>Rate limited per GM (<see cref="GmStreamOptions.MaxLinesPerSecond"/>): excess lines are dropped, never queued without bound, and the next delivered batch is preceded by one "N lines suppressed" notice.</item>
/// <item>A subscription ends after <see cref="GmStreamOptions.AutoOffAfter"/> (<see cref="Sweep"/>) or when the GM logs out (<see cref="UnsubscribeAll"/>); the GM is told when it times out.</item>
/// <item>Zero cost when nobody listens: <see cref="IsActive"/> is one volatile read, so a producer guards the formatting with it.</item>
/// </list>
/// <para>
/// Thread safety: all members may be called from any thread (map threads publish, the world thread subscribes). Publishing
/// never touches a session: lines go to an outbox that <see cref="Drain"/> hands to the world thread, which owns the player lookup
/// and the send (<see cref="GmStreamFeature"/>). Channel names are case-insensitive.
/// </para>
/// </summary>
public sealed class GmStream
{
    private sealed class Subscriber
    {
        public readonly Dictionary<string, DateTimeOffset> Channels = new(StringComparer.OrdinalIgnoreCase);
        public long WindowStartTicks;
        public bool WindowOpen;
        public int InWindow;
        public int Suppressed;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<ObjectGuid, Subscriber> _subscribers = [];
    private const int MaxOutbox = 1024;
    private readonly Queue<GmStreamLine> _outbox = new();
    private readonly TimeProvider _clock;
    private int _subscriptionCount;

    public GmStream(GmStreamOptions? options = null, TimeProvider? clock = null)
    {
        Options = options ?? new GmStreamOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Options.MaxLinesPerSecond, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Options.MaxChannelsPerGm, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Options.AutoOffAfter, TimeSpan.Zero);
        _clock = clock ?? TimeProvider.System;
    }

    public GmStreamOptions Options { get; }

    /// <summary>True while any GM follows any channel: the cheap guard before formatting a line.</summary>
    public bool IsActive => Volatile.Read(ref _subscriptionCount) != 0;

    /// <summary>Whether any GM follows <paramref name="channel"/>.</summary>
    public bool HasSubscribers(string channel)
    {
        if (!IsActive)
        {
            return false;
        }

        lock (_gate)
        {
            foreach (Subscriber subscriber in _subscribers.Values)
            {
                if (subscriber.Channels.ContainsKey(channel))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Follow <paramref name="channel"/> as <paramref name="gm"/>; following an already followed channel renews its timeout.
    /// False when the GM already follows <see cref="GmStreamOptions.MaxChannelsPerGm"/> other channels.
    /// </summary>
    public bool Subscribe(ObjectGuid gm, string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        DateTimeOffset expires = _clock.GetUtcNow() + Options.AutoOffAfter;
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(gm, out Subscriber? subscriber))
            {
                subscriber = new Subscriber();
                _subscribers[gm] = subscriber;
            }

            if (!subscriber.Channels.ContainsKey(channel))
            {
                if (subscriber.Channels.Count >= Options.MaxChannelsPerGm)
                {
                    DropIfEmpty(gm, subscriber);
                    return false;
                }

                Interlocked.Increment(ref _subscriptionCount);
            }

            subscriber.Channels[channel] = expires;
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
            return count;
        }
    }

    /// <summary>The channels <paramref name="gm"/> follows with the time each turns off, ordered by name.</summary>
    public IReadOnlyList<(string Channel, DateTimeOffset ExpiresAt)> Subscriptions(ObjectGuid gm)
    {
        lock (_gate)
        {
            return _subscribers.TryGetValue(gm, out Subscriber? subscriber)
                ? [.. subscriber.Channels.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => (c.Key, c.Value))]
                : [];
        }
    }

    /// <summary>
    /// Offer <paramref name="line"/> to every GM following <paramref name="channel"/>, within each GM's rate limit. Returns how
    /// many GMs it was queued for (the others were over their limit).
    /// </summary>
    public int Publish(string channel, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!IsActive)
        {
            return 0;
        }

        long now = _clock.GetUtcNow().UtcTicks;
        const long second = TimeSpan.TicksPerSecond;
        string tagged = "[" + channel + "] " + line;
        int queued = 0;
        lock (_gate)
        {
            foreach ((ObjectGuid gm, Subscriber subscriber) in _subscribers)
            {
                if (!subscriber.Channels.ContainsKey(channel))
                {
                    continue;
                }

                if (!subscriber.WindowOpen || now - subscriber.WindowStartTicks >= second)
                {
                    subscriber.WindowStartTicks = now;
                    subscriber.WindowOpen = true;
                    subscriber.InWindow = 0;
                    if (subscriber.Suppressed > 0)
                    {
                        Enqueue(new GmStreamLine(gm, string.Create(CultureInfo.InvariantCulture, $"[stream] {subscriber.Suppressed} lines suppressed (limit {Options.MaxLinesPerSecond}/s)")));
                        subscriber.Suppressed = 0;
                    }
                }

                if (subscriber.InWindow >= Options.MaxLinesPerSecond)
                {
                    subscriber.Suppressed++;
                    continue;
                }

                subscriber.InWindow++;
                Enqueue(new GmStreamLine(gm, tagged));
                queued++;
            }
        }

        return queued;
    }

    /// <summary>End every subscription past its timeout and queue a notice for the GM. Returns how many ended.</summary>
    public int Sweep()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        int ended = 0;
        lock (_gate)
        {
            List<ObjectGuid>? empty = null;
            foreach ((ObjectGuid gm, Subscriber subscriber) in _subscribers)
            {
                foreach (string channel in subscriber.Channels.Where(c => c.Value <= now).Select(c => c.Key).ToArray())
                {
                    subscriber.Channels.Remove(channel);
                    Interlocked.Decrement(ref _subscriptionCount);
                    Enqueue(new GmStreamLine(gm, $"[stream] {channel} turned off (timed out after {Options.AutoOffAfter.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture)} min)"));
                    ended++;
                }

                if (subscriber.Channels.Count == 0)
                {
                    (empty ??= []).Add(gm);
                }
            }

            if (empty is not null)
            {
                foreach (ObjectGuid gm in empty)
                {
                    _subscribers.Remove(gm);
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
            if (_outbox.Count == 0)
            {
                return 0;
            }

            lines = [.. _outbox];
            _outbox.Clear();
        }

        foreach (GmStreamLine line in lines)
        {
            deliver(line);
        }

        return lines.Length;
    }

    private void Enqueue(GmStreamLine line)
    {
        if (_outbox.Count >= MaxOutbox)
        {
            _outbox.Dequeue(); // nobody is draining: keep the newest
        }

        _outbox.Enqueue(line);
    }

    private void DropIfEmpty(ObjectGuid gm, Subscriber subscriber)
    {
        if (subscriber.Channels.Count == 0)
        {
            _subscribers.Remove(gm);
        }
    }
}
