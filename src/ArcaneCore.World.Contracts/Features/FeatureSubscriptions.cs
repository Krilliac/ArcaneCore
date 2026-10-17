using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Features;

/// <summary>
/// The core-owned bag every subscription of a detachable feature goes through (docs/design/hot-reload-tiers.md, section
/// 4.2): each entry is the undo of one subscription, and <see cref="Dispose"/> runs them all, newest first, so a detach
/// removes every handler the feature put on a core object. A subscription made around the bag (a bare <c>+=</c>) keeps the
/// feature, and later its pack, alive and still called after detach.
/// <para>
/// Thread-safe. After <see cref="Dispose"/> every subscribe throws <see cref="ObjectDisposedException"/> before it
/// touches the core object, so late work of a detached feature cannot re-subscribe behind the host's back.
/// </para>
/// </summary>
public sealed class FeatureSubscriptions : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Action> _undo = [];
    private bool _disposed;

    public FeatureSubscriptions(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        Owner = owner;
    }

    /// <summary>Who subscribed (the feature name), for logs.</summary>
    public string Owner { get; }

    /// <summary>Subscriptions currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count;
            }
        }
    }

    /// <summary>Whether the bag was disposed (the feature detached).</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>
    /// Subscribe <paramref name="handler"/> with <paramref name="add"/> and record <paramref name="remove"/> as its undo,
    /// e.g. <c>bag.Subscribe(handler, h =&gt; group.LeaderChanged += h, h =&gt; group.LeaderChanged -= h)</c>.
    /// </summary>
    public void Subscribe<THandler>(THandler handler, Action<THandler> add, Action<THandler> remove)
        where THandler : Delegate
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            add(handler);
            _undo.Add(() => remove(handler));
        }
    }

    /// <summary>Hold a registration that is undone by disposing it (a hook or waiver registration, a timer).</summary>
    public void Add(IDisposable registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            if (_disposed)
            {
                registration.Dispose();
                throw new ObjectDisposedException(GetType().FullName, $"the subscriptions of {Owner} were disposed; the registration was undone");
            }

            _undo.Add(registration.Dispose);
        }
    }

    /// <summary><see cref="WorldRuntime.PlayerLoggedIn"/>.</summary>
    public void OnPlayerLoggedIn(WorldRuntime world, Action<Player> handler)
    {
        ArgumentNullException.ThrowIfNull(world);
        Subscribe(handler, h => world.PlayerLoggedIn += h, h => world.PlayerLoggedIn -= h);
    }

    /// <summary><see cref="WorldRuntime.PlayerLoggingOut"/>.</summary>
    public void OnPlayerLoggingOut(WorldRuntime world, Action<Player> handler)
    {
        ArgumentNullException.ThrowIfNull(world);
        Subscribe(handler, h => world.PlayerLoggingOut += h, h => world.PlayerLoggingOut -= h);
    }

    /// <summary><see cref="WorldRuntime.Updated"/> (once per tick, after the maps).</summary>
    public void OnUpdated(WorldRuntime world, Action<uint> handler)
    {
        ArgumentNullException.ThrowIfNull(world);
        Subscribe(handler, h => world.Updated += h, h => world.Updated -= h);
    }

    /// <summary><see cref="WorldRuntime.WorldTick"/> (once per tick, before the maps).</summary>
    public void OnWorldTick(WorldRuntime world, Action<uint> handler)
    {
        ArgumentNullException.ThrowIfNull(world);
        Subscribe(handler, h => world.WorldTick += h, h => world.WorldTick -= h);
    }

    /// <summary><see cref="WorldRuntime.MapCreated"/>.</summary>
    public void OnMapCreated(WorldRuntime world, Action<Map> handler)
    {
        ArgumentNullException.ThrowIfNull(world);
        Subscribe(handler, h => world.MapCreated += h, h => world.MapCreated -= h);
    }

    /// <summary><see cref="WorldRuntime.MapUnloading"/>.</summary>
    public void OnMapUnloading(WorldRuntime world, Action<Map> handler)
    {
        ArgumentNullException.ThrowIfNull(world);
        Subscribe(handler, h => world.MapUnloading += h, h => world.MapUnloading -= h);
    }

    /// <summary><see cref="SpellSystem.RegisterObserver"/>, undone by <see cref="SpellSystem.UnregisterObserver"/>.</summary>
    public void Observe(SpellSystem spells, ISpellCastObserver observer)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            spells.RegisterObserver(observer);
            _undo.Add(() => spells.UnregisterObserver(observer));
        }
    }

    /// <summary>
    /// Undo every subscription, newest first. Every undo runs even when one throws; the failures are then thrown together
    /// (<see cref="AggregateException"/>). A second call does nothing.
    /// </summary>
    public void Dispose()
    {
        Action[] undo;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            undo = [.. _undo];
            _undo.Clear();
        }

        List<Exception>? failures = null;
        for (int i = undo.Length - 1; i >= 0; i--)
        {
            try
            {
                undo[i]();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException($"{failures.Count} unsubscribe(s) of {Owner} failed", failures);
        }
    }
}
