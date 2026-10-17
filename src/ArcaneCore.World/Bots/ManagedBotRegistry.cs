using System.Collections.Concurrent;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Bots;

/// <summary>
/// One managed bot that is logged in: its registry row, its service scope and its session. Core-owned (docs/design/hot-reload-tiers.md,
/// section 4.1): it outlives a detach of the playerbot features, which rebuild their brains around it when they attach again.
/// </summary>
public sealed class ManagedBot
{
    private ManagedPlayerbot _record;

    public ManagedBot(ManagedPlayerbot record, AsyncServiceScope scope, WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(session);
        _record = record;
        BotId = record.BotId;
        Scope = scope;
        Session = session;
    }

    public Guid BotId { get; }

    /// <summary>The registry row as last written by this process. Any thread.</summary>
    public ManagedPlayerbot Record
    {
        get => Volatile.Read(ref _record);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.BotId != BotId)
            {
                throw new ArgumentException($"the row of bot {value.BotId} cannot replace the row of bot {BotId}", nameof(value));
            }

            Volatile.Write(ref _record, value);
        }
    }

    /// <summary>The scope the session was created in (disposed when the bot logs out).</summary>
    public AsyncServiceScope Scope { get; }

    /// <summary>The bot's ordinary world session.</summary>
    public WorldSession Session { get; }

    /// <summary>The character's name (empty before the character is in the world).</summary>
    public string Name => Session.Player?.Name ?? string.Empty;

    /// <summary>Whether the row says running and the session is open (the <c>Running</c> state of a bot's status line).</summary>
    public bool Running => Record.State == ManagedPlayerbotState.Running && Session.State != SessionState.Closed;
}

