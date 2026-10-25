using ArcaneCore.Kernel.Diagnostics;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Ops.Control;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The world daemon's map feed (docs/areas/map-feed.md): a local, read-only named pipe over which a visualizer (<c>tools/ArcaneCore.Atlas</c>)
/// watches one map (players, bots, creatures, game objects, creature splines, bot routes, loaded grids) and asks line-of-sight, height,
/// path and under-map questions. Off by default (<see cref="WorldMapFeedOptions.Enabled"/>). Protocol: one JSON object per line, protocol 1.
/// <para>
/// Security: exactly the control pipe's (<see cref="LocalPipeServer"/> + <see cref="ControlPipeSecurity"/>): the daemon's account and
/// SYSTEM only, NETWORK denied, <c>PIPE_REJECT_REMOTE_CLIENTS</c> read back from the kernel. All <see cref="WorldMapFeedOptions.MaxConnections"/>
/// instances of the name are created at start, the first with <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c> (a name another process created first is
/// refused, never joined), so no other process can hold a slot and a client beyond the limit finds the pipe busy. The feed never changes
/// the world: it reads, and its questions never load a tile.
/// </para>
/// <para>
/// Threads: the accept loops and the connections run on the thread pool. The only code on the world thread is <see cref="OnUpdated"/> (the
/// post-barrier <see cref="WorldRuntime.Updated"/> handler, attached only while somebody is subscribed, so an idle feed costs the world
/// thread nothing) and the queries a connection hands to <see cref="WorldRuntime.InvokeAsync{T}"/>. A capture is throttled per
/// subscription by the elapsed time the world reports, shared between subscriptions of the same view, copied into pooled frames, and
/// skipped for a connection that is behind; serialization and every write happen on the connection's own tasks.
/// </para>
/// </summary>
public sealed class WorldMapFeed : IHostedService
{
    /// <summary>Logger category of the feed.</summary>
    public const string LogCategory = "ArcaneCore.MapFeed";

    /// <summary>The frame rate a subscription gets when it does not ask for one (before the <see cref="WorldMapFeedOptions.MaxHz"/> cap).</summary>
    public const double DefaultHz = 4.0;

    /// <summary>The slowest frame rate a subscription may have.</summary>
    public const double MinHz = 0.1;

    private static readonly Lazy<string> Sha = new(ReadBuildSha);

    private readonly WorldMapFeedOptions _options;
    private readonly WorldPacketToolsOptions _toolsOptions;
    private readonly WorldRuntime _world;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private readonly List<NamedPipeServerStream> _servers = [];
    private readonly List<Task> _loops = [];
    private readonly FramePool _pool;
    private readonly FeedFrame?[] _tickFrames;
    private readonly HashSet<ulong> _botGuids = [];
    private readonly Action<Exception> _onRouteFault;
    private FeedSubscription[] _subscriptions = [];
    private bool _attached;
    private bool _registryResolved;
    private ManagedBotRegistry? _registry;
    private int _tickFrameCount;
    private bool _botsReady;
    private IBotRouteSource? _routes;
    private long _updatedCalls;
    private long _framesCaptured;
    private long _framesDropped;
    private long _captureFaults;
    private long _routeFaults;
    private long _connections;
    private int _active;

    /// <summary>Build the feed from its options and the world it reads; nothing is created until <see cref="StartAsync"/>.</summary>
    public WorldMapFeed(IOptions<WorldMapFeedOptions> options, WorldRuntime world, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _toolsOptions = services.GetService<IOptions<WorldPacketToolsOptions>>()?.Value ?? new WorldPacketToolsOptions();
        _world = world;
        _services = services;
        _logger = loggerFactory.CreateLogger(LogCategory);
        _onRouteFault = NoteRouteFault;

        // Sized from the limit the options allow (a value outside it keeps the feed off, but the service is still built).
        int connections = Math.Clamp(_options.MaxConnections, 1, WorldMapFeedOptions.MaxMaxConnections);

        // The separate web pipe's connections are served by this feed too (their frames come from the same pool).
        if (services.GetService<IOptions<WebFeedPipeOptions>>()?.Value is { Enabled: true } web)
        {
            connections += Math.Clamp(web.MaxConnections, 1, WebFeedPipeOptions.MaxMaxConnections);
        }

        // Per connection at most MaxInFlightFrames frames are reserved, and one tick's own frames are held until the tick's end.
        _pool = new FramePool(((MapFeedConnection.MaxInFlightFrames + 1) * connections) + 1);
        _tickFrames = new FeedFrame?[connections];
    }

