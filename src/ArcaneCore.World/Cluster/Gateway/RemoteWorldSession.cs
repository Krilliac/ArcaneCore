using System.Collections.Concurrent;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>
/// The World side of one gateway-backed client (Tier 3a): a <see cref="WorldSession"/> in remote mode
/// (<c>WorldSession.Remote.cs</c>) with its own DI scope, as <see cref="WorldServer"/> gives each socket connection, and the
/// task that runs it. Ends when the session ends (then the gateway is told) or the gateway closes it.
/// </summary>
public sealed class RemoteWorldSession : IGatewayRemoteSession
{
    private readonly WorldSession _session;
    private readonly AsyncServiceScope _scope;
    private readonly IGatewaySessionOutput _output;
    private readonly CancellationToken _stopping;
    private readonly Action<Task> _track;
    private int _started;

    internal RemoteWorldSession(WorldSession session, AsyncServiceScope scope, IGatewaySessionOutput output, CancellationToken stopping, Action<Task> track)
    {
        _session = session;
        _scope = scope;
        _output = output;
        _stopping = stopping;
        _track = track;
    }

    /// <summary>The world session (account, state, player, the forwarded client endpoint).</summary>
    public WorldSession Session => _session;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _session.StartRemote();
        _track(Task.Run(RunAsync, CancellationToken.None));
    }

    private async Task RunAsync()
    {
        try
        {
            await _session.RunRemoteAsync(_stopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.remote-session", ex);
            AnomalyLedger.Shared.Report("cluster", "gateway-fault", _session.RemoteEndpoint, $"error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
            _session.Logger.LogFault("session", ex, "[{Endpoint}] gateway session error", _session.RemoteEndpoint);
        }
        finally
        {
            _output.Close(ClusterSessionDetachReason.WorkerClosed);
            await _scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Deliver(WorldOpcode opcode, byte[] payload) => _session.DeliverRemote(opcode, payload);

    public void Deliver(WorldOpcode opcode, byte[] payload, uint receivedMs) => _session.DeliverRemote(opcode, payload, receivedMs);

    public void CloseFromGateway(ClusterSessionDetachReason reason)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            // Opened but never started (the client left while the open ran): release the claim and the scope here.
            _session.AbandonRemote();
            _track(_scope.DisposeAsync().AsTask());
            return;
        }

        _session.CloseRemoteFromGateway("gateway closed the session (" + reason + ")");
    }
}

/// <summary>
/// Opens <see cref="RemoteWorldSession"/>s for the <see cref="GatewaySessionLinkHost"/>: a DI scope, a remote-mode
/// <see cref="WorldSession"/> and <see cref="WorldSession.AuthenticateRemoteAsync"/> (account status, bans of the forwarded
/// address, the account claim). Tracks the running sessions so the endpoint can wait for them at shutdown, before the world
/// host drains its stores (as <see cref="WorldServer"/> does for socket sessions).
/// </summary>
public sealed class WorldGatewaySessionAcceptor(
    IServiceScopeFactory scopes,
    OpcodeTable opcodes,
    WorldRuntime world,
    SessionRegistry registry,
    WorldSessionOptions options,
    ILoggerFactory loggers,
    WorkerAdmission? admission = null) : IGatewaySessionAcceptor
{
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Every session opened through this acceptor and still open, newest last (diagnostics and tests).</summary>
    public ConcurrentDictionary<long, RemoteWorldSession> Sessions { get; } = new();

    /// <summary>
    /// Raised (on the thread that starts the session) right after a session was opened, started and entered in <see cref="Sessions"/>; the argument is the new world session. The transfer target uses it to start a shadow
    /// load that waited for the client's session (design _wf/handoff-control-DESIGN.md section 4.4). A handler's exception is recorded and does not affect the session.
    /// </summary>
    public event Action<WorldSession>? SessionOpened;

    public async ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken)
    {
        // A standby world (not yet promoted) or a draining one admits no session; nothing is created for the refused client.
        if (admission is { IsOpen: false, ClosedReason: var closed })
            return GatewaySessionAcceptResult.Refused(AuthResponseCode.Unavailable, "the worker admits no sessions (" + closed + ")");

        AsyncServiceScope scope = scopes.CreateAsyncScope();
        WorldSession session = WorldSession.CreateRemote(output, request.ClientEndpoint, scope.ServiceProvider, opcodes, world, registry,
            options, loggers.CreateLogger<WorldSession>());
        AuthResponseCode code;
        try
        {
            code = await session.AuthenticateRemoteAsync(request.AccountId, request.AccountName, request.Build, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            session.AbandonRemote();
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (code != AuthResponseCode.Ok)
        {
            session.AbandonRemote();
            await scope.DisposeAsync().ConfigureAwait(false);
            return GatewaySessionAcceptResult.Refused(code, "refused by the worker (" + code + ")");
        }

        RemoteWorldSession remote = null!;
        remote = new RemoteWorldSession(session, scope, output, _stopping.Token, task =>
        {
            Sessions[request.SessionGeneration] = remote;
            _running[task] = 0;
            RaiseSessionOpened(session);
            _ = task.ContinueWith(done =>
            {
                _running.TryRemove(done, out _);
                Sessions.TryRemove(new KeyValuePair<long, RemoteWorldSession>(request.SessionGeneration, remote));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        });
        return GatewaySessionAcceptResult.Accepted(remote);
    }

    private void RaiseSessionOpened(WorldSession session)
    {
        try
        {
            SessionOpened?.Invoke(session);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.transport", ex);
        }
    }

    /// <summary>
    /// A session the gateway moved here after a login redirect: <see cref="OpenAsync"/>, then the buffered CMSG_PLAYER_LOGIN is queued to the new session
    /// (it runs once the link host starts it), so the character logs in on this worker, which owns its map. Only CMSG_PLAYER_LOGIN is adopted.
    /// </summary>
    public async ValueTask<GatewaySessionAcceptResult> OpenAdoptedAsync(GatewaySessionAdoptRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.LoginOpcode != (uint)WorldOpcode.CmsgPlayerLogin)
            return GatewaySessionAcceptResult.Refused(AuthResponseCode.Failed, "an adopted session carries CMSG_PLAYER_LOGIN only");

        GatewaySessionAcceptResult result = await OpenAsync(request.Open, output, cancellationToken).ConfigureAwait(false);
        result.Session?.Deliver(WorldOpcode.CmsgPlayerLogin, request.LoginPayload.ToArray());
        return result;
    }

    /// <summary>Stop every running session and wait for them (their closes are posted to the world thread, which still runs).</summary>
    public async Task StopAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task[] running = [.. _running.Keys];
        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.transport", ex);
        }
    }
}
