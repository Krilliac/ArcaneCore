using System.Threading.Channels;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net;

/// <summary>
/// Where the server packets of a gateway-backed session go (Tier 3a passthrough, <c>docs/design/hot-reload-tiers.md</c> §5.3).
/// Called under the session's send lock, so the order of calls is the order the client must see; the implementation copies
/// the payload and must not block.
/// </summary>
public interface IRemoteSessionOutput
{
    /// <summary>Queue one server packet for the gateway. False when the link is gone; the session then closes.</summary>
    bool TrySend(WorldOpcode opcode, ReadOnlySpan<byte> payload);
}

/// <summary>
/// A world session whose client socket lives in an <c>ArcaneCore.Gateway</c> process (Tier 3a). The gateway owns the socket,
/// the header cipher, PING/PONG and the digest check of CMSG_AUTH_SESSION; this side keeps everything after it: account
/// status and ban re-check, the one-session-per-account claim, the character screen and the world. Opcodes arrive already
/// decrypted through <see cref="DeliverRemote"/> and leave through <see cref="IRemoteSessionOutput"/>; everything else
/// (dispatch, packet budgets, queue bounds, handler fault containment, Warden) is the unchanged socket path.
/// </summary>
public sealed partial class WorldSession
{
    private IRemoteSessionOutput? _remoteOutput;
    private Channel<RemoteInbound>? _remoteInbound;
    private uint _remoteBuild;
    private byte[]? _remoteSessionKey;

    private readonly record struct RemoteInbound(WorldOpcode Opcode, byte[] Payload);

    /// <summary>True for a session fed by a gateway instead of a socket.</summary>
    internal bool IsRemote => _remoteOutput is not null;