    /// <summary>The pipe name being served (without <c>\\.\pipe\</c>), or null when the pipe is off.</summary>
    public string? PipeName { get; private set; }

    /// <summary>Whether an accept loop is running.</summary>
    public bool IsListening => _loops.Count > 0 && _loops.Exists(static t => !t.IsCompleted);

    /// <summary>Connections accepted so far (tests).</summary>
    public long ConnectionsServed => Volatile.Read(ref _connections);

    /// <summary>Connections being served now (tests).</summary>
    internal int ActiveConnections => Volatile.Read(ref _active);

    /// <summary>The pipe name template (tests pick a unique one: parallel test hosts share a process id, and the name is held exclusively).</summary>
    internal string PipeNameTemplate { get; init; } = WorldMapFeedOptions.PipeNameTemplate;

    /// <summary>How long a connection without a subscription may stay silent before it is closed.</summary>
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How long a write may make no progress before the connection is closed.</summary>
    internal TimeSpan WriteStallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a query waits for the world thread.</summary>
    internal TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The packet tools (docs/areas/map-feed.md, "Packet tools"), or null while they are off: they need the feed listening,
    /// <see cref="WorldPacketToolsOptions.Enabled"/>, and the Development environment or <see cref="WorldPacketToolsOptions.AllowOutsideDevelopment"/>.
    /// </summary>
    internal PacketTools? Tools { get; private set; }

    /// <summary>The host environment name the tools are judged by (tests set it; else the host's, "Production" when there is none).</summary>
    internal string? EnvironmentName { get; init; }

    /// <summary>Whether this is the live server (null: read <see cref="WorldPacketToolsOptions.LiveServerVariable"/>); tests set it instead of the process environment.</summary>
    internal bool? LiveServer { get; init; }

    /// <summary>The ring of this world's anomalies (docs/areas/atlas-insight.md): attached while the feed listens, null before and after.</summary>
    internal MapFeedAnomalyRing? AnomalyRing { get; private set; }

    /// <summary>The spell system, whose aura holders an <c>inspect</c> lists (null when the spell feature is not attached).</summary>
    internal SpellSystem? Spells => _services.GetService<SpellFeature>()?.System;

    /// <summary>The playerbot pack's route seam (null without managed bots or without the pack).</summary>
    internal IBotRouteSource? RouteSource => _services.GetService<ManagedBotRegistry>()?.RouteSource;