/// <summary>
/// The core-owned state of the managed bots (docs/design/hot-reload-tiers.md, sections 4.1 and 4.4 2a): the bots that are logged
/// in, the split-brain latch, and the seams through which the core reaches the playerbot pack (a session policy, a cast refusal
/// observer, a tick hang sink). The playerbot features keep their brains and plans; everything that must survive a detach of those
/// features, or a swap of their pack, lives here. A pinned feature (discovered, never detached).
/// <para>
/// Each seam holds at most one registration, made through the pack's <see cref="FeatureSubscriptions"/> so that a detach removes it;
/// the core treats an empty seam as "no playerbot pack" and stays correct without it.
/// </para>
/// <para>
/// At <see cref="WorldLifecycleStage.BeforeWorldStop"/> (after the playerbot shutdown, which runs first) a bot still logged in is one
/// whose feature was detached: its session is closed and its character flushed here, and its registry row is left as it was so the
/// next start restores it.
/// </para>
/// </summary>
public sealed class ManagedBotRegistry(IServiceProvider? services = null, ILogger<ManagedBotRegistry>? logger = null) : IWorldFeature
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<Guid, ManagedBot> _bots = new();
    private volatile bool _splitBrain;
    private IManagedBotSessionPolicy? _sessionPolicy;
    private IBotCastRefusalObserver? _castRefusals;
    private IBotTickHangSink? _hangSink;
    private IDisposable? _shutdownHook;

    /// <summary>How long the shutdown fallback waits for the sessions it closes.</summary>
    internal static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The bots logged in now (ordered by bot id). Any thread.</summary>
    public IReadOnlyList<ManagedBot> Bots => [.. _bots.Values.OrderBy(b => b.BotId)];

    /// <summary>How many bots are logged in.</summary>
    public int Count => _bots.Count;

    /// <summary>How many bots are running (row running, session open): the <c>.server info</c> bot count.</summary>
    public int RunningCount => _bots.Values.Count(b => b.Running);

    /// <summary>The names of the running bots (case-insensitive set).</summary>
    public HashSet<string> RunningNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedBot bot in _bots.Values)
        {
            if (bot.Running && bot.Name is { Length: > 0 } name && !string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Set when another world process changed the playerbot rows (split brain): this process then writes no playerbot row until an
    /// operator clears it or the process restarts. Kept here so a detach of the bot features does not forget it.
    /// </summary>
    public bool SplitBrain
    {
        get => _splitBrain;
        set => _splitBrain = value;
    }

    /// <summary>The session policy of the attached playerbot pack (null: none attached).</summary>
    public IManagedBotSessionPolicy? SessionPolicy => Volatile.Read(ref _sessionPolicy);

    /// <summary>The cast refusal observer of the attached bot QA (null: none).</summary>
    public IBotCastRefusalObserver? CastRefusals => Volatile.Read(ref _castRefusals);

    /// <summary>The tick hang sink of the attached bot QA (null: none).</summary>
    public IBotTickHangSink? HangSink => Volatile.Read(ref _hangSink);

    public bool TryAdd(ManagedBot bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        return _bots.TryAdd(bot.BotId, bot);
    }

    /// <summary>Remove <paramref name="bot"/> (that entry only: a newer entry of the same bot stays).</summary>
    public bool TryRemove(ManagedBot bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        return _bots.TryRemove(new KeyValuePair<Guid, ManagedBot>(bot.BotId, bot));
    }

    public bool TryGet(Guid botId, out ManagedBot bot) => _bots.TryGetValue(botId, out bot!);

    /// <summary>Use <paramref name="policy"/> until the result is disposed. One at a time.</summary>
    public IDisposable UseSessionPolicy(IManagedBotSessionPolicy policy)
    {
        Claim(ref _sessionPolicy, policy, "session policy");
        return new Registration(() => Interlocked.CompareExchange(ref _sessionPolicy, null, policy));
    }

    /// <summary>Use <paramref name="observer"/> until the result is disposed. One at a time.</summary>
    public IDisposable UseCastRefusals(IBotCastRefusalObserver observer)
    {
        Claim(ref _castRefusals, observer, "cast refusal observer");
        return new Registration(() => Interlocked.CompareExchange(ref _castRefusals, null, observer));
    }

    /// <summary>Use <paramref name="sink"/> until the result is disposed. One at a time.</summary>
    public IDisposable UseHangSink(IBotTickHangSink sink)
    {
        Claim(ref _hangSink, sink, "tick hang sink");
        return new Registration(() => Interlocked.CompareExchange(ref _hangSink, null, sink));
    }

    public void Attach(WorldRuntime world)
    {
        if (services?.GetService<IWorldLifecycleHooks>() is { } hooks && _shutdownHook is null)
        {
            _shutdownHook = hooks.Register(WorldLifecycleStage.BeforeWorldStop, "managed-bots.orphans", _ => LogOutOrphansAsync());
        }
    }

    /// <summary>The features stop after the world: whatever the shutdown hook did not reach is closed here.</summary>
    public Task StopAsync() => LogOutOrphansAsync();

    /// <summary>
    /// Close the session of every bot still logged in and flush its character (bounded by <see cref="CloseTimeout"/>), then dispose
    /// its scope. A bot is still here at shutdown only when its feature was detached (the playerbot shutdown stops every bot it runs).
    /// The registry row is not written: it still says running and desired, so the next start restores the bot.
    /// </summary>
    internal async Task LogOutOrphansAsync()
    {
        ManagedBot[] orphans = [.. _bots.Values];
        if (orphans.Length == 0)
        {
            return;
        }

        _logger.LogWarning("{Count} managed bot(s) are still logged in at shutdown with no playerbot feature attached; closing their sessions", orphans.Length);
        foreach (ManagedBot bot in orphans)
        {
            bot.Session.Kick();
        }

        try
        {
            await Task.WhenAll(orphans.Select(b => b.Session.ManagedClosed)).WaitAsync(CloseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Managed bot sessions did not all close within {Seconds} s at shutdown", CloseTimeout.TotalSeconds);
        }

        CharacterSaveQueue? saves = services?.GetService<CharacterSaveQueue>();
        foreach (ManagedBot bot in orphans)
        {
            try
            {
                if (saves is not null)
                {
                    await saves.FlushCharacterAsync(bot.Record.CharacterId, CancellationToken.None).WaitAsync(CloseTimeout).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Managed bot {BotId}: the character save did not complete at shutdown", bot.BotId);
            }
            finally
            {
                TryRemove(bot);
                await bot.Scope.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static void Claim<T>(ref T? slot, T value, string what)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Interlocked.CompareExchange(ref slot, value, null) is not null)
        {
            throw new InvalidOperationException($"a {what} is already registered");
        }
    }

    private sealed class Registration(Action undo) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                undo();
            }
        }
    }
}
