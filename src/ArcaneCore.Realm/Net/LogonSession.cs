using System.Buffers.Binary;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Kernel.Resilience;
using ArcaneCore.Realm.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Realm.Net;

/// <summary>
/// Handles one logon connection: the SRP6 challenge/proof exchange and the realm-list
/// reply. Drives the command loop documented in docs/M1_ACCEPTANCE.md.
///
/// Flow and packet shapes verified against vmangos src/realmd/AuthSocket.cpp; the
/// auto-create-on-login behavior follows WCell Services/WCell.AuthServer/Authentication.cs.
/// <para>
/// Transport protections (docs/ops/netguard.md): the unauthenticated lifetime and the frame read
/// deadline come from <c>Net:Protection</c> (through the <see cref="NetGuard"/> of the listener,
/// or the defaults when a host constructs the session without one, so they are never off by
/// accident); the per-address failure budget needs the guard's table and is skipped without it.
/// </para>
/// </summary>
public sealed class LogonSession(
    NetworkStream stream,
    IAccountStore accountStore,
    IRealmStore realmStore,
    AuthOptions options,
    ILogger logger,
    string remoteEndpoint,
    IBanStore? banStore = null,
    NetGuard? guard = null,
    RealmIpBanCache? ipBanCache = null)
{
    private static readonly NetProtectionOptions DefaultProtection = new();

    private readonly IBanStore? _banStore = banStore; // optional: null keeps every pre-ban call site unchanged
    private readonly RealmIpBanCache? _ipBanCache = ipBanCache; // optional: null reads the row on every challenge (vmangos realmd)
    private readonly NetProtectionOptions _protection = guard?.Options ?? DefaultProtection;
    private readonly IpKey? _address = IpKey.TryParse(remoteEndpoint, out IpKey parsedAddress) ? parsedAddress : null;
    private string _username = string.Empty;
    private Srp6Server? _srp;
    private bool _isAutocreate;
    private byte[]? _autocreateVerifier;
    private bool _authenticated;
    private byte[]? _reconnectChallenge;
    private byte[]? _reconnectSessionKey;
    private bool _closeRequested;
    private CancellationToken _sessionToken;
    private CancellationTokenSource? _unauthenticatedLifetime;
    private ReadDeadline? _deadline;

    // vmangos AuthSocket.cpp:248-262: the challenge body is sizeof(sAuthLogonChallengeBody) = 47 at
    // most and 47 - AUTH_LOGON_MAX_NAME (16) = 31 at least; username_len above 16 is dropped.
    private const int ChallengeMinBody = 31;
    private const int ChallengeMaxBody = 47;
    private const int MaxUsernameLength = 16;

    // Locales realmd accepts (AuthSocket.cpp:213-230); the client sends them byte-reversed.
    private static readonly HashSet<string> AllowedLocales = new(StringComparer.Ordinal)
    {
        "enUS", "enGB", "koKR", "frFR", "deDE", "zhCN", "zhTW", "esES", "esMX", "ruRU",
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // vmangos MaxSessionDuration (AuthSocket.cpp:76-82): a hard cap on one connection's life.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.MaxSessionDurationSeconds > 0)
        {
            session.CancelAfter(TimeSpan.FromSeconds(options.MaxSessionDurationSeconds));
        }

        // Net:Protection:LogonUnauthenticatedLifetime: a connection without a successful proof is
        // closed well before the 300 s session cap. Disarmed (never re-armed) by the first good proof.
        using var unauthenticated = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        TimeSpan lifetime = _protection.LogonUnauthenticatedLifetime;
        if (lifetime > TimeSpan.Zero)
        {
            unauthenticated.CancelAfter(lifetime);
        }

        _unauthenticatedLifetime = unauthenticated;

        // Net:Protection:FrameReadTimeout (Auth:ReadTimeoutSeconds, when set, wins): one deadline per
        // connection, re-armed per packet, so no timer or token source is allocated per read.
        TimeSpan frameTimeout = options.ReadTimeoutSeconds > 0 ? TimeSpan.FromSeconds(options.ReadTimeoutSeconds) : _protection.FrameReadTimeout;
        using var deadline = new ReadDeadline(unauthenticated.Token, frameTimeout);
        _deadline = deadline;

        try
        {
            await RunCommandsAsync(unauthenticated.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (deadline.Expired)
            {
                if (guard is not null)
                {
                    guard.ReportFrameTimeout(remoteEndpoint);
                }
                else
                {
                    logger.LogWarning("[{Endpoint}] packet not completed within {Timeout}; closing", remoteEndpoint, frameTimeout);
                }
            }
            else if (unauthenticated.IsCancellationRequested && !session.IsCancellationRequested)
            {
                if (guard is not null)
                {
                    guard.ReportUnauthenticatedTimeout(remoteEndpoint, lifetime);
                }
                else
                {
                    logger.LogInformation("[{Endpoint}] not authenticated within {Lifetime}; closing", remoteEndpoint, lifetime);
                }
            }
            else
            {
                logger.LogInformation("[{Endpoint}] logon connection timed out; closing", remoteEndpoint);
            }
        }
        finally
        {
            _deadline = null;
            _unauthenticatedLifetime = null;
        }
    }

    private async Task RunCommandsAsync(CancellationToken cancellationToken)
    {
        _sessionToken = cancellationToken;
        byte[] commandBuffer = new byte[1];
        while (!cancellationToken.IsCancellationRequested && !_closeRequested)
        {
            int read = await stream.ReadAsync(commandBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break; // client closed the connection
            }

            var command = (AuthCommand)commandBuffer[0];
            try
            {
                switch (command)
                {
                    case AuthCommand.LogonChallenge:
                        await HandleChallengeAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case AuthCommand.LogonProof:
                        await HandleProofAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case AuthCommand.ReconnectChallenge:
                        await HandleReconnectChallengeAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case AuthCommand.ReconnectProof:
                        await HandleReconnectProofAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case AuthCommand.RealmList:
                        await HandleRealmListAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        logger.LogWarning("[{Endpoint}] unsupported logon command 0x{Command:X2}; closing",
                            remoteEndpoint, commandBuffer[0]);
                        return;
                }
            }
            catch (ResilienceException ex)
            {
                await RefuseDatabaseUnavailableAsync(command, ex, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// The auth database is down, too slow, or its circuit is open (docs/ops/resilience.md): fail closed. Nothing was
    /// authenticated, the SRP state is dropped, the client gets WOW_FAIL_DB_BUSY so it shows a message instead of a
    /// silent disconnect, and the connection closes. One line per refused connection without a stack; the breaker
    /// logged the cause once when it opened. vmangos realmd has no equivalent: a dead database there fails every query.
    /// </summary>
    private async Task RefuseDatabaseUnavailableAsync(AuthCommand command, ResilienceException reason, CancellationToken cancellationToken)
    {
        ResetChallengeState();
        _closeRequested = true;
        logger.LogWarning("[{Endpoint}] auth database unavailable ({Reason}); refusing logon (fail closed)", remoteEndpoint, reason.Message);
        switch (command)
        {
            case AuthCommand.LogonChallenge:
                await SendChallengeFailureAsync(AuthResult.FailDbBusy, cancellationToken).ConfigureAwait(false);
                break;
            case AuthCommand.LogonProof:
                await SendProofFailureAsync(AuthResult.FailDbBusy, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleChallengeAsync(CancellationToken cancellationToken)
    {
        // vmangos sets m_status = STATUS_INVALID on handler entry (AuthSocket.cpp:327): a new
        // challenge discards every piece of the previous one, so a proof can never be mixed with
        // another challenge's SRP state, username or auto-create flag.
        ResetChallengeState();

        // header after the command byte: protocol_version(1) + size(2 LE), then size bytes of body.
        byte[] header = new byte[3];
        await ReadPacketPartAsync(header, cancellationToken).ConfigureAwait(false);
        ushort bodySize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1, 2));

        // Out-of-window sizes are dropped without a reply, as realmd does, before anything is allocated.
        if (bodySize < ChallengeMinBody || bodySize > ChallengeMaxBody)
        {
            logger.LogInformation("[{Endpoint}] challenge body size {Size} outside {Min}..{Max}; closing",
                remoteEndpoint, bodySize, ChallengeMinBody, ChallengeMaxBody);
            RecordFailure();
            _closeRequested = true;
            return;
        }

        byte[] body = new byte[bodySize];
        await ReadPacketPartAsync(body, cancellationToken).ConfigureAwait(false);

        // The size window above (31..47) is what makes the fixed offsets below (locale at 17..20,
        // username length at 29) in range; keep the two in step.
        Invariant.Assert(body.Length >= ChallengeMinBody && ChallengeMinBody > 29, $"challenge body of {body.Length} bytes is shorter than the fixed fields it is indexed by");

        // Net:Protection:AuthFailureBurstPerIp: an address whose failure budget is spent is refused
        // before the locale check, the ban lookup and the account lookup (no query for a guesser).
        // FAIL_NOACCESS is what realmd answers a refused address (AuthSocket.cpp:338-352); then close.
        if (guard is not null && !guard.AllowsAuthAttempt(_address))
        {
            await SendChallengeFailureAsync(AuthResult.FailNoAccess, cancellationToken).ConfigureAwait(false);
            _closeRequested = true;
            return;
        }

        if (body[29] > MaxUsernameLength || !AllowedLocales.Contains(ReadLocale(body)))
        {
            logger.LogInformation("[{Endpoint}] challenge with bad username length or locale; closing", remoteEndpoint);
            RecordFailure();
            _closeRequested = true;
            return;
        }

        if (!LogonChallengeRequest.TryParse(body, out LogonChallengeRequest? request) || request is null)
        {
            logger.LogWarning("[{Endpoint}] malformed logon challenge", remoteEndpoint);
            RecordFailure();
            await SendChallengeFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Build != ClientBuild.Vanilla1121)
        {
            logger.LogInformation("[{Endpoint}] rejected build {Build} (only {Supported} is supported)",
                remoteEndpoint, request.Build, ClientBuild.Vanilla1121);
            await SendChallengeFailureAsync(AuthResult.VersionInvalid, cancellationToken).ConfigureAwait(false);
            return;
        }

        string username = request.Username.ToUpperInvariant();
        if (options.StrictUsernameCharset && !IsPrintableAscii(username))
        {
            logger.LogInformation("[{Endpoint}] rejected account name '{Account}' (not printable ASCII)",
                remoteEndpoint, LogSafe.Escape(username));
            RecordFailure();
            await SendChallengeFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        // IP ban: FAIL_NOACCESS before the account is even looked up and before any SRP state exists
        // (vmangos AuthSocket.cpp:338-352). A store error propagates and closes the connection (fail closed).
        // ArcaneCore validates the build and name above first, so a wrong-build client sees VersionInvalid.
        string? address = AccountBanEvaluator.AddressOfEndpoint(remoteEndpoint);
        if (_banStore is not null && address is not null && await IsIpBannedAsync(address, cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation("[{Endpoint}] banned address tried to log in as '{Account}'",
                remoteEndpoint, LogSafe.Escape(username));
            RecordFailure();
            await SendChallengeFailureAsync(AuthResult.FailNoAccess, cancellationToken).ConfigureAwait(false);
            return;
        }

        Account? account = await accountStore.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            if (!options.AutocreateAccounts)
            {
                logger.LogInformation("[{Endpoint}] unknown account '{Account}'", remoteEndpoint, LogSafe.Escape(username));
                RecordFailure();
                await SendChallengeFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
                return;
            }

            // WCell-style auto-create: build the verifier as if password == username.
            // The account is only persisted later if the client's proof validates against it.
            byte[] salt = WowSrp6.GenerateSalt();
            BigInteger verifier = WowSrp6.ComputeVerifier(salt, username, username);
            _srp = new Srp6Server(salt, verifier);
            _isAutocreate = true;
            _autocreateVerifier = WowSrp6.ToFixedLittleEndian(verifier, WowSrp6.KeyLength);
            logger.LogInformation("[{Endpoint}] auto-create candidate '{Account}' (proof pending)",
                remoteEndpoint, LogSafe.Escape(username));
        }
        else
        {
            switch (account.Status)
            {
                case AccountStatus.Banned:
                    RecordFailure();
                    await SendChallengeFailureAsync(AuthResult.Banned, cancellationToken).ConfigureAwait(false);
                    return;
                case AccountStatus.Suspended:
                    RecordFailure();
                    await SendChallengeFailureAsync(AuthResult.Suspended, cancellationToken).ConfigureAwait(false);
                    return;
            }

            // Ban rows: a permanent one answers FAIL_BANNED, a temporary one FAIL_SUSPENDED
            // (AuthSocket.cpp:464-476). The status column above stays an always-honoured override.
            if (_banStore is not null
                && await _banStore.GetActiveAccountBanAsync(account.Id, cancellationToken).ConfigureAwait(false) is { } ban)
            {
                logger.LogInformation("[{Endpoint}] banned account '{Account}' tried to log in ({Kind})",
                    remoteEndpoint, LogSafe.Escape(username), ban.IsPermanent ? "permanent" : "temporary");
                RecordFailure();
                await SendChallengeFailureAsync(
                    ban.IsPermanent ? AuthResult.Banned : AuthResult.Suspended, cancellationToken).ConfigureAwait(false);
                return;
            }

            // A zero or degenerate salt/verifier would let anyone forge the proof (vmangos
            // SRP6.cpp:188-199 refuses them; answer FAIL_NOACCESS).
            _srp = Srp6Server.TryCreate(account.Salt, WowSrp6.FromLittleEndian(account.Verifier));
            if (_srp is null)
            {
                logger.LogWarning("[{Endpoint}] account '{Account}' has a degenerate SRP salt or verifier; refusing",
                    remoteEndpoint, LogSafe.Escape(username));
                await SendChallengeFailureAsync(AuthResult.FailNoAccess, cancellationToken).ConfigureAwait(false);
                return;
            }

            _isAutocreate = false;
        }

        // Only now, after every early-out, does the connection commit to this account.
        _username = username;

        await SendChallengeSuccessAsync(_srp, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleProofAsync(CancellationToken cancellationToken)
    {
        byte[] body = new byte[LogonProofRequest.BodyLength];
        await ReadPacketPartAsync(body, cancellationToken).ConfigureAwait(false);

        // One proof per challenge (vmangos STATUS_INVALID on entry, AuthSocket.cpp:555): the SRP
        // state is consumed whether the proof succeeds or fails, so it cannot be retried or replayed.
        Srp6Server? srp = _srp;
        string username = _username;
        bool isAutocreate = _isAutocreate;
        byte[]? autocreateVerifier = _autocreateVerifier;
        ResetChallengeState();

        // The state machine: a proof consumes the challenge, so from here the session holds no SRP state
        // and is not authenticated until this very proof says so (vmangos STATUS_INVALID on entry).
        Invariant.Assert(_srp is null && !_authenticated && _username.Length == 0, "a logon proof consumes the challenge state before it is judged");

        if (srp is null
            || !LogonProofRequest.TryParse(body, out LogonProofRequest? request)
            || request is null)
        {
            logger.LogWarning("[{Endpoint}] logon proof without a valid challenge", remoteEndpoint);
            RecordFailure();
            await SendProofFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        // PIN / authenticator security flags are not supported in M1.
        if (request.SecurityFlags != 0)
        {
            logger.LogInformation("[{Endpoint}] unsupported security flags 0x{Flags:X2}",
                remoteEndpoint, request.SecurityFlags);
            await SendProofFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!srp.TryAcceptProof(username, request.ClientPublicKey, request.ClientProof)
            || srp.SessionKey is null || srp.ServerProof is null)
        {
            logger.LogInformation("[{Endpoint}] invalid proof for '{Account}'", remoteEndpoint, LogSafe.Escape(username));
            RecordFailure();
            await SendProofFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        // What is about to be persisted: K is the 40-byte key the world daemon keys its header cipher
        // with (account.sessionkey), and an auto-create candidate carries the verifier its challenge was
        // built from. Either missing means the challenge handler and this handler disagree; refuse rather
        // than store a row the world cannot use or an account with no verifier.
        if (!Invariant.Check(srp.SessionKey.Length == WowSrp6.SessionKeyLength, $"session key of {srp.SessionKey.Length} bytes about to be stored for an account")
            || !Invariant.Check(!isAutocreate || autocreateVerifier is not null, "auto-create proof without the verifier its challenge was built from"))
        {
            await SendProofFailureAsync(AuthResult.FailNoAccess, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (isAutocreate)
        {
            await accountStore.CreateAsync(
                new Account
                {
                    Username = username,
                    Salt = srp.Salt,
                    Verifier = autocreateVerifier!,
                    SessionKey = srp.SessionKey,
                    Status = AccountStatus.Active,
                },
                cancellationToken).ConfigureAwait(false);
            logger.LogInformation("[{Endpoint}] auto-created account '{Account}'", remoteEndpoint, LogSafe.Escape(username));
        }
        else
        {
            await accountStore.UpdateSessionKeyAsync(username, srp.SessionKey, cancellationToken)
                .ConfigureAwait(false);
        }

        _authenticated = true;
        _unauthenticatedLifetime?.CancelAfter(Timeout.InfiniteTimeSpan); // proven: the pre-proof lifetime no longer applies
        logger.LogInformation("[{Endpoint}] '{Account}' authenticated", remoteEndpoint, LogSafe.Escape(username));
        await SendProofSuccessAsync(srp.ServerProof, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleReconnectChallengeAsync(CancellationToken cancellationToken)
    {
        ResetChallengeState();
        byte[] header = new byte[3];
        await ReadPacketPartAsync(header, cancellationToken).ConfigureAwait(false);
        ushort bodySize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1, 2));
        if (bodySize < ChallengeMinBody || bodySize > ChallengeMaxBody)
        {
            RecordFailure();
            _closeRequested = true;
            return;
        }

        byte[] body = new byte[bodySize];
        await ReadPacketPartAsync(body, cancellationToken).ConfigureAwait(false);

        // Net:Protection:AuthFailureBurstPerIp applies to a reconnect exactly as to a logon challenge: a spent
        // address is refused before the ban and account lookups (see HandleChallengeAsync).
        if (guard is not null && !guard.AllowsAuthAttempt(_address))
        {
            await SendReconnectFailureAsync(AuthResult.FailNoAccess, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (body[29] > MaxUsernameLength || !AllowedLocales.Contains(ReadLocale(body))
            || !LogonChallengeRequest.TryParse(body, out LogonChallengeRequest? request) || request is null
            || request.Build != ClientBuild.Vanilla1121)
        {
            RecordFailure();
            await SendReconnectFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        string username = request.Username.ToUpperInvariant();
        if (options.StrictUsernameCharset && !IsPrintableAscii(username))
        {
            RecordFailure();
            await SendReconnectFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? address = AccountBanEvaluator.AddressOfEndpoint(remoteEndpoint);
        if (_banStore is not null && address is not null && await IsIpBannedAsync(address, cancellationToken).ConfigureAwait(false))
        {
            RecordFailure();
            await SendReconnectFailureAsync(AuthResult.FailNoAccess, cancellationToken).ConfigureAwait(false);
            return;
        }

        Account? account = await accountStore.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);
        bool accountBanned = account?.Status == AccountStatus.Banned
            || account is not null && _banStore is not null
            && await _banStore.GetActiveAccountBanAsync(account.Id, cancellationToken).ConfigureAwait(false) is not null;
        if (account is null || account.Status != AccountStatus.Active
            || accountBanned
            || account.SessionKey is not { Length: 40 } sessionKey)
        {
            AuthResult result = accountBanned ? AuthResult.Banned
                : account?.Status == AccountStatus.Suspended ? AuthResult.Suspended : AuthResult.UnknownAccount;
            RecordFailure();
            await SendReconnectFailureAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        _username = username;
        _reconnectSessionKey = sessionKey.ToArray();
        _reconnectChallenge = RandomNumberGenerator.GetBytes(16);
        var writer = new PacketWriter(34);
        writer.WriteByte((byte)AuthCommand.ReconnectChallenge);
        writer.WriteByte((byte)AuthResult.Success);
        writer.WriteBytes(_reconnectChallenge);
        writer.WriteBytes(AuthConstants.VersionChallenge);
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleReconnectProofAsync(CancellationToken cancellationToken)
    {
        const int bodyLength = 16 + 20 + 20 + 1;
        byte[] body = new byte[bodyLength];
        await ReadPacketPartAsync(body, cancellationToken).ConfigureAwait(false);
        byte[]? challenge = _reconnectChallenge;
        byte[]? sessionKey = _reconnectSessionKey;
        string username = _username;
        _authenticated = false;
        _reconnectChallenge = null;
        _reconnectSessionKey = null;

        if (challenge is null || sessionKey is null || username.Length == 0 || body[^1] != 0)
        {
            CloseReconnectProof();
            return;
        }

        byte[] input = new byte[username.Length + 16 + 16 + sessionKey.Length];
        int offset = 0;
        offset += System.Text.Encoding.ASCII.GetBytes(username, input.AsSpan(offset));
        body.AsSpan(0, 16).CopyTo(input.AsSpan(offset)); offset += 16;
        challenge.CopyTo(input, offset); offset += challenge.Length;
        sessionKey.CopyTo(input, offset);
        byte[] expected = SHA1.HashData(input);
        bool valid = CryptographicOperations.FixedTimeEquals(expected, body.AsSpan(16, 20));
        if (!valid)
        {
            CloseReconnectProof();
            return;
        }

        _authenticated = true;
        var writer = new PacketWriter(2);
        writer.WriteByte((byte)AuthCommand.ReconnectProof);
        writer.WriteByte((byte)AuthResult.Success);
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Charge one failed attempt to this connection's address (Net:Protection:AuthFailureBurstPerIp).</summary>
    private void RecordFailure() => guard?.RecordAuthFailure(_address);

    /// <summary>
    /// Read the rest of a packet whose command byte has arrived, bounded by the frame deadline
    /// (Auth:ReadTimeoutSeconds, else Net:Protection:FrameReadTimeout). The deadline is armed for the
    /// read and disarmed after it; its expiry surfaces as the OperationCanceledException RunAsync reports.
    /// </summary>
    private async Task ReadPacketPartAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        ReadDeadline? deadline = _deadline;
        if (deadline is null || !deadline.Enabled)
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }

        deadline.Arm();
        await stream.ReadExactlyAsync(buffer, deadline.Token).ConfigureAwait(false);
        deadline.Disarm();
    }

    /// <summary>The country field (body offset 17), reversed on the wire (AuthSocket.cpp:301-304).</summary>
    private static string ReadLocale(byte[] body)
    {
        char[] chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            chars[3 - i] = (char)body[17 + i];
        }

        return new string(chars);
    }

    /// <summary>
    /// The IP-ban check of a challenge or a reconnect challenge: through the listener's <see cref="RealmIpBanCache"/>
    /// when there is one, otherwise one row read per call (vmangos AuthSocket.cpp:338-352). A store error propagates.
    /// </summary>
    private async ValueTask<bool> IsIpBannedAsync(string address, CancellationToken cancellationToken)
        => _ipBanCache is not null
            ? await _ipBanCache.IsBannedAsync(address, _banStore!, cancellationToken).ConfigureAwait(false)
            : await _banStore!.GetActiveIpBanAsync(address, cancellationToken).ConfigureAwait(false) is not null;

    private static bool IsPrintableAscii(string value)
    {
        foreach (char c in value)
        {
            if (c < 0x21 || c > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private void ResetChallengeState()
    {
        _srp = null;
        _isAutocreate = false;
        _autocreateVerifier = null;
        _username = string.Empty;
        _authenticated = false; // a new challenge or proof always revokes the previous authentication
        _reconnectChallenge = null;
        _reconnectSessionKey = null;
    }

    private async Task HandleRealmListAsync(CancellationToken cancellationToken)
    {
        // request body is a single unused uint32.
        byte[] discard = new byte[4];
        await ReadPacketPartAsync(discard, cancellationToken).ConfigureAwait(false);

        if (!_authenticated)
        {
            logger.LogWarning("[{Endpoint}] realm list requested before authentication", remoteEndpoint);
            return;
        }

        // Authenticated means the proof consumed the SRP state; a session that is both authenticated and
        // holding an Srp6Server could answer a second proof against a stale challenge.
        Invariant.Assert(_srp is null, "an authenticated logon session holds no SRP state");

        IReadOnlyList<RealmEntry> realms = await realmStore.GetRealmsAsync(cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> packet = RealmListWriter.Build(realms, charactersPerRealm: 0);
        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("[{Endpoint}] sent realm list ({Count} realm(s))", remoteEndpoint, realms.Count);
    }

    // --- reply builders ----------------------------------------------------------

    private async Task SendChallengeSuccessAsync(Srp6Server srp, CancellationToken cancellationToken)
    {
        var writer = new PacketWriter(128);
        writer.WriteByte((byte)AuthCommand.LogonChallenge);
        writer.WriteByte(0x00); // protocol/unk
        writer.WriteByte((byte)AuthResult.Success);
        writer.WriteBytes(srp.PublicEphemeral);          // B (32, LE)
        writer.WriteByte(1);                             // g length
        writer.WriteByte(WowSrp6.Generator);             // g = 7
        writer.WriteByte((byte)WowSrp6.KeyLength);       // N length = 32
        writer.WriteBytes(WowSrp6.NLittleEndian);        // N (32, LE)
        writer.WriteBytes(srp.Salt);                     // s (32)
        writer.WriteBytes(AuthConstants.VersionChallenge); // crc_salt (16)
        writer.WriteByte(0x00);                          // security flag (none)
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendChallengeFailureAsync(AuthResult result, CancellationToken cancellationToken)
    {
        var writer = new PacketWriter(3);
        writer.WriteByte((byte)AuthCommand.LogonChallenge);
        writer.WriteByte(0x00);
        writer.WriteByte((byte)result);
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendProofSuccessAsync(byte[] serverProof, CancellationToken cancellationToken)
    {
        // AUTH_LOGON_PROOF_S (build < 6299): cmd, error=0, M2[20], surveyId(u32)=0.
        var writer = new PacketWriter(26);
        writer.WriteByte((byte)AuthCommand.LogonProof);
        writer.WriteByte((byte)AuthResult.Success);
        writer.WriteBytes(serverProof);
        writer.WriteUInt32(0); // survey id
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendProofFailureAsync(AuthResult result, CancellationToken cancellationToken)
    {
        // vmangos failure path: cmd, error, uint16(0).
        var writer = new PacketWriter(4);
        writer.WriteByte((byte)AuthCommand.LogonProof);
        writer.WriteByte((byte)result);
        writer.WriteUInt16(0);
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendReconnectFailureAsync(AuthResult result, CancellationToken cancellationToken)
    {
        var writer = new PacketWriter(2);
        writer.WriteByte((byte)AuthCommand.ReconnectChallenge);
        writer.WriteByte((byte)result);
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
        _closeRequested = true;
    }

    /// <summary>A failed or out-of-order reconnect proof is a failed authentication attempt (Net:Protection:AuthFailureBurstPerIp).</summary>
    private void CloseReconnectProof()
    {
        RecordFailure();
        _closeRequested = true;
    }
}