    /// <summary>
    /// A session for a client the gateway has already authenticated. <paramref name="clientEndpoint"/> is the client's own
    /// address as the gateway saw it ("ip:port"), so IP bans, the live ban enforcement and every log line see the player,
    /// not the gateway.
    /// </summary>
    internal static WorldSession CreateRemote(
        IRemoteSessionOutput output, string clientEndpoint, IServiceProvider services, OpcodeTable opcodes, WorldRuntime world,
        SessionRegistry registry, WorldSessionOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientEndpoint);
        // The inbound bound mirrors World:MaxQueuedWorldPackets: a gateway can outrun a slow session task (character
        // screen handlers await the database) the way a socket could not, so the backlog is bounded here.
        int capacity = options.MaxQueuedWorldPackets > 0 ? options.MaxQueuedWorldPackets : 8192;
        return new WorldSession(Stream.Null, clientEndpoint, services, opcodes, world, registry, options, logger)
        {
            _remoteOutput = output,
            _remoteInbound = Channel.CreateBounded<RemoteInbound>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            }),
        };
    }

    /// <summary>
    /// The part of <see cref="HandleAuthSessionAsync"/> after the digest: the account row (status, security), account and IP
    /// bans of the forwarded address, the account settings, the claim (which kicks an older session of the account), the
    /// ban re-check after registration, and the character screen. Ok, or the code the gateway sends to the client in plain
    /// text. The digest itself was checked by the gateway against the same stored session key.
    /// </summary>
    internal async Task<AuthResponseCode> AuthenticateRemoteAsync(int accountId, string accountName, uint build, CancellationToken cancellationToken)
    {
        Invariant.Assert(IsRemote, "AuthenticateRemoteAsync is for gateway-backed sessions");
        if (!ClientProtocols.TryGet(build, out IClientProtocol protocol))
        {
            return AuthResponseCode.VersionMismatch;
        }

        IAccountStore accounts = Services.GetRequiredService<IAccountStore>();
        Account? stored = await accounts.FindByUsernameAsync(accountName, cancellationToken).ConfigureAwait(false);
        if (stored?.SessionKey is null || stored.Id != accountId)
        {
            return AuthResponseCode.UnknownAccount;
        }

        if (stored.Status != AccountStatus.Active)
        {
            _logger.LogInformation("[{Endpoint}] refused gateway login for {Status} account", RemoteEndpoint, stored.Status);
            return AuthResponseCode.Banned;
        }

        IBanStore? bans = Services.GetService<IBanStore>();
        if (bans is not null && await IsBannedAsync(bans, stored.Id, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("[{Endpoint}] refused gateway login for banned account or address", RemoteEndpoint);
            return AuthResponseCode.Banned;
        }

        // An earlier session's unwritten values, laid over the loaded settings (see WorldSession.HandleAuthSessionAsync).
        AccountDataFeature? accountData = Services.GetService<AccountDataFeature>() is { IsAttached: true } attached ? attached : null;
        AccountDataFeature.PendingAccountData? pendingData = accountData?.Snapshot(stored.Id);
        AccountSettings settings = await Services.GetRequiredService<IAccountDataStore>().GetAsync(stored.Id).ConfigureAwait(false);
        AttachAccountData(accountData, stored.Id, settings, pendingData);
        lock (_sendLock)
        {
            AccountId = stored.Id;
            AccountName = accountName;
            Security = stored.Security;
            Settings = settings;
        }

        _registry.Register(this);

        Account? recheck = await accounts.FindByUsernameAsync(accountName, cancellationToken).ConfigureAwait(false);
        if ((recheck is not null && recheck.Status != AccountStatus.Active)
            || (bans is not null && await IsBannedAsync(bans, stored.Id, cancellationToken).ConfigureAwait(false)))
        {
            _logger.LogInformation("[{Endpoint}] '{Account}' was banned while authenticating through the gateway; refusing", RemoteEndpoint, accountName);
            return AuthResponseCode.Banned;
        }

        lock (_sendLock)
        {
            if (_state == SessionState.Closed || _kick.IsCancellationRequested || !_registry.IsCurrent(this))
            {
                return AuthResponseCode.Failed;
            }

            _protocol = protocol;
            _remoteBuild = build;
            _remoteSessionKey = stored.SessionKey;
            _state = SessionState.CharacterSelect;
        }

        _logger.LogInformation("[{Endpoint}] '{Account}' authenticated through the gateway", RemoteEndpoint, accountName);
        return AuthResponseCode.Ok;
    }

    /// <summary>
    /// After the gateway has been told the session is open (so nothing this sends can overtake SMSG_AUTH_RESPONSE): start
    /// Warden on this side exactly as <see cref="HandleAuthSessionAsync"/> does. Warden traffic is ordinary opcodes, so it is
    /// forwarded through the gateway unchanged.
    /// </summary>
    internal void StartRemote()
    {
        Invariant.Assert(IsRemote, "StartRemote is for gateway-backed sessions");
        if (_remoteSessionKey is { } key)
        {
            Services.GetService<Warden.WardenFeature>()?.OnAuthenticated(this, _remoteBuild, key);
        }
    }

    /// <summary>
    /// Hand one client opcode to the session (the link's receive loop). Never blocks: a session whose backlog is over its bound
    /// is kicked, as a socket session over World:MaxQueuedWorldPackets is.
    /// </summary>
    internal void DeliverRemote(WorldOpcode opcode, byte[] payload)
    {
        if (_remoteInbound is not { } inbound || inbound.Writer.TryWrite(new RemoteInbound(opcode, payload)))
        {
            return;
        }

        _logger.LogWarning("[{Endpoint}] gateway inbound backlog over its bound; disconnecting", RemoteEndpoint);
        Kick("gateway inbound backlog over its bound");
    }

    /// <summary>The gateway closed the client side (disconnect, superseded, link lost): stop reading and close.</summary>
    internal void CloseRemoteFromGateway(string reason)
    {
        _remoteInbound?.Writer.TryComplete();
        Kick(reason);
    }

    /// <summary>A session that authenticated (or was registered) but is not started: close it so the claim is released.</summary>
    internal void AbandonRemote()
    {
        _remoteInbound?.Writer.TryComplete();
        Close();
    }

    /// <summary>
    /// The session task of a gateway-backed session: the read loop of <see cref="RunAsync"/> with the gateway as the stream.
    /// CMSG_PING was answered by the gateway; its latency is still folded into <see cref="LatencyMs"/> here (the anticheat
    /// slack reads it). Ends when the gateway closes the session, the session is kicked or the server stops; closes the session.
    /// </summary>
    internal async Task RunRemoteAsync(CancellationToken cancellationToken)
    {
        Invariant.Assert(IsRemote, "RunRemoteAsync is for gateway-backed sessions");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _kick.Token);
        try
        {
            await foreach (RemoteInbound packet in _remoteInbound!.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                ArcaneCore.World.Ops.Metrics.WorldPacketMetrics.Table.RecordIn((uint)packet.Opcode, packet.Payload.Length + 6);
                if (packet.Opcode == WorldOpcode.CmsgPing)
                {
                    var reader = new PacketReader(packet.Payload);
                    if (reader.TryReadUInt32(out _) && reader.TryReadUInt32(out uint latency))
                    {
                        RecordLatencySample(latency);
                    }

                    continue;
                }

                CapturePacket(true, packet.Opcode, packet.Payload);
                if (!await DispatchFromClientAsync(packet.Opcode, packet.Payload, linked.Token).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // gateway close, kick or shutdown
        }
        finally
        {
            Close();
        }
    }

    private void SendRemote(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        bool linkGone = false;
        lock (_sendLock)
        {
            if (_state == SessionState.Closed)
            {
                NoteSendAfterClose(opcode);
                return;
            }

            // Observed before the send so the observer cannot race the client receiving the packet.
            _outboundPacketObserver?.Observe(opcode, payload.ToArray());
            if (!_remoteOutput!.TrySend(opcode, payload))
            {
                linkGone = true;
            }
            else
            {
                CapturePacket(false, opcode, payload);
            }
        }

        if (linkGone)
        {
            Kick("the gateway link is gone");
            return;
        }

        ArcaneCore.World.Ops.Metrics.WorldPacketMetrics.Table.RecordOut((uint)opcode, payload.Length + 4);
    }
}