    /// <summary>Whether <paramref name="guid"/> is a managed bot's player (world thread).</summary>
    internal bool IsBot(ulong guid)
    {
        if (_services.GetService<ManagedBotRegistry>() is not { Count: > 0 } registry)
        {
            return false;
        }

        foreach (ManagedBot bot in registry.Bots)
        {
            if (bot.Session.Player is { } player && player.Guid.Value == guid)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The server streams of the pipe instances (tests read their security descriptors); empty when the pipe is off.</summary>
    internal IReadOnlyList<NamedPipeServerStream> Servers => _servers;

    /// <summary>The protocol extension (the action channel), or null when the host registered none. Set when the feed starts.</summary>
    internal IFeedExtension? Extension { get; set; }

    /// <summary>Whether <c>World:Control:MapFeed:Enabled</c> is set (the web pipe and the action channel are served by an enabled feed only).</summary>
    internal bool IsEnabled => _options.Enabled;

    /// <summary>Whether this process is the live server (the <see cref="LiveServer"/> override, else the <see cref="WorldPacketToolsOptions.LiveServerVariable"/> marker).</summary>
    internal bool IsLiveServer => LiveServer ?? WorldPacketToolsOptions.IsLiveServer();

    /// <summary>The world the feed reads.</summary>
    internal WorldRuntime World => _world;

    /// <summary>The build id the <c>hello</c> reply names (the source revision of the assembly, else its version).</summary>
    internal static string BuildSha => Sha.Value;

    /// <summary>Subscriptions now (tests).</summary>
    internal int SubscriptionCount => Volatile.Read(ref _subscriptions).Length;

    /// <summary>Whether the capture handler is attached to <see cref="WorldRuntime.Updated"/>: false while nobody is subscribed (tests).</summary>
    internal bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                return _attached;
            }
        }
    }

    /// <summary>How often the capture handler ran with a subscription present (tests: stays flat while nobody is subscribed).</summary>
    internal long UpdatedCalls => Volatile.Read(ref _updatedCalls);

    /// <summary>Frames captured (shared frames count once; tests).</summary>
    internal long FramesCaptured => Volatile.Read(ref _framesCaptured);

    /// <summary>Frames a connection did not get because it was behind or the pool was spent (tests).</summary>
    internal long FramesDropped => Volatile.Read(ref _framesDropped);

    /// <summary>Captures that threw (the exception was logged and swallowed; tests).</summary>
    internal long CaptureFaults => Volatile.Read(ref _captureFaults);

    /// <summary>Bot route copies that threw (the route was skipped and logged; tests).</summary>
    internal long RouteFaults => Volatile.Read(ref _routeFaults);

    /// <summary>The frame pool (tests).</summary>
    internal FramePool Pool => _pool;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        MapFeedExtensions.FeedStarted(this, _services);
        if (!_options.Enabled)
        {
            _logger.LogDebug("map feed off: World:Control:MapFeed:Enabled is false");
            if (_toolsOptions.Enabled)
            {
                _logger.LogWarning("packet tools off: they are served by the map feed, and World:Control:MapFeed:Enabled is false");
            }

            return Task.CompletedTask;
        }

        IReadOnlyList<string> problems = _options.Problems();
        if (problems.Count > 0)
        {
            _logger.LogWarning("map feed off: {Problems}", string.Join("; ", problems));
            return Task.CompletedTask;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("map feed off: World:Control:MapFeed is Windows-only (a named pipe with an access list)");
            return Task.CompletedTask;
        }

        string? name = ControlPipeSecurity.ResolveName(PipeNameTemplate, Environment.ProcessId, out string? problem);
        if (name is null)
        {
            _logger.LogWarning("map feed off: the pipe name: {Problem}", problem);
            return Task.CompletedTask;
        }

        // The ring exists before the pipe does, so a tool that connects at once never finds the anomalies query unanswerable.
        AnomalyRing = new MapFeedAnomalyRing(AnomalyLedger.Shared, _world.Identity, _options.AnomaliesUnowned);
        Listen(name);
        if (PipeName is not null)
        {
            EnableTools();
        }
        else
        {
            AnomalyRing.Dispose();
            AnomalyRing = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Turn the packet tools on when the options and the environment permit (docs/areas/map-feed.md, "Packet tools"). Off by default;
    /// refused outside Development unless <see cref="WorldPacketToolsOptions.AllowOutsideDevelopment"/> is also set, and always refused on the live server. Either way the
    /// decision is logged at Warning: the tools let a local tool act as any online character.
    /// </summary>
    private void EnableTools()
    {
        if (!_toolsOptions.Enabled)
        {
            return;
        }

        if (LiveServer ?? WorldPacketToolsOptions.IsLiveServer())
        {
            _logger.LogWarning("packet tools refused: this is the live server ({Variable}); no setting turns them on here", WorldPacketToolsOptions.LiveServerVariable);
            return;
        }

        string environment = EnvironmentName ?? _services.GetService<IHostEnvironment>()?.EnvironmentName ?? Environments.Production;
        if (!_toolsOptions.PermitsEnvironment(environment, liveServer: false))
        {
            _logger.LogWarning(
                "packet tools off: the environment is '{Environment}', not Development; set {Key}=true as well to allow them here",
                environment, WorldPacketToolsOptions.SectionName + ":" + nameof(WorldPacketToolsOptions.AllowOutsideDevelopment));
            return;
        }

        Tools = new PacketTools(_services, _world, _logger);
        if (_services.GetService<PacketToolsPermit>() is { } permit)
        {
            permit.Granted = true;
        }

        if (string.Equals(environment, WorldPacketToolsOptions.DevelopmentEnvironment, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "packet tools on: map-feed clients on this machine may run console commands, inject and send packets as any online character, and tap their traffic (World:Control:PacketTools)");
        }
        else
        {
            _logger.LogWarning(
                "packet tools on OUTSIDE Development (environment '{Environment}', World:Control:PacketTools:AllowOutsideDevelopment): map-feed clients on this machine may run console commands, inject and send packets as any online character, and tap their traffic",
                environment);
        }
    }

    [SupportedOSPlatform("windows")]
    private void Listen(string name)
    {
        string user;
        var servers = new List<NamedPipeServerStream>(_options.MaxConnections);
        try
        {
            System.Security.Principal.SecurityIdentifier sid = ControlPipeSecurity.CurrentUser();
            user = sid.Value;
            for (int i = 0; i < _options.MaxConnections; i++)
            {
                // Not NamedPipeServerStreamAcl: the runtime cannot pass PIPE_REJECT_REMOTE_CLIENTS (LocalPipeServer). Every instance of the
                // name is created now, before any client is served, so none is left for another process to take.
                servers.Add(LocalPipeServer.Create(name, ControlPipeSecurity.Create(sid), (uint)_options.MaxConnections, firstInstance: i == 0));
            }

            foreach (NamedPipeServerStream server in servers)
            {
                if (!LocalPipeServer.RejectsRemoteClients(server.SafePipeHandle))
                {
                    // Never serve a pipe whose remote-client rejection did not take: the NETWORK deny entry would be the only barrier left.
                    throw new IOException("the pipe was created without remote-client rejection");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // FILE_FLAG_FIRST_PIPE_INSTANCE on the first instance; a later instance that cannot be created means another process took
            // the name between two of ours. Either way the name is not ours alone: serve nothing.
            _logger.LogWarning(ex, "map feed off: could not create \\\\.\\pipe\\{Name} (does another process already hold the name?)", name);
            foreach (NamedPipeServerStream server in servers)
            {
                server.Dispose();
            }

            return;
        }

        _servers.AddRange(servers);
        PipeName = name;
        foreach (NamedPipeServerStream server in servers)
        {
            _loops.Add(Task.Run(() => AcceptLoopAsync(server, _stopping.Token)));
        }

        _logger.LogWarning(
            "map feed on: \\\\.\\pipe\\{Name} streams map state (read-only) to {User} and SYSTEM on this machine, up to {Max} tools at once (World:Control:MapFeed)",
            name, user, _options.MaxConnections);
    }

    [SupportedOSPlatform("windows")]
    private async Task AcceptLoopAsync(NamedPipeServerStream server, CancellationToken token)
    {
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            bool connected = false;
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                connected = true;
                failures = 0;
                Interlocked.Increment(ref _connections);
                await ServeConnectionAsync(server, DescribeClient(server), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("ops.map-feed", ex);
                _logger.LogWarning(ex, "map feed: a connection ended with an error");
                if (++failures >= 5)
                {
                    _logger.LogWarning("map feed: this pipe instance failed {Failures} times in a row and is closed", failures);
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * failures), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (connected)
                {
                    try
                    {
                        server.Disconnect();
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                    {
                        // Already disconnected or disposed: nothing to release.
                    }
                }
            }
        }
    }

    /// <summary>Serve one connection on <paramref name="stream"/> until it ends (tests call this with an in-memory stream).</summary>
    internal async Task ServeConnectionAsync(Stream stream, string client, CancellationToken token, FeedRole role = FeedRole.Unbound)
    {
        Interlocked.Increment(ref _active);
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
            await new MapFeedConnection(this, stream, client, _logger, linked.Token, role).RunAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>
    /// Make <paramref name="request"/> the subscription of <paramref name="connection"/> (replacing its previous one) and, if it is the
    /// first, attach the capture handler. Called from the connection's reader; never from the world thread.
    /// </summary>
    internal void Subscribe(MapFeedConnection connection, in FeedRequest request)
    {
        double requested = request.Hz > 0 ? request.Hz : DefaultHz;
        double hz = Math.Max(MinHz, Math.Min(requested, _options.MaxHz));
        if (hz < requested)
        {
            _logger.LogDebug("map feed: {Client} asked for {Requested} Hz and gets {Hz} Hz (World:Control:MapFeed:MaxHz)", connection.Client, requested, hz);
        }

        var subscription = new FeedSubscription(connection, request.MapId, request.InstanceId, request.Kinds, request.Paths, hz);
        lock (_gate)
        {
            FeedSubscription[] next = [.. Volatile.Read(ref _subscriptions).Where(s => !ReferenceEquals(s.Connection, connection)), subscription];
            Volatile.Write(ref _subscriptions, next);
            if (!_attached)
            {
                _world.Updated += OnUpdated;
                _attached = true;
            }
        }
    }

    /// <summary>Remove the subscription of <paramref name="connection"/>, if any; the last one detaches the capture handler.</summary>
    internal void Unsubscribe(MapFeedConnection connection)
    {
        lock (_gate)
        {
            FeedSubscription[] current = Volatile.Read(ref _subscriptions);
            FeedSubscription[] next = [.. current.Where(s => !ReferenceEquals(s.Connection, connection))];
            if (next.Length == current.Length)
            {
                return;
            }

            Volatile.Write(ref _subscriptions, next);
            if (next.Length == 0 && _attached)
            {
                _world.Updated -= OnUpdated;
                _attached = false;
            }
        }
    }

    /// <summary>
    /// The capture, on the world thread after the maps updated. Catches everything: a handler that throws would be logged as a fault by
    /// the world, and a failing visualizer must never be one.
    /// </summary>
    private void OnUpdated(uint diffMs)
    {
        FeedSubscription[] subscriptions = Volatile.Read(ref _subscriptions);
        if (subscriptions.Length == 0)
        {
            return;
        }

        Interlocked.Increment(ref _updatedCalls);
        _botsReady = false;
        _routes = null;
        try
        {
            foreach (FeedSubscription subscription in subscriptions)
            {
                if (subscription.FirstFrame)
                {
                    subscription.FirstFrame = false;
                    subscription.AccumulatedMs = 0;
                }
                else
                {
                    subscription.AccumulatedMs += diffMs;
                    if (subscription.AccumulatedMs < subscription.IntervalMs)
                    {
                        continue;
                    }

                    subscription.AccumulatedMs -= subscription.IntervalMs;
                    if (subscription.AccumulatedMs >= subscription.IntervalMs)
                    {
                        subscription.AccumulatedMs = 0; // a long tick does not buy a burst of frames
                    }
                }

                try
                {
                    CaptureFor(subscription, diffMs);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("ops.map-feed", ex);
                    long faults = Interlocked.Increment(ref _captureFaults);
                    if (faults == 1 || faults % 100 == 0)
                    {
                        _logger.LogWarning(ex, "map feed: a capture failed and was skipped ({Faults} so far)", faults);
                    }
                }
            }
        }
        finally
        {
            for (int i = 0; i < _tickFrameCount; i++)
            {
                _tickFrames[i]!.Release();
                _tickFrames[i] = null;
            }

            _tickFrameCount = 0;
            _routes = null;
        }
    }

    /// <summary>One due subscription: find or capture the frame of its view and queue it on its connection.</summary>
    private void CaptureFor(FeedSubscription subscription, uint diffMs)
    {
        Map? map = _world.FindMap(subscription.MapId, subscription.InstanceId);
        if (map is null || map.IsUnloaded)
        {
            if (!subscription.MapMissingReported)
            {
                subscription.MapMissingReported = true;
                subscription.Connection.NoteMapMissing(subscription.MapId, subscription.InstanceId);
            }

            return;
        }

        subscription.MapMissingReported = false;
        MapFeedConnection connection = subscription.Connection;
        if (!connection.TryReserveFrame())
        {
            Drop(connection);
            return;
        }

        FeedView view = new(subscription.MapId, subscription.InstanceId, subscription.Kinds, subscription.Paths);
        FeedFrame? frame = FindTickFrame(view);
        if (frame is null)
        {
            if (!_botsReady)
            {
                _botsReady = true;
                _routes = PrepareBots();
            }

            frame = _tickFrameCount < _tickFrames.Length ? _pool.Rent() : null;
            if (frame is null)
            {
                connection.ReleaseFrameReservation();
                Drop(connection);
                return;
            }

            _tickFrames[_tickFrameCount++] = frame; // the tick's own holder, released at its end
            try
            {
                MapFeedCapture.Fill(
                    frame,
                    map,
                    _world,
                    view,
                    _options.MaxEntitiesPerFrame,
                    _botGuids.Count == 0 ? null : _botGuids,
                    _routes,
                    diffMs,
                    _onRouteFault);
            }
            catch
            {
                // A half-filled frame must not be found by the next subscription of the same view.
                _tickFrames[--_tickFrameCount] = null;
                frame.Release();
                connection.ReleaseFrameReservation();
                throw;
            }

            Interlocked.Increment(ref _framesCaptured);
        }

        frame.AddHolder();
        if (!connection.TryEnqueueFrame(frame))
        {
            frame.Release();
            connection.ReleaseFrameReservation();
            Drop(connection);
        }
    }

    private FeedFrame? FindTickFrame(in FeedView view)
    {
        for (int i = 0; i < _tickFrameCount; i++)
        {
            if (_tickFrames[i]!.View == view)
            {
                return _tickFrames[i];
            }
        }

        return null;
    }

    private void Drop(MapFeedConnection connection)
    {
        connection.NoteDropped();
        Interlocked.Increment(ref _framesDropped);
    }

    private void NoteRouteFault(Exception ex)
    {
        long faults = Interlocked.Increment(ref _routeFaults);
        if (faults == 1 || faults % 100 == 0)
        {
            _logger.LogWarning(ex, "map feed: the bot route source failed and the route was skipped ({Faults} so far)", faults);
        }
    }

    /// <summary>Collect the managed bots' player guids (so players can be told from bots) and read the route seam, once per tick.</summary>
    private IBotRouteSource? PrepareBots()
    {
        if (!_registryResolved)
        {
            _registry = _services.GetService<ManagedBotRegistry>();
            _registryResolved = true;
        }

        _botGuids.Clear();
        if (_registry is not { Count: > 0 } registry)
        {
            return null;
        }

        foreach (ManagedBot bot in registry.Bots)
        {
            if (bot.Session.Player is { } player)
            {
                _botGuids.Add(player.Guid.Value);
            }
        }

        return registry.RouteSource;
    }

    [SupportedOSPlatform("windows")]
    private static string DescribeClient(NamedPipeServerStream server)
    {
        if (!MapFeedNative.GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid))
        {
            return "client pid unknown";
        }

        string name;
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            name = "exited";
        }

        return $"client pid {pid} {name}";
    }

    private static string ReadBuildSha()
    {
        string? version = typeof(WorldMapFeed).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            return "unknown";
        }

        // "1.0.0+<commit>": the part after '+' is the source revision when the build recorded one.
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        string sha = plus >= 0 && plus + 1 < version.Length ? version[(plus + 1)..] : version;
        return sha.Length > 64 ? sha[..64] : sha;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        AnomalyRing?.Dispose();
        AnomalyRing = null;
        lock (_gate)
        {
            Volatile.Write(ref _subscriptions, []);
            if (_attached)
            {
                _world.Updated -= OnUpdated;
                _attached = false;
            }
        }

        if (_loops.Count > 0)
        {
            try
            {
                await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger.LogWarning("map feed: the accept loops did not end within 5 s");
            }
        }

        foreach (NamedPipeServerStream server in _servers)
        {
            server.Dispose();
        }

        if (_services.GetService<PacketToolsPermit>() is { } stoppedPermit)
        {
            stoppedPermit.Granted = false;
        }

        if (Tools is { } tools)
        {
            await tools.DisposeAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// What one connection watches. The settings are fixed; <see cref="FirstFrame"/>, <see cref="AccumulatedMs"/> and
    /// <see cref="MapMissingReported"/> belong to the world thread (only <see cref="OnUpdated"/> touches them).
    /// </summary>
    private sealed class FeedSubscription(MapFeedConnection connection, uint mapId, uint instanceId, FeedKinds kinds, bool paths, double hz)
    {
        public MapFeedConnection Connection { get; } = connection;

        public uint MapId { get; } = mapId;

        public uint InstanceId { get; } = instanceId;

        public FeedKinds Kinds { get; } = kinds;

        public bool Paths { get; } = paths;

        /// <summary>The time between frames in world milliseconds.</summary>
        public long IntervalMs { get; } = Math.Max(1L, (long)Math.Round(1000.0 / hz));

        /// <summary>True until the first tick after subscribing has run: that tick captures at once and starts <see cref="AccumulatedMs"/> at 0.</summary>
        public bool FirstFrame { get; set; } = true;

        /// <summary>Time since the last frame; reaches <see cref="IntervalMs"/> after a full interval of game time.</summary>
        public long AccumulatedMs { get; set; }

        public bool MapMissingReported { get; set; }
    }
}

/// <summary>The one native call the feed needs besides <see cref="LocalPipeServer"/>.</summary>
internal static partial class MapFeedNative
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
