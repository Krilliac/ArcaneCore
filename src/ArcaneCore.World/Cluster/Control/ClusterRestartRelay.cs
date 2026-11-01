using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Control;

/// <summary>What the gateway answered to an operator's restart request: whether it took it, why not, and the operation id the verdict and the journal are filed under.</summary>
public sealed record ClusterRestartSubmission(bool Accepted, string Reason, Guid OperationId);

/// <summary>
/// The worker end of the operator <c>.cluster restart</c> when the whole-World handoff is coordinated by the gateway (design _wf/handoff-control-DESIGN.md section 3.4).
/// <c>.cluster restart</c> uses it when one is registered: it sends the request to the gateway and returns the gateway's answer; the verdict arrives later as a text notice on the
/// operator's current session, which may by then live on another worker.
/// </summary>
public interface IClusterRestartRelay
{
    /// <summary>
    /// Sends the restart of the worker named in <paramref name="arguments"/> on behalf of <paramref name="session"/> (a session that came in through a gateway link) and waits for the gateway's answer.
    /// <paramref name="security"/> is only a claim: the gateway re-checks the account's security in the authentication database and refuses a claim the database contradicts.
    /// Never throws for a gateway refusal or a lost link: those are an answer with <see cref="ClusterRestartSubmission.Accepted"/> false.
    /// </summary>
    Task<ClusterRestartSubmission> SubmitRestartAsync(WorldSession session, AccountSecurity security, string arguments, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IClusterRestartRelay"/> over the worker's gateway link host (<see cref="GatewaySessionLinkHost"/>): a <see cref="ControlSubmit"/> of kind
/// <see cref="ControlSubmitKind.OperatorRestart"/> goes out on the link that carries the operator's session, the <see cref="ControlSubmitReply"/> completes the call, and a
/// <see cref="ControlNotice"/> of kind <see cref="ControlNoticeKind.OperatorText"/> is delivered to the session that carries the notice's gateway generation on this worker, as
/// system chat on that session's world thread.
/// <para>
/// Registered only in worker mode with <c>Cluster:Worker:Handoff:Enabled</c> (see <c>WorkerTransferHosting.AddWorkerControl</c>); Combined and single-process worlds register nothing,
/// so <c>.cluster restart</c> behaves as before. <see cref="Attach"/> is called once by <see cref="GatewayWorkerEndpoint"/> when it starts its link host.
/// </para>
/// Thread-safe: any number of calls may be pending; the reply and notice handlers run on the link's receive loop and never block.
/// </summary>
public sealed class ClusterRestartRelay(TimeSpan? replyTimeout = null, ILogger<ClusterRestartRelay>? logger = null) : IClusterRestartRelay
{
    private readonly TimeSpan _replyTimeout = replyTimeout ?? TimeSpan.FromSeconds(10);
    private readonly ILogger _logger = logger ?? NullLogger<ClusterRestartRelay>.Instance;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<ControlSubmitReply>> _pending = new();
    private const ulong RelayIdBit = 1UL << 63;
    private long _nextRequestId;
    private volatile GatewaySessionLinkHost? _host;
    private volatile WorldGatewaySessionAcceptor? _acceptor;

    /// <summary>True once <see cref="Attach"/> ran.</summary>
    public bool IsAttached => _host is not null;

    /// <summary>
    /// Connects the relay to a started link host and the acceptor whose sessions it serves. Called once, before the host starts; a second call with another host replaces the first
    /// (a restarted endpoint). The relay keeps the subscriptions for the life of the host.
    /// </summary>
    public void Attach(GatewaySessionLinkHost host, WorldGatewaySessionAcceptor acceptor)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(acceptor);
        if (_host is { } previous)
        {
            previous.ControlSubmitReplyReceived -= OnReply;
            previous.ControlNoticeReceived -= OnNotice;
        }

        _acceptor = acceptor;
        _host = host;
        host.ControlSubmitReplyReceived += OnReply;
        host.ControlNoticeReceived += OnNotice;
    }

    /// <inheritdoc />
    public async Task<ClusterRestartSubmission> SubmitRestartAsync(WorldSession session, AccountSecurity security, string arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(arguments);
        if (_host is not { } host || _acceptor is not { } acceptor)
        {
            return Refused("this worker's gateway link is not running");
        }

        long generation = GenerationOf(acceptor, session);
        if (generation == 0)
        {
            return Refused("your session does not come through a gateway link");
        }

        ulong requestId = RelayIdBit | (ulong)Interlocked.Increment(ref _nextRequestId); // the far-teleport router counts up from 1 on the same link; the top bit keeps the two id spaces apart
        var waiting = new TaskCompletionSource<ControlSubmitReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = waiting;
        try
        {
            var body = new ControlRestartSubmit(session.AccountId, generation, (byte)security, arguments);
            if (!host.TrySendControlSubmit(generation, new ControlSubmit(requestId, body)))
            {
                return Refused("no gateway link carries your session");
            }

            ControlSubmitReply reply = await waiting.Task.WaitAsync(_replyTimeout, cancellationToken).ConfigureAwait(false);
            return new ClusterRestartSubmission(reply.Accepted, reply.Reason, reply.OperationId);
        }
        catch (TimeoutException)
        {
            return Refused("the gateway did not answer in time; check '.cluster handoff status'");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private static ClusterRestartSubmission Refused(string reason) => new(false, reason, Guid.Empty);

    /// <summary>The gateway generation of <paramref name="session"/> on this worker, or 0 when the acceptor does not hold it.</summary>
    private static long GenerationOf(WorldGatewaySessionAcceptor acceptor, WorldSession session)
    {
        foreach (KeyValuePair<long, RemoteWorldSession> entry in acceptor.Sessions)
        {
            if (ReferenceEquals(entry.Value.Session, session))
            {
                return entry.Key;
            }
        }

        return 0;
    }

    private void OnReply(ControlSubmitReply reply)
    {
        if (_pending.TryGetValue(reply.RequestId, out TaskCompletionSource<ControlSubmitReply>? waiting))
        {
            waiting.TrySetResult(reply);
        }
        else
        {
            _logger.LogDebug("Control submit reply {RequestId} has no waiting request", reply.RequestId);
        }
    }

    private void OnNotice(ControlNotice notice)
    {
        if (notice.Kind != ControlNoticeKind.OperatorText)
        {
            return; // transfer outcomes belong to the transfer relay
        }

        if (_acceptor is not { } acceptor
            || !acceptor.Sessions.TryGetValue(notice.SessionGeneration, out RemoteWorldSession? remote)
            || remote.Session.AccountId != notice.AccountId)
        {
            _logger.LogInformation("Operator notice for account {Account} (generation {Generation}) has no session on this worker: {Text}", notice.AccountId, notice.SessionGeneration, notice.Text);
            return;
        }

        WorldSession session = remote.Session;
        string text = notice.Text;
        try
        {
            session.World.Post(() =>
            {
                foreach (string line in text.Split('\n'))
                {
                    session.Send(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(line));
                }
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.operator-restart-notice", ex, "the operator notice could not be delivered");
        }
    }
}
